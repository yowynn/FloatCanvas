using FloatCanvas.Core;
using SkiaSharp;

namespace FloatCanvas.Rendering;

public sealed partial class SceneRenderer
{
    internal bool UsesDepthComposition(SceneDocument document) => CanvasDepthOrder.IsEnabled(document) &&
        (HasVisibleBillboard(document) || LiveEffectContext is { } context && CanvasDepthOrder.IsEnabled(context) && HasVisibleBillboard(context));

    private static bool HasVisibleBillboard(SceneDocument document)
    {
        CanvasMapping? mapping = null;
        foreach (var item in document.Elements)
            if (item is { IsBillboard: true, Visible: true } && item.Style.Opacity > 0 &&
                CanvasDepthOrder.TryAnchorDepth(mapping ??= CanvasMapping.Create(document), item.BillboardAnchor, out _)) return true;
        return false;
    }

    /// <summary>
    /// A finite painter sequence: far plane strip, billboards at its near boundary, then the next strip.
    /// Live filters read only the completed prefix, so intersecting filters cannot recursively sample each other.
    /// One working image is retained, rather than one full-size image for every depth band.
    /// </summary>
    private void DrawDepthScene(SKCanvas canvas, SceneDocument document, AssetLibrary assets,
        bool includeBackground, bool includeCanvasBackground, bool includeGrid)
    {
        DrawBackground(canvas, document, assets, includeBackground, includeCanvasBackground: false);
        double opacity = Math.Clamp(document.LayerOpacity, 0, 1);
        if (!(opacity > 0)) { DrawPlaneColor(canvas, document, includeCanvasBackground); return; }
        var source = LiveEffectContext is { } context && CanvasDepthOrder.IsEnabled(context) ? context : document;
        var mapping = ReferenceEquals(source, document) ? _mapping! : CanvasMapping.Create(source);
        var viewport = source.Viewport.Normalize();
        // Ordinary depth composition has no outside-pixel dependencies. Keep the document's mapping
        // and grid extent intact, but rasterize only the current output's pixels. Filters and isolated
        // selection sampling retain the full source viewport, including their cross-tile dependencies.
        bool localOutput = ReferenceEquals(source, document) && !HasLiveEffects(source);
        if (localOutput) viewport = DepthOutputBounds(canvas, viewport);
        if (viewport.IsEmpty) return;
        int width = checked((int)Math.Ceiling(viewport.Width)), height = checked((int)Math.Ceiling(viewport.Height));
        if (width <= 0 || height <= 0 || (long)width * height > int.MaxValue / 4)
            throw new InvalidOperationException("The depth composition viewport is too large.");
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var imageBounds = new RectD(viewport.X, viewport.Y, width, height);
        using var workingBuffer = RentDepthSurface(canvas, info, "Cannot allocate the depth composition surface.");
        var working = workingBuffer.Surface;
        working.Canvas.Clear(SKColors.Transparent); working.Canvas.Translate(-(float)viewport.X, -(float)viewport.Y);
        // Transparent exports omit the canvas color from their output, while live filters still
        // sample the original colored canvas, just as they sample an omitted desktop background.
        bool separateSampling = !ReferenceEquals(source, document) || !includeCanvasBackground &&
            (source.Canvas.BackgroundColor >> 24) != 0 && HasLiveEffects(source);
        using var selectedBuffer = !separateSampling ? null : RentDepthSurface(canvas, info, "Cannot allocate the selected depth composition surface.");
        var selected = selectedBuffer?.Surface;
        if (selected is not null) { selected.Canvas.Clear(SKColors.Transparent); selected.Canvas.Translate(-(float)viewport.X, -(float)viewport.Y); }
        var chosen = selected is null ? null : document.Elements.Select(e => e.Id).ToHashSet();
        // Ordinary plane ink is independent of the depth bands. Rasterize it once and use disjoint
        // clips of that one image, rather than rerasterizing every path/text/image for every billboard.
        // A planar live filter must retain the finite event sequence because its lower source changes.
        using var planeBuffer = selected is null && (mapping.PlaneToScreen.M31 != 0 || mapping.PlaneToScreen.M32 != 0) &&
            !source.Elements.Any(e => e.Visible && e.Style.Opacity > 0 && !e.IsBillboard && ElementGeometry.IsLiveEffect(e.Kind))
            ? RentDepthSurface(canvas, info, "Cannot allocate the shared plane composition surface.") : null;
        using var plane = planeBuffer is null ? null : BuildDepthPlane(planeBuffer.Surface, source, assets,
            imageBounds, includeCanvasBackground, includeGrid);
        try
        {
            foreach (var entry in DepthEvents(source, mapping))
            {
                if (entry.Index < 0)
                {
                    if (plane is not null) DrawDepthPlaneImage(working.Canvas, plane, imageBounds, entry.Clip);
                    else DrawEventBase(working.Canvas, source, entry.Clip, selected is not null || includeCanvasBackground, includeGrid);
                    if (selected is not null) DrawEventBase(selected.Canvas, document, entry.Clip, includeCanvasBackground, includeGrid);
                    continue;
                }
                var element = source.Elements[entry.Index];
                if (plane is not null && !element.IsBillboard) continue;
                if (ElementGeometry.IsLiveEffect(element.Kind))
                {
                    // Images belong to this single event. A planar filter may occur in several disjoint strips.
                    ClearLiveEffectImages();
                    using var prefix = working.Snapshot();
                    _depthSamplePainter = target =>
                    {
                        DrawBackground(target, source, assets, includeCanvasBackground: false, sampling: true);
                        target.DrawImage(prefix, ToRect(imageBounds), Nearest);
                    };
                    DrawDepthElement(working.Canvas, source, assets, entry);
                    if (selected is not null && chosen!.Contains(element.Id)) DrawDepthElement(selected.Canvas, source, assets, entry);
                    _depthSamplePainter = null;
                    ClearLiveEffectImages();
                }
                else
                {
                    DrawDepthElement(working.Canvas, source, assets, entry);
                    if (selected is not null && chosen!.Contains(element.Id)) DrawDepthElement(selected.Canvas, source, assets, entry);
                }
            }
            using var image = (selected ?? working).Snapshot();
            if (opacity >= 1) canvas.DrawImage(image, ToRect(imageBounds), Nearest);
            else
            {
                // Linear interpolation of the raw depth scene and the annotation-free plane color.
                // This applies annotation opacity once while keeping even a translucent canvas color unchanged.
                int saved = canvas.SaveLayer();
                try
                {
                    DrawPlaneColor(canvas, document, includeCanvasBackground, 1 - opacity);
                    using var alpha = new SKPaint { Color = SKColors.White.WithAlpha(Alpha(opacity)), BlendMode = SKBlendMode.Plus };
                    canvas.DrawImage(image, ToRect(imageBounds), Nearest, alpha);
                }
                finally { canvas.RestoreToCount(saved); }
            }
        }
        finally { _depthSamplePainter = null; ClearLiveEffectImages(); }
    }

