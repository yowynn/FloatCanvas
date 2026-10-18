using FloatCanvas.Core;
using SkiaSharp;

namespace FloatCanvas.Rendering;

public sealed partial class SceneRenderer
{
    private SceneDocument? _drawingDocument;
    private readonly Dictionary<Guid, (SKImage Image, RectD Bounds)> _liveEffectImages = [];
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
            ElementGeometry.IsLiveEffect(e.Kind) && ElementGeometry.Bounds(e, includeStroke: false).Intersects(document.Viewport));

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
        var visibleBounds = ElementGeometry.Bounds(effect, includeStroke: false);
        if (sourceDocument is { } doc) visibleBounds = visibleBounds.Intersect(doc.Viewport);
        if (visibleBounds.IsEmpty || !canvas.LocalClipBounds.IntersectsWith(ToRect(visibleBounds))) return;
        if (!_liveEffectImages.TryGetValue(effect.Id, out var sampled))
        {
            bool blur = effect.Kind == ElementKind.LiveBlur;
            var sampleBounds = blur ? LiveEffectSamplingBounds(effect) : visibleBounds;
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
            if (sourceDocument?.Background is not null) DrawBackground(sampling.Canvas, sourceDocument, assets);
            else LiveBackdrop?.Draw(sampling.Canvas);
            // A filter samples only its lower scene, including earlier live filters. It never
            // samples the final display or its own output. Prefix images are cached per frame.
            DrawElements(sampling.Canvas, elements, assets, 0, index);
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
        var corners = ElementGeometry.Outline(effect).Select(effect.Transform.TransformPoint).ToArray();
        if (corners.Length < 3) return;
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
}
