using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FloatCanvas.App;

internal sealed record UpdateInstallResult(bool Success, bool RolledBack, string Version, string? Error);

/// <summary>A second copy of the application replaces only files owned by the portable package.</summary>
internal static class AppUpdateInstaller
{
    private const string ManifestName = "PACKAGE-INFO.json";
    private const string StartupPlanEnvironment = "FLOATCANVAS_UPDATE_STARTUP_PLAN";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static readonly string[] Required = { "FloatCanvas.exe", "FloatCanvas.dll", "FloatCanvas.Core.dll", "FloatCanvas.Localization.dll", "FloatCanvas.Rendering.dll", "FloatCanvas.Storage.dll", "FloatCanvas.Windows.dll", "FloatCanvas.deps.json", "FloatCanvas.runtimeconfig.json", "SkiaSharp.dll", "SkiaSharp.HarfBuzz.dll", "HarfBuzzSharp.dll", "libSkiaSharp.dll", "libHarfBuzzSharp.dll", ManifestName };

    internal static bool CanUpdateDirectory(string installDir)
    {
        try { var root = Root(installDir); ReadManifest(root); ProbeWritable(root); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException) { return false; }
    }

    internal static string Prepare(PreparedUpdate update, string installDir, string profileDir, string[] restartArgs)
    {
        string install = Root(installDir), profile = Root(profileDir), source = Root(update.PackageDirectory);
        Separate(install, profile); Separate(install, source);
        var old = ReadManifest(install); var replacement = ReadManifest(source);
        RequireNewer(replacement.Version, old.Version);
        if (replacement.Version != update.Version || !Set(replacement.Files).SetEquals(update.Files))
            throw new InvalidOperationException("The downloaded package manifest does not match the prepared update.");
        string[] arguments = RestartArguments(restartArgs, profile);
        ProbeWritable(install);
        var oldFiles = Snapshot(install, old.Files); var newFiles = Snapshot(source, replacement.Files);
        var owned = Set(old.Files);
        foreach (var file in newFiles)
            if (!owned.Contains(file.Path) && (File.Exists(Child(install, file.Path)) || Directory.Exists(Child(install, file.Path))))
                throw new IOException("An untracked file would be overwritten by the update: " + file.Path);
        string jobs = Path.Combine(profile, "updates", "jobs"); Directory.CreateDirectory(jobs); Root(jobs);
        string job = Path.Combine(jobs, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(job); Root(job);
        string package = Path.Combine(job, "package"), worker = Path.Combine(job, "worker");
        Directory.CreateDirectory(package); Directory.CreateDirectory(worker); Directory.CreateDirectory(Path.Combine(job, "backup"));
        CopySnapshot(source, package, newFiles); CopySnapshot(install, worker, oldFiles);
        // Verify complete copies before the running application considers exiting.
        Verify(package, newFiles); Verify(worker, oldFiles);
        using var parent = Process.GetCurrentProcess();
        var plan = new InstallPlan(1, install, profile, parent.Id, parent.StartTime.ToUniversalTime().Ticks, replacement.Version, arguments, oldFiles, newFiles);
        string path = Path.Combine(job, "plan.json"); WriteJson(path, plan); return path;
    }

    internal static void Launch(string planPath)
    {
        var (plan, job) = ReadPlan(planPath);
        Verify(Path.Combine(job, "worker"), plan.OldFiles);
        var start = new ProcessStartInfo(Child(Path.Combine(job, "worker"), "FloatCanvas.exe"))
        { WorkingDirectory = Path.Combine(job, "worker"), UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(Path.GetFullPath(planPath));
        using var process = Process.Start(start) ?? throw new IOException("The update helper could not start.");
        string ready = Path.Combine(job, "ready.json");
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(8))
        {
            if (IdentityMatches(ready, process)) return;
            if (process.HasExited) throw new IOException("The update helper exited before it was ready (" + process.ExitCode + ").");
            Thread.Sleep(40);
        }
        // This is only the helper created above; the running application has not been asked to exit.
        if (!process.HasExited) process.Kill();
        throw new IOException("The update helper did not become ready. The application can continue running.");
    }

    internal static int Run(string planPath) => Run(planPath, null);

    // The seam replaces process timing and file/start failures in isolated transactional tests.
    internal static int Run(string planPath, InstallerHooks? hooks)
    {
        InstallPlan? plan = null; string? job = null; bool changed = false, ownsJob = false, parentExited = false;
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            (plan, job) = ReadPlan(planPath);
            if (plan.ParentProcessId == Environment.ProcessId) throw new InvalidOperationException("An update worker cannot wait for itself.");
            using var guard = new FileStream(Path.Combine(job, "worker.lock"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            ownsJob = true;
            using (var current = Process.GetCurrentProcess()) WriteJson(Path.Combine(job, "ready.json"), Identity(current));
            Log(job, "Waiting for the bound parent process to exit.");
            if (!(hooks?.WaitForParent?.Invoke(plan) ?? WaitForParent(plan)))
                throw new IOException("The application did not exit in time. No installed files were changed.");
            parentExited = true;
            // Nothing in the installation is touched until the parent has exited and every input is checked again.
            var currentManifest = ReadManifest(plan.InstallDirectory); RequireNewer(plan.Version, currentManifest.Version); ProbeWritable(plan.InstallDirectory);
            Verify(plan.InstallDirectory, plan.OldFiles); Verify(Path.Combine(job, "package"), plan.NewFiles);
            var old = plan.OldFiles.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            foreach (var file in plan.NewFiles)
                if (!old.ContainsKey(file.Path) && (File.Exists(Child(plan.InstallDirectory, file.Path)) || Directory.Exists(Child(plan.InstallDirectory, file.Path))))
                    throw new IOException("An untracked file appeared at an update destination: " + file.Path);
            hooks?.BeforeBackup?.Invoke();
            CopySnapshot(plan.InstallDirectory, Path.Combine(job, "backup"), plan.OldFiles);
            Verify(Path.Combine(job, "backup"), plan.OldFiles);
            foreach (var file in plan.NewFiles.OrderBy(f => f.Path.Equals(ManifestName, StringComparison.OrdinalIgnoreCase)))
            {
                touched.Add(file.Path); changed = true; hooks?.BeforeReplace?.Invoke(file.Path);
                AtomicCopy(Child(Path.Combine(job, "package"), file.Path), Child(plan.InstallDirectory, file.Path));
            }
            var newPaths = Set(plan.NewFiles.Select(f => f.Path));
            foreach (var file in plan.OldFiles.Where(f => !newPaths.Contains(f.Path)))
            { touched.Add(file.Path); changed = true; File.Delete(Child(plan.InstallDirectory, file.Path)); }
            Verify(plan.InstallDirectory, plan.NewFiles);
            SaveResult(plan, new(true, false, plan.Version, null));
            Restart(plan, job, hooks); Log(job, "Update installed and restarted."); return 0;
        }
        catch (Exception ex)
        {
            if (plan is null || job is null || !ownsJob) return 2; // Invalid/duplicate workers never authorize even a result/log write.
            Log(job, ex.ToString());
            bool rolledBack = false; string error = ex.Message;
            if (changed)
            {
                try
                {
                    var old = plan.OldFiles.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
                    Verify(Path.Combine(job, "backup"), plan.OldFiles);
                    foreach (string path in touched)
                    {
                        if (old.TryGetValue(path, out var original))
                        {
                            string target = Child(plan.InstallDirectory, path);
                            if (!File.Exists(target) || !string.Equals(Hash(target), original.Sha256, StringComparison.OrdinalIgnoreCase))
                                AtomicCopy(Child(Path.Combine(job, "backup"), path), target);
                        }
                        else File.Delete(Child(plan.InstallDirectory, path));
                    }
                    Verify(plan.InstallDirectory, plan.OldFiles); rolledBack = true;
                }
                catch (Exception rollback) { error += " Recovery failed; the original files remain in the backup directory. " + rollback.Message; Log(job, rollback.ToString()); }
            }
            try { SaveResult(plan, new(false, rolledBack, plan.Version, error)); }
            catch (Exception resultError) when (resultError is IOException or UnauthorizedAccessException) { Log(job, "Could not write update result: " + resultError); }
            if (rolledBack || (parentExited && !changed))
            {
                try { Verify(plan.InstallDirectory, plan.OldFiles); Restart(plan, job, hooks); }
                catch (Exception restart) { Log(job, "The restored application could not restart: " + restart); }
            }
            return 1;
        }
    }

    internal static UpdateInstallResult? ConsumeResult(string profileDir)
    {
        string path = Path.Combine(Root(profileDir), "updates", "result.json");
        if (!File.Exists(path)) return null;
        NoReparse(path);
        try { var result = ReadJson<UpdateInstallResult>(path); File.Delete(path); return result; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Called only after the normal application session has started successfully.</summary>
    internal static void AcknowledgeStartup()
    {
        string? path = Environment.GetEnvironmentVariable(StartupPlanEnvironment);
        Environment.SetEnvironmentVariable(StartupPlanEnvironment, null);
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var (plan, job) = ReadPlan(path);
            if (!Same(AppContext.BaseDirectory, plan.InstallDirectory)) return;
            using var current = Process.GetCurrentProcess();
            WriteJson(Path.Combine(job, "started.json"), Identity(current));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException) { }
    }

    private static void Restart(InstallPlan plan, string job, InstallerHooks? hooks)
    {
        string executable = Child(plan.InstallDirectory, "FloatCanvas.exe");
        if (hooks?.Restart is { } restart) { restart(executable, plan.RestartArguments); return; }
        var start = new ProcessStartInfo(executable) { WorkingDirectory = plan.InstallDirectory, UseShellExecute = false };
        string started = Path.Combine(job, "started.json"); NoReparse(started); if (File.Exists(started)) File.Delete(started);
        start.Environment[StartupPlanEnvironment] = Path.Combine(job, "plan.json");
        foreach (string argument in plan.RestartArguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("The updated application could not start.");
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (IdentityMatches(started, process)) return;
            if (process.WaitForExit(60)) throw new IOException("The application exited before confirming startup (" + process.ExitCode + ").");
        }
        // Terminate only this unsuccessful launch, so its own file locks do not prevent rollback.
        if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); }
        throw new IOException("The application did not confirm a successful startup.");
    }

    private static ProcessIdentity Identity(Process process) => new(process.Id, process.StartTime.ToUniversalTime().Ticks);
    private static bool IdentityMatches(string path, Process process)
    {
        try { return File.Exists(path) && ReadJson<ProcessIdentity>(path) == Identity(process); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return false; }
    }

    private static bool WaitForParent(InstallPlan plan)
    {
        try
        {
            using var parent = Process.GetProcessById(plan.ParentProcessId);
            if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartUtcTicks) return true; // Reused PID belongs to somebody else.
            return parent.WaitForExit(30_000);
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
    }

    private static (InstallPlan Plan, string Job) ReadPlan(string path)
    {
        path = Path.GetFullPath(path); NoReparse(path);
        var plan = ReadJson<InstallPlan>(path);
        if (plan.Format != 1 || plan.ParentProcessId <= 0 || plan.ParentStartUtcTicks <= 0)
            throw new InvalidOperationException("Invalid update process binding.");
        string install = Root(plan.InstallDirectory), profile = Root(plan.ProfileDirectory);
        if (install != plan.InstallDirectory || profile != plan.ProfileDirectory) throw new InvalidOperationException("Update paths must be canonical.");
        Separate(install, profile);
        string job = Root(Path.GetDirectoryName(path)!);
        string expected = Path.Combine(profile, "updates", "jobs", Path.GetFileName(job));
        if (!Guid.TryParseExact(Path.GetFileName(job), "N", out _) || !Same(job, expected) || !Same(path, Path.Combine(job, "plan.json")))
            throw new InvalidOperationException("The update plan is outside its isolated job directory.");
        Root(Path.Combine(job, "worker")); Root(Path.Combine(job, "package")); Root(Path.Combine(job, "backup"));
        ValidateFiles(plan.OldFiles); ValidateFiles(plan.NewFiles); Version(plan.Version);
        if (!RestartArguments(plan.RestartArguments, profile).SequenceEqual(plan.RestartArguments)) throw new InvalidOperationException("Invalid restart arguments.");
        var packageManifest = ReadManifest(Path.Combine(job, "package"));
        var oldManifest = ReadManifest(Path.Combine(job, "worker"));
        RequireNewer(packageManifest.Version, oldManifest.Version);
        if (packageManifest.Version != plan.Version || !Set(packageManifest.Files).SetEquals(plan.NewFiles.Select(f => f.Path)) || !Set(oldManifest.Files).SetEquals(plan.OldFiles.Select(f => f.Path)))
            throw new InvalidOperationException("Update plan and package manifests disagree.");
        return (plan, job);
    }

    private static PackageManifest ReadManifest(string directory)
    {
        directory = Root(directory); var manifest = ReadJson<PackageManifest>(Child(directory, ManifestName));
        if (manifest.Product != "浮绘 FloatCanvas" || manifest.RuntimeIdentifier != "win-x64" || manifest.SelfContained || manifest.Configuration != "Release")
            throw new InvalidOperationException("Automatic replacement requires an official Windows x64 Release portable package.");
        Version(manifest.Version);
        if (manifest.Files is null || manifest.Files.Length is < 1 or > 4096) throw new InvalidOperationException("The package has no valid owned-file manifest.");
        var files = Set(manifest.Files);
        if (files.Count != manifest.Files.Length || Required.Any(required => !files.Contains(required))) throw new InvalidOperationException("The package file list is incomplete or contains duplicates.");
        foreach (string relative in manifest.Files)
        {
            string file = Child(directory, relative);
            if (!File.Exists(file) || new FileInfo(file).Length == 0) throw new IOException("The portable package is incomplete: " + relative);
        }
        return manifest;
    }

    private static string[] RestartArguments(string[] arguments, string profile)
    {
        if (arguments is null || arguments.Length > 8) throw new InvalidOperationException("Invalid update restart arguments.");
        bool primary = false, all = false, recovered = false, hasProfile = false;
        for (int i = 0; i < arguments.Length; i++)
        {
            switch (arguments[i])
            {
                case "--profile":
                    if (hasProfile || ++i == arguments.Length || !Same(Root(arguments[i]), profile)) throw new InvalidOperationException("The restart profile must match the update profile.");
                    hasProfile = true; break;
                case "--recover": if (recovered) throw new InvalidOperationException("Duplicate recovery option."); recovered = true; break;
                case "--primary-screen": if (primary) throw new InvalidOperationException("Duplicate screen option."); primary = true; break;
                case "--all-screens": if (all) throw new InvalidOperationException("Duplicate screen option."); all = true; break;
                default: throw new InvalidOperationException("Unsupported update restart argument.");
            }
        }
        if (!hasProfile || !recovered || (primary && all)) throw new InvalidOperationException("Updates must restart with the same profile and recovery enabled.");
        return arguments.ToArray();
    }

    private static OwnedFile[] Snapshot(string root, IEnumerable<string> files) => files.Select(path => new OwnedFile(path, Hash(Child(root, path)))).ToArray();
    private static void ValidateFiles(OwnedFile[] files)
    {
        if (files is null || files.Length is < 1 or > 4096 || Set(files.Select(f => f.Path)).Count != files.Length || Required.Any(r => !files.Any(f => string.Equals(f.Path, r, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("Invalid update file list.");
        foreach (var file in files) { Relative(file.Path); if (!Regex.IsMatch(file.Sha256 ?? "", "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)) throw new InvalidOperationException("Invalid update file hash."); }
    }
    private static void Verify(string directory, IEnumerable<OwnedFile> files)
    {
        Root(directory);
        foreach (var file in files)
            if (!string.Equals(Hash(Child(directory, file.Path)), file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Update file verification failed: " + file.Path);
    }
    private static void CopySnapshot(string source, string target, IEnumerable<OwnedFile> files)
    {
        foreach (var file in files) AtomicCopy(Child(source, file.Path), Child(target, file.Path));
    }
    private static void AtomicCopy(string source, string target)
    {
        NoReparse(source); NoReparse(target); Directory.CreateDirectory(Path.GetDirectoryName(target)!); NoReparse(target);
        string temp = target + ".update-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { input.CopyTo(output); output.Flush(true); }
            NoReparse(target); File.Move(temp, target, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static string Hash(string path) { NoReparse(path); using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static HashSet<string> Set(IEnumerable<string> files) => new(files, StringComparer.OrdinalIgnoreCase);
    private static string Root(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute local directory is required.");
        string root = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (root.Length <= 3 || root.StartsWith("\\\\", StringComparison.Ordinal) || !Directory.Exists(root)) throw new IOException("A local portable directory is required.");
        NoReparse(root); return root;
    }
    private static string Child(string root, string relative)
    { Relative(relative); string child = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))); if (!Below(child, root)) throw new InvalidOperationException("An update path escapes its directory."); NoReparse(child); return child; }
    private static void Relative(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 240 || Path.IsPathRooted(path) || path.Contains('\\') || path.Contains(':') || path.Split('/').Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Regex.IsMatch(part, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            throw new InvalidOperationException("Invalid package-relative filename: " + path);
        if (Path.GetExtension(path).Equals(".pdb", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Update packages must not contain debug symbols.");
    }
    private static void NoReparse(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Update paths cannot contain symbolic links or junctions."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }
    private static bool Below(string child, string root) => child.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    private static void Separate(string a, string b) { if (Same(a, b) || Below(a, b) || Below(b, a)) throw new InvalidOperationException("Installation, profile and downloaded package directories must be separate."); }
    private static void Version(string version) { if (!SemVersion.TryParse(version, out _)) throw new InvalidOperationException("Invalid update version."); }
    private static void RequireNewer(string replacement, string installed)
    {
        if (!SemVersion.TryParse(replacement, out var next) || !SemVersion.TryParse(installed, out var current) || next!.CompareTo(current) <= 0)
            throw new InvalidOperationException("The update must be newer than the installed package.");
    }
    private static void ProbeWritable(string directory)
    {
        Root(directory); string path = Path.Combine(directory, ".floatcanvas-write-" + Guid.NewGuid().ToString("N"));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose); stream.WriteByte(1);
    }
    private static T ReadJson<T>(string path)
    {
        NoReparse(path); if (new FileInfo(path).Length > 1_048_576) throw new InvalidOperationException("Update metadata is too large.");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? throw new InvalidOperationException("Update metadata is empty.");
    }
    private static void WriteJson<T>(string path, T value)
    {
        NoReparse(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); NoReparse(path);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(value, Json)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void SaveResult(InstallPlan plan, UpdateInstallResult result) => WriteJson(Path.Combine(plan.ProfileDirectory, "updates", "result.json"), result);
    private static void Log(string job, string message) { try { string path = Path.Combine(job, "worker.log"); NoReparse(path); File.AppendAllText(path, DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

    internal sealed record OwnedFile(string Path, string Sha256);
    private sealed record ProcessIdentity(int ProcessId, long StartUtcTicks);
    internal sealed record InstallPlan(int Format, string InstallDirectory, string ProfileDirectory, int ParentProcessId, long ParentStartUtcTicks, string Version, string[] RestartArguments, OwnedFile[] OldFiles, OwnedFile[] NewFiles);
    private sealed record PackageManifest([property: JsonRequired] string Product, [property: JsonRequired] string Version, [property: JsonRequired] string RuntimeIdentifier, [property: JsonRequired] bool SelfContained, [property: JsonRequired] string Configuration, [property: JsonRequired] string[] Files);
    internal sealed class InstallerHooks
    {
        internal Func<InstallPlan, bool>? WaitForParent { get; init; }
        internal Action? BeforeBackup { get; init; }
        internal Action<string>? BeforeReplace { get; init; }
        internal Action<string, string[]>? Restart { get; init; }
    }
}
