using FloatCanvas.Core;
using SkiaSharp;

namespace FloatCanvas.Rendering;

public sealed partial class SceneRenderer
{
    private CancellationToken pickingCancellation;
    /// <summary>Tests visible rendered ink in a screen-space rectangle, using bounded raster tiles.</summary>
    public bool IntersectsElementInk(SceneDocument document, AssetLibrary assets, CanvasElement element, RectD rectangle,
        CancellationToken cancellationToken = default)
    {
        if (!element.Visible || element.Style.Opacity <= 0 || document.LayerOpacity <= 0) return false;
        var previousCancellation = pickingCancellation; pickingCancellation = cancellationToken;
        BeginFrame(document);
        try { return IntersectsElementInkCore(document, assets, element, rectangle, cancellationToken); }
        finally { EndFrame(); pickingCancellation = previousCancellation; }
    }

    private bool IntersectsElementInkCore(SceneDocument document, AssetLibrary assets, CanvasElement element, RectD rectangle,
        CancellationToken cancellationToken = default)
    {
        var bounds = ProjectedBounds(document, element, includeStroke: true).Intersect(document.Viewport).Intersect(rectangle);
        if (bounds.IsEmpty || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height)) return false;
        // Live effects own their sampling region even when the desktop happens to be uniform.
        if (ElementGeometry.IsLiveEffect(element.Kind)) return true;
        int width = (int)Math.Clamp(Math.Ceiling(bounds.Width) + 1, 1, 256);
        int height = (int)Math.Clamp(Math.Ceiling(bounds.Height) + 1, 1, 256);
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Cannot allocate the selection sample.");
        for (double top = Math.Floor(bounds.Top); top < bounds.Bottom; top += height)
        for (double left = Math.Floor(bounds.Left); left < bounds.Right; left += width)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var canvas = surface.Canvas;
            canvas.RestoreToCount(1); canvas.ResetMatrix(); canvas.Clear(SKColors.Transparent);
            canvas.Save(); canvas.Translate((float)-left, (float)-top); canvas.ClipRect(ToRect(bounds));
            DrawElement(canvas, element, assets, MappingFor(document).PlaneToScreen, document);
            using var pixels = surface.PeekPixels();
            var bytes = pixels.GetPixelSpan();
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (bytes[y * pixels.RowBytes + x * 4 + 3] != 0) return true;
        }
        return false;
    }

    /// <summary>Spatial picking with the same element transforms and plane ink as screen composition.</summary>
    public CanvasElement? PickDepth(SceneDocument document, AssetLibrary assets, PointD point, double tolerance = 4,
        CancellationToken cancellationToken = default)
    {
        if (!(document.LayerOpacity > 0)) return null;
        var previousCancellation = pickingCancellation; pickingCancellation = cancellationToken;
        var mapping = CanvasMapping.Create(document);
        bool planeSafe = CanvasDepthOrder.TryPlaneDepth(mapping, point, out double planeDepth);
        using var surface = SKSurface.Create(new SKImageInfo(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Cannot allocate the selection sample.");
        BeginFrame(document);
        try
        {
            byte Sample(Action<SKCanvas> draw)
            {
                var canvas = surface.Canvas;
                canvas.ResetMatrix(); canvas.Clear(SKColors.Transparent);
                canvas.Translate((float)(.5 - point.X), (float)(.5 - point.Y));
                draw(canvas);
                using var pixels = surface.PeekPixels();
                return pixels.GetPixelColor(0, 0).Alpha;
            }
            foreach (int index in CanvasDepthOrder.BackToFrontAt(document, mapping, point).Reverse())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = document.Elements[index];
                if (!item.Visible || item.Style.Opacity <= 0) continue;
                if (!item.IsBillboard && !planeSafe) continue;
                if (item.IsBillboard && !mapping.IsElementSafe(item)) continue;
                double depth = planeDepth;
                if (item.IsBillboard && !CanvasDepthOrder.TryAnchorDepth(mapping, item.BillboardAnchor, out depth)) continue;
                bool hit = ElementGeometry.HitTestProjected(item, point, mapping.PlaneToScreen, tolerance);
                if (!hit) continue;
                byte alpha = ElementGeometry.IsLiveEffect(item.Kind)
                    ? ElementGeometry.HitTestProjected(item, point, mapping.PlaneToScreen, 0)
                        ? Alpha(item.Style.Opacity) : (byte)0
                    : Sample(canvas => DrawElement(canvas, item, assets, mapping.PlaneToScreen, document));
                bool ink = alpha > 0 || tolerance > 0 && IntersectsElementInkCore(document, assets, item,
                    new(point.X - tolerance, point.Y - tolerance, tolerance * 2, tolerance * 2), cancellationToken);
                if (!item.Locked && ink) return item;
                if (alpha == 255) return null;
            }
            return null;
        }
        finally { EndFrame(); pickingCancellation = previousCancellation; }
    }
}
