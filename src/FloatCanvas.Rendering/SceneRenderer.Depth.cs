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
        double opacity = Math.Clamp(document.LayerOpacity, 0, 1);
        bool photograph = includeBackground && document.Background is { Visible: true };
        DrawBackground(canvas, document, assets, includeBackground, includeCanvasBackground && photograph, canvasOpacity: opacity);
        if (!(opacity > 0)) return;
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
        using var boundarySamples = new DepthBoundarySamples(source, mapping, imageBounds, localOutput);
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
        bool sourceColor = (selected is not null || includeCanvasBackground) &&
            !(includeBackground && source.Background is { Visible: true });
        // Ordinary plane ink is independent of the depth bands. Rasterize it once and use disjoint
        // clips of that one image, rather than rerasterizing every path/text/image for every billboard.
        // A planar live filter must retain the finite event sequence because its lower source changes.
        using var planeBuffer = selected is null && (mapping.PlaneToScreen.M31 != 0 || mapping.PlaneToScreen.M32 != 0) &&
            !source.Elements.Any(e => e.Visible && e.Style.Opacity > 0 && !e.IsBillboard && ElementGeometry.IsLiveEffect(e.Kind))
            ? RentDepthSurface(canvas, info, "Cannot allocate the shared plane composition surface.") : null;
        using var plane = planeBuffer is null ? null : BuildDepthPlane(planeBuffer.Surface, source, assets,
            imageBounds, sourceColor, includeGrid);
        try
        {
            foreach (var entry in DepthEvents(source, mapping, boundarySamples))
            {
                if (entry.Index < 0)
                {
                    // Color and grid are the lowest ink on the plane. Their disjoint depth
                    // strips can cover the portion of a billboard that lies below the plane.
                    if (plane is not null && !entry.Equality) DrawDepthPlaneImage(working.Canvas, plane, imageBounds, entry);
                    else DrawDepthGround(working.Canvas, source, entry, sourceColor, includeGrid);
                    if (selected is not null)
                        DrawDepthGround(selected.Canvas, document, entry, includeCanvasBackground && !photograph, includeGrid);
                    continue;
                }
                var element = source.Elements[entry.Index];
                if (plane is not null && !element.IsBillboard && !entry.Equality) continue;
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
                using var alpha = new SKPaint { Color = SKColors.White.WithAlpha(Alpha(opacity)) };
                canvas.DrawImage(image, ToRect(imageBounds), Nearest, alpha);
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
        RectD bounds, bool includeCanvasBackground, bool includeGrid)
    {
        surface.Canvas.Clear(SKColors.Transparent); surface.Canvas.Translate(-(float)bounds.X, -(float)bounds.Y);
        surface.Canvas.ClipRect(ToRect(document.Viewport));
        DrawPlaneColor(surface.Canvas, document, includeCanvasBackground);
        if (includeGrid) DrawGrid(surface.Canvas, document);
        var transform = TransformFor(document);
        foreach (var element in document.Elements)
            if (!element.IsBillboard && element.Visible && element.Style.Opacity > 0) DrawElement(surface.Canvas, element, assets, transform, document);
        return surface.Snapshot();
    }

    private void DrawDepthGround(SKCanvas canvas, SceneDocument document, DepthEvent entry,
        bool includeCanvasBackground, bool includeGrid)
    {
        DrawDepthEvent(canvas, entry, () =>
        {
            DrawPlaneColor(canvas, document, includeCanvasBackground);
            if (includeGrid) DrawGrid(canvas, document);
        });
    }

    private static void DrawDepthPlaneImage(SKCanvas canvas, SKImage image, RectD bounds, DepthEvent entry)
        => DrawDepthEvent(canvas, entry, () => canvas.DrawImage(image, ToRect(bounds), Nearest));

    internal void DrawPlaneColor(SKCanvas canvas, SceneDocument document, bool include, double opacity = 1)
    {
        if (!include || _renderSpace == SceneRenderSpace.PlaneContent || (document.Canvas.BackgroundColor >> 24) == 0) return;
        int saved = canvas.Save();
        try
        {
            if (!ClipSafePlane(canvas, document)) return;
            var color = new SKColor(document.Canvas.BackgroundColor);
            using var paint = new SKPaint { Color = color.WithAlpha((byte)Math.Round(color.Alpha * opacity)) };
            canvas.DrawRect(ToRect(document.Viewport), paint);
        }
        finally { canvas.RestoreToCount(saved); }
    }

    private void DrawDepthElement(SKCanvas canvas, SceneDocument document, AssetLibrary assets, DepthEvent entry)
    {
        bool knownHardClip = canvas.IsClipRect;
        var element = document.Elements[entry.Index];
        DrawDepthEvent(canvas, entry, () =>
        {
            if (ElementGeometry.IsLiveEffect(element.Kind)) DrawLivePixelate(canvas, document.Elements, entry.Index, assets);
            else DrawElement(canvas, element, assets, TransformFor(document), document, knownHardClip);
        });
    }

    private static void DrawDepthEvent(SKCanvas canvas, DepthEvent entry, Action draw)
    {
        int saved = canvas.Save();
        try { if (ClipDepthEvent(canvas, entry)) draw(); }
        finally { canvas.RestoreToCount(saved); }
        if (entry.CorrectionSamples is not { IsEmpty: false } corrections) return;
        saved = canvas.Save();
        try
        {
            // Finish this event's disjoint boundary samples before advancing to the next
            // layer. A live filter therefore reuses the same frozen lower-prefix image.
            canvas.ClipPath(corrections, antialias: false);
            if (!canvas.IsClipEmpty) draw();
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

    private static bool ClipDepthEvent(SKCanvas canvas, DepthEvent entry)
    {
        if (!ClipDepthEvent(canvas, entry.Clip)) return false;
        if (entry.Samples is not null)
            canvas.ClipPath(entry.Samples, entry.Equality ? SKClipOperation.Intersect : SKClipOperation.Difference, false);
        return !canvas.IsClipEmpty;
    }

    private static IEnumerable<DepthEvent> DepthEvents(SceneDocument document, CanvasMapping mapping, DepthBoundarySamples boundaries)
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
            // A plane/billboard equality is a real sample, not a floating-point tolerance.
            // Exclude those samples from both adjacent strips, then interleave their plane
            // ink with the equal-depth billboards by the persisted layer index.
            var equal = boundaries.ForDepth(group.Key);
            // At equal depth the ground is below every ordinary layer, including billboards.
            if (equal is not null && !equal.IsEmpty) yield return new(-1, null, equal, true);
            int first = 0;
            foreach (int index in group)
            {
                foreach (var entry in EqualityPlane(first, index, equal)) yield return entry;
                yield return new(index, null);
                first = index + 1;
            }
            foreach (var entry in EqualityPlane(first, document.Elements.Count, equal)) yield return entry;
            far = group.Key;
        }
        foreach (var entry in PlaneStrip(0, far)) yield return entry;

        IEnumerable<DepthEvent> PlaneStrip(double near, double farDepth)
        {
            var polygon = CanvasDepthOrder.ClipPlaneDepthRange(mapping, document.Viewport, near, farDepth);
            if (polygon.Count < 3) yield break;
            using var correctionSamples = boundaries.ForRange(near, farDepth);
            yield return new(-1, polygon, boundaries.All, CorrectionSamples: correctionSamples);
            for (int index = 0; index < document.Elements.Count; index++)
                if (document.Elements[index] is { IsBillboard: false, Visible: true } e && e.Style.Opacity > 0)
                    yield return new(index, polygon, boundaries.All, CorrectionSamples: correctionSamples);
        }

        IEnumerable<DepthEvent> EqualityPlane(int first, int end, SKPath? samples)
        {
            if (samples is null || samples.IsEmpty) yield break;
            for (int i = first; i < end; i++)
                if (document.Elements[i] is { IsBillboard: false, Visible: true } e && e.Style.Opacity > 0)
                    yield return new(i, null, samples, true);
        }
    }

    private readonly record struct DepthEvent(int Index, IReadOnlyList<PointD>? Clip, SKPath? Samples = null,
        bool Equality = false, SKPath? CorrectionSamples = null);

    private sealed class DepthBoundarySamples : IDisposable
    {
        private readonly Dictionary<double, SKPath> samples = [];
        private readonly List<(int Row, int Column, double Depth)> corrections = [];
        private readonly RectD bounds;
        internal SKPath All { get; }
        internal SKPath? ForDepth(double depth) => samples.GetValueOrDefault(depth);

        internal DepthBoundarySamples(SceneDocument document, CanvasMapping mapping, RectD bounds, bool correctRasterOwnership)
        {
            this.bounds = bounds;
            if (mapping.PlaneToScreen.M31 == 0 && mapping.PlaneToScreen.M32 == 0) { All = new(); return; }
            var inverse = mapping.ScreenToPlane;
            var depths = document.Elements.Where(e => e.IsBillboard && e.Visible && e.Style.Opacity > 0)
                .Select(e => mapping.PlaneToScreen.Denominator(e.BillboardAnchor)).Where(d => double.IsFinite(d) && d > 0).Distinct().ToArray();
            // Filters and isolated contexts already share one full-source raster phase.
            // Preserve their exact equality masks and lower-prefix sampling unchanged.
            if (!correctRasterOwnership) { All = BuildEqualitySamples(mapping, depths); return; }
            var pixels = new Dictionary<long, double>();
            int width = (int)bounds.Width;
            double gradient = Math.Sqrt(inverse.M31 * inverse.M31 + inverse.M32 * inverse.M32);
            foreach (double depth in depths)
            {
                for (int row = 0; row < (int)bounds.Height; row++)
                {
                    double y = bounds.Top + row + .5;
                    int first = 0, last = width - 1;
                    if (inverse.M31 == 0)
                    {
                        if (Math.Abs(inverse.Denominator(new(bounds.Left + .5, y)) - 1 / depth) > gradient) continue;
                    }
                    else
                    {
                        // A one-pixel perpendicular band covers the hard-clip quantization error.
                        // Near-horizontal boundaries may cover an entire row, rather than a few columns.
                        double center = (1 / depth - inverse.M32 * y - inverse.M33) / inverse.M31 - bounds.Left - .5;
                        double radius = gradient / Math.Abs(inverse.M31);
                        double left = Math.Floor(center - radius), right = Math.Ceiling(center + radius);
                        if (double.IsNaN(left) || double.IsNaN(right) || right < 0 || left >= width) continue;
                        first = (int)Math.Clamp(left, 0, width - 1); last = (int)Math.Clamp(right, 0, width - 1);
                    }
                    for (int column = first; column <= last; column++)
                        if (CanvasDepthOrder.TryPlaneDepth(mapping, new(bounds.Left + column + .5, y), out double actual))
                            pixels.TryAdd((long)row * width + column, actual);
                }
            }
            foreach (var pixel in pixels.OrderBy(pair => pair.Key))
                corrections.Add(((int)(pixel.Key / width), (int)(pixel.Key % width), pixel.Value));
            using var all = new SKPathBuilder();
            AddRuns(all, _ => true);
            All = all.Detach();
            foreach (double depth in depths)
            {
                using var builder = new SKPathBuilder();
                AddRuns(builder, actual => actual == depth);
                samples[depth] = builder.Detach();
            }
        }

        internal SKPath? ForRange(double near, double far)
        {
            if (corrections.Count == 0) return null;
            using var ownedBuilder = new SKPathBuilder();
            // The band only corrects raster ownership. It never turns nearby depths into ties.
            AddRuns(ownedBuilder, actual => actual > near && actual < far);
            return ownedBuilder.Detach();
        }

        private void AddRuns(SKPathBuilder builder, Func<double, bool> include)
        {
            int row = -1, first = 0, end = 0;
            int pendingTop = -1, pendingBottom = 0, pendingLeft = 0, pendingRight = 0;
            foreach (var pixel in corrections)
            {
                if (!include(pixel.Depth)) { Flush(); continue; }
                if (pixel.Row == row && pixel.Column == end) end++;
                else { Flush(); row = pixel.Row; first = pixel.Column; end = first + 1; }
            }
            Flush();
            EmitPending();
            void Flush()
            {
                if (row < 0) return;
                if (pendingTop >= 0 && pendingBottom == row && pendingLeft == first && pendingRight == end) pendingBottom++;
                else
                {
                    EmitPending();
                    pendingTop = row; pendingBottom = row + 1; pendingLeft = first; pendingRight = end;
                }
                row = -1;
            }
            void EmitPending()
            {
                if (pendingTop < 0) return;
                builder.AddRect(new((float)(bounds.Left + pendingLeft), (float)(bounds.Top + pendingTop),
                    (float)(bounds.Left + pendingRight), (float)(bounds.Top + pendingBottom)));
                pendingTop = -1;
            }
        }

        private SKPath BuildEqualitySamples(CanvasMapping mapping, IReadOnlyList<double> depths)
        {
            using var all = new SKPathBuilder();
            var inverse = mapping.ScreenToPlane;
            foreach (double depth in depths)
            {
                using var builder = new SKPathBuilder();
                for (int row = 0; row < (int)bounds.Height; row++)
                {
                    double y = bounds.Top + row + .5;
                    if (inverse.M31 == 0)
                    {
                        if (Equal(bounds.Left + .5, y)) Add(0, (int)bounds.Width, row);
                    }
                    else
                    {
                        double x = (1 / depth - inverse.M32 * y - inverse.M33) / inverse.M31;
                        if (!double.IsFinite(x) || x < bounds.Left - 4 || x > bounds.Right + 4) continue;
                        int center = (int)Math.Floor(x - bounds.Left - .5);
                        for (int column = Math.Max(0, center - 3); column <= Math.Min((int)bounds.Width - 1, center + 3); column++)
                            if (Equal(bounds.Left + column + .5, y)) Add(column, column + 1, row);
                    }
                }
                samples[depth] = builder.Detach();
                bool Equal(double x, double y) => CanvasDepthOrder.TryPlaneDepth(mapping, new(x, y), out double actual) && actual == depth;
                void Add(int left, int right, int row)
                {
                    var rect = new SKRect((float)(bounds.Left + left), (float)(bounds.Top + row), (float)(bounds.Left + right), (float)(bounds.Top + row + 1));
                    builder.AddRect(rect); all.AddRect(rect);
                }
            }
            return all.Detach();
        }

        public void Dispose() { All.Dispose(); foreach (var path in samples.Values) path.Dispose(); }
    }
}
