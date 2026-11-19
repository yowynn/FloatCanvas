using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private PixelBounds? _queuedBrushTipBounds;

    private readonly record struct BrushTip(ElementKind Kind, ElementStyle Style, Projective2D Placement, PixelBounds Bounds);

    private BrushTip? CurrentBrushTip()
    {
        var tool = _editor.InteractionTool;
        if (tool is not (EditorTool.Freehand or EditorTool.Highlighter) || !_host.IsEditing || !_host.IsVisible ||
            !_hasEditableCanvasOccurrence || _editor.CanvasContentHidden || _editor.HasPointerGesture ||
            _canvasNavigationSpaceHeld || _canvasCalibration.IsActive || _inlineText is not null || _eyedropper is not null ||
            _cleanCaptureDepth != 0 || DocumentOperationBusy || _host.IsReceivingDrop ||
            _projectionHoverPoint is not { } pointer || _projectionHoverSession != _host.EditingSessionVersion ||
            !Active.CanInsertInEditScope || _editor.CanvasDisplayOpacity <= 0) return null;

        var point = new PointD(pointer.X - _editor.CanvasOrigin.X, pointer.Y - _editor.CanvasOrigin.Y);
        var viewport = _editor.CanvasViewport;
        var modifiers = _host.InteractionModifiers;
        if (!viewport.Contains(point) || (modifiers & OverlayModifiers.Alt) != 0 ||
            _editor.ShouldShowSelection(modifiers) && _editor.IsSelectionFrameHit(point, includeInterior: true)) return null;

        var mapping = _editor.CurrentCanvasMapping;
        Projective2D placement;
        if (_editor.BillboardMode)
        {
            var scale = _editor.UsesScreenBillboardCreation ? 1 : _editor.ActiveBillboardDepthLine?.Scale;
            if (scale is not { } depthScale) return null;
            placement = Projective2D.FromAffine(new(depthScale, 0, 0, depthScale, point.X, point.Y));
        }
        else
        {
            if (!mapping.TryToPlane(point, out var plane)) return null;
            placement = mapping.PlaneToScreen.Compose(Projective2D.FromAffine(Affine2D.Translation(plane.X, plane.Y)));
        }

        var style = _editor.Style;
        var kind = tool == EditorTool.Highlighter ? ElementKind.Highlighter : ElementKind.Freehand;
        if (kind == ElementKind.Highlighter && style.StrokeWidth < 16)
        {
            style = style.Clone();
            style.StrokeWidth = 16;
        }
        if (style.StrokeWidth <= 0 || style.Opacity <= 0 || style.Foreground >> 24 == 0) return null;
        double radius = style.StrokeWidth / 2;
        var extent = kind == ElementKind.Highlighter && style.HighlighterTip == HighlighterTip.Flat
            ? ElementGeometry.FlatHighlighterExtent(style) : new PointD(radius, radius);
        if (!placement.TryTransformBounds(new(-extent.X, -extent.Y, extent.X * 2, extent.Y * 2), out var bounds)) return null;
        bounds = bounds.Inflate(2).Intersect(viewport);
        if (bounds.IsEmpty) return null;
        var origin = _editor.CanvasOrigin;
        int left = (int)Math.Floor(bounds.Left + origin.X), top = (int)Math.Floor(bounds.Top + origin.Y);
        int right = (int)Math.Ceiling(bounds.Right + origin.X), bottom = (int)Math.Ceiling(bounds.Bottom + origin.Y);
        return new(kind, style, placement, new(left, top, right - left, bottom - top));
    }

    private void UpdateBrushTipDamage()
    {
        var next = CurrentBrushTip()?.Bounds;
        if (next == _queuedBrushTipBounds) return;
        var old = _queuedBrushTipBounds;
        _queuedBrushTipBounds = next;
        if (old is { } prior && next is { } current) QueueCanvasPresentation([prior, current]);
        else if ((old ?? next) is { } damage) QueueCanvasPresentation([damage]);
    }

    private void DrawBrushTip(SKCanvas canvas)
    {
        if (CurrentBrushTip() is not { } tip) return;
        var viewport = _editor.CanvasViewport;
        int saved = canvas.Save();
        try
        {
            canvas.ClipRect(new((float)viewport.Left, (float)viewport.Top, (float)viewport.Right, (float)viewport.Bottom));
            SceneRenderer.DrawStrokeTip(canvas, tip.Kind, tip.Style, tip.Placement, _editor.CanvasDisplayOpacity);
        }
        finally { canvas.RestoreToCount(saved); }
    }
}
