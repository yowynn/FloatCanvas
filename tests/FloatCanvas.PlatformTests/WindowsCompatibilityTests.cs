using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.PlatformTests;

/// <summary>Exercises version policy and hidden owned HWNDs without input, activation or desktop capture.</summary>
internal static class WindowsCompatibilityTests
{
    internal static int Run(string evidencePath)
    {
        var checks = new List<object>();
        Exception? failure = null;
        nint foreground = Native.GetForegroundWindow(); Native.GetCursorPos(out var cursor);
        void Check(string name, bool passed)
        {
            checks.Add(new { Name = name, Passed = passed });
            Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}");
            if (!passed) throw new InvalidOperationException(name);
        }
        try
        {
            foreach (string language in new[] { "zh-CN", "en" })
                foreach (string key in new[] { "Overlay.PlatformUnsupported", "Overlay.DpiContextFailed", "Overlay.DpiContextRestoreFailed", "Capture.LiveExclusionUnsupported" })
                    Check(language + " localizes " + key, !string.IsNullOrWhiteSpace(L.Get(key, language)) && L.Get(key, language) != key);

            var legacy = WindowsCapabilities.FromVersion(new Version(10, 0, 14393));
            nint previous = NativeMethods.GetThreadDpiAwarenessContext();
            using (new ThreadDpiAwarenessScope(legacy))
                Check("1607 uses the real PMv1 physical-pixel context", AreDpiAwarenessContextsEqual(NativeMethods.GetThreadDpiAwarenessContext(), -3));
            Check("PMv1 scope restores the original real thread context", AreDpiAwarenessContextsEqual(NativeMethods.GetThreadDpiAwarenessContext(), previous));
            if (WindowsCapabilities.Current.SupportsPerMonitorV2)
            {
                using (new ThreadDpiAwarenessScope(WindowsCapabilities.Current))
                    Check("current Windows retains PMv2", AreDpiAwarenessContextsEqual(NativeMethods.GetThreadDpiAwarenessContext(), -4));
                Check("PMv2 scope restores the original real thread context", AreDpiAwarenessContextsEqual(NativeMethods.GetThreadDpiAwarenessContext(), previous));
            }
            int attempted = 0;
            bool rejected = false;
            try { using var failed = new ThreadDpiAwarenessScope(legacy, _ => { attempted++; return 0; }); }
            catch (Win32Exception) { rejected = true; }
            Check("a failed DPI context switch is explicit and is not restored as a valid context", rejected && attempted == 1);
            attempted = 0; rejected = false;
            try { using var failedRestore = new ThreadDpiAwarenessScope(legacy, _ => ++attempted == 1 ? 123 : 0); }
            catch (Win32Exception) { rejected = true; }
            Check("a failed DPI context restoration is explicit", rejected && attempted == 2);

            var monitor = new MonitorInfo("FloatCanvas.Compatibility.Hidden", new(80, 80, 24, 24), new(80, 80, 24, 24), 96, 96, true);
            IReadOnlyList<MonitorInfo> Enumerate() => [monitor];
            Check("the existing three-argument fixture constructor remains available", typeof(OverlayHost).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                [typeof(OverlayMonitorScope), typeof(Func<IReadOnlyList<MonitorInfo>>), typeof(bool)], null) is not null);
            using (var current = new OverlayHost(OverlayMonitorScope.PrimaryScreen, Enumerate, false))
                Check("normal host construction uses current capabilities without showing windows", ReferenceEquals(current.Capabilities, WindowsCapabilities.Current) &&
                    !current.IsVisible && current.Diagnostics.All(w => !w.DisplayVisible && !w.InputVisible));
            bool oldRejected = false;
            try { using var invalid = new OverlayHost(OverlayMonitorScope.PrimaryScreen, Enumerate, false, WindowsCapabilities.FromVersion(new(10, 0, 14392))); }
            catch (PlatformNotSupportedException) { oldRejected = true; }
            Check("only systems below 1607 are rejected at construction", oldRejected);
            foreach (int build in new[] { 14393, 17763, 19044 })
            {
                var capabilities = WindowsCapabilities.FromVersion(new Version(10, 0, build));
                using var host = new OverlayHost(OverlayMonitorScope.PrimaryScreen, Enumerate, false, capabilities);
                Check(build + " creates hidden production display/input HWNDs", host.Capabilities == capabilities && !host.IsEditing &&
                    host.Diagnostics.Count == 1 && host.Diagnostics.All(w => w.DisplayHandle != 0 && w.InputHandle != 0 && !w.DisplayVisible && !w.InputVisible));
                if (!capabilities.SupportsCaptureExclusion)
                {
                    bool unavailable = false;
                    try { host.SetLiveCaptureExclusion(true); } catch (PlatformNotSupportedException) { unavailable = true; }
                    Check(build + " rejects only unsupported live exclusion", unavailable);
                    host.SetLiveCaptureExclusion(false);
                    Check(build + " allows no-op live exclusion cleanup", !host.IsEditing);
                }
                using (var scope = host.HideForCapture())
                    Check(build + " begins a clean-capture transaction while remaining hidden", scope.NotBeforeTimestamp > 0 && !host.IsVisible &&
                        host.Diagnostics.All(w => !w.DisplayVisible && !w.InputVisible));
                Check(build + " restores the originally hidden host", !host.IsVisible && !host.IsEditing && host.Diagnostics.All(w => !w.DisplayVisible && !w.InputVisible));
            }

