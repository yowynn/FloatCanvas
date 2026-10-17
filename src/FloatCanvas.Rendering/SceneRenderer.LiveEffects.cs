using FloatCanvas.Core;
using SkiaSharp;

namespace FloatCanvas.Rendering;

public sealed partial class SceneRenderer
{
    private SceneDocument? _drawingDocument;
    private readonly Dictionary<Guid,(SKImage Image,RectD Bounds)> _liveEffectImages=[];
    public long LiveEffectBuildCount { get; private set; }
    private void ClearLiveEffectImages()
    {
        foreach(var entry in _liveEffectImages.Values)entry.Image.Dispose();
        _liveEffectImages.Clear();
    }
    /// <summary>Latest complete desktop frame in document coordinates. Never saved as a document resource.</summary>
    public DesktopBackdrop? LiveBackdrop { get; set; }
    /// <summary>Optional full scene for an editable selection's externally rendered appearance.</summary>
    public SceneDocument? LiveEffectContext { get; set; }
    public static bool HasLiveEffects(SceneDocument document) =>
        document.LayerOpacity > 0 && document.Elements.Any(e => e.Visible && e.Style.Opacity > 0 &&
            e.Kind == ElementKind.LivePixelate && ElementGeometry.Bounds(e, includeStroke: false).Intersects(document.Viewport));

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
        var bounds = ElementGeometry.Bounds(effect, includeStroke:false);
        if (sourceDocument is { } doc) bounds = bounds.Intersect(doc.Viewport);
        if (bounds.IsEmpty || !canvas.LocalClipBounds.IntersectsWith(ToRect(bounds))) return;
        if (!_liveEffectImages.TryGetValue(effect.Id,out var sampled))
        {
        double block = Math.Clamp(effect.Effects.FirstOrDefault()?.Amount ?? 12,1,1024);
        int width = Math.Clamp((int)Math.Ceiling(bounds.Width/block),1,4096);
        int height = Math.Clamp((int)Math.Ceiling(bounds.Height/block),1,4096);
        using var small = SKSurface.Create(new SKImageInfo(width,height,SKColorType.Bgra8888,SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Cannot allocate the live pixelation surface.");
        // If capture is temporarily unavailable, cover the region. A stale or missing
        // desktop image must never expose the unprocessed content underneath.
        small.Canvas.Clear(new SKColor(0xFF50545B));
        small.Canvas.Scale((float)(width/bounds.Width),(float)(height/bounds.Height));
        small.Canvas.Translate(-(float)bounds.X,-(float)bounds.Y);
        if (sourceDocument?.Background is not null) DrawBackground(small.Canvas,sourceDocument,assets);
        else LiveBackdrop?.Draw(small.Canvas);
        // Compose only objects below this element, in their existing z-order. Earlier
        // live effects are evaluated against their own prefixes, never the final display.
        DrawElements(small.Canvas,elements,assets,0,index);
        sampled=(small.Snapshot(),bounds);
        _liveEffectImages.Add(effect.Id,sampled); LiveEffectBuildCount++;
        }
        using var maskBuilder = new SKPathBuilder();
        var corners = ElementGeometry.Outline(effect).Select(effect.Transform.TransformPoint).ToArray();
        if (corners.Length < 3) return;
        maskBuilder.MoveTo(ToPoint(corners[0]));
        foreach(var point in corners.Skip(1))maskBuilder.LineTo(ToPoint(point));
        maskBuilder.Close();
        using var mask = maskBuilder.Detach();
        int saved=canvas.Save();
        try
        {
            canvas.ClipPath(mask,antialias:true);
            using var paint = new SKPaint { Color=SKColors.White.WithAlpha(Alpha(effect.Style.Opacity)) };
            canvas.DrawImage(sampled.Image,ToRect(sampled.Bounds),Nearest,paint);
        }
        finally { canvas.RestoreToCount(saved); }
    }
}
