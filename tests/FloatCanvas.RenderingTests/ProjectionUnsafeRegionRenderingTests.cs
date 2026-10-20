using System.Text.Json;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class ProjectionUnsafeRegionRenderingTests
{
    internal static void Run(Action<string, bool> check)
    {
        var scene = HorizonScene(new(0, 0, 240, 180));
        var mapping = CanvasMapping.Create(scene);
        string before = JsonSerializer.Serialize(scene);
        using (var pixels = Hint(scene, scene.Viewport, scene.Viewport))
        {
            var sky = pixels.GetPixel(120, 50);
            var margin = pixels.GetPixel(120, 105);
            check("unsafe-region hint softly tints both the far side and the finite horizon margin", sky.Alpha is >= 18 and <= 28 &&
                sky.Red > sky.Green && sky.Red > sky.Blue && margin.Alpha == sky.Alpha &&
                !mapping.IsScreenSafe(new(120, 50)) && !mapping.IsScreenSafe(new(120, 105)));
            check("unsafe-region hint leaves all safe ground pixels transparent", Transparent(pixels, new(0, 111, 240, 69)) && mapping.IsScreenSafe(new(120, 111)));
        }
        check("unsafe-region hint does not modify the scene", JsonSerializer.Serialize(scene) == before);

        using (var pixels = Hint(scene, new(40, 20, 100, 100), scene.Viewport))
            check("unsafe-region hint respects the requested screen subregion", pixels.GetPixel(70, 50).Alpha is >= 18 and <= 28 &&
                pixels.GetPixel(20, 50).Alpha == 0 && pixels.GetPixel(70, 10).Alpha == 0 && pixels.GetPixel(70, 115).Alpha == 0);
        using (var pixels = Hint(scene, new(-50, -50, 340, 280), new(-50, -50, 340, 280)))
            check("unsafe-region hint clips the request to the document viewport", pixels.GetPixel(170, 100).Alpha is >= 18 and <= 28 &&
                pixels.GetPixel(10, 100).Alpha == 0 && pixels.GetPixel(170, 10).Alpha == 0 && pixels.GetPixel(330, 100).Alpha == 0);

        scene = HorizonScene(new(-480, -300, 240, 180));
        using (var pixels = Hint(scene, scene.Viewport, scene.Viewport))
            check("unsafe-region hint keeps the same threshold on a negative-origin screen", pixels.GetPixel(120, 50).Alpha is >= 18 and <= 28 &&
                pixels.GetPixel(120, 105).Alpha is >= 18 and <= 28 && Transparent(pixels, new(0, 111, 240, 69)));

        scene = HorizonScene(new(0, 0, 240, 180));
        scene.Canvas.Projection.Matrix = Projective2D.FromAffine(Affine2D.Scale(1e9, 1e9));
        using (var pixels = Hint(scene, scene.Viewport, scene.Viewport))
            check("an empty safe domain tints the entire requested viewport", CanvasMapping.Create(scene).SafeScreenPolygon.Count == 0 &&
                Enumerable.Range(0, pixels.Height).All(y => Enumerable.Range(0, pixels.Width).All(x => pixels.GetPixel(x, y).Alpha is >= 18 and <= 28)));

        scene.Canvas.Projection.Matrix = Projective2D.Identity;
        using (var pixels = Hint(scene, scene.Viewport, scene.Viewport))
            check("a fully safe viewport produces no hint pixels", Transparent(pixels, scene.Viewport));
        scene.Canvas.Projection.Matrix = new(1, 0, 0, 0, 1, 200, 0, .01, 1);
        scene.Canvas.Projection.Enabled = false;
        using (var pixels = Hint(scene, scene.Viewport, scene.Viewport))
            check("disabled perspective produces no hint pixels", Transparent(pixels, scene.Viewport));

        scene = HorizonScene(new(0, 0, 240, 180));
        using var renderer = new SceneRenderer();
        var assets = new AssetLibrary();
        var exportedBefore = renderer.ExportPng(scene, assets, true);
        using (var hint = Hint(scene, scene.Viewport, scene.Viewport))
            check("the explicit editing hint has visible pixels", hint.GetPixel(120, 50).Alpha > 0);
        using (var normal = SKBitmap.Decode(renderer.ExportPng(scene, assets, true)))
            check("ordinary scene rendering and export omit the transient hint", Transparent(normal, scene.Viewport) &&
                renderer.ExportPng(scene, assets, true).SequenceEqual(exportedBefore));
    }

    private static SceneDocument HorizonScene(RectD viewport)
    {
        var scene = new SceneDocument { Viewport = viewport };
        scene.Canvas.Projection = CanvasCalibration.CreateInitial(viewport);
        scene.Canvas.Projection.Enabled = true;
        // Local screen horizon y=100, shared safety threshold y=110.
        scene.Canvas.Projection.Matrix = Projective2D.FromAffine(Affine2D.Translation(viewport.Left, viewport.Top))
            .Compose(new(1, 0, 0, 0, 1, 200, 0, .01, 1));
        return scene;
    }

    private static SKBitmap Hint(SceneDocument scene, RectD region, RectD target)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)target.Width, (int)target.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Translate(-(float)target.Left, -(float)target.Top);
        SceneRenderer.DrawProjectionUnsafeRegion(surface.Canvas, scene, region);
        using var image = surface.Snapshot();
        return SKBitmap.FromImage(image);
    }

    private static bool Transparent(SKBitmap pixels, RectD region) => Enumerable.Range((int)region.Top, (int)region.Height)
        .All(y => Enumerable.Range((int)region.Left, (int)region.Width).All(x => pixels.GetPixel(x, y).Alpha == 0));
}