    private static RectD DepthOutputBounds(SKCanvas canvas, RectD viewport)
    {
        var matrix = canvas.TotalMatrix;
        // Other transforms keep their existing raster phase and resolution. Desktop outputs use
        // integer translations, so their device clip maps exactly to document pixel coordinates.
        if (matrix.ScaleX != 1 || matrix.ScaleY != 1 || matrix.SkewX != 0 || matrix.SkewY != 0 ||
            matrix.Persp0 != 0 || matrix.Persp1 != 0 || matrix.Persp2 != 1 ||
            matrix.TransX != Math.Truncate(matrix.TransX) || matrix.TransY != Math.Truncate(matrix.TransY) ||
            viewport.X != Math.Truncate(viewport.X) || viewport.Y != Math.Truncate(viewport.Y)) return viewport;
        var clip = canvas.DeviceClipBounds;
        var visible = viewport.Intersect(new(clip.Left - matrix.TransX, clip.Top - matrix.TransY, clip.Width, clip.Height));
        if (visible.IsEmpty) return default;
        double left = Math.Floor(visible.Left), top = Math.Floor(visible.Top);
        return new(left, top, Math.Ceiling(visible.Right) - left, Math.Ceiling(visible.Bottom) - top);
    }

    private SKImage BuildDepthPlane(SKSurface surface, SceneDocument document, AssetLibrary assets,
        RectD bounds, bool background, bool grid)
    {
        surface.Canvas.Clear(SKColors.Transparent); surface.Canvas.Translate(-(float)bounds.X, -(float)bounds.Y);
        surface.Canvas.ClipRect(ToRect(document.Viewport));
        DrawPlaneColor(surface.Canvas, document, background);
        if (grid) DrawGrid(surface.Canvas, document);
        var transform = TransformFor(document);
        foreach (var element in document.Elements)
            if (!element.IsBillboard && element.Visible && element.Style.Opacity > 0) DrawElement(surface.Canvas, element, assets, transform, document);
        return surface.Snapshot();
    }

