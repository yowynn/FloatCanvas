using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed record UpdateInstallResult(bool Success, bool RolledBack, string Version, string? Error);

/// <summary>A second copy of the application replaces only files owned by the portable package.</summary>
internal static class AppUpdateInstaller
{
    private const string ManifestName = "PACKAGE-INFO.json";
    private const string StartupPlanEnvironment = "FLOATCANVAS_UPDATE_STARTUP_PLAN";
    internal const string RecoveryPointerName = ".floatcanvas-update";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static readonly string[] Required = { "FloatCanvas.exe", "FloatCanvas.dll", "FloatCanvas.Core.dll", "FloatCanvas.Localization.dll", "FloatCanvas.Rendering.dll", "FloatCanvas.Storage.dll", "FloatCanvas.Windows.dll", "FloatCanvas.deps.json", "FloatCanvas.runtimeconfig.json", "SkiaSharp.dll", "SkiaSharp.HarfBuzz.dll", "HarfBuzzSharp.dll", "libSkiaSharp.dll", "libHarfBuzzSharp.dll", ManifestName };

    internal static bool CanUpdateDirectory(string installDir) => GetUpdateDirectoryError(installDir) is null;

    internal static string? GetUpdateDirectoryError(string installDir)
    {
        try
        {
            var root = Root(installDir);
            if (InstallerRegistration.Scope(root) == InstallationScope.AllUsers) return L.Get("Updates.RequiresSetup");
            ReadManifest(root); ProbeWritable(root); return null;
        }
        catch (IncompletePackageFileException ex) { return L.Format("Updates.IncompletePackage", ex.RelativeFile); }
        catch (UnauthorizedAccessException) { return L.Get("Updates.DirectoryAccessDenied"); }
        catch (Exception ex) when (ex is IOException or ArgumentException or JsonException or InvalidOperationException) { return ex.Message; }
    }

    private sealed class IncompletePackageFileException(string relativeFile) : IOException("The portable package is incomplete: " + relativeFile)
    { internal string RelativeFile { get; } = relativeFile; }