            // Visible in Win32 terms but deliberately far outside the desktop, with no activation.
            // Prior affinity must survive the hide/show cycle; Windows reports zero while hidden.
            nint window = NativeMethods.CreateWindowExW(NativeMethods.ExToolWindow | NativeMethods.ExNoActivate, "STATIC", "FloatCanvas compatibility fixture",
                NativeMethods.WsPopup, -32000, -32000, 8, 8, 0, 0, NativeMethods.GetModuleHandleW(null), 0);
            if (window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                NativeMethods.ShowWindow(window, 8);
                Check("owned offscreen fixture begins visible without activation", NativeMethods.IsWindowVisible(window) && Native.GetForegroundWindow() == foreground);
                Check("owned offscreen fixture accepts a prior capture affinity", NativeMethods.SetWindowDisplayAffinity(window, 1));
                bool affinityRead = NativeMethods.GetWindowDisplayAffinity(window, out uint affinityBefore);
                Console.WriteLine($"Offscreen affinity before hiding: read={affinityRead}, value={affinityBefore}.");
                int restored = 0, hidden = 0;
                CaptureVisibilityScope BeginLegacyCapture(Action ready) => new([window], [],
                    _ => { restored++; return new(CaptureRestoreStatus.Restored); }, (_, _) => ready(),
                    legacy, () => true, true, (_, _) => throw new InvalidOperationException("No input window in this fixture"),
                    _ => { }, _ => { });
                using (var scope = BeginLegacyCapture(() => hidden++))
                {
                    Check("1607 one-shot capture hides its own window", !NativeMethods.IsWindowVisible(window) && hidden == 1 && scope.NotBeforeTimestamp > 0);
                    bool duringRead = NativeMethods.GetWindowDisplayAffinity(window, out uint affinity);
                    Console.WriteLine($"Offscreen affinity while hidden: read={duringRead}, value={affinity}.");
                    Check("1607 hide-only capture reads the hidden window state without failing", duringRead);
                }
                Check("hide-only capture restores prior visibility and callback", NativeMethods.IsWindowVisible(window) && restored == 1);
                bool afterRead = NativeMethods.GetWindowDisplayAffinity(window, out uint original);
                Console.WriteLine($"Offscreen affinity after restoration: read={afterRead}, value={original}.");
                Check("hide-only capture preserves prior affinity after restoration", afterRead && original == affinityBefore);
                NativeMethods.ShowWindow(window, 0);
                bool hideFailed = false;
                try { using var scope = BeginLegacyCapture(() => throw new InvalidOperationException("fixture")); }
                catch (InvalidOperationException) { hideFailed = true; }
                Check("hide-only capture failures still restore the original hidden state", hideFailed && restored == 2 && !NativeMethods.IsWindowVisible(window));
            }
            finally { NativeMethods.DestroyWindow(window); }
            Native.GetCursorPos(out var after);
            Check("compatibility checks do not change foreground or pointer", Native.GetForegroundWindow() == foreground && after.X == cursor.X && after.Y == cursor.Y);
        }
        catch (Exception error) { failure = error; Console.Error.WriteLine(error); }
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        File.WriteAllText(evidencePath, JsonSerializer.Serialize(new
        {
            Passed = failure is null, Checks = checks, Error = failure?.ToString(),
            NativeInput = false, DesktopCapture = false, ForegroundActivation = false,
            Scope = "Version policy and old-version branches run on the current OS with injected capabilities. This does not establish Windows 10 hardware or mixed-DPI acceptance."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Windows compatibility checks: {(failure is null ? "PASS" : "FAIL")} ({checks.Count}); evidence: {evidencePath}");
        return failure is null ? 0 : 1;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);
}
