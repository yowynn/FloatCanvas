using FloatCanvas.Windows;
using SkiaSharp;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private bool HasCanvasViewingBadge => _host.IsEditing && _host.IsVisible && _editor.Tool != EditorTool.PointerPresentation;
    private PixelBounds CanvasViewingBadgeBounds
    {
        get
        {
            var monitor = _host.PrimaryMonitor; double dpi = monitor.DpiX / 96d;
            return new(monitor.WorkArea.Left + (int)Math.Round(12 * dpi), monitor.WorkArea.Bottom - (int)Math.Round(40 * dpi),
                (int)Math.Ceiling(88 * dpi), (int)Math.Ceiling(28 * dpi));
        }
    }
    private void DrawCanvasViewingBadge(SKCanvas canvas)
    {
        if (!HasCanvasViewingBadge) return;
        var bounds = CanvasViewingBadgeBounds; float dpi = _host.PrimaryMonitor.DpiX / 96f;
        using var paint = new SKPaint { Color = new(24, 28, 36, 200), IsAntialias = true };
        canvas.DrawRoundRect(new(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom), 5 * dpi, 5 * dpi, paint);
        using var font = new SKFont(SKTypeface.Default, 12 * dpi);
        paint.Color = new(245, 247, 250);
        float baseline = bounds.Top + bounds.Height / 2f - (font.Metrics.Ascent + font.Metrics.Descent) / 2;
        canvas.DrawText($"{EditingCanvasView.Zoom * 100:0.##}%", bounds.Left + 8 * dpi, baseline, SKTextAlign.Left, font, paint);
    }
}
