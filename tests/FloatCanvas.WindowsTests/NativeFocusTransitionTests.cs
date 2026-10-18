using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using FloatCanvas.Windows;
using static FloatCanvas.Windows.NativeMethods;

namespace FloatCanvas.WindowsTests;

/// <summary>Opt-in bounded native windows and real keyboard hooks; no injected input or user profile access.</summary>
internal static class NativeFocusTransitionTests
{
    public static void Run(Action<bool, string> check, bool waitForActivation)
    {
        nint originalForeground = GetForegroundWindow();
        var bounds = new PixelBounds(40, 120, 420, 220);
        var monitor = new MonitorInfo("focus-transition-fixture", bounds, bounds, 96, 96, true);
        using var host = new OverlayHost(OverlayMonitorScope.PrimaryScreen, () => [monitor], false);
        var failures = new List<Exception>();
        host.Error += failures.Add;
        int focusExits = 0;
        host.EditingFocusLost += () => focusExits++;
        host.EnableKeyboardIsolation((_, complete) => complete());
        // APPWINDOW is fixture-only, so supported desktop automation can discover this
        // independent target without changing any production overlay/window policy.
        nint owner = CreateWindowExW(0x40000 | ExTopmost, "STATIC", "FloatCanvas focus transition fixture",
            WsPopup, 60, 130, 300, 160, 0, 0, GetModuleHandleW(null), 0);
        check(owner != 0, "Native focus fixture creates its own bounded return-target window");
        try
        {
            ShowWindow(owner, 5); SetForegroundWindow(owner); SetFocus(owner);
            if (waitForActivation && GetForegroundWindow() != owner)
            {
                Console.WriteLine($"WAITING_OWN_FOCUS_FIXTURE pid={Environment.ProcessId}; hwnd={owner}; timeout=30s");
                var activation = Stopwatch.StartNew();
                while (GetForegroundWindow() != owner && activation.Elapsed < TimeSpan.FromSeconds(30)) Pump(10);
            }
            check(GetForegroundWindow() == owner, "Only a self-owned fixture window is activated before hook testing");
            check(host.TryEnterEditing(out _), "Real host enters editing with keyboard isolation enabled");
            var isolation = (KeyboardInputIsolation)typeof(OverlayHost).GetField("_keyboardIsolation",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            long initialLease = isolation.CurrentLeaseId;
            using (host.BeginInternalWindowTransition())
            {
                SetForegroundWindow(owner); SetFocus(owner);
                isolation.EndLease(KeyboardLeaseEndReason.ForegroundLost);
                check(!isolation.IsLeaseActive, "The real hook service has ended before queued foreground-loss delivery");
            }
            Pump(90);
            check(host.IsEditing && host.IsKeyboardIsolationActive && isolation.CurrentLeaseId > initialLease && focusExits == 0,
                "A bounded internal return resumes the foreground-ended lease and preserves editing");
            check(GetForegroundWindow() == owner && GetFocus() == owner,
                "Renewing the lease leaves the actual owner foreground and keyboard focus unchanged");

            long beforeExpired = isolation.CurrentLeaseId;
            using (host.BeginInternalWindowTransition()) Pump(50);
            isolation.EndLease(KeyboardLeaseEndReason.ForegroundLost);
            Pump(50);
            check(!host.IsEditing && !isolation.IsLeaseActive && isolation.CurrentLeaseId == beforeExpired,
                "A token expires on the next UI turn even when its using scope remains open");

            foreach (var reason in new[] { KeyboardLeaseEndReason.SystemEscape, KeyboardLeaseEndReason.EmergencyEscape,
                KeyboardLeaseEndReason.HeartbeatExpired, KeyboardLeaseEndReason.QueueOverflow, KeyboardLeaseEndReason.Explicit })
            {
                check(host.TryEnterEditing(out _), "Fixture re-enters for end reason " + reason);
                using (host.BeginInternalWindowTransition())
                {
                    SetForegroundWindow(owner); SetFocus(owner);
                    isolation.EndLease(reason);
                }
                Pump(50);
                check(!host.IsEditing && !isolation.IsLeaseActive && isolation.LastEndReason == reason,
                    "Even an active internal transition never recovers " + reason);
            }

            check(host.TryEnterEditing(out _), "Fixture enters the final session");
            long oldSession = host.EditingSessionVersion;
            var oldTransition = host.BeginInternalWindowTransition();
            host.ExitEditing(restorePreviousForeground: false);
            check(host.TryEnterEditing(out _) && host.EditingSessionVersion != oldSession,
                "Explicit re-entry advances the editing session generation");
            oldTransition.Dispose();
            PostMessageW(host.ControlHandle, WmValidateEditingFocus, unchecked((nuint)oldSession), 0);
            Pump(80);
            check(host.IsEditing && host.IsKeyboardIsolationActive,
                "Old transition completion, foreground validation and lease-end packets cannot end the new session");
            check(failures.Count == 0, "Native focus boundaries report no callback or isolation failures");
        }
        finally
        {
            host.ExitEditing(restorePreviousForeground: false);
            bool restore = OwnsForeground();
            if (owner != 0) DestroyWindow(owner);
            Pump(30);
            if (originalForeground != 0 && IsWindow(originalForeground) && restore)
                SetForegroundWindow(originalForeground);
        }
    }

    private static bool OwnsForeground() => GetWindowThreadProcessId(GetForegroundWindow(), out var pid) != 0 && pid == Environment.ProcessId;

    private static void Pump(int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            while (PeekMessageW(out var message, 0, 0, 0, 1))
            {
                TranslateMessage(ref message);
                DispatchMessageW(ref message);
            }
            Thread.Sleep(2);
        } while (timer.ElapsedMilliseconds < milliseconds);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Message
    { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] private static extern bool PeekMessageW(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessageW(ref Message message);
}
