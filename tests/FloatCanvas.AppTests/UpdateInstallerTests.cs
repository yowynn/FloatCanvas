using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FloatCanvas.App;

internal static class UpdateInstallerTests
{
    private static readonly string[] Required = { "FloatCanvas.exe", "FloatCanvas.dll", "FloatCanvas.Core.dll", "FloatCanvas.Localization.dll", "FloatCanvas.Rendering.dll", "FloatCanvas.Storage.dll", "FloatCanvas.Windows.dll", "FloatCanvas.deps.json", "FloatCanvas.runtimeconfig.json", "SkiaSharp.dll", "SkiaSharp.HarfBuzz.dll", "HarfBuzzSharp.dll", "libSkiaSharp.dll", "libHarfBuzzSharp.dll", "PACKAGE-INFO.json" };

    internal static void Run(Action<string, bool> check)
    {
        BasicTransaction(check); Rollback(check, false); Rollback(check, true); PreflightAndReadOnly(check); WaitingAndTampering(check); Validation(check); Cancellation(check);
    }

    private static void Cancellation(Action<string, bool> check)
    {
        using (var f = new Fixture())
        {
            string jobs = Path.Combine(f.Profile, "updates", "jobs"); Directory.CreateDirectory(jobs);
            using (var large = new FileStream(Path.Combine(f.Source, "FloatCanvas.dll"), FileMode.Create)) large.SetLength(64 * 1024 * 1024);
            using var cancelled = new CancellationTokenSource();
            using var watcher = new FileSystemWatcher(jobs) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName, EnableRaisingEvents = true };
            watcher.Created += (_, args) => { if (args.FullPath.Contains(Path.DirectorySeparatorChar + "package" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) cancelled.Cancel(); };
            bool observed = false;
            try { AppUpdateInstaller.Prepare(f.Update, f.Install, f.Profile, f.Arguments, cancelled.Token); }
            catch (OperationCanceledException) { observed = true; }
            check("updater observes cancellation during real package copying", observed);
            check("updater cancelled preparation removes its unstarted job", Directory.GetDirectories(jobs).Length == 0);
            check("updater cancelled preparation never changes installed user or owned files", f.Original.All(pair => File.ReadAllText(Path.Combine(f.Install, pair.Key)) == pair.Value) && File.ReadAllText(Path.Combine(f.Install, "user.notes")) == "user data");
        }
        using (var f = new Fixture())
        {
            string plan = f.Plan(); AppUpdateInstaller.DiscardPreparation(plan);
            check("updater can discard a prepared but unlaunched job", !Directory.Exists(Path.GetDirectoryName(plan)));
            plan = f.Plan(); AppUpdateInstaller.Run(plan, new() { Restart = (_, _) => { } });
            check("updater never discards a transaction that has started", Throws(() => AppUpdateInstaller.DiscardPreparation(plan)) && File.Exists(plan));
        }
    }

    private static void PreflightAndReadOnly(Action<string, bool> check)
    {
        using (var f = new Fixture())
        {
            string plan = f.Plan(); int restarts = 0;
            int code = AppUpdateInstaller.Run(plan, new() { BeforeBackup = () => throw new IOException("Simulated backup failure"), Restart = (_, _) => restarts++ });
            check("updater restarts intact old application after preflight failure", code == 1 && restarts == 1);
            check("updater preflight failure leaves all owned files unchanged", f.Original.All(pair => File.ReadAllText(Path.Combine(f.Install, pair.Key)) == pair.Value));
            check("updater preflight failure does not claim rollback", AppUpdateInstaller.ConsumeResult(f.Profile) is { Success: false, RolledBack: false });
        }
        using (var f = new Fixture())
        {
            string plan = f.Plan(); string readOnly = Path.Combine(f.Install, "FloatCanvas.dll");
            File.SetAttributes(readOnly, File.GetAttributes(readOnly) | FileAttributes.ReadOnly);
            try
            {
                int restarts = 0;
                int code = AppUpdateInstaller.Run(plan, new() { Restart = (_, _) => restarts++ });
                check("updater handles an actual read-only destination transactionally", code == 1 && restarts == 1 && AppUpdateInstaller.ConsumeResult(f.Profile) is { Success: false, RolledBack: true });
                check("updater rollback skips untouched read-only originals", f.Original.All(pair => File.ReadAllText(Path.Combine(f.Install, pair.Key)) == pair.Value));
            }
            finally { File.SetAttributes(readOnly, FileAttributes.Normal); }
        }
    }

