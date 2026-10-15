using FloatCanvas.Core;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

/// <summary>Changes only the physical-screen origin of a frozen document, never its pixel geometry.</summary>
internal static class CaptureViewportPlacement
{
    internal static PointD ResolveOrigin(RectD viewport, PointD currentOrigin, IReadOnlyList<MonitorInfo> activeMonitors)
    {
        ArgumentNullException.ThrowIfNull(activeMonitors);
        if (activeMonitors.Count == 0) throw new ArgumentException("An active screen is required.", nameof(activeMonitors));
        var mapped = Affine2D.Translation(currentOrigin.X, currentOrigin.Y).TransformBounds(viewport);
        foreach (var monitor in activeMonitors)
        {
            var bounds = monitor.Bounds;
            // Preserve any positive-area visibility, including a crop spanning several screens.
            if (!mapped.Intersect(new(bounds.Left, bounds.Top, bounds.Width, bounds.Height)).IsEmpty) return currentOrigin;
        }
        var primary = activeMonitors.FirstOrDefault(m => m.IsPrimary) ?? activeMonitors[0];
        return CenterOrigin(viewport, primary.Bounds);
    }

    internal static PointD CenterOrigin(RectD viewport, PixelBounds screen) =>
        new(screen.Left + screen.Width / 2d - viewport.Center.X, screen.Top + screen.Height / 2d - viewport.Center.Y);
}
