using FloatCanvas.Core;
using SkiaSharp;

namespace FloatCanvas.Rendering;

public sealed partial class SceneRenderer
{
    private SceneDocument? _drawingDocument;
    private readonly Dictionary<Guid, (SKImage Image, RectD Bounds)> _liveEffectImages = [];
    private Action<SKCanvas>? _depthSamplePainter;
    public long LiveEffectBuildCount { get; private set; }
    private void ClearLiveEffectImages()
    {
        foreach (var entry in _liveEffectImages.Values) entry.Image.Dispose();
        _liveEffectImages.Clear();
    }
    /// <summary>Latest complete desktop tiles in document coordinates; never a saved document resource.</summary>
    public DesktopBackdrop? LiveBackdrop { get; set; }
    /// <summary>Full lower scene for exporting an editable selection, clipboard object, or stamp.</summary>
    public SceneDocument? LiveEffectContext { get; set; }
    public static bool HasLiveEffects(SceneDocument document) =>
        document.LayerOpacity > 0 && document.Elements.Any(e => e.Visible && e.Style.Opacity > 0 &&
            ElementGeometry.IsLiveEffect(e.Kind) && !LiveEffectSamplingBounds(document, e).IsEmpty);

    /// <summary>Bounded screen-pixel capture ROI. Geometry follows the canvas; blur and pixelation sizes remain screen pixels.</summary>
    public static RectD LiveEffectSamplingBounds(SceneDocument document, CanvasElement effect)
    {
        var outline = LiveMask(document, effect, SceneRenderSpace.ScreenComposite);
        if (outline.Count < 3) return default;
        var bounds = PointBounds(outline);
        return (effect.Kind == ElementKind.LiveBlur ? bounds.Inflate(Math.Ceiling(3 * BlurAmount(effect))) : bounds).Intersect(document.Viewport);
    }

    /// <summary>Desktop ROI needed by a filter, including the Gaussian support outside its visible mask.</summary>
    public static RectD LiveEffectSamplingBounds(CanvasElement effect)
    {
        var bounds = ElementGeometry.Bounds(effect, includeStroke: false);
        return effect.Kind == ElementKind.LiveBlur ? bounds.Inflate(Math.Ceiling(3 * BlurAmount(effect))) : bounds;
    }
    private static double BlurAmount(CanvasElement effect) => Math.Clamp(effect.Effects.FirstOrDefault()?.Amount ?? 12, .1, 256);

