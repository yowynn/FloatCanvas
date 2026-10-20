using FloatCanvas.Core;
using SkiaSharp;

namespace FloatCanvas.Rendering;

public sealed partial class SceneRenderer
{
    /// <summary>Spatial picking with the same element transforms and plane ink as screen composition.</summary>
    public CanvasElement? PickDepth(SceneDocument document, AssetLibrary assets, PointD point, double tolerance = 4)
    {
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
            byte planeAlpha = Sample(canvas =>
            {
                DrawPlaneColor(canvas, document, true);
                DrawGrid(canvas, document);
            });
            foreach (int index in CanvasDepthOrder.BackToFrontAt(document, mapping, point).Reverse())
            {
                var item = document.Elements[index];
                if (!item.Visible || item.Style.Opacity <= 0) continue;
                if (!item.IsBillboard && !planeSafe) continue;
                if (item.IsBillboard && !mapping.IsElementSafe(item)) continue;
                double depth = planeDepth;
                if (item.IsBillboard && !CanvasDepthOrder.TryAnchorDepth(mapping, item.BillboardAnchor, out depth)) continue;
                // Base color and grid lie behind all elements at the plane depth, but in front of farther billboards.
                if (planeSafe && depth > planeDepth && planeAlpha == 255) return null;
                bool hit = ElementGeometry.HitTestProjected(item, point, mapping.PlaneToScreen, tolerance);
                if (!hit) continue;
                byte alpha = ElementGeometry.IsLiveEffect(item.Kind)
                    ? ElementGeometry.HitTestProjected(item, point, mapping.PlaneToScreen, 0)
                        ? Alpha(item.Style.Opacity) : (byte)0
                    : Sample(canvas => DrawElement(canvas, item, assets, mapping.PlaneToScreen, document));
                // Preserve frame picking for text and geometric tolerance around strokes, while images expose their alpha holes.
                if (!item.Locked && (item.Kind != ElementKind.Image || alpha > 0)) return item;
                if (alpha == 255) return null;
            }
            return null;
        }
        finally { EndFrame(); }
    }
}
