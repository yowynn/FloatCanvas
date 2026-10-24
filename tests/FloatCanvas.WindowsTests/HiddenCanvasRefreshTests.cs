using System.Runtime.ExceptionServices;
using System.Reflection;
using FloatCanvas.Windows;
using SkiaSharp;
using static FloatCanvas.Windows.NativeMethods;

namespace FloatCanvas.WindowsTests;

internal static class HiddenCanvasRefreshTests
{
    internal static void Run(Action<bool, string> check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                nint foreground = GetForegroundWindow();
                int left = GetSystemMetrics(76) + GetSystemMetrics(78) + 4096;
                var monitors = new[]
                {
                    new MonitorInfo("refresh-a", new(left, 0, 80, 80), new(left, 0, 80, 80), 96, 96, true),
                    new MonitorInfo("refresh-b", new(left + 80, 0, 80, 80), new(left + 80, 0, 80, 80), 96, 96, false)
                };
                using var host = new OverlayHost(OverlayMonitorScope.AllScreens, () => monitors, false);
                host.ApplyRenderPreference(false);
                bool hasContent = true;
                int draws = 0;
                host.Draw += surface => { surface.Canvas.Clear(hasContent ? SKColors.Red : SKColors.Transparent); draws++; };
                host.SetVisible(true);
                host.SetVisible(false);
                hasContent = false; draws = 0;
                host.Render();
                check(draws == 0, "hidden edits defer presentation until the canvas is shown");
                bool refreshedWhileHidden = false;
                host.FrameSubmitted += _ => refreshedWhileHidden = host.Diagnostics.All(row => !IsWindowVisible(row.DisplayHandle));
                host.SetVisible(true);
                check(refreshedWhileHidden, "cleared content is submitted before any retained display becomes visible");
                check(draws == 2 && host.Diagnostics.All(row => IsWindowVisible(row.DisplayHandle)), "both displays show the current clear frame");
                check(GetForegroundWindow() == foreground && !host.IsEditing, "refresh never activates or acquires editing input");
                host.SetVisible(false);
                // Probe native input availability without activating or acquiring an input lease.
                // A visible input carrier and hidden display are a valid editing combination.
                const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
                var editing = typeof(OverlayHost).GetField("<IsEditing>k__BackingField", fields)!;
                var available = typeof(OverlayHost).GetMethod("IsCanvasPointerAvailable", fields)!;
                var pairs = ((System.Collections.IEnumerable)typeof(OverlayHost).GetField("_windows", fields)!.GetValue(host)!).Cast<object>().ToArray();
                try
                {
                    editing.SetValue(host, true);
                    foreach (var row in host.Diagnostics) ShowWindow(row.InputHandle, 8);
                    check(pairs.All(pair => (bool)available.Invoke(host, [pair.GetType().GetProperty("Input")!.GetValue(pair)])!),
                        "hidden displays do not block new gestures on their visible editing input carriers");
                    editing.SetValue(host, false);
                    check(pairs.All(pair => !(bool)available.Invoke(host, [pair.GetType().GetProperty("Input")!.GetValue(pair)])!),
                        "pass-through still blocks canvas gesture availability with a visible carrier");
                }
                finally
                {
                    editing.SetValue(host, false);
                    foreach (var row in host.Diagnostics) ShowWindow(row.InputHandle, 0);
                }
                check(GetForegroundWindow() == foreground && host.Diagnostics.All(row => !row.DisplayVisible && !row.InputVisible),
                    "hidden input availability probe preserves foreground and cleans its own surfaces");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
