using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using FloatCanvas.App;

internal static class UpdateCacheTests
{
    private static readonly string[] Required = { "FloatCanvas.exe", "FloatCanvas.dll", "FloatCanvas.Core.dll", "FloatCanvas.Localization.dll", "FloatCanvas.Rendering.dll", "FloatCanvas.Storage.dll", "FloatCanvas.Windows.dll", "FloatCanvas.deps.json", "FloatCanvas.runtimeconfig.json", "SkiaSharp.dll", "SkiaSharp.HarfBuzz.dll", "HarfBuzzSharp.dll", "libSkiaSharp.dll", "libHarfBuzzSharp.dll", "PACKAGE-INFO.json", "Docs/guide.md" };

    internal static void Run(Action<string, bool> check)
    {
        BoundedSuccessfulUpdates(check); ProtectedJobs(check); FailedJobDoesNotDisableCollection(check); RecoveryLauncherCopies(check); DamagedBackup(check); UnknownContent(check); JunctionPreflight(check); DownloadOwnership(check);
    }

    private static void BoundedSuccessfulUpdates(Action<string, bool> check)
    {
        using var f = new Fixture(); var jobs = new List<string>(); var downloads = new List<string>(); long plateau = 0;
        for (int generation = 1; generation <= 10; generation++)
        {
            jobs.Add(f.Job(generation)); downloads.Add(f.Download(generation));
            AppUpdateInstaller.CleanupCompletedJobs(f.Profile);
            int count = Math.Min(generation, 2);
            check("update cache bounds backups and completed metadata after success " + generation,
                Directory.GetDirectories(f.Jobs).Length == count && jobs.Take(Math.Max(0, generation - 2)).All(path => !Directory.Exists(path)) &&
                jobs.TakeLast(count).All(path => File.Exists(Path.Combine(path, "backup", "FloatCanvas.dll")) && File.Exists(Path.Combine(path, "plan.json")) &&
                    !Directory.Exists(Path.Combine(path, "worker")) && !Directory.Exists(Path.Combine(path, "package"))));
            check("update cache retains the newest completed download after success " + generation,
                Directory.GetDirectories(f.Downloads).SequenceEqual(new[] { downloads[^1] }));
            long bytes = Directory.GetFiles(f.Profile, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
            if (generation == 2) plateau = bytes;
            if (generation > 2) check("update cache bytes stay bounded after success " + generation, bytes <= plateau + 4096);
        }
        var before = f.Snapshot(f.Profile); AppUpdateInstaller.CleanupCompletedJobs(f.Profile);
        check("update cache collection is idempotent", f.Matches(f.Profile, before));
    }

    private static void ProtectedJobs(Action<string, bool> check)
    {
        using var f = new Fixture();
        string prepared = f.Job(1, null), replacing = f.Job(2, "Replacing"), failed = f.Job(3, "RolledBack");
        string live = f.Job(4), recovering = f.Job(5), foreign = f.Job(6);
        string otherProfile = Path.Combine(f.Root, "other-profile"); Directory.CreateDirectory(otherProfile);
        string foreignPlan = Path.Combine(foreign, "plan.json");
        var foreignValue = JsonSerializer.Deserialize<AppUpdateInstaller.InstallPlan>(File.ReadAllText(foreignPlan))!;
        File.WriteAllText(foreignPlan, JsonSerializer.Serialize(foreignValue with { ProfileDirectory = otherProfile }));
        File.WriteAllText(Path.Combine(f.Install, AppUpdateInstaller.RecoveryPointerName), Path.Combine(recovering, "plan.json"));
        f.Download(1); f.Download(2);
        var before = f.Snapshot(f.Jobs);
        using (var lease = new FileStream(Path.Combine(live, "worker.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            AppUpdateInstaller.CleanupCompletedJobs(f.Profile);
        check("update cache leaves failed pending live foreign and recoverable committed jobs byte-identical", f.Matches(f.Jobs, before));
        check("committed recovery pointer retains worker and replacement package", File.Exists(Path.Combine(recovering, "worker", "FloatCanvas.exe")) && File.Exists(Path.Combine(recovering, "package", "FloatCanvas.exe")));
        check("protected jobs do not retain unrelated old complete downloads", Directory.GetDirectories(f.Downloads).Length == 1);
    }

    private static void FailedJobDoesNotDisableCollection(Action<string, bool> check)
    {
        using var f = new Fixture(); string failed = f.Job(1, "RolledBack"); var failedBefore = f.Snapshot(failed);
        long plateau = 0;
        for (int generation = 2; generation <= 11; generation++)
        {
            f.Job(generation); f.Download(generation); AppUpdateInstaller.CleanupCompletedJobs(f.Profile);
            long bytes = Directory.GetFiles(f.Profile, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
            if (generation == 3) plateau = bytes;
            check("failed job preserves evidence while success/download cache stays bounded at generation " + generation,
                f.Matches(failed, failedBefore) && Directory.GetDirectories(f.Jobs).Length == 1 + Math.Min(2, generation - 1) &&
                Directory.GetDirectories(f.Downloads).Length == 1 && (generation <= 3 || bytes <= plateau + 4096));
        }
    }

    private static void RecoveryLauncherCopies(Action<string, bool> check)
    {
        using var f = new Fixture(); string stale = f.Job(1), tampered = f.Job(2), live = f.Job(3); f.Job(4); f.Job(5);
        string staleCopy = Path.Combine(stale, "worker", "recovery-launcher-123.exe");
        File.Copy(Path.Combine(stale, "package", "FloatCanvas.exe"), staleCopy);
        File.WriteAllText(Path.Combine(tampered, "worker", "recovery-launcher-456.exe"), "unrelated executable");
        string liveCopy = Path.Combine(live, "worker", "recovery-launcher-789.exe"); File.Copy(Path.Combine(live, "worker", "FloatCanvas.exe"), liveCopy);
        var tamperedBefore = f.Snapshot(tampered); var liveBefore = f.Snapshot(live);
        using (var mapped = new FileStream(liveCopy, FileMode.Open, FileAccess.Read, FileShare.Read)) AppUpdateInstaller.CleanupCompletedJobs(f.Profile);
        check("verified stale recovery launcher copies do not block obsolete job collection", !Directory.Exists(stale));
        check("unknown same-name and occupied recovery launchers preserve the entire job", f.Matches(tampered, tamperedBefore) && f.Matches(live, liveBefore));
        AppUpdateInstaller.CleanupCompletedJobs(f.Profile);
        check("released verified recovery launcher is collectible on a later pass", !Directory.Exists(live) && f.Matches(tampered, tamperedBefore));
    }

    private static void DamagedBackup(Action<string, bool> check)
    {
        using var f = new Fixture(); string oldest = f.Job(1), middle = f.Job(2), newest = f.Job(3);
        File.AppendAllText(Path.Combine(newest, "backup", "FloatCanvas.dll"), "damage");
        var damaged = f.Snapshot(newest); AppUpdateInstaller.CleanupCompletedJobs(f.Profile);
        check("damaged latest backup does not consume either verified retention slot", Directory.Exists(Path.Combine(oldest, "backup")) && Directory.Exists(Path.Combine(middle, "backup")) && f.Matches(newest, damaged));
    }

    private static void UnknownContent(Action<string, bool> check)
    {
        using var f = new Fixture(); string unknownFile = f.Job(1), unknownDirectory = f.Job(2); f.Job(3); f.Job(4);
        File.WriteAllText(Path.Combine(unknownFile, "package", "my-note.txt"), "user data");
        Directory.CreateDirectory(Path.Combine(unknownDirectory, "do-not-delete"));
        var fileBefore = f.Snapshot(unknownFile); var directoryBefore = f.Snapshot(unknownDirectory);
        AppUpdateInstaller.CleanupCompletedJobs(f.Profile);
        check("update cache preserves unowned files and all sibling recovery data", f.Matches(unknownFile, fileBefore));
        check("update cache preserves unowned empty directories", f.Matches(unknownDirectory, directoryBefore) && Directory.Exists(Path.Combine(unknownDirectory, "do-not-delete")));
    }

    private static void JunctionPreflight(Action<string, bool> check)
    {
        using var f = new Fixture(); string old = f.Job(1); f.Job(2); f.Job(3);
        string outside = Path.Combine(f.Root, "outside"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "outside sentinel");
        string junction = Path.Combine(old, "worker", "zz-junction");
        f.CreateJunction(junction, outside);
        var before = f.Snapshot(old, junction); var outsideBefore = f.Snapshot(outside);
        AppUpdateInstaller.CleanupCompletedJobs(f.Profile);
        check("update cleanup preflights nested junctions before deleting any sibling or backup", f.Matches(old, before, junction));
        check("update cleanup never follows a junction outside its job", f.Matches(outside, outsideBefore) && Directory.Exists(junction));
    }

    private static void DownloadOwnership(Action<string, bool> check)
    {
        using var f = new Fixture(); f.Job(1); string oldest = f.Download(1), latest = f.Download(2), unknown = f.Download(3);
        File.WriteAllText(Path.Combine(unknown, "my-note.txt"), "keep me");
        string active = Path.Combine(f.Downloads, "download-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(active);
        File.WriteAllText(Path.Combine(active, "package.zip"), "still downloading");
        Directory.SetLastWriteTimeUtc(active, DateTime.UtcNow.AddDays(1));
        var unknownBefore = f.Snapshot(unknown); var activeBefore = f.Snapshot(active);
        AppUpdateInstaller.CleanupCompletedJobs(f.Profile);
        check("unowned and incomplete downloads do not displace the latest complete download", !Directory.Exists(oldest) && Directory.Exists(latest) && f.Matches(unknown, unknownBefore) && f.Matches(active, activeBefore));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "FloatCanvas-update-cache-" + Guid.NewGuid().ToString("N"));
        internal string Profile => Path.Combine(Root, "profile");
        internal string Install => Path.Combine(Root, "installed");
        internal string Jobs => Path.Combine(Profile, "updates", "jobs");
        internal string Downloads => Path.Combine(Profile, "updates", "downloads");
        private readonly List<string> junctions = new();

        internal Fixture() { Directory.CreateDirectory(Install); Directory.CreateDirectory(Jobs); Directory.CreateDirectory(Downloads); }

        internal string Job(int sequence, string? phase = "Committed")
        {
            string job = Path.Combine(Jobs, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(job);
            string version = "1.0." + sequence, previous = "1.0." + (sequence - 1);
            Package(Path.Combine(job, "backup"), previous); Package(Path.Combine(job, "worker"), previous); Package(Path.Combine(job, "package"), version);
            var oldFiles = SnapshotOwned(Path.Combine(job, "backup")); var newFiles = SnapshotOwned(Path.Combine(job, "package"));
            var plan = new AppUpdateInstaller.InstallPlan(1, Install, Profile, int.MaxValue, 1, version, new[] { "--profile", Profile, "--recover" }, oldFiles, newFiles);
            File.WriteAllText(Path.Combine(job, "plan.json"), JsonSerializer.Serialize(plan));
            File.WriteAllText(Path.Combine(job, "worker.lock"), ""); File.WriteAllText(Path.Combine(job, "worker.log"), "completed fixture");
            if (phase is not null)
            {
                string journal = Path.Combine(job, "journal.json");
                File.WriteAllText(journal, JsonSerializer.Serialize(new { Phase = phase, Touched = Required }));
                File.SetLastWriteTimeUtc(journal, DateTime.UtcNow.AddMinutes(sequence));
            }
            return job;
        }

        internal string Download(int sequence)
        {
            string path = Path.Combine(Downloads, "download-" + Guid.NewGuid().ToString("N"));
            Package(Path.Combine(path, "FloatCanvas-1.0." + sequence + "-win-x64"), "1.0." + sequence);
            Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(sequence)); return path;
        }

        private static void Package(string directory, string version)
        {
            foreach (string relative in Required)
            {
                string path = Path.Combine(directory, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, version + ":" + new string('x', 4096));
            }
            File.WriteAllText(Path.Combine(directory, "PACKAGE-INFO.json"), JsonSerializer.Serialize(new { Product = "浮绘 FloatCanvas", Version = version, RuntimeIdentifier = "win-x64", SelfContained = false, Configuration = "Release", Files = Required }));
        }

        private static AppUpdateInstaller.OwnedFile[] SnapshotOwned(string directory) => Required.Select(relative => new AppUpdateInstaller.OwnedFile(relative, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, relative)))))).ToArray();

        internal Dictionary<string, string> Snapshot(string directory, string? excluded = null)
        {
            var result = new Dictionary<string, string>(); var pending = new Stack<string>(); pending.Push(directory);
            while (pending.TryPop(out string? parent))
                foreach (string path in Directory.GetFileSystemEntries(parent))
                {
                    if (path == excluded) continue;
                    if (Directory.Exists(path)) { result[Path.GetRelativePath(directory, path) + "/"] = "directory"; pending.Push(path); }
                    else result[Path.GetRelativePath(directory, path)] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
                }
            return result;
        }
        internal bool Matches(string directory, Dictionary<string, string> before, string? excluded = null) => Directory.Exists(directory) && Snapshot(directory, excluded) is var after && before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out string? value) && value == pair.Value);

        internal void CreateJunction(string path, string target)
        {
            if (!Path.GetFullPath(path).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Path.GetFullPath(target).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture junction paths must stay within its isolated directory.");
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.Environment["FLOATCANVAS_TEST_JUNCTION"] = path; start.Environment["FLOATCANVAS_TEST_TARGET"] = target;
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("New-Item -ItemType Junction -Path $env:FLOATCANVAS_TEST_JUNCTION -Target $env:FLOATCANVAS_TEST_TARGET -ErrorAction Stop | Out-Null");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start fixture junction creation.");
            if (!process.WaitForExit(10000)) { process.Kill(); throw new TimeoutException("Fixture junction creation timed out."); }
            if (process.ExitCode != 0 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) throw new IOException("Fixture junction was not created.");
            junctions.Add(path);
        }

        public void Dispose()
        {
            foreach (string junction in junctions) if (Directory.Exists(junction)) Directory.Delete(junction);
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
