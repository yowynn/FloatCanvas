using FloatCanvas.Core;
using SkiaSharp;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private void DrawBillboardGuides(SKCanvas canvas)
    {
        if (_canvasCalibration.IsActive) return;
        using var line = new SKPaint { Color = new(210,139,63), StrokeWidth = 1.5f, IsAntialias = true, Style = SKPaintStyle.Stroke };
        if (_editor.PendingBillboardOutline is { Count: 4 } outline)
        {
            using var path = new SKPathBuilder(); path.MoveTo((float)outline[0].X, (float)outline[0].Y);
            foreach (var corner in outline.Skip(1)) path.LineTo((float)corner.X, (float)corner.Y);
            path.Close(); using var shape = path.Detach(); canvas.DrawPath(shape, line);
        }
        var rawDraft = _editor.ScreenBillboardPending ? _editor.ScreenBillboardDraft :
            _inlineText is { ScreenDraft: { } textDraft, ResolvedScreenDraft: null } ? textDraft : null;
        if (rawDraft is not null)
        {
            var box = ElementGeometry.LocalToPlane(rawDraft).TransformBounds(ElementGeometry.LocalBounds(rawDraft));
            using var pendingDash = SKPathEffect.CreateDash([5, 4], 0);
            line.PathEffect = pendingDash;
            canvas.DrawRect((float)box.X, (float)box.Y, (float)box.Width, (float)box.Height, line);
            line.PathEffect = null;
        }
        if ((_editor.VisibleBillboardDepthLine ?? _inlineBillboardDepth) is { } depth && depth.TryClip(Active.Document.Viewport, out var first, out var last))
        {
            using var dash = SKPathEffect.CreateDash([8, 5], 0);
            line.PathEffect = dash;
            canvas.DrawLine((float)first.X, (float)first.Y, (float)last.X, (float)last.Y, line);
            line.PathEffect = null;
        }
        var guide = _editor.BillboardDrawingGuide ??
            ((_inlineText is not null || _editor.ShouldShowSelection(_host.InteractionModifiers)) ? _editor.BillboardSelectionGuide : null);
        if (guide is null && _inlineText?.ResolvedScreenDraft is { } placedText)
        {
            var mapping = _editor.CurrentCanvasMapping;
            guide = (mapping.ElementToScreen(placedText).TransformPoint(ElementGeometry.LocalBounds(placedText).Center),
                mapping.ToScreen(placedText.BillboardAnchor));
        }
        if (guide is null && _inlineText?.ScreenDraft is { } inlineDraft)
        {
            var box = ElementGeometry.LocalToPlane(inlineDraft).TransformBounds(ElementGeometry.LocalBounds(inlineDraft));
            guide = (box.Center, new(box.Center.X, box.Bottom));
        }
        if (guide is not { } points) return;
        canvas.DrawLine((float)points.Center.X, (float)points.Center.Y, (float)points.Anchor.X, (float)points.Anchor.Y, line);
        using var ground = new SKPaint { Color = line.Color, IsAntialias = true };
        canvas.DrawCircle((float)points.Anchor.X, (float)points.Anchor.Y, 3.5f, ground);
        using var center = new SKPaint { Color = new(35,131,226), StrokeWidth = 1.8f, Style = SKPaintStyle.Stroke, IsAntialias = true };
        canvas.DrawCircle((float)points.Center.X, (float)points.Center.Y, 6, center);
    }
}