    private static void BasicTransaction(Action<string, bool> check)
    {
        using var f = new Fixture(); string plan = f.Plan();
        check("updater accepts a writable Release portable package", AppUpdateInstaller.CanUpdateDirectory(f.Install));
        check("updater worker excludes untracked user files", !File.Exists(Path.Combine(Path.GetDirectoryName(plan)!, "worker", "user.notes")));
        check("updater snapshot includes original manifest", File.Exists(Path.Combine(Path.GetDirectoryName(plan)!, "worker", "PACKAGE-INFO.json")));
        int restarts = 0;
        int code = AppUpdateInstaller.Run(plan, new() { Restart = (exe, args) => { restarts++; check("updater restarts the installed executable", exe == Path.Combine(f.Install, "FloatCanvas.exe")); check("updater keeps profile, recovery and screen scope", args.SequenceEqual(f.Arguments)); } });
        check("updater commits and starts once", code == 0 && restarts == 1);
        check("updater replaces application content", File.ReadAllText(Path.Combine(f.Install, "FloatCanvas.dll")) == "new FloatCanvas.dll");
        check("updater installs new nested files", File.ReadAllText(Path.Combine(f.Install, "Docs", "new.md")) == "new Docs/new.md");
        check("updater removes obsolete package files", !File.Exists(Path.Combine(f.Install, "obsolete.txt")));
        check("updater preserves untracked user content", File.ReadAllText(Path.Combine(f.Install, "user.notes")) == "user data");
        var result = AppUpdateInstaller.ConsumeResult(f.Profile);
        check("updater publishes a successful restart result", result is { Success: true, RolledBack: false, Version: "0.2.1", Error: null });
        check("updater result is consumed once", AppUpdateInstaller.ConsumeResult(f.Profile) is null);
    }

    private static void Rollback(Action<string, bool> check, bool startup)
    {
        using var f = new Fixture(); string plan = f.Plan(); int replaced = 0, restarts = 0;
        var hooks = new AppUpdateInstaller.InstallerHooks
        {
            BeforeReplace = _ => { if (!startup && ++replaced == 2) throw new IOException("Simulated replacement failure"); },
            Restart = (_, _) => { if (startup && ++restarts == 1) throw new IOException("Simulated startup failure"); if (!startup) restarts++; }
        };
        int code = AppUpdateInstaller.Run(plan, hooks); var result = AppUpdateInstaller.ConsumeResult(f.Profile);
        string scenario = startup ? "startup failure" : "replacement failure";
        check("updater reports rollback after " + scenario, code == 1 && result is { Success: false, RolledBack: true } && result.Error!.Contains("Simulated"));
        check("updater restores all original owned files after " + scenario, f.Original.All(pair => File.ReadAllText(Path.Combine(f.Install, pair.Key)) == pair.Value));
        check("updater removes new files after " + scenario, !File.Exists(Path.Combine(f.Install, "Docs", "new.md")));
        check("updater preserves user files after " + scenario, File.ReadAllText(Path.Combine(f.Install, "user.notes")) == "user data");
        check("updater restarts original application after " + scenario, restarts == (startup ? 2 : 1));
    }

