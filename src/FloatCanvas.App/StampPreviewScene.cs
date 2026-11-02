using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

namespace FloatCanvas.App;

/// <summary>Fits a prefab's own preview without rescaling its authored geometry or calibration.</summary>
internal static class StampPreviewScene
{
    internal static SceneDocument Prepare(SceneDocument source)
    {
        var document = SceneEvaluation.Build(source).RenderDocument;
        var bounds = document.Elements.Select(element => SceneRenderer.ProjectedBounds(document, element, true))
            .DefaultIfEmpty(document.Viewport).Aggregate(RectD.Union);
        document.Canvas = GroupCanvasScene.ResolveEditingCanvas(document.Canvas, document.Viewport);
        document.Viewport = new(bounds.X, bounds.Y, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
        return document;
    }

    internal static byte[] Render(SceneRenderer renderer, SceneDocument document, AssetLibrary assets, int maximumEdge)
    {
        var bounds = document.Viewport;
        double factor = maximumEdge / Math.Max(bounds.Width, bounds.Height);
        using var surface = SKSurface.Create(new SKImageInfo(Math.Max(1, (int)Math.Ceiling(bounds.Width * factor)),
            Math.Max(1, (int)Math.Ceiling(bounds.Height * factor)), SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Scale((float)factor); surface.Canvas.Translate(-(float)bounds.X, -(float)bounds.Y);
        renderer.Draw(surface.Canvas, document, assets);
        using var image = surface.Snapshot(); using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}
