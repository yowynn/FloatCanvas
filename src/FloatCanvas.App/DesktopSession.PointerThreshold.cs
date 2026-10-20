using FloatCanvas.Windows;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    // Pointer positions are physical pixels; keep the six-DIP click tolerance tied
    // to the monitor where this gesture starts, including negative desktop origins.
    internal static double ClickPlacementThresholdFor(IReadOnlyList<MonitorInfo> monitors, OverlayPoint point)
    {
        var monitor = monitors.FirstOrDefault(value => value.Bounds.Contains(point)) ??
            monitors.FirstOrDefault(value => value.IsPrimary) ?? monitors.FirstOrDefault();
        uint dpi = monitor is null ? 96 : Math.Max(monitor.DpiX, monitor.DpiY);
        return 6d * (dpi == 0 ? 96 : dpi) / 96;
    }
}
