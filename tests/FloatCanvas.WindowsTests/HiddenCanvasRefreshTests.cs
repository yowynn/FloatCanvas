using System.Runtime.ExceptionServices;
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
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
