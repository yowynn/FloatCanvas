using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class HorizonSafetyRenderingTests
{
    internal static void Run(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer();
        var assets = new AssetLibrary();
        var scene = Scene();
        var mapping = CanvasMapping.Create(scene);
        check("horizon fixture contains both sky and usable ground", !mapping.IsScreenSafe(new(120, 95)) && mapping.IsScreenSafe(new(120, 150)));
        scene.Canvas.BackgroundColor = 0xFF00FF00;
        using (var pixels = Pixels(renderer, scene, assets, false))
            check("canvas color stops at the safety threshold and fills ground outside the quad", pixels.GetPixel(10, 170) == SKColors.Lime && EmptyRows(pixels, 0, 109));

        scene.Canvas.BackgroundColor = 0;
        scene.Canvas.Grid.Visible = true;
        scene.Canvas.Grid.Width = 3;
        scene.Canvas.Grid.Color = 0xFFFFFFFF;
        using (var pixels = Pixels(renderer, scene, assets))
            check("grid is finite and ends before the visible horizon", EmptyRows(pixels, 0, 109) && AnyInk(pixels, 112, 179));
        using (var surface = SKSurface.Create(new SKImageInfo(240, 180)))
        {
            surface.Canvas.Clear(SKColors.Transparent);
            renderer.Draw(surface.Canvas, scene, assets, calibrationReference: true);
            using var image = surface.Snapshot(); using var pixels = SKBitmap.FromImage(image);
            check("calibration reference shares the clipped grid domain", EmptyRows(pixels, 0, 109) && AnyInk(pixels, 112, 179));
        }

        scene.Canvas.Grid.Visible = false;
        var crossing = Box();
        scene.Elements = [crossing];
        using (var pixels = Pixels(renderer, scene, assets))
            check("source geometry crossing the projective pole renders only its finite ground portion", pixels.GetPixel(120, 150) == SKColors.Red && EmptyRows(pixels, 0, 109));
        crossing.Kind = ElementKind.Image;
        crossing.AssetId = assets.Add(Image());
        using (var pixels = Pixels(renderer, scene, assets))
            check("projective image crossing the pole is clipped before image sampling", pixels.GetPixel(120, 150).Red > 250 && EmptyRows(pixels, 0, 109));
        crossing.Style.Opacity = .5;
        using (var pixels = Pixels(renderer, scene, assets))
            check("finite source fallback applies per-element opacity once", pixels.GetPixel(120, 150).Alpha is >= 126 and <= 129 && EmptyRows(pixels, 0, 109));
        crossing.Kind = ElementKind.Rectangle; crossing.Style.Opacity = 1;
        using var baseline = Pixels(renderer, scene, assets);
        using (var cache = new OverlaySceneCache(renderer))
        using (var surface = SKSurface.Create(new SKImageInfo(240, 180, SKColorType.Bgra8888, SKAlphaType.Premul)))
        {
            surface.Canvas.Clear(SKColors.Transparent);
            cache.DrawOnClearedRasterTarget(surface, scene, assets, 1, 0);
            using var image = surface.Snapshot(); using var cached = SKBitmap.FromImage(image);
            check("finite horizon pixels agree between export and overlay raster cache", cached.Bytes.SequenceEqual(baseline.Bytes));
            long built = cache.BuildCount;
            surface.Canvas.Clear(SKColors.Transparent); cache.DrawOnClearedRasterTarget(surface, scene, assets, 1, 0);
            check("horizon safety keeps ordinary static cache reuse", cache.BuildCount == built && cache.HitCount > 0);
            scene.Canvas.Projection.Matrix = scene.Canvas.Projection.Matrix with { M23 = 210 };
            surface.Canvas.Clear(SKColors.Transparent); cache.DrawOnClearedRasterTarget(surface, scene, assets, 1, 0);
            check("uncommitted horizon preview invalidates static cache", cache.BuildCount > built);
        }

        scene = Scene(); scene.Elements = [Box()];
        var capture = renderer.ExportSelectionPng(scene, assets,
            new(CaptureShape.Rectangle, CaptureSource.CanvasOnly, scene.Viewport, [], 0, 0), false);
        using (var pixels = SKBitmap.Decode(capture.PngBytes))
            check("selection capture preserves safety clipping", EmptyRows(pixels, 0, 109) && pixels.GetPixel(120, 150) == SKColors.Red);
        var live = Box(); live.Kind = ElementKind.LiveBlur; live.Effects = [new() { Kind = ImageEffectKind.Blur, Amount = 3 }];
        scene.Elements.Add(live);
        var roi = SceneRenderer.LiveEffectSamplingBounds(scene, live);
        using (var pixels = Pixels(renderer, scene, assets))
            check("cross-horizon live blur has finite sampling and clips its output", roi.Top >= 100 && roi.Bottom <= 180 && roi.Width <= 240 &&
                EmptyRows(pixels, 0, 109) && pixels.GetPixel(120, 150).Red > 240);

        foreach (bool depth in new[] { false, true })
        {
            scene = Scene(); scene.Canvas.DepthPriorityOcclusion = depth;
            var board = Board(scene); scene.Elements = [board];
            using (var pixels = Pixels(renderer, scene, assets))
                check($"billboard top remains visible above the horizon (depth {depth})", pixels.GetPixel(120, 70) == SKColors.Blue);
            check($"billboard top is pickable in sky (depth {depth})", renderer.PickDepth(scene, assets, new(120, 70))?.Id == board.Id);
            scene.Canvas.BackgroundColor = 0xFF00FF00;
            using (var pixels = Pixels(renderer, scene, assets, false))
                check($"plane color does not hide the sky portion of a safe billboard (depth {depth})", pixels.GetPixel(120, 70) == SKColors.Blue && pixels.GetPixel(10, 70).Alpha == 0);
            scene.Elements.Insert(0, Box());
            using (var pixels = Pixels(renderer, scene, assets, false))
                check($"depth composition never resurrects the far side of the plane (depth {depth})", pixels.GetPixel(10, 95).Alpha == 0 && pixels.GetPixel(10, 170) == SKColors.Red);
            check($"plane content is not pickable in the horizon band (depth {depth})", renderer.PickDepth(scene, assets, new(10, 105)) is null);
            board.BillboardAnchor = CanvasMapping.Create(scene).ScreenToPlane.TransformPoint(new(120, 105));
            using (var pixels = Pixels(renderer, scene, assets))
                check($"unsafe billboard anchor hides the entire billboard (depth {depth})", pixels.GetPixel(120, 70).Alpha == 0);
        }

        scene = Scene(); scene.Elements = [Box()];
        var original = scene.Elements[0].Clone();
        for (int i = 0; i < 12; i++)
        {
            scene.Canvas.Projection.Matrix = scene.Canvas.Projection.Matrix with { M23 = 180 + i * 5 };
            using var pixels = Pixels(renderer, scene, assets);
            check($"continuous horizon preview remains finite and rejects sky frame {i}", EmptyRows(pixels, 0, 109) && AnyInk(pixels, 130, 179));
        }
        check("preview clipping preserves original source geometry", scene.Elements[0].Transform == original.Transform && scene.Elements[0].Width == original.Width && scene.Elements[0].Height == original.Height);
    }

    private static SceneDocument Scene()
    {
        var scene = new SceneDocument { Viewport = new(0, 0, 240, 180) };
        scene.Canvas.Projection = CanvasCalibration.CreateInitial(scene.Viewport);
        scene.Canvas.Projection.Enabled = true;
        // Inverse denominator is (screenY - 100) / 100; the front ground is below y=110.
        scene.Canvas.Projection.Matrix = new(1, 0, 0, 0, 1, 200, 0, .01, 1);
        return scene;
    }

    private static CanvasElement Box() => new()
    {
        Kind = ElementKind.Rectangle, Width = 4000, Height = 1300,
        Transform = Affine2D.Translation(-1000, -300), Style = new() { Fill = 0xFFFF0000, StrokeWidth = 0 }
    };

    private static CanvasElement Board(SceneDocument scene) => new()
    {
        Kind = ElementKind.Rectangle, Width = 80, Height = 240, IsBillboard = true, BillboardHeight = 80,
        BillboardAnchor = CanvasMapping.Create(scene).ScreenToPlane.TransformPoint(new(120, 150)),
        Style = new() { Fill = 0xFF0000FF, StrokeWidth = 0 }
    };

    private static SKBitmap Pixels(SceneRenderer renderer, SceneDocument scene, AssetLibrary assets, bool transparent = true) =>
        SKBitmap.Decode(renderer.ExportPng(scene, assets, transparent));

    private static bool EmptyRows(SKBitmap pixels, int first, int last) => Enumerable.Range(first, last - first + 1)
        .All(y => Enumerable.Range(0, pixels.Width).All(x => pixels.GetPixel(x, y).Alpha == 0));

    private static bool AnyInk(SKBitmap pixels, int first, int last) => Enumerable.Range(first, last - first + 1)
        .Any(y => Enumerable.Range(0, pixels.Width).Any(x => pixels.GetPixel(x, y).Alpha > 0));

    private static byte[] Image()
    {
        using var surface = SKSurface.Create(new SKImageInfo(16, 16)); surface.Canvas.Clear(SKColors.Red);
        using var image = surface.Snapshot(); using var bytes = image.Encode(SKEncodedImageFormat.Png, 100); return bytes.ToArray();
    }

    internal static void WriteEvidence(string directory)
    {
        Directory.CreateDirectory(directory);
        using var renderer = new SceneRenderer(); var assets = new AssetLibrary();
        var scene = Scene(); scene.Canvas.Grid.Visible = true; scene.Canvas.Grid.Color = 0xFF285A35; scene.Canvas.Grid.Width = 2;
        scene.Canvas.BackgroundColor = 0xFFE8F0DE;
        File.WriteAllBytes(Path.Combine(directory, "grid-horizon.png"), renderer.ExportPng(scene, assets));
        scene.Canvas.Grid.Visible = false; scene.Canvas.BackgroundColor = 0;
        scene.Elements = [Box()];
        File.WriteAllBytes(Path.Combine(directory, "planar-source-clipped.png"), renderer.ExportPng(scene, assets, true));
        scene.Elements = [Board(scene)]; scene.Canvas.Grid.Visible = true; scene.Canvas.DepthPriorityOcclusion = true;
        File.WriteAllBytes(Path.Combine(directory, "billboard-sky-depth.png"), renderer.ExportPng(scene, assets, true));
        scene.Elements.Insert(0, Box());
        var live = Box(); live.Kind = ElementKind.LivePixelate; live.Effects = [new() { Kind = ImageEffectKind.Pixelate, Amount = 8 }];
        scene.Elements.Add(live);
        File.WriteAllBytes(Path.Combine(directory, "live-effect-depth-clipped.png"), renderer.ExportPng(scene, assets, true));
        File.WriteAllText(Path.Combine(directory, "geometry.txt"),
            "Raw renderer outputs, 240 x 180 pixels. Screen horizon y=100; safety threshold y=110. " +
            "Ground y>=110, sky y<100. The red planar source extends across its projection pole. " +
            "The blue billboard has a safe ground anchor at (120,150); its upper part must remain visible and pickable in the sky.");
    }
}