    private static void DrawDepthPlaneImage(SKCanvas canvas, SKImage image, RectD bounds, IReadOnlyList<PointD>? polygon)
    {
        int saved = canvas.Save();
        try { if (ClipDepthEvent(canvas, polygon)) canvas.DrawImage(image, ToRect(bounds), Nearest); }
        finally { canvas.RestoreToCount(saved); }
    }

    private static void DrawPlaneColor(SKCanvas canvas, SceneDocument document, bool include, double opacity = 1)
    {
        if (!include || (document.Canvas.BackgroundColor >> 24) == 0) return;
        var color = new SKColor(document.Canvas.BackgroundColor);
        using var paint = new SKPaint { Color = color.WithAlpha((byte)Math.Round(color.Alpha * opacity)) };
        canvas.DrawRect(ToRect(document.Viewport), paint);
    }

    private void DrawEventBase(SKCanvas canvas, SceneDocument document, IReadOnlyList<PointD>? polygon, bool background, bool grid)
    {
        int saved = canvas.Save();
        try
        {
            if (!ClipDepthEvent(canvas, polygon)) return;
            DrawPlaneColor(canvas, document, background);
            if (grid) DrawGrid(canvas, document);
        }
        finally { canvas.RestoreToCount(saved); }
    }

    private void DrawDepthElement(SKCanvas canvas, SceneDocument document, AssetLibrary assets, DepthEvent entry)
    {
        int saved = canvas.Save();
        try
        {
            if (!ClipDepthEvent(canvas, entry.Clip)) return;
            var element = document.Elements[entry.Index];
            if (ElementGeometry.IsLiveEffect(element.Kind)) DrawLivePixelate(canvas, document.Elements, entry.Index, assets);
            else DrawElement(canvas, element, assets, TransformFor(document), document);
        }
        finally { canvas.RestoreToCount(saved); }
    }

    private static bool ClipDepthEvent(SKCanvas canvas, IReadOnlyList<PointD>? polygon)
    {
        if (polygon is null) return true;
        if (polygon.Count < 3) return false;
        using var builder = new SKPathBuilder();
        builder.MoveTo(ToPoint(polygon[0]));
        foreach (var point in polygon.Skip(1)) builder.LineTo(ToPoint(point));
        builder.Close(); using var path = builder.Detach();
        // Complementary non-AA clips assign every physical sample to one strip, with no double alpha seam.
        canvas.ClipPath(path, antialias: false);
        return !canvas.IsClipEmpty;
    }

    private static IEnumerable<DepthEvent> DepthEvents(SceneDocument document, CanvasMapping mapping)
    {
        var projection = mapping.PlaneToScreen;
        if (projection.M31 == 0 && projection.M32 == 0)
        {
            yield return new(-1, null);
            for (int index = 0; index < document.Elements.Count; index++)
                if (document.Elements[index].Visible && document.Elements[index].Style.Opacity > 0) yield return new(index, null);
            yield break;
        }
        var billboards = Enumerable.Range(0, document.Elements.Count)
            .Where(i => document.Elements[i] is { IsBillboard: true, Visible: true } e && e.Style.Opacity > 0 && CanvasDepthOrder.TryAnchorDepth(mapping, e.BillboardAnchor, out _))
            .OrderByDescending(i => projection.Denominator(document.Elements[i].BillboardAnchor)).ThenBy(i => i).ToArray();
        double far = double.PositiveInfinity;
        foreach (var group in billboards.GroupBy(i => projection.Denominator(document.Elements[i].BillboardAnchor)))
        {
            foreach (var entry in PlaneStrip(group.Key, far)) yield return entry;
            foreach (int index in group) yield return new(index, null);
            far = group.Key;
        }
        foreach (var entry in PlaneStrip(0, far)) yield return entry;

        IEnumerable<DepthEvent> PlaneStrip(double near, double farDepth)
        {
            var polygon = CanvasDepthOrder.ClipPlaneDepthRange(mapping, document.Viewport, near, farDepth);
            if (polygon.Count < 3) yield break;
            yield return new(-1, polygon);
            for (int index = 0; index < document.Elements.Count; index++)
                if (document.Elements[index] is { IsBillboard: false, Visible: true } e && e.Style.Opacity > 0) yield return new(index, polygon);
        }
    }

    private readonly record struct DepthEvent(int Index, IReadOnlyList<PointD>? Clip);
}
