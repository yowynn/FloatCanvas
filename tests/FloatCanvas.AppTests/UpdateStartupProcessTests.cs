using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using FloatCanvas.App;

internal static class UpdateStartupProcessTests
{
    private const string FixtureMarker = "startup-process.fixture";
    private const string StartupPlan = "FLOATCANVAS_UPDATE_STARTUP_PLAN";
    private static readonly string[] Required = ["FloatCanvas.exe", "FloatCanvas.dll", "FloatCanvas.Core.dll", "FloatCanvas.Localization.dll", "FloatCanvas.Rendering.dll", "FloatCanvas.Storage.dll", "FloatCanvas.Windows.dll", "FloatCanvas.deps.json", "FloatCanvas.runtimeconfig.json", "SkiaSharp.dll", "SkiaSharp.HarfBuzz.dll", "HarfBuzzSharp.dll", "libSkiaSharp.dll", "libHarfBuzzSharp.dll", "PACKAGE-INFO.json"];
    private sealed record Outcome(string Name, bool Passed);

    internal static bool TryRunChild(string[] args, out int code)
    {
        code = 72;
        if (args.Length == 0 || args[0] is not ("--update-startup-parent" or "--update-startup-child")) return false;
        if (args.Length < 3 || !Path.IsPathFullyQualified(args[1]) || !File.Exists(Path.Combine(args[1], FixtureMarker))) return true;
        try
        {
            code = args[0] == "--update-startup-parent" ? Parent(args[1], bool.Parse(args[2]))
                : Child(args[1], bool.Parse(args[2]), int.Parse(args[3], CultureInfo.InvariantCulture), long.Parse(args[4], CultureInfo.InvariantCulture));
        }
        catch (Exception error)
        {
            File.AppendAllText(Path.Combine(args[1], "process-errors.log"), error + Environment.NewLine);
            Console.Error.WriteLine(error); code = 73;
        }
        return true;
    }

    internal static void Run(Action<string, bool> check)
    {
        string root = Path.Combine(ApplicationPaths.ProfileDirectory, "update-startup-process-" + Guid.NewGuid().ToString("N"));
        string install = Path.Combine(root, "install");
        Directory.CreateDirectory(install); File.WriteAllText(Path.Combine(root, FixtureMarker), "owned test processes only");
        bool completed = false;
        try
        {
            foreach (string path in Directory.GetFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(install, Path.GetRelativePath(AppContext.BaseDirectory, path));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(path, target);
            }
            string original = Path.Combine(install, "FloatCanvas.AppTests.exe");
            File.Copy(original, Path.Combine(install, "FloatCanvas.exe"), true);
            foreach (bool validImage in new[] { true, false })
            {
                string executable = Path.Combine(install, validImage ? "FloatCanvas.exe" : "FloatCanvas.AppTests.exe");
                using var process = Process.Start(Start(executable, "--update-startup-parent", root, validImage.ToString()))!;
                try
                {
                    if (!process.WaitForExit(20000)) throw new TimeoutException("The owned startup parent did not finish.");
                    if (process.ExitCode != 0 && File.Exists(Path.Combine(root, "process-errors.log")))
                        Console.Error.WriteLine(File.ReadAllText(Path.Combine(root, "process-errors.log")));
                    check("startup binding fixture exits cleanly for " + (validImage ? "launcher" : "other parent image"), process.ExitCode == 0);
                    string report = Path.Combine(root, validImage ? "launcher.json" : "other-image.json");
                    foreach (var outcome in JsonSerializer.Deserialize<Outcome[]>(File.ReadAllText(report))!) check(outcome.Name, outcome.Passed);
                }
                finally { Stop(process); }
            }
            completed = true;
        }
        finally
        {
            if (completed) Directory.Delete(root, true);
            else Console.Error.WriteLine("Startup process evidence retained: " + root);
        }
    }

    private static int Parent(string root, bool validImage)
    {
        string executable = Path.Combine(root, "install", "FloatCanvas.AppTests.exe");
        using var sibling = Process.Start(Start(executable, "--overlay-probe-wait"))!;
        using var child = Process.Start(Start(executable, "--update-startup-child", root, validImage.ToString(),
            sibling.Id.ToString(CultureInfo.InvariantCulture), sibling.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(CultureInfo.InvariantCulture)))!;
        try
        {
            if (!child.WaitForExit(15000)) throw new TimeoutException("The owned validation child did not finish.");
            return child.ExitCode;
        }
        finally { Stop(child); Stop(sibling); }
    }

