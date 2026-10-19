using FloatCanvas.Core;
using SkiaSharp;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private void DrawBillboardGuides(SKCanvas canvas)
    {
        if (_canvasCalibration.IsActive) return;
        using var line = new SKPaint { Color = new(210,139,63), StrokeWidth = 1.5f, IsAntialias = true, Style = SKPaintStyle.Stroke };
        if ((_editor.VisibleBillboardDepthLine ?? _inlineBillboardDepth) is { } depth && depth.TryClip(Active.Document.Viewport, out var first, out var last))
        {
            using var dash = SKPathEffect.CreateDash([8, 5], 0);
            line.PathEffect = dash;
            canvas.DrawLine((float)first.X, (float)first.Y, (float)last.X, (float)last.Y, line);
            line.PathEffect = null;
        }
        var guide = _editor.BillboardDrawingGuide ??
            ((_inlineText is not null || _editor.ShouldShowSelection(_host.InteractionModifiers)) ? _editor.BillboardSelectionGuide : null);
        if (guide is not { } points) return;
        canvas.DrawLine((float)points.Center.X, (float)points.Center.Y, (float)points.Anchor.X, (float)points.Anchor.Y, line);
        using var ground = new SKPaint { Color = line.Color, IsAntialias = true };
        canvas.DrawCircle((float)points.Anchor.X, (float)points.Anchor.Y, 3.5f, ground);
        using var center = new SKPaint { Color = new(35,131,226), StrokeWidth = 1.8f, Style = SKPaintStyle.Stroke, IsAntialias = true };
        canvas.DrawCircle((float)points.Center.X, (float)points.Center.Y, 6, center);
    }
}