    private static void WaitingAndTampering(Action<string, bool> check)
    {
        using (var f = new Fixture())
        {
            string plan = f.Plan(); int calls = 0;
            int code = AppUpdateInstaller.Run(plan, new() { WaitForParent = _ => false, Restart = (_, _) => calls++ });
            check("updater timeout leaves installation unchanged", code == 1 && calls == 0 && f.Original.All(pair => File.ReadAllText(Path.Combine(f.Install, pair.Key)) == pair.Value));
            check("updater timeout reports no rollback needed", AppUpdateInstaller.ConsumeResult(f.Profile) is { Success: false, RolledBack: false });
        }
        using (var f = new Fixture())
        {
            string plan = f.Plan(); File.AppendAllText(Path.Combine(Path.GetDirectoryName(plan)!, "package", "FloatCanvas.dll"), "tampered");
            int code = AppUpdateInstaller.Run(plan, new() { Restart = (_, _) => throw new Exception("Must not restart") });
            check("updater rechecks downloaded hashes before replacing", code == 1 && File.ReadAllText(Path.Combine(f.Install, "FloatCanvas.dll")) == "old FloatCanvas.dll");
            check("updater reports tampering without rollback", AppUpdateInstaller.ConsumeResult(f.Profile) is { Success: false, RolledBack: false });
        }
        using (var f = new Fixture())
        {
            string plan = f.Plan(); File.WriteAllText(Path.Combine(f.Install, "Docs", "new.md"), "new user file");
            int code = AppUpdateInstaller.Run(plan, new() { Restart = (_, _) => throw new Exception("Must not restart") });
            check("updater rechecks untracked collisions after parent exit", code == 1 && File.ReadAllText(Path.Combine(f.Install, "Docs", "new.md")) == "new user file");
        }
        using (var f = new Fixture())
        {
            string plan = f.Plan(); File.AppendAllText(Path.Combine(f.Install, "FloatCanvas.dll"), " changed");
            int code = AppUpdateInstaller.Run(plan, new() { Restart = (_, _) => throw new Exception("Must not restart") });
            check("updater refuses changing an installation modified since preparation", code == 1 && File.ReadAllText(Path.Combine(f.Install, "FloatCanvas.dll")).EndsWith(" changed"));
        }
    }

    private static void Validation(Action<string, bool> check)
    {
        using (var f = new Fixture())
        {
            check("updater rejects a development directory", !AppUpdateInstaller.CanUpdateDirectory(AppContext.BaseDirectory));
            check("updater rejects an empty directory", !AppUpdateInstaller.CanUpdateDirectory(f.Profile));
            File.WriteAllText(Path.Combine(f.Install, "Docs", "new.md"), "user-owned collision");
            check("updater refuses overwriting untracked user data", Throws(() => f.Plan()));
            check("updater collision leaves original package untouched", f.Original.All(pair => File.ReadAllText(Path.Combine(f.Install, pair.Key)) == pair.Value));
        }
        foreach (string bad in new[] { "../escape", "C:/escape", "dir\\escape", "x:stream", "CON.txt", "ends. ", "double//slash", "debug.pdb", "FloatCanvas.DLL" })
        {
            using var f = new Fixture(); var files = Required.Concat(new[] { bad }).ToArray(); f.Manifest(f.Source, "0.2.1", files);
            check("updater rejects unsafe or duplicate owned path " + bad, Throws(() => f.Plan()));
        }
        foreach (string configuration in new[] { "Debug", "release", "" })
        {
            using var f = new Fixture(); f.Manifest(f.Install, "0.2.0", Required.Concat(new[] { "obsolete.txt" }).ToArray(), configuration);
            check("updater requires exact Release manifest " + configuration, !AppUpdateInstaller.CanUpdateDirectory(f.Install));
        }
        using (var f = new Fixture())
        {
            check("updater refuses launch arguments other than declared recovery options", Throws(() => AppUpdateInstaller.Prepare(f.Update, f.Install, f.Profile, new[] { "--profile", f.Profile, "--recover", "--smoke" })));
            check("updater requires explicit recovery", Throws(() => AppUpdateInstaller.Prepare(f.Update, f.Install, f.Profile, new[] { "--profile", f.Profile })));
            check("updater refuses a different restart profile", Throws(() => AppUpdateInstaller.Prepare(f.Update, f.Install, f.Profile, new[] { "--profile", f.Source, "--recover" })));
            check("updater refuses contradictory screen scopes", Throws(() => AppUpdateInstaller.Prepare(f.Update, f.Install, f.Profile, new[] { "--profile", f.Profile, "--recover", "--primary-screen", "--all-screens" })));
            check("updater refuses install and profile overlap", Throws(() => AppUpdateInstaller.Prepare(f.Update, f.Install, f.Install, f.Arguments)));
        }
        using (var f = new Fixture())
        {
            string plan = f.Plan(); var value = JsonSerializer.Deserialize<AppUpdateInstaller.InstallPlan>(File.ReadAllText(plan))!;
            File.WriteAllText(plan, JsonSerializer.Serialize(value with { InstallDirectory = f.Profile }));
            check("updater rejects a plan targeting its own profile", AppUpdateInstaller.Run(plan, new() { Restart = (_, _) => throw new Exception("Must not restart") }) == 2);
            check("updater invalid plan cannot write result to redirected profile", !File.Exists(Path.Combine(f.Profile, "updates", "result.json")));
        }
        foreach (string version in new[] { "0.2.0", "0.1.9", "0.2.0-preview.1", "0.2.0+metadata" })
        {
            using var f = new Fixture(); f.Manifest(f.Source, version, f.Update.Files.ToArray());
            check("updater refuses equal or older releases " + version, Throws(() => AppUpdateInstaller.Prepare(f.Update with { Version = version }, f.Install, f.Profile, f.Arguments)));
        }
        using (var f = new Fixture())
        {
            string plan = f.Plan(); f.Manifest(f.Install, "0.3.0", Required.Concat(new[] { "obsolete.txt" }).ToArray());
            string newerManifest = File.ReadAllText(Path.Combine(f.Install, "PACKAGE-INFO.json"));
            int restarts = 0;
            int code = AppUpdateInstaller.Run(plan, new() { Restart = (_, _) => restarts++ });
            check("updater rechecks installed version immediately before mutation", code == 1 && restarts == 0 && File.ReadAllText(Path.Combine(f.Install, "PACKAGE-INFO.json")) == newerManifest);
            check("updater late downgrade rejection leaves application binary intact", File.ReadAllText(Path.Combine(f.Install, "FloatCanvas.dll")) == "old FloatCanvas.dll");
        }
        using (var f = new Fixture())
        {
            string plan = f.Plan(); int restarts = 0;
            int first = AppUpdateInstaller.Run(plan, new() { Restart = (_, _) => restarts++ });
            int second = AppUpdateInstaller.Run(plan, new() { Restart = (_, _) => restarts++ });
            check("updater refuses replay of an executed job", first == 0 && second == 2 && restarts == 1);
            check("updater duplicate worker does not overwrite successful result", AppUpdateInstaller.ConsumeResult(f.Profile) is { Success: true });
        }
    }