    internal static string Prepare(PreparedUpdate update, string installDir, string profileDir, string[] restartArgs, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string install = Root(installDir), profile = Root(profileDir), source = Root(update.PackageDirectory);
        if (InstallerRegistration.Scope(install) == InstallationScope.AllUsers) throw new InvalidOperationException("All-users installations must be updated through Setup.");
        Separate(install, profile); Separate(install, source);
        var old = ReadManifest(install); var replacement = ReadManifest(source);
        RequireNewer(replacement.Version, old.Version);
        if (replacement.Version != update.Version || !Set(replacement.Files).SetEquals(update.Files))
            throw new InvalidOperationException("The downloaded package manifest does not match the prepared update.");
        string[] arguments = RestartArguments(restartArgs, profile);
        ProbeWritable(install);
        var oldFiles = Snapshot(install, old.Files, cancellationToken); var newFiles = Snapshot(source, replacement.Files, cancellationToken);
        var owned = Set(old.Files);
        foreach (var file in newFiles)
            if (!owned.Contains(file.Path) && (File.Exists(Child(install, file.Path)) || Directory.Exists(Child(install, file.Path))))
                throw new IOException("An untracked file would be overwritten by the update: " + file.Path);
        string jobs = Path.Combine(profile, "updates", "jobs"); Directory.CreateDirectory(jobs); Root(jobs);
        string job = Path.Combine(jobs, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(job); Root(job);
        try
        {
            string package = Path.Combine(job, "package"), worker = Path.Combine(job, "worker");
            Directory.CreateDirectory(package); Directory.CreateDirectory(worker); Directory.CreateDirectory(Path.Combine(job, "backup"));
            CopySnapshot(source, package, newFiles, cancellationToken); CopySnapshot(install, worker, oldFiles, cancellationToken);
            // Verify complete copies before the running application considers exiting.
            Verify(package, newFiles, cancellationToken); Verify(worker, oldFiles, cancellationToken);
            using var parent = Process.GetCurrentProcess();
            var plan = new InstallPlan(1, install, profile, parent.Id, parent.StartTime.ToUniversalTime().Ticks, replacement.Version, arguments, oldFiles, newFiles);
            string path = Path.Combine(job, "plan.json"); WriteJson(path, plan); return path;
        }
        catch
        {
            // No worker has received this private path, and installation has not changed.
            try { DeleteOwnedDirectory(jobs, Path.GetFileName(job)); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    internal static void DiscardPreparation(string planPath)
    {
        var (plan, job) = ReadPlan(planPath);
        using (var lease = new FileStream(Path.Combine(job, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            if (ReadJournal(job) is not null || File.Exists(Path.Combine(job, "ready.json")) || File.Exists(Child(plan.InstallDirectory, RecoveryPointerName)))
                throw new IOException("A started update cannot be discarded as preparation.");
        }
        DeleteOwnedDirectory(Path.GetDirectoryName(job)!, Path.GetFileName(job));
    }

    internal static void Launch(string planPath)
    {
        var (plan, job) = ReadPlan(planPath);
        Verify(Path.Combine(job, "worker"), plan.OldFiles);
        string helper = Child(Path.Combine(job, "worker"), "FloatCanvas.Host.exe");
        if (!File.Exists(helper)) helper = Child(Path.Combine(job, "worker"), "FloatCanvas.exe");
        var start = new ProcessStartInfo(helper)
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
        InstallPlan? plan = null; string? job = null; bool changed = false, ownsJob = false, parentExited = false, committed = false;
        FileStream? guard = null;
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            (plan, job) = ReadPlan(planPath);
            if (InstallerRegistration.Scope(plan.InstallDirectory) == InstallationScope.AllUsers)
                throw new InvalidOperationException("All-users installations must be updated through Setup.");
            if (plan.ParentProcessId == Environment.ProcessId) throw new InvalidOperationException("An update worker cannot wait for itself.");
            // The OS owns the lease: a stale file does not imply a live worker.
            guard = new FileStream(Path.Combine(job, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var journal = ReadJournal(job);
            if (journal?.Phase is "Committed" or "RolledBack")
            {
                Verify(plan.InstallDirectory, journal.Phase == "Committed" ? plan.NewFiles : plan.OldFiles);
                ClearRecoveryPointer(plan, planPath);
                RefreshInstallerRegistration(plan, job); return 2;
            }
            ownsJob = true;
            using (var current = Process.GetCurrentProcess()) WriteJson(Path.Combine(job, "ready.json"), Identity(current));
            Log(job, "Waiting for the bound parent process to exit.");
            if (!(hooks?.WaitForParent?.Invoke(plan) ?? WaitForParent(plan)))
                throw new IOException("The application did not exit in time. No installed files were changed.");
            parentExited = true;
            if (journal is not null)
            {
                // Never validate a partially installed directory as an old package. The durable
                // write-ahead list is the authority for an interrupted replacement or rollback.
                foreach (string path in journal.Touched) { Relative(path); touched.Add(path); }
                changed = true;
                if (journal.Phase == "Starting" && StartedProcessAlive(job))
                {
                    Verify(plan.InstallDirectory, plan.NewFiles);
                    WriteJournal(job, "Committed", touched); committed = true; ClearRecoveryPointer(plan, planPath);
                    RefreshInstallerRegistration(plan, job);
                    SaveResult(plan, new(true, false, plan.Version, null)); return 0;
                }
                if (journal.Phase == "Starting") StopUnacknowledgedLaunch(plan, job);
                throw new IOException("An interrupted update was detected; restoring the complete previous version.");
            }
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
            WriteJournal(job, "Replacing", touched);
            WriteRecoveryPointer(plan, planPath);
            hooks?.AfterCheckpoint?.Invoke("RecoveryPointer");
            foreach (var file in plan.NewFiles.OrderBy(f => f.Path.Equals("FloatCanvas.exe", StringComparison.OrdinalIgnoreCase) ? -1 : f.Path.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) ? 1 : 0))
            {
                touched.Add(file.Path); changed = true; WriteJournal(job, "Replacing", touched); hooks?.BeforeReplace?.Invoke(file.Path);
                AtomicCopy(Child(Path.Combine(job, "package"), file.Path), Child(plan.InstallDirectory, file.Path));
                hooks?.AfterReplace?.Invoke(file.Path);
            }
            var newPaths = Set(plan.NewFiles.Select(f => f.Path));
            foreach (var file in plan.OldFiles.Where(f => !newPaths.Contains(f.Path)))
            { touched.Add(file.Path); changed = true; WriteJournal(job, "Replacing", touched); File.Delete(Child(plan.InstallDirectory, file.Path)); hooks?.AfterDelete?.Invoke(file.Path); }
            Verify(plan.InstallDirectory, plan.NewFiles);
            WriteJournal(job, "Starting", touched);
            Restart(plan, job, hooks, () =>
            {
                hooks?.AfterCheckpoint?.Invoke("StartupAcknowledged");
                // Keep the launch lease until the commit is durable. A crash or failed
                // commit stops the uncommitted child before recovery replaces its files.
                WriteJournal(job, "Committed", touched); committed = true;
            });
            hooks?.AfterCheckpoint?.Invoke("Committed"); ClearRecoveryPointer(plan, planPath);
            RefreshInstallerRegistration(plan, job);
            SaveResult(plan, new(true, false, plan.Version, null));
            Log(job, "Update installed and restarted."); return 0;
        }
        catch (Exception ex)
        {
            if (plan is null || job is null || !ownsJob) return 2; // Invalid/duplicate workers never authorize even a result/log write.
            Log(job, ex.ToString());
            if (committed)
            {
                // Notification/cleanup failure must never roll back an acknowledged live version.
                try { SaveResult(plan, new(true, false, plan.Version, null)); } catch (Exception notificationError) when (notificationError is IOException or UnauthorizedAccessException) { Log(job, notificationError.ToString()); }
                return 0;
            }
            bool rolledBack = false; string error = ex.Message;
            if (changed)
            {
                try
                {
                    var old = plan.OldFiles.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
                    Verify(Path.Combine(job, "backup"), plan.OldFiles);
                    WriteJournal(job, "RollingBack", touched);
                    foreach (string path in touched)
                    {
                        if (old.TryGetValue(path, out var original))
                        {
                            string target = Child(plan.InstallDirectory, path);
                            if (!File.Exists(target) || !string.Equals(Hash(target), original.Sha256, StringComparison.OrdinalIgnoreCase))
                                AtomicCopy(Child(Path.Combine(job, "backup"), path), target);
                        }
                        else
                        {
                            string target = Child(plan.InstallDirectory, path);
                            // The write-ahead entry may precede creation of its parent
                            // directory. Absence already is the complete old state.
                            if (File.Exists(target)) File.Delete(target);
                        }
                    }
                    Verify(plan.InstallDirectory, plan.OldFiles); rolledBack = true;
                    WriteJournal(job, "RolledBack", touched); ClearRecoveryPointer(plan, planPath);
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
        finally { guard?.Dispose(); }
    }

    private static InstallJournal? ReadJournal(string job)
    {
        string path = Path.Combine(job, "journal.json");
        if (!File.Exists(path)) return null;
        var state = ReadJson<InstallJournal>(path);
        if (state.Phase is not ("Replacing" or "Starting" or "RollingBack" or "Committed" or "RolledBack") || state.Touched is null || state.Touched.Length > 8192)
            throw new InvalidDataException("Invalid update journal; keep the backup for manual recovery.");
        return state;
    }
    private static void WriteJournal(string job, string phase, IEnumerable<string> touched) =>
        WriteJson(Path.Combine(job, "journal.json"), new InstallJournal(phase, touched.ToArray()));
    private static void WriteRecoveryPointer(InstallPlan plan, string planPath)
    {
        string marker = Child(plan.InstallDirectory, RecoveryPointerName);
        if (File.Exists(marker) && !Same(File.ReadAllText(marker), planPath))
            throw new IOException("Another update requires recovery before installing this update.");
        AtomicWrite(marker, System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(planPath)));
    }
    private static void ClearRecoveryPointer(InstallPlan plan, string planPath)
    {
        string marker = Child(plan.InstallDirectory, RecoveryPointerName);
        if (File.Exists(marker) && Same(File.ReadAllText(marker), planPath)) File.Delete(marker);
    }
    private static bool StartedProcessAlive(string job)
    {
        try
        {
            var identity = ReadJson<ProcessIdentity>(Path.Combine(job, "started.json"));
            using var process = Process.GetProcessById(identity.ProcessId);
            return IdentityMatches(Path.Combine(job, "started.json"), process) && !process.HasExited;
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or InvalidOperationException) { return false; }
    }
    private static void StopUnacknowledgedLaunch(InstallPlan plan, string job)
    {
        string path = Path.Combine(job, "launching.json");
        if (!File.Exists(path)) return;
        var identity = ReadJson<ProcessIdentity>(path);
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (!IdentityMatches(path, process) || process.HasExited) return;
            string? executable = process.MainModule?.FileName;
            if (executable is null || (!Same(executable, Child(plan.InstallDirectory, "FloatCanvas.exe")) && !Same(executable, Child(plan.InstallDirectory, "FloatCanvas.Host.exe"))))
                throw new IOException("Recovery will not terminate a process outside this installation.");
            process.Kill(); if (!process.WaitForExit(5000)) throw new IOException("The unconfirmed update launch could not stop.");
        }
        catch (ArgumentException) { } // The bound process already exited.
    }

    internal static UpdateInstallResult? ConsumeResult(string profileDir)
    {
        string path = Path.Combine(Root(profileDir), "updates", "result.json");
        if (!File.Exists(path)) return null;
        NoReparse(path);
        try { var result = ReadJson<UpdateInstallResult>(path); File.Delete(path); return result; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    // Only committed jobs are collectible. Failed, pending and live jobs remain recovery evidence.
    internal static void CleanupCompletedJobs(string profileDir, int retainedBackups = 2)
    {
        if (retainedBackups < 1) throw new ArgumentOutOfRangeException(nameof(retainedBackups));
        string profile = Root(profileDir), jobs = Path.Combine(profile, "updates", "jobs");
        NoReparse(jobs);
        int retained = 0;
        // The journal timestamp is stable; removing payloads changes the directory timestamp.
        foreach (string job in (Directory.Exists(jobs) ? Directory.GetDirectories(jobs) : Array.Empty<string>()).OrderByDescending(path => File.GetLastWriteTimeUtc(Path.Combine(path, "journal.json"))))
        {
            if (!Guid.TryParseExact(Path.GetFileName(job), "N", out _)) continue;
            try
            {
                NoReparse(job);
                if (ReadJournal(job)?.Phase != "Committed") continue;
                // Open, rather than create: inspecting pending/foreign jobs must not modify them.
                // Delete sharing lets this lease remain held while an obsolete whole job is removed.
                string lockPath = Child(job, "worker.lock");
                using var lease = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
                string planPath = Child(job, "plan.json");
                var plan = ReadJson<InstallPlan>(planPath);
                if (plan.Format != 1 || !Same(plan.ProfileDirectory, profile) || plan.ProfileDirectory != profile)
                    throw new InvalidDataException("The completed update does not belong to this profile.");
                string install = Root(plan.InstallDirectory); Separate(install, profile);
                ValidateFiles(plan.OldFiles); ValidateFiles(plan.NewFiles);
                string pointer = Child(install, RecoveryPointerName);
                if (File.Exists(pointer))
                {
                    if (new FileInfo(pointer).Length > 32768) throw new InvalidDataException("Invalid update recovery pointer.");
                    if (Same(File.ReadAllText(pointer), planPath)) continue;
                }
                // Inspect the entire job before deleting even one payload. A nested junction or
                // unexpected user file must preserve all evidence, including otherwise safe siblings.
                var tree = InspectCleanupTree(job);
                ValidateCleanupJob(job, tree.Files, plan);
                ValidateCleanupDirectories(job, tree.Directories, tree.Files,
                    new[] { Path.Combine(job, "backup"), Path.Combine(job, "worker"), Path.Combine(job, "package") });
                // Retain verified old generations, rather than counting a damaged backup as usable.
                if (retained < retainedBackups)
                {
                    Verify(Path.Combine(job, "backup"), plan.OldFiles); retained++;
                    DeleteOwnedDirectory(job, "package"); DeleteOwnedDirectory(job, "worker");
                }
                // Release only after plan/payload removal; Windows cannot remove the parent of
                // a delete-pending lock until its handle closes. No runnable plan remains then.
                else DeleteCleanupTree(tree, lease.Dispose);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException) { }
        }
        // Prepared jobs own their package/worker copies; none references this download cache.
        // A permanently failed job therefore must not disable collection of future downloads.
        string downloads = Path.Combine(profile, "updates", "downloads");
        if (!Directory.Exists(downloads)) return;
        NoReparse(downloads);
        bool keptDownload = false;
        foreach (string download in Directory.GetDirectories(downloads).OrderByDescending(Directory.GetLastWriteTimeUtc))
        {
            if (!Path.GetFileName(download).StartsWith("download-", StringComparison.Ordinal) ||
                !Guid.TryParseExact(Path.GetFileName(download)[9..], "N", out _)) continue;
            try
            {
                var tree = InspectCleanupTree(download);
                // A completed download has one extracted package and no ZIP/in-progress files.
                string[] packages = Directory.GetDirectories(download);
                if (packages.Length != 1 || Directory.GetFiles(download).Length != 0) continue;
                var manifest = ReadManifest(packages[0]);
                if (Path.GetFileName(packages[0]) != "FloatCanvas-" + manifest.Version + "-win-x64") continue;
                var owned = Set(manifest.Files.Select(path => Path.GetFullPath(Path.Combine(packages[0], path.Replace('/', Path.DirectorySeparatorChar)))));
                if (tree.Files.Any(path => !owned.Contains(path))) continue;
                ValidateCleanupDirectories(download, tree.Directories, owned, new[] { packages[0] });
                if (!keptDownload) { keptDownload = true; continue; }
                DeleteCleanupTree(tree);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException) { }
        }
    }

    private static void ValidateCleanupJob(string job, IReadOnlyList<string> files, InstallPlan plan)
    {
        var metadata = Set(new[] { "plan.json", "journal.json", "worker.lock", "ready.json", "started.json", "launching.json", "worker.log" });
        var old = Set(plan.OldFiles.Select(file => file.Path)); var replacement = Set(plan.NewFiles.Select(file => file.Path));
        foreach (string path in files)
        {
            string relative = Path.GetRelativePath(job, path).Replace('\\', '/');
            int slash = relative.IndexOf('/');
            if (slash < 0 && metadata.Contains(relative)) continue;
            if (slash > 0 && (relative[..slash] switch { "backup" or "worker" => old.Contains(relative[(slash + 1)..]), "package" => replacement.Contains(relative[(slash + 1)..]), _ => false })) continue;
            var launcher = Regex.Match(relative, "^worker/recovery-launcher-([1-9][0-9]{0,9})\\.exe$", RegexOptions.CultureInvariant);
            if (launcher.Success && uint.TryParse(launcher.Groups[1].Value, out _))
            {
                string digest = Hash(path);
                if (plan.OldFiles.Concat(plan.NewFiles).Any(file => file.Path.Equals("FloatCanvas.exe", StringComparison.OrdinalIgnoreCase) && file.Sha256.Equals(digest, StringComparison.OrdinalIgnoreCase)))
                {
                    // A mapped launcher cannot be opened for writing. Probe before any job
                    // deletion so a live private launcher keeps every sibling intact.
                    using var probe = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
                    continue;
                }
            }
            throw new IOException("An unowned file is present in the update job; preserve it for inspection.");
        }
    }

    private static (List<string> Files, List<string> Directories) InspectCleanupTree(string directory)
    {
        NoReparse(directory);
        var files = new List<string>(); var directories = new List<string> { directory };
        for (int index = 0; index < directories.Count; index++)
        {
            NoReparse(directories[index]);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directories[index]))
            {
                NoReparse(entry);
                if (Directory.Exists(entry)) directories.Add(entry); else files.Add(entry);
                if (files.Count + directories.Count > 32768) throw new IOException("The update cache tree exceeds its ownership bounds.");
            }
        }
        return (files, directories);
    }

    private static void ValidateCleanupDirectories(string root, IEnumerable<string> directories, IEnumerable<string> files, IEnumerable<string> containers)
    {
        var allowed = Set(containers.Append(root));
        foreach (string file in files)
            for (string? directory = Path.GetDirectoryName(file); directory is not null && Below(directory, root); directory = Path.GetDirectoryName(directory)) allowed.Add(directory);
        if (directories.Any(path => !allowed.Contains(path))) throw new IOException("An unowned directory is present in the update cache.");
    }

    private static void DeleteCleanupTree((List<string> Files, List<string> Directories) tree, Action? beforeRootDelete = null)
    {
        foreach (string file in tree.Files) { NoReparse(file); File.Delete(file); }
        for (int i = tree.Directories.Count - 1; i >= 0; i--)
        {
            if (i == 0) beforeRootDelete?.Invoke();
            NoReparse(tree.Directories[i]); Directory.Delete(tree.Directories[i]);
        }
    }

    private static void DeleteOwnedDirectory(string parent, string name)
    {
        string directory = Child(parent, name);
        if (!Directory.Exists(directory)) return;
        // Preflight the whole tree before the first deletion, then recheck each path at deletion.
        DeleteCleanupTree(InspectCleanupTree(directory));
    }

    /// <summary>Called only after the normal application session has started successfully.</summary>
    internal static void AcknowledgeStartup()
    {
        string? path = Environment.GetEnvironmentVariable(StartupPlanEnvironment);
        string? launcherId = Environment.GetEnvironmentVariable(UpdateStartupProcess.LauncherIdEnvironment);
        string? launcherCreated = Environment.GetEnvironmentVariable(UpdateStartupProcess.LauncherCreationEnvironment);
        Environment.SetEnvironmentVariable(StartupPlanEnvironment, null);
        Environment.SetEnvironmentVariable(UpdateStartupProcess.LauncherIdEnvironment, null);
        Environment.SetEnvironmentVariable(UpdateStartupProcess.LauncherCreationEnvironment, null);
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var (plan, job) = ReadPlan(path);
            if (!Same(AppContext.BaseDirectory, plan.InstallDirectory)) return;
            using var current = Process.GetCurrentProcess();
            using var launcher = UpdateStartupProcess.TryGetLauncher(plan.InstallDirectory, launcherId, launcherCreated);
            WriteJson(Path.Combine(job, "started.json"), Identity(launcher ?? current));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException or System.ComponentModel.Win32Exception) { }
    }

    private static void Restart(InstallPlan plan, string job, InstallerHooks? hooks, Action? acknowledge = null)
    {
        string executable = Child(plan.InstallDirectory, "FloatCanvas.exe");
        if (hooks?.Restart is { } restart) { restart(executable, plan.RestartArguments); acknowledge?.Invoke(); return; }
        // The helper joins a kill-on-close job before creating its child. If it is
        // killed between CreateProcess and publishing the child's identity, the OS
        // terminates the unrecorded child too, so recovery never meets an orphan lock.
        using var launchLease = new UpdateLaunchLease();
        // Bypass the recovery launcher while this live worker owns the transaction.
        string host = Child(plan.InstallDirectory, "FloatCanvas.Host.exe");
        if (File.Exists(host)) executable = host;
        var start = new ProcessStartInfo(executable) { WorkingDirectory = plan.InstallDirectory, UseShellExecute = false };
        string started = Path.Combine(job, "started.json"); NoReparse(started); if (File.Exists(started)) File.Delete(started);
        start.Environment[StartupPlanEnvironment] = Path.Combine(job, "plan.json");
        foreach (string argument in plan.RestartArguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("The updated application could not start.");
        try
        {
            WriteJson(Path.Combine(job, "launching.json"), Identity(process));
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(20))
            {
                if (IdentityMatches(started, process)) { acknowledge?.Invoke(); launchLease.Release(); return; }
                if (process.WaitForExit(60)) throw new IOException("The application exited before confirming startup (" + process.ExitCode + ").");
            }
            throw new IOException("The application did not confirm a successful startup.");
        }
        catch
        {
            // Every failure after CreateProcess (including publishing the identity or
            // committing the journal) must release this precise child's file locks.
            if (!process.HasExited) { process.Kill(); if (!process.WaitForExit(5000)) throw new IOException("The unconfirmed application could not stop before recovery."); }
            throw;
        }
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
            if (!File.Exists(file) || new FileInfo(file).Length == 0) throw new IncompletePackageFileException(relative);
        }
        return manifest;
    }

    internal static string RefreshInstallerFileList(string directory)
    {
        var manifest = ReadManifest(directory);
        if (manifest.Files.Any(path => path.Equals(InstallerRegistration.FilesName, StringComparison.OrdinalIgnoreCase) ||
            path.Equals(RecoveryPointerName, StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(path, @"^unins\d+\.(exe|dat|msg)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            throw new InvalidOperationException("The package must not own installer or recovery metadata.");
        AtomicWrite(Child(directory, InstallerRegistration.FilesName), Encoding.UTF8.GetBytes(string.Join("\r\n", manifest.Files) + "\r\n"));
        return manifest.Version;
    }

    private static void RefreshInstallerRegistration(InstallPlan plan, string job) =>
        InstallerRegistration.Refresh(plan.InstallDirectory, error => Log(job, "Could not refresh the Windows installation registration: " + error));

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

    private static OwnedFile[] Snapshot(string root, IEnumerable<string> files, CancellationToken cancellationToken = default) => files.Select(path => new OwnedFile(path, Hash(Child(root, path), cancellationToken))).ToArray();
    private static void ValidateFiles(OwnedFile[] files)
    {
        if (files is null || files.Length is < 1 or > 4096 || Set(files.Select(f => f.Path)).Count != files.Length || Required.Any(r => !files.Any(f => string.Equals(f.Path, r, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("Invalid update file list.");
        foreach (var file in files) { Relative(file.Path); if (!Regex.IsMatch(file.Sha256 ?? "", "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)) throw new InvalidOperationException("Invalid update file hash."); }
    }
    private static void Verify(string directory, IEnumerable<OwnedFile> files, CancellationToken cancellationToken = default)
    {
        Root(directory);
        foreach (var file in files)
            if (!string.Equals(Hash(Child(directory, file.Path), cancellationToken), file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Update file verification failed: " + file.Path);
    }
    private static void CopySnapshot(string source, string target, IEnumerable<OwnedFile> files, CancellationToken cancellationToken = default)
    {
        foreach (var file in files) AtomicCopy(Child(source, file.Path), Child(target, file.Path), cancellationToken);
    }
    private static void AtomicCopy(string source, string target, CancellationToken cancellationToken = default)
    {
        NoReparse(source); NoReparse(target); Directory.CreateDirectory(Path.GetDirectoryName(target)!); NoReparse(target);
        string temp = target + ".update-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[81920]; int count;
                while ((count = input.Read(buffer)) > 0) { cancellationToken.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
                cancellationToken.ThrowIfCancellationRequested(); output.Flush(true);
            }
            NoReparse(target);
            for (int retry = 0; ; retry++)
            {
                try { File.Move(temp, target, true); break; }
                catch (Exception error) when (retry < 50 && Path.GetFileName(target).Equals("FloatCanvas.exe", StringComparison.OrdinalIgnoreCase) && (error is IOException or UnauthorizedAccessException))
                { cancellationToken.ThrowIfCancellationRequested(); Thread.Sleep(40); }
            }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static string Hash(string path, CancellationToken cancellationToken = default)
    {
        NoReparse(path); using var stream = File.OpenRead(path); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920]; int count;
        while ((count = stream.Read(buffer)) > 0) { cancellationToken.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
        cancellationToken.ThrowIfCancellationRequested(); return Convert.ToHexString(hash.GetHashAndReset());
    }
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
        => AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(value, Json));
    private static void AtomicWrite(string path, byte[] data)
    {
        NoReparse(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); NoReparse(path);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(data); output.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void SaveResult(InstallPlan plan, UpdateInstallResult result) => WriteJson(Path.Combine(plan.ProfileDirectory, "updates", "result.json"), result);
    private static void Log(string job, string message) { try { string path = Path.Combine(job, "worker.log"); NoReparse(path); File.AppendAllText(path, DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

    internal sealed record OwnedFile(string Path, string Sha256);
    private sealed record ProcessIdentity(int ProcessId, long StartUtcTicks);
    private sealed record InstallJournal(string Phase, string[] Touched);
    internal sealed record InstallPlan(int Format, string InstallDirectory, string ProfileDirectory, int ParentProcessId, long ParentStartUtcTicks, string Version, string[] RestartArguments, OwnedFile[] OldFiles, OwnedFile[] NewFiles);
    private sealed record PackageManifest([property: JsonRequired] string Product, [property: JsonRequired] string Version, [property: JsonRequired] string RuntimeIdentifier, [property: JsonRequired] bool SelfContained, [property: JsonRequired] string Configuration, [property: JsonRequired] string[] Files);
    internal sealed class InstallerHooks
    {
        internal Func<InstallPlan, bool>? WaitForParent { get; init; }
        internal Action? BeforeBackup { get; init; }
        internal Action<string>? BeforeReplace { get; init; }
        internal Action<string>? AfterReplace { get; init; }
        internal Action<string>? AfterDelete { get; init; }
        internal Action<string>? AfterCheckpoint { get; init; }
        internal Action<string, string[]>? Restart { get; init; }
    }
}
