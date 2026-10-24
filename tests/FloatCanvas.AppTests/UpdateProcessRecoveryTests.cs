using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using FloatCanvas.App;

/// <summary>
/// Separate processes exercise the real durable journal, file replacement and recovery code.
/// Only parent waiting and application restart are substituted; no UI, user profile or native build.
/// </summary>
internal static class UpdateProcessRecoveryTests
{
    private static readonly string[] Required = { "FloatCanvas.exe", "FloatCanvas.dll", "FloatCanvas.Core.dll", "FloatCanvas.Localization.dll", "FloatCanvas.Rendering.dll", "FloatCanvas.Storage.dll", "FloatCanvas.Windows.dll", "FloatCanvas.deps.json", "FloatCanvas.runtimeconfig.json", "SkiaSharp.dll", "SkiaSharp.HarfBuzz.dll", "HarfBuzzSharp.dll", "libSkiaSharp.dll", "libHarfBuzzSharp.dll", "PACKAGE-INFO.json" };
    private static readonly string[] NewFiles = Required.Concat(new[] { "Docs/new.md" }).ToArray();
    private const string FixtureEnvironment = "FLOATCANVAS_UPDATE_TEST_FIXTURE";

    // This entry runs before the AppTests parent configures ApplicationPaths or creates a WPF app.
    internal static int Child(string planPath, string stage, int desired)
    {
        string? fixture = Environment.GetEnvironmentVariable(FixtureEnvironment);
        if (fixture is null || !Path.IsPathFullyQualified(fixture) || !Directory.Exists(fixture) ||
            !Path.GetFullPath(planPath).StartsWith(Path.GetFullPath(fixture) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(fixture, "process-test.fixture")) || desired < 1 || desired > NewFiles.Length ||
            stage is not ("before" or "after" or "delete" or "RecoveryPointer" or "StartupAcknowledged" or "Committed" or "resume")) return 72;
        int seen = 0;
        void Interrupt(string current)
        {
            // Environment.Exit bypasses the installer's catch/finally, unlike throwing a test exception.
            if (current == stage && ++seen == desired) Environment.Exit(91);
        }
        return AppUpdateInstaller.Run(planPath, new()
        {
            WaitForParent = _ => true,
            BeforeReplace = _ => Interrupt("before"), AfterReplace = _ => Interrupt("after"), AfterDelete = _ => Interrupt("delete"),
            AfterCheckpoint = Interrupt, Restart = (_, _) => { }
        });
    }

    internal static void Run(Action<string, bool> check)
    {
        var cases = new List<(string Stage, int Index)>();
        foreach (string stage in new[] { "before", "after" })
            for (int index = 1; index <= NewFiles.Length; index++) cases.Add((stage, index));
        cases.AddRange(new[] { ("delete", 1), ("RecoveryPointer", 1), ("StartupAcknowledged", 1), ("Committed", 1) });
        foreach (var point in cases)
        {
            using var fixture = new Fixture();
            var interrupted = fixture.Execute(point.Stage, point.Index);
            string pointer = Path.Combine(fixture.Install, AppUpdateInstaller.RecoveryPointerName);
            bool crashBound = interrupted.Code == 91 && File.Exists(pointer) && File.ReadAllText(pointer) == fixture.Plan;
            var resumed = fixture.Execute("resume", 1);
            var actual = Snapshot(fixture.Install);
            bool completeOld = Equal(actual, fixture.Old), completeNew = Equal(actual, fixture.New);
            bool expectedGeneration = point.Stage == "Committed" ? completeNew && resumed.Code == 2 : completeOld && resumed.Code == 1;
            bool preserved = File.ReadAllText(Path.Combine(fixture.Install, "user.notes")) == "preserved user data" &&
                File.ReadAllText(Path.Combine(fixture.Profile, "document.floatcanvas")) == "preserved project";
            bool pointerCleared = !File.Exists(pointer);
            var replay = fixture.Execute("resume", 1);
            bool idempotent = replay.Code == 2 && Equal(actual, Snapshot(fixture.Install));
            bool passed = crashBound && (completeOld != completeNew) && expectedGeneration && preserved && pointerCleared && idempotent;
            if (!passed)
                Console.Error.WriteLine($"Hard interruption {point.Stage}[{point.Index}]: crash={interrupted.Code}, bound={crashBound}, resume={resumed.Code}, old={completeOld}, new={completeNew}, user={preserved}, pointerCleared={pointerCleared}, replay={replay.Code}, idempotent={idempotent}. {interrupted.Output} {resumed.Output} {replay.Output}");
            check($"updater hard interruption {point.Stage}[{point.Index}] recovers one complete generation and preserves user files", passed);
            fixture.Completed = true;
        }
    }

