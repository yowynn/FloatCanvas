using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using FloatCanvas.Windows;

namespace FloatCanvas.PlatformTests;

/// <summary>Creates hidden production HWNDs and directly queries activation policy; never shows a surface, acquires focus or sends input.</summary>
internal static class WindowPolicyFixture
{
    private sealed record PolicyCheck(string Name, bool Passed);
    internal static int Run(string evidencePath)
    {
        var checks = new List<PolicyCheck>();
        var stages = new List<object>();
        nint foreground = Native.GetForegroundWindow(); Native.GetCursorPos(out var cursor);
        int notificationBeforeHr = SHQueryUserNotificationState(out int notificationBefore);
        OverlayHost? host = null;
        int applicationGateCalls = 0, pointerEvents = 0;
        nint[] handles = [];
        Exception? failure = null;
        void Check(string name, bool passed)
        {
            checks.Add(new(name, passed));
            if (!passed) throw new InvalidOperationException(name);
        }
        try
        {
            host = new OverlayHost(OverlayMonitorScope.AllScreens, initiallyVisible: false);
            host.CanBeginCanvasGesture = () => { applicationGateCalls++; return false; };
            host.Pointer += _ => pointerEvents++;
            handles = host.Diagnostics.SelectMany(w => new[] { w.DisplayHandle, w.InputHandle }).Append(host.ControlHandle).ToArray();
            void Inspect(string phase)
            {
                var diagnostics = host.Diagnostics;
                Check(phase + ": every physical monitor retains its display/input pair", diagnostics.Count == host.Monitors.Count && diagnostics.Count > 0);
                Check(phase + ": control is non-fullscreen before first show", GetProp(host.ControlHandle, "NonRudeHWND") == 1 && !Native.IsWindowVisible(host.ControlHandle));
                foreach (var window in diagnostics)
                {
                    var monitor = host.Monitors.Single(m => m.Bounds == window.Bounds);
                    foreach (var (kind, handle) in new[] { ("display", window.DisplayHandle), ("input", window.InputHandle) })
                    {
                        Native.GetWindowRect(handle, out var bounds); Native.GetClientRect(handle, out var client);
                        Check(phase + ": " + kind + " declares NonRudeHWND before first show", GetProp(handle, "NonRudeHWND") == 1);
                        Check(phase + ": " + kind + " stays hidden", !Native.IsWindowVisible(handle));
                        Check(phase + ": " + kind + " keeps the whole monitor with no edge gap", bounds.Left == monitor.Bounds.Left && bounds.Top == monitor.Bounds.Top && bounds.Right == monitor.Bounds.Right && bounds.Bottom == monitor.Bounds.Bottom && client.Right == monitor.Bounds.Width && client.Bottom == monitor.Bounds.Height);
                    }
                    Check(phase + ": display keeps topmost layered transparent noactivate tool styles", (window.DisplayExtendedStyle & 0x080800A8) == 0x080800A8);
                    Check(phase + ": input keeps topmost layered noactivate tool styles without click-through or native disabling", (window.InputExtendedStyle & 0x08080088) == 0x08080088 && (window.InputExtendedStyle & 0x20) == 0 && IsWindowEnabled(window.InputHandle));
                }
                var pairs = (System.Collections.IEnumerable)typeof(OverlayHost).GetField("_windows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(host)!;
                var handler = typeof(OverlayHost).GetMethod("OnMessage", BindingFlags.NonPublic | BindingFlags.Instance)!;
                foreach (var pair in pairs)
                {
                    var input = pair.GetType().GetProperty("Input", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pair)!;
                    // A direct handler query is neither a posted input message nor a real
                    // mouse event. It proves WM_MOUSEACTIVATE cannot bypass the press gate.
                    var result = (nint)handler.Invoke(host, [input, 0x21u, (nuint)0, (nint)0])!;
                    Check(phase + ": activation query rejects automatic activation while preserving the eventual mouse message", result == 3);
                }
                Check(phase + ": activation queries do not call the business gate or emit pointer events", applicationGateCalls == 0 && pointerEvents == 0);
                Check(phase + ": hidden contract check owns no input isolation or pointer capture", !host.IsEditing && !host.IsKeyboardIsolationEnabled && !host.IsKeyboardIsolationActive && host.CaptureHandle == 0 && host.PresentedFrameCount == 0);
                stages.Add(new { Phase = phase, Control = host.ControlHandle.ToInt64(), Windows = diagnostics.Select(w => new
                {
                    Display = w.DisplayHandle.ToInt64(), Input = w.InputHandle.ToInt64(), w.Bounds,
                    w.DisplayExtendedStyle, w.InputExtendedStyle, w.DisplayVisible, w.InputVisible,
                    DisplayNonRude = GetProp(w.DisplayHandle, "NonRudeHWND").ToInt64(), InputNonRude = GetProp(w.InputHandle, "NonRudeHWND").ToInt64()
                }).ToArray() });
            }
            Inspect("Created");
            var old = host.Diagnostics.SelectMany(w => new[] { w.DisplayHandle, w.InputHandle }).ToArray();
            typeof(OverlayHost).GetMethod("RebuildMonitors", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(host, null);
            Inspect("LayoutRebuilt");
            Check("rebuild destroys each old production HWND", old.All(h => !IsWindow(h)));
            handles = handles.Concat(host.Diagnostics.SelectMany(w => new[] { w.DisplayHandle, w.InputHandle })).ToArray();
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            host?.Dispose();
            Native.GetCursorPos(out var afterCursor);
            checks.Add(new("no foreground or cursor change", Native.GetForegroundWindow() == foreground && afterCursor.X == cursor.X && afterCursor.Y == cursor.Y));
            checks.Add(new("all recorded own HWNDs destroyed", handles.All(h => !IsWindow(h))));
            int notificationAfterHr = SHQueryUserNotificationState(out int notificationAfter);
            bool passed = failure is null && checks.All(c => c.Passed);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(evidencePath))!);
            File.WriteAllText(evidencePath, JsonSerializer.Serialize(new
            {
                Passed = passed, Checks = checks, Stages = stages, Failure = failure?.ToString(),
                NotificationObservation = new { BeforeHResult = notificationBeforeHr, Before = notificationBefore, AfterHResult = notificationAfterHr, After = notificationAfter },
                VisibleWindowsShown = false, ForegroundActivation = false, PhysicalInputInjection = false, DesktopCapture = false,
                DirectActivationPolicyQueries = true, InputMessagesInjected = false,
                Scope = "Production HWND registration, full bounds, styles, rebuild and direct WM_MOUSEACTIVATE-handler policy queries only. No real gesture or foreground/focus acquisition is exercised. Notification values are read-only etiquette observations; this does not establish Windows Do Not Disturb UI state, taskbar behavior, game coverage or third-party picker compatibility."
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Window policy hidden-native checks: {(passed ? "PASS" : "FAIL")} ({checks.Count}); evidence: {evidencePath}");
            if (failure is not null) Console.Error.WriteLine(failure);
        }
        return failure is null && checks.All(c => c.Passed) ? 0 : 1;
    }

    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)] private static extern nint GetProp(nint window, string name);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(nint window);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
}
