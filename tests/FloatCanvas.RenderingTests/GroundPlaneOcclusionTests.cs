using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class GroundPlaneOcclusionTests
{
    internal static void Run(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer();
        var assets = new AssetLibrary();
        var scene = Scene();
        using (var pixels = Pixels(renderer, scene, assets))
        {
            var above = pixels.GetPixel(80, 35);
            var below = pixels.GetPixel(80, 100);
            Console.WriteLine($"GROUND_PLANE height={scene.Elements.Single().BillboardHeight:R} above(80,35)={above} below(80,100)={below}");
            Evidence(pixels, "opaque.png");
            check("opaque canvas ground keeps the billboard above the plane visible", above == SKColors.Red);
            check("opaque canvas ground occludes the negative-height billboard below the plane", below == SKColors.Lime);
            using var cache = new OverlaySceneCache(renderer);
            using var surface = SKSurface.Create(new SKImageInfo(160, 140, SKColorType.Bgra8888, SKAlphaType.Premul));
            surface.Canvas.Clear(SKColors.Transparent);
            cache.DrawOnClearedRasterTarget(surface, scene, assets, 1, 0);
            using var image = surface.Snapshot();
            using var cached = SKBitmap.FromImage(image);
            check("live overlay cache route retains the same ground occlusion", cached.Bytes.SequenceEqual(pixels.Bytes));
            var eventScene = scene.Clone();
            eventScene.Elements.Add(new() { Kind = ElementKind.LivePixelate, Width = 1, Height = 1,
                ProjectivePlacement = CanvasMapping.Create(scene).ScreenToPlane.Compose(Projective2D.FromAffine(Affine2D.Translation(10000, 10000))),
                Style = new() { StrokeWidth = 0 }, Effects = [new() { Kind = ImageEffectKind.Pixelate, Amount = 1 }] });
            using var events = Pixels(renderer, eventScene, assets);
            check("live-effect event route preserves opaque ground intersection", events.GetPixel(80, 35) == SKColors.Red && events.GetPixel(80, 100) == SKColors.Lime);
        }
        scene.Canvas.DepthPriorityOcclusion = false;
        using (var pixels = Pixels(renderer, scene, assets))
            check("disabled depth ordering retains billboard layer order over the canvas color", pixels.GetPixel(80, 35) == SKColors.Red && pixels.GetPixel(80, 100) == SKColors.Red);
        scene.Canvas.DepthPriorityOcclusion = true;
        scene.Canvas.BackgroundColor = 0x8000FF00;
        using (var pixels = Pixels(renderer, scene, assets))
        {
            Evidence(pixels, "translucent.png");
            var mixed = pixels.GetPixel(80, 100);
            check("translucent ground blends over only the submerged billboard pixels", pixels.GetPixel(80, 35) == SKColors.Red &&
                mixed.Alpha == 255 && mixed.Red is >= 126 and <= 128 && mixed.Green is >= 127 and <= 129 && mixed.Blue == 0);
            check("uncovered translucent ground retains its own alpha", pixels.GetPixel(5, 5) == new SKColor(0, 255, 0, 128));
        }
        scene.LayerOpacity = .5;
        using (var pixels = Pixels(renderer, scene, assets))
            check("global opacity is applied after the ground and intersecting billboard compose", pixels.GetPixel(80, 35).Alpha == 128 &&
                pixels.GetPixel(80, 100).Alpha == 128 && pixels.GetPixel(5, 5).Alpha == 64);
        var equalScene = Scene();
        equalScene.Canvas.Projection.Matrix = Projective2D.Identity;
        using (var pixels = Pixels(renderer, equalScene, assets))
            check("equal-depth ground remains the bottom plane layer beneath a billboard", pixels.GetPixel(80, 60) == SKColors.Red);
        var boundaryScene = Scene();
        boundaryScene.Canvas.BackgroundColor = 0x8000FF00;
        boundaryScene.Elements.Single().Style.Fill = 0;
        using (var pixels = Pixels(renderer, boundaryScene, assets))
            check("a transparent billboard depth boundary neither drops nor doubles translucent ground alpha",
                Enumerable.Range(45, 11).All(y => Enumerable.Range(70, 21).All(x => pixels.GetPixel(x, y).Alpha == 128)));
        HeightEdits(renderer, assets, check);
        Grid(renderer, assets, check);
    }

    private static void HeightEdits(SceneRenderer renderer, AssetLibrary assets, Action<string, bool> check)
    {
        var scene = Scene();
        var mapping = CanvasMapping.Create(scene);
        var board = scene.Elements.Single();
        mapping.TryGetDepthScale(board.BillboardAnchor, out double scale);
        board.BillboardHeight = -90 / scale;
        using var cache = new OverlaySceneCache(renderer);
        using var surface = SKSurface.Create(new SKImageInfo(160, 140, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        cache.DrawOnClearedRasterTarget(surface, scene, assets, 1, 1, 0, 0);
        using (var image = surface.Snapshot())
        using (var pixels = SKBitmap.FromImage(image))
            check("an entirely submerged billboard is hidden by opaque ground", mapping.ProjectedBounds(board).Contains(new(80, 100)) &&
                pixels.GetPixel(80, 100) == SKColors.Lime);
        board.BillboardHeight = 30 / scale;
        surface.Canvas.Clear(SKColors.Transparent);
        cache.DrawOnClearedRasterTarget(surface, scene, assets, 1, 1, 0, 0);
        using (var image = surface.Snapshot())
        using (var pixels = SKBitmap.FromImage(image))
            check("changing billboard height during a preview updates ground occlusion in the same cached scene", pixels.GetPixel(80, 35) == SKColors.Red &&
                pixels.GetPixel(80, 100) == SKColors.Lime);
    }

    private static void Grid(SceneRenderer renderer, AssetLibrary assets, Action<string, bool> check)
    {
        var scene = Scene();
        scene.Canvas.BackgroundColor = 0;
        scene.Canvas.Grid = new() { Visible = true, Color = 0xFF00FF00, Width = 4 };
        var board = scene.Elements.Single();
        board.Visible = false;
        using var gridOnly = Pixels(renderer, scene, assets);
        (int X, int Y) Find(int firstY, int lastY, bool ink) =>
            (from y in Enumerable.Range(firstY, lastY - firstY + 1)
             from x in Enumerable.Range(45, 70)
             let color = gridOnly.GetPixel(x, y)
             where ink ? color == SKColors.Lime : color.Alpha == 0
             select (x, y)).First();
        var aboveLine = Find(30, 40, true);
        var belowLine = Find(80, 105, true);
        var belowGap = Find(80, 105, false);
        board.Visible = true;
        using var pixels = Pixels(renderer, scene, assets);
        Evidence(pixels, "grid-only.png");
        Console.WriteLine($"GROUND_GRID above={aboveLine}:{pixels.GetPixel(aboveLine.X, aboveLine.Y)} below={belowLine}:{pixels.GetPixel(belowLine.X, belowLine.Y)} gap={belowGap}:{pixels.GetPixel(belowGap.X, belowGap.Y)}");
        check("grid lines behind the above-ground billboard remain hidden", pixels.GetPixel(aboveLine.X, aboveLine.Y) == SKColors.Red);
        check("grid lines on the ground occlude the submerged part of the billboard", pixels.GetPixel(belowLine.X, belowLine.Y) == SKColors.Lime);
        check("transparent grid cells leave the submerged billboard visible", pixels.GetPixel(belowGap.X, belowGap.Y) == SKColors.Red);
        scene.Canvas.Grid.Visible = false;
        using var hidden = Pixels(renderer, scene, assets);
        check("hiding the grid removes its plane occlusion", hidden.GetPixel(belowLine.X, belowLine.Y) == SKColors.Red);
    }

    private static SceneDocument Scene()
    {
        var matrix = new Projective2D(1, 0, 0, 0, 1, 0, 0, -.003, 1);
        var scene = new SceneDocument { Viewport = new(0, 0, 160, 140), Canvas = new()
        {
            BackgroundColor = 0xFF00FF00, DepthPriorityOcclusion = true,
            Projection = new() { Enabled = true, CellSize = 20, Matrix = matrix,
                Quad = [new(0, 0), new(160, 0), new(250, 187.5), new(0, 187.5)] }
        } };
        var mapping = CanvasMapping.Create(scene);
        mapping.TryToPlane(new(80, 50), out var anchor);
        mapping.TryGetDepthScale(anchor, out double scale);
        scene.Elements.Add(new() { Kind = ElementKind.Rectangle, IsBillboard = true, BillboardAnchor = anchor,
            BillboardHeight = -20 / scale, Width = 100 / scale, Height = 90 / scale,
            Style = new() { Fill = 0xFFFF0000, StrokeWidth = 0 } });
        return scene;
    }

    private static SKBitmap Pixels(SceneRenderer renderer, SceneDocument scene, AssetLibrary assets)
    {
        using var surface = SKSurface.Create(new SKImageInfo(160, 140, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        renderer.Draw(surface.Canvas, scene, assets);
        using var image = surface.Snapshot();
        return SKBitmap.FromImage(image);
    }

    private static void Evidence(SKBitmap pixels, string name)
    {
        if (Environment.GetEnvironmentVariable("FLOATCANVAS_GROUND_PLANE_EVIDENCE") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        using var image = SKImage.FromBitmap(pixels);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(directory, name), encoded.ToArray());
    }
}