    // Entry name is retained for the shared renderer dispatcher; both kinds compose the same lower prefix.
    private void DrawLivePixelate(SKCanvas canvas, IReadOnlyList<CanvasElement> elements, int index, AssetLibrary assets)
    {
        var effect = elements[index];
        var sourceDocument = LiveEffectContext ?? _drawingDocument;
        if (LiveEffectContext is { } context)
        {
            int original = context.Elements.FindIndex(e => e.Id == effect.Id);
            if (original >= 0) { elements = context.Elements; index = original; }
        }
        if (effect.Style.Opacity <= 0) return;
        var corners = sourceDocument is null ? ElementGeometry.Outline(effect).Select(ElementGeometry.LocalToPlane(effect).TransformPoint).ToArray() :
            LiveMask(sourceDocument, effect, _renderSpace);
        if (corners.Count < 3) return;
        var visibleBounds = PointBounds(corners);
        if (visibleBounds.IsEmpty || !canvas.LocalClipBounds.IntersectsWith(ToRect(visibleBounds))) return;
        if (!_liveEffectImages.TryGetValue(effect.Id, out var sampled))
        {
            bool blur = effect.Kind == ElementKind.LiveBlur;
            var sampleBounds = blur ? visibleBounds.Inflate(Math.Ceiling(3 * BlurAmount(effect))) : visibleBounds;
            if (sourceDocument is { } source) sampleBounds = sampleBounds.Intersect(source.Viewport);
            if (sampleBounds.IsEmpty) return;
            double block = Math.Clamp(effect.Effects.FirstOrDefault()?.Amount ?? 12, 1, 1024);
            double scale = Math.Min(1, 4096 / Math.Max(sampleBounds.Width, sampleBounds.Height));
            int width = blur ? Math.Max(1, (int)Math.Ceiling(sampleBounds.Width * scale)) : Math.Clamp((int)Math.Ceiling(sampleBounds.Width / block), 1, 4096);
            int height = blur ? Math.Max(1, (int)Math.Ceiling(sampleBounds.Height * scale)) : Math.Clamp((int)Math.Ceiling(sampleBounds.Height / block), 1, 4096);
            using var sampling = CreateIntermediateSurface(canvas,
                new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), "Cannot allocate the live filter sampling surface.");
            // Missing or stale capture covers rather than revealing unprocessed desktop pixels.
            sampling.Canvas.Clear(new SKColor(0xFF50545B));
            sampling.Canvas.Scale((float)(width / sampleBounds.Width), (float)(height / sampleBounds.Height));
            sampling.Canvas.Translate(-(float)sampleBounds.X, -(float)sampleBounds.Y);
            if (_depthSamplePainter is not null) _depthSamplePainter(sampling.Canvas);
            else if (sourceDocument is not null)
            {
                DrawBackground(sampling.Canvas, sourceDocument, assets, sampling: true);
                DrawGrid(sampling.Canvas, sourceDocument);
            }
            else (ScreenBackdrop ?? LiveBackdrop)?.Draw(sampling.Canvas);
            // A filter samples only its lower scene, including earlier live filters. It never
            // samples the final display or its own output. Prefix images are cached per frame.
            if (_depthSamplePainter is null) DrawElements(sampling.Canvas, elements, assets, 0, index, sourceDocument);
            if (blur)
            {
                using var raw = sampling.Snapshot();
                using var filtered = CreateIntermediateSurface(canvas,
                    new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), "Cannot allocate the live blur surface.");
                filtered.Canvas.Clear(SKColors.Transparent);
                using var filter = SKImageFilter.CreateBlur((float)(BlurAmount(effect) * width / sampleBounds.Width),
                    (float)(BlurAmount(effect) * height / sampleBounds.Height), SKShaderTileMode.Clamp);
                using var paint = new SKPaint { ImageFilter = filter };
                filtered.Canvas.DrawImage(raw, 0, 0, Nearest, paint);
                sampled = (filtered.Snapshot(), sampleBounds);
            }
            else sampled = (sampling.Snapshot(), sampleBounds);
            _liveEffectImages.Add(effect.Id, sampled); LiveEffectBuildCount++;
        }
        using var maskBuilder = new SKPathBuilder();
        maskBuilder.MoveTo(ToPoint(corners[0]));
        foreach (var point in corners.Skip(1)) maskBuilder.LineTo(ToPoint(point));
        maskBuilder.Close();
        using var mask = maskBuilder.Detach();
        int saved = canvas.Save();
        try
        {
            canvas.ClipPath(mask, antialias: true);
            using var paint = new SKPaint { Color = SKColors.White.WithAlpha(Alpha(effect.Style.Opacity)) };
            canvas.DrawImage(sampled.Image, ToRect(sampled.Bounds), effect.Kind == ElementKind.LiveBlur ? Linear : Nearest, paint);
        }
        finally { canvas.RestoreToCount(saved); }
    }

    private static IReadOnlyList<PointD> LiveMask(SceneDocument document, CanvasElement effect, SceneRenderSpace space)
    {
        if (effect.IsBillboard && space == SceneRenderSpace.ScreenComposite)
        {
            var billboardMapping = CanvasMapping.Create(document);
            if (!billboardMapping.TryGetDepthScale(effect.BillboardAnchor, out _)) return [];
            var billboardTransform = billboardMapping.ElementToScreen(effect);
            return ClipPolygon(ElementGeometry.Outline(effect).Select(billboardTransform.TransformPoint).ToArray(),
                [new(document.Viewport.Left, document.Viewport.Top), new(document.Viewport.Right, document.Viewport.Top),
                 new(document.Viewport.Right, document.Viewport.Bottom), new(document.Viewport.Left, document.Viewport.Bottom)]);
        }
        var local = ElementGeometry.LocalToPlane(effect);
        IReadOnlyList<PointD> polygon = ElementGeometry.Outline(effect).Select(local.TransformPoint).ToArray();
        var mapping = CanvasMapping.Create(document);
        if (space == SceneRenderSpace.PlaneContent) return polygon;
        // Clip in the plane before division. The inverse viewport polygon selects the visible
        // branch and keeps near-horizon values out of integer capture dimensions.
        polygon = ClipPolygon(polygon, mapping.VisiblePlanePolygon);
        var output = new List<PointD>(polygon.Count);
        foreach (var point in polygon)
            if (mapping.PlaneToScreen.TryTransformPoint(point, out var screen)) output.Add(screen);
        return output;
    }

    private static RectD PointBounds(IReadOnlyList<PointD> points)
    {
        double left = points.Min(p => p.X), top = points.Min(p => p.Y);
        return new(left, top, points.Max(p => p.X) - left, points.Max(p => p.Y) - top);
    }

    private static IReadOnlyList<PointD> ClipPolygon(IReadOnlyList<PointD> subject, IReadOnlyList<PointD> clip)
    {
        if (clip.Count < 3) return [];
        double area = 0;
        for (int i = 0; i < clip.Count; i++) area += Cross(clip[i], clip[(i + 1) % clip.Count]);
        double sign = area >= 0 ? 1 : -1;
        var result = subject.ToList();
        for (int i = 0; i < clip.Count && result.Count > 0; i++)
        {
            var a = clip[i]; var edge = clip[(i + 1) % clip.Count] - a;
            var input = result; result = [];
            var previous = input[^1]; double before = sign * Cross(edge, previous - a);
            foreach (var current in input)
            {
                double after = sign * Cross(edge, current - a);
                if ((before >= 0) != (after >= 0))
                    result.Add(previous + (current - previous) * (before / (before - after)));
                if (after >= 0) result.Add(current);
                previous = current; before = after;
            }
        }
        return result;
    }

    private static double Cross(PointD a, PointD b) => a.X * b.Y - a.Y * b.X;
}