    private static Dictionary<string, string> Snapshot(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(directory, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.OrdinalIgnoreCase);
    private static bool Equal(Dictionary<string, string> first, Dictionary<string, string> second) => first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out string? value) && pair.Value == value);

    private sealed class Fixture : IDisposable
    {
        private string Root { get; } = Path.Combine(Path.GetTempPath(), "FloatCanvas-update-process-" + Guid.NewGuid().ToString("N"));
        internal string Install => Path.Combine(Root, "installed");
        internal string Profile => Path.Combine(Root, "profile");
        internal string Plan { get; }
        internal Dictionary<string, string> Old { get; }
        internal Dictionary<string, string> New { get; }
        internal bool Completed { get; set; }

        internal Fixture()
        {
            string source = Path.Combine(Root, "download");
            Package(Install, "1.0.0", Required.Concat(new[] { "obsolete.txt" }).ToArray()); Package(source, "1.1.0", NewFiles);
            Directory.CreateDirectory(Profile); File.WriteAllText(Path.Combine(Root, "process-test.fixture"), "isolated update process recovery test");
            File.WriteAllText(Path.Combine(Install, "user.notes"), "preserved user data");
            File.WriteAllText(Path.Combine(Profile, "document.floatcanvas"), "preserved project");
            // Expected results come from the original source bytes, never the installer's plan/journal.
            Old = Snapshot(Install); New = Snapshot(source); New.Add("user.notes", Old["user.notes"]);
            Plan = AppUpdateInstaller.Prepare(new(source, "1.1.0", NewFiles), Install, Profile, new[] { "--profile", Profile, "--recover" });
        }

        internal (int Code, string Output) Execute(string stage, int index)
        {
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("The AppTests executable is unavailable.");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Root
            };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(UpdateProcessRecoveryTests).Assembly.Location);
            start.ArgumentList.Add("--update-fault-child"); start.ArgumentList.Add(Plan); start.ArgumentList.Add(stage); start.ArgumentList.Add(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.Environment[FixtureEnvironment] = Root;
            using var process = Process.Start(start) ?? throw new IOException("Could not start the isolated update test child.");
            Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(20000))
            {
                process.Kill(); process.WaitForExit(5000);
                throw new TimeoutException("The isolated update test child did not finish: " + stage + ". Fixture: " + Root);
            }
            return (process.ExitCode, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
        }

        private static void Package(string directory, string version, string[] files)
        {
            foreach (string relative in files)
            {
                string path = Path.Combine(directory, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, version + " " + relative);
            }
            File.WriteAllText(Path.Combine(directory, "PACKAGE-INFO.json"), JsonSerializer.Serialize(new { Product = "浮绘 FloatCanvas", Version = version, RuntimeIdentifier = "win-x64", SelfContained = false, Configuration = "Release", Files = files }));
        }

        public void Dispose()
        {
            if (!Completed) { Console.Error.WriteLine("Retained failed update process fixture: " + Root); return; }
            string target = Path.GetFullPath(Root), temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(target).StartsWith("FloatCanvas-update-process-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture cleanup path.");
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
    }
}