    private static int Child(string root, bool validImage, int siblingId, long siblingCreated)
    {
        string install = Path.Combine(root, "install");
        using var current = Process.GetCurrentProcess();
        using var parent = Process.GetProcessById(ParentId());
        string id = parent.Id.ToString(CultureInfo.InvariantCulture);
        long created = parent.StartTime.ToUniversalTime().ToFileTimeUtc();
        string creation = created.ToString(CultureInfo.InvariantCulture);
        string context = validImage ? "launcher" : "other image";
        var outcomes = new List<Outcome>();
        void Check(string name, bool passed) => outcomes.Add(new(context + ": " + name, passed));
        bool Accepts(string directory, string? processId, string? fileTime)
        {
            using var actual = UpdateStartupProcess.TryGetLauncher(directory, processId, fileTime);
            return actual is not null;
        }
        Check("real direct parent requires the exact installed launcher image", Accepts(install, id, creation) == validImage);
        Check("absent launcher identity retains direct Host mode", !Accepts(install, null, null));
        Check("a forged process id is rejected", !Accepts(install, "2147483647", creation));
        Check("self cannot replace the direct parent", !Accepts(install, current.Id.ToString(CultureInfo.InvariantCulture), current.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(CultureInfo.InvariantCulture)));
        Check("a live sibling is rejected despite valid creation time", !Accepts(install, siblingId.ToString(CultureInfo.InvariantCulture), siblingCreated.ToString(CultureInfo.InvariantCulture)));
        Check("forged parent creation time is rejected", !Accepts(install, id, (created + 1).ToString(CultureInfo.InvariantCulture)));
        Check("another installation path is rejected", !Accepts(Path.Combine(root, "other-install"), id, creation));
        Check("nondecimal parent id is rejected", !Accepts(install, "+" + id, creation));
        Check("nondecimal creation time is rejected", !Accepts(install, id, " " + creation));

        string profile = Path.Combine(root, "profile"), source = Path.Combine(root, "source");
        Directory.CreateDirectory(profile); Directory.CreateDirectory(source);
        foreach (string file in Required.Where(x => x != "PACKAGE-INFO.json"))
        {
            string installed = Path.Combine(install, file);
            if (!File.Exists(installed)) File.WriteAllText(installed, "isolated package metadata");
            File.Copy(installed, Path.Combine(source, file), true);
        }
        void Manifest(string directory, string version) => File.WriteAllText(Path.Combine(directory, "PACKAGE-INFO.json"),
            JsonSerializer.Serialize(new { Product = "浮绘 FloatCanvas", Version = version, RuntimeIdentifier = "win-x64", SelfContained = false, Configuration = "Release", Files = Required }));
        Manifest(install, "0.3.0"); Manifest(source, "0.3.1");
        string plan = AppUpdateInstaller.Prepare(new PreparedUpdate(source, "0.3.1", Required), install, profile, ["--profile", profile, "--recover"]);
        string started = Path.Combine(Path.GetDirectoryName(plan)!, "started.json");
        int Acknowledge(string? launcherId, string? launcherTime)
        {
            Environment.SetEnvironmentVariable(StartupPlan, plan);
            Environment.SetEnvironmentVariable(UpdateStartupProcess.LauncherIdEnvironment, launcherId);
            Environment.SetEnvironmentVariable(UpdateStartupProcess.LauncherCreationEnvironment, launcherTime);
            AppUpdateInstaller.AcknowledgeStartup();
            using var result = JsonDocument.Parse(File.ReadAllText(started));
            Check("startup context is consumed", Environment.GetEnvironmentVariable(StartupPlan) is null &&
                Environment.GetEnvironmentVariable(UpdateStartupProcess.LauncherIdEnvironment) is null &&
                Environment.GetEnvironmentVariable(UpdateStartupProcess.LauncherCreationEnvironment) is null);
            return result.RootElement.GetProperty("ProcessId").GetInt32();
        }
        Check("modern direct Host startup acknowledges itself", Acknowledge(null, null) == current.Id);
        Check("forged binding acknowledges self instead of another process", Acknowledge(siblingId.ToString(CultureInfo.InvariantCulture), siblingCreated.ToString(CultureInfo.InvariantCulture)) == current.Id);
        Check("legacy launcher startup acknowledges only a validated parent", Acknowledge(id, creation) == (validImage ? parent.Id : current.Id));
        using (var result = JsonDocument.Parse(File.ReadAllText(started)))
            Check("acknowledgement retains the bound process creation time", result.RootElement.GetProperty("StartUtcTicks").GetInt64() == (validImage ? parent : current).StartTime.ToUniversalTime().Ticks);
        File.WriteAllText(Path.Combine(root, validImage ? "launcher.json" : "other-image.json"), JsonSerializer.Serialize(outcomes));
        return 0;
    }

    private static int ParentId()
    {
        // The fixture records its owned parent's identity in the child's environment.
        return int.Parse(Environment.GetEnvironmentVariable("FLOATCANVAS_STARTUP_FIXTURE_PARENT")!, CultureInfo.InvariantCulture);
    }

    private static ProcessStartInfo Start(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["FLOATCANVAS_STARTUP_FIXTURE_PARENT"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        start.Environment.Remove(StartupPlan);
        start.Environment.Remove(UpdateStartupProcess.LauncherIdEnvironment);
        start.Environment.Remove(UpdateStartupProcess.LauncherCreationEnvironment);
        return start;
    }
    private static void Stop(Process process)
    {
        if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
    }
}
