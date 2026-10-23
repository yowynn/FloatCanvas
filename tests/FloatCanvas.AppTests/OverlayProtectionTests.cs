using FloatCanvas.App;
using System.Diagnostics;
using System.IO;

internal static class OverlayProtectionTests
{
    internal static void Run(Action<string, bool> check)
    {
        bool desktopAccepted = false;
        try { desktopAccepted = ProjectLaunchRequest.Parse(["--desktop-mode"]) is null; }
        catch (ArgumentException) { }
        check("ordinary desktop recovery can start without requiring the enhanced backend", desktopAccepted);
        check("enhancement retry is an explicit launch choice", ProjectLaunchRequest.Parse(["--retry-fullscreen"]) is null);
        bool conflict = false;
        try { ProjectLaunchRequest.Parse(["--desktop-mode", "--retry-fullscreen"]); }
        catch (ArgumentException) { conflict = true; }
        check("desktop-only and retry-enhancement choices cannot conflict", conflict);

        string root = Path.Combine(Path.GetTempPath(), "FloatCanvas-OverlayProtection", Guid.NewGuid().ToString("N"));
        string Profile(string name) => Path.Combine(root, name);
        string Marker(string profile) => Path.Combine(profile, "overlay-backend-state.json");
        int probes = 0, initializations = 0;
        (bool, string) Available() { probes++; return (true, "available"); }
        var forced = new OverlayBackendProtection(Profile("forced"), true, false, Available);
        check("desktop-only never probes or loads the uncertain ABI", !forced.TryEnable(() => initializations++) && probes == 0 && initializations == 0);
        check("explicit desktop startup does not write an enhanced-session marker", !File.Exists(Marker(Profile("forced"))));

        var unavailable = new OverlayBackendProtection(Profile("unavailable"), false, false, () => (false, "unsupported"));
        check("failed isolated probe selects ordinary desktop before native initialization", !unavailable.TryEnable(() => initializations++) && initializations == 0 && unavailable.Failure == "unsupported");
        check("failed backend is durably disabled", File.ReadAllText(Marker(Profile("unavailable"))).Contains("disabled"));
        unavailable.Complete();
        check("desktop exit does not erase the failed-backend marker", File.Exists(Marker(Profile("unavailable"))));

        var failed = new OverlayBackendProtection(Profile("initialization"), false, false, Available);
        check("main-process initialization failure also falls back", !failed.TryEnable(() => throw new DllNotFoundException("fixture missing DLL")) && failed.IsDisabled);

        string clean = Profile("clean");
        var normal = new OverlayBackendProtection(clean, false, false, Available);
        bool markedBeforeLoad = false;
        check("supported backend initializes once behind a durable guard", normal.TryEnable(() => { markedBeforeLoad = File.Exists(Marker(clean)); initializations++; }) && markedBeforeLoad);
        int before = initializations;
        normal.TryEnable(() => initializations++);
        check("guard does not repeatedly initialize the backend", initializations == before);
        normal.Complete();
        check("confirmed normal disposal clears the active marker", !File.Exists(Marker(clean)));

        string crashed = Profile("unclean");
        var interrupted = new OverlayBackendProtection(crashed, false, false, Available);
        interrupted.TryEnable(() => { }); // Simulate process loss by omitting Complete.
        int previousProbes = probes;
        var nextLaunch = new OverlayBackendProtection(crashed, false, false, Available);
        check("unclean enhanced exit makes the next launch desktop-only without another probe", !nextLaunch.TryEnable(() => initializations++) && probes == previousProbes);
        var retry = new OverlayBackendProtection(crashed, false, true, Available);
        check("explicit retry can revalidate a previously disabled backend", retry.TryEnable(() => { }));
        retry.Disable("runtime failure");
        retry.Complete();
        check("runtime failure remains disabled after clean desktop shutdown", File.ReadAllText(Marker(crashed)).Contains("runtime failure"));
        check("disabled backend cannot be retried inside the same session", !retry.TryEnable(() => initializations++));

        Directory.CreateDirectory(root);
        string blocked = Path.Combine(root, "not-a-directory"); File.WriteAllText(blocked, "fixture");
        previousProbes = probes;
        var unwritable = new OverlayBackendProtection(blocked, false, false, Available);
        check("failure to persist safety state prevents loading uncertain code", !unwritable.TryEnable(() => initializations++) && probes == previousProbes);

        bool fatal = false;
        try { new OverlayBackendProtection(Profile("fatal"), false, false, Available).TryEnable(() => throw new AccessViolationException("managed test sentinel")); }
        catch (AccessViolationException) { fatal = true; }
        check("corrupted-state faults are not mislabeled as catchable live recovery", fatal);

        ProcessStartInfo Child(string argument)
        {
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "FloatCanvas.AppTests.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add(argument); return start;
        }
        check("nonzero probe process exit cannot take down its parent", !OverlayBackendProtection.ProbeProcess(Child("--overlay-probe-exit"), 3000).Available);
        check("hung probe is terminated and selects ordinary desktop", !OverlayBackendProtection.ProbeProcess(Child("--overlay-probe-wait"), 200).Available);
    }
}
