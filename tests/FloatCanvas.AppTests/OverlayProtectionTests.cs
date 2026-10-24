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
        OverlayBackendProtection.ProbeResult Available() { probes++; return new(true, "available"); }
        var forced = new OverlayBackendProtection(Profile("forced"), true, false, Available);
        check("desktop-only never probes or loads the uncertain ABI", !forced.TryEnable(() => initializations++) && probes == 0 && initializations == 0);
        check("explicit desktop startup does not write an enhanced-session marker", !File.Exists(Marker(Profile("forced"))));

        var unavailable = new OverlayBackendProtection(Profile("unavailable"), false, false, () => new(false, "unsupported"));
        check("failed isolated probe selects ordinary desktop before native initialization", !unavailable.TryEnable(() => initializations++) && initializations == 0 && unavailable.Failure == "unsupported");
        check("current failure is recorded for diagnostics", File.ReadAllText(Marker(Profile("unavailable"))).Contains("disabled"));
        unavailable.Complete();
        check("desktop exit does not erase the failed-backend marker", File.Exists(Marker(Profile("unavailable"))));
        var recovered = new OverlayBackendProtection(Profile("unavailable"), false, false, Available);
        check("a past failed probe cannot veto a currently successful probe", recovered.TryEnable(() => { }));
        recovered.Complete();

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
        check("previous unclean exit must re-probe and retain a currently supported backend", nextLaunch.TryEnable(() => initializations++) && probes == previousProbes + 1);
        nextLaunch.Complete();
        var retry = new OverlayBackendProtection(crashed, false, true, Available);
        check("explicit retry can revalidate a previously disabled backend", retry.TryEnable(() => { }));
        retry.Disable("runtime failure");
        retry.Complete();
        check("runtime failure remains disabled after clean desktop shutdown", File.ReadAllText(Marker(crashed)).Contains("runtime failure"));
        check("disabled backend cannot be retried inside the same session", !retry.TryEnable(() => initializations++));
        var afterFailure = new OverlayBackendProtection(crashed, false, false, Available);
        check("recorded runtime failure cannot disable the next supported launch", afterFailure.TryEnable(() => { }));
        afterFailure.Complete();

        var inconclusive = new OverlayBackendProtection(Profile("inconclusive"), false, false, () => new(false, "timeout", true));
        check("inconclusive probe cannot veto successful actual initialization", inconclusive.TryEnable(() => { }) && inconclusive.Failure is null && inconclusive.Warning == "timeout");
        inconclusive.Complete();
        var actualFailure = new OverlayBackendProtection(Profile("inconclusive-failed-init"), false, false, () => new(false, "timeout", true));
        check("actual initialization failure still falls back after an inconclusive probe", !actualFailure.TryEnable(() => throw new DllNotFoundException("actual missing backend")) && actualFailure.Failure == "actual missing backend");

        Directory.CreateDirectory(root);
        string blocked = Path.Combine(root, "not-a-directory"); File.WriteAllText(blocked, "fixture");
        previousProbes = probes;
        var unwritable = new OverlayBackendProtection(blocked, false, false, Available);
        check("diagnostic marker write failure cannot disable a supported backend", unwritable.TryEnable(() => initializations++) && probes == previousProbes + 1 && unwritable.Failure is null);
        check("diagnostic write failure is reported without an unsupported notice", unwritable.Warning is not null);

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
        var timedOut = OverlayBackendProtection.ProbeProcess(Child("--overlay-probe-wait"), 200);
        check("hung child probe is terminated but support remains inconclusive", !timedOut.Available && timedOut.Inconclusive);
        var missingProbe = Child("--overlay-probe-exit"); missingProbe.FileName = Path.Combine(root, "missing-probe.exe");
        check("failure to launch a diagnostic child is not a capability failure", OverlayBackendProtection.ProbeProcess(missingProbe, 200).Inconclusive);
    }
}