    private static bool Throws(Action action) { try { action(); return false; } catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or JsonException) { return true; } }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "FloatCanvas-update-installer-" + Guid.NewGuid().ToString("N"));
        internal string Install => Path.Combine(root, "installed");
        internal string Profile => Path.Combine(root, "profile");
        internal string Source => Path.Combine(Profile, "download");
        internal Dictionary<string, string> Original { get; }
        internal string[] Arguments => new[] { "--profile", Profile, "--recover", "--primary-screen" };
        internal PreparedUpdate Update => new(Source, "0.2.1", Required.Concat(new[] { "Docs/new.md" }).ToArray());

        internal Fixture()
        {
            Directory.CreateDirectory(Install); Directory.CreateDirectory(Profile); Directory.CreateDirectory(Source);
            WritePackage(Install, "old", "0.2.0", Required.Concat(new[] { "obsolete.txt" }).ToArray());
            WritePackage(Source, "new", "0.2.1", Update.Files.ToArray());
            Directory.CreateDirectory(Path.Combine(Install, "Docs")); File.WriteAllText(Path.Combine(Install, "user.notes"), "user data");
            Original = Required.Concat(new[] { "obsolete.txt" }).ToDictionary(path => path, path => File.ReadAllText(Path.Combine(Install, path)));
        }
        private void WritePackage(string path, string prefix, string version, string[] files)
        {
            foreach (string file in files.Where(file => file != "PACKAGE-INFO.json")) { string target = Path.Combine(path, file); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllText(target, prefix + " " + file); }
            Manifest(path, version, files);
        }
        internal void Manifest(string directory, string version, string[] files, string configuration = "Release") => File.WriteAllText(Path.Combine(directory, "PACKAGE-INFO.json"), JsonSerializer.Serialize(new { Product = "浮绘 FloatCanvas", Version = version, RuntimeIdentifier = "win-x64", SelfContained = false, Configuration = configuration, Files = files }));
        internal string Plan()
        {
            string path = AppUpdateInstaller.Prepare(Update, Install, Profile, Arguments);
            var plan = JsonSerializer.Deserialize<AppUpdateInstaller.InstallPlan>(File.ReadAllText(path))!;
            // Simulate an already-exited parent. This PID can never be this fixture process and no process is killed.
            File.WriteAllText(path, JsonSerializer.Serialize(plan with { ParentProcessId = int.MaxValue, ParentStartUtcTicks = 1 })); return path;
        }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
