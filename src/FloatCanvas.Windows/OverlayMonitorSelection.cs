using L = FloatCanvas.Localization.AppText;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("FloatCanvas.PlatformTests")]

namespace FloatCanvas.Windows;

/// <summary>Chooses active screens before any native windows or raster buffers are allocated.</summary>
internal static class OverlayMonitorSelection
{
    internal static IReadOnlyList<MonitorInfo> Select(IReadOnlyList<MonitorInfo> monitors, OverlayMonitorScope scope)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        var valid = monitors.Where(m => !string.IsNullOrWhiteSpace(m.DeviceName) &&
            m.Bounds.Width > 0 && m.Bounds.Height > 0 && m.WorkArea.Width > 0 && m.WorkArea.Height > 0 &&
            m.DpiX > 0 && m.DpiY > 0).OrderByDescending(m => m.IsPrimary);
        var selected = (scope == OverlayMonitorScope.PrimaryScreen ? valid.Take(1) : valid).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException(L.Get("Overlay.NoAvailableMonitor"));
        return Array.AsReadOnly(selected);
    }
}
