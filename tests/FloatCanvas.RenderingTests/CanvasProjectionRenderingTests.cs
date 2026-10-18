using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class CanvasProjectionRenderingTests
{
    internal static void Run(Action<string, bool> check)
    {
        GridCountSweep(check);
        CalibrationReference(check);
        var assets = new AssetLibrary();
        using var renderer = new SceneRenderer();
        var scene = Scene();
        var h = scene.Canvas.Projection.Matrix;
        scene.Elements.Add(Box(80, 100, 40, 30, 0xFFFF0000));
        using (var pixels = Pixels(renderer, scene, assets))
            check("canvas H projects ordinary geometry outside the reference rectangle", pixels.GetPixel(80, 90) == SKColors.Red && pixels.GetPixel(115, 125).Alpha == 0);

        // The screen photograph is retained once. H cancels its capture-time inverse P,
        // so even a one-pixel checkerboard must come back without a second resampling pass.
        byte[] source = Image(48, 32, (x, y) => (x + y) % 2 == 0 ? SKColors.Black : SKColors.White);
        string id = assets.Add(source);
        var photograph = new CanvasElement { Kind = ElementKind.Image, AssetId = id, Width = 48, Height = 32,
            ProjectivePlacement = h.Inverse().Compose(Projective2D.FromAffine(Affine2D.Translation(60, 40))) };
        scene.Elements = [photograph];
        using (var pixels = Pixels(renderer, scene, assets))
        using (var expected = SKBitmap.Decode(source))
        {
            bool exact = true;
            for (int y = 1; y < 31; y++) for (int x = 1; x < 47; x++) exact &= pixels.GetPixel(x + 60, y + 40) == expected.GetPixel(x, y);
            check("H and image P compose before the only image sample", exact);
        }
        using (var plane = SKBitmap.Decode(renderer.ExportPng(scene, assets, true, SceneRenderSpace.PlaneContent)))
            check("plane-content output retains P while omitting H", plane.GetPixel(75, 55).Alpha == 255 && plane.GetPixel(61, 41).Alpha == 0);

        scene.Elements = [];
        scene.Canvas.BackgroundColor = 0xFF112233; scene.LayerOpacity = .25;
        using (var opaque = SKBitmap.Decode(renderer.ExportPng(scene, assets)))
        using (var transparent = Pixels(renderer, scene, assets))
            check("canvas color is independent of layer opacity and explicit transparent export removes it", opaque.GetPixel(5, 5) == new SKColor(0xFF112233) && transparent.GetPixel(5, 5).Alpha == 0);
        var capture = renderer.ExportSelectionPng(scene, assets, new CaptureSelection(CaptureShape.Rectangle, CaptureSource.CanvasOnly, new(0, 0, 30, 20), [], 0, 0), false);
        using (var pixels = SKBitmap.Decode(capture.PngBytes))
            check("canvas-only capture retains the user's canvas color", pixels.GetPixel(5, 5) == new SKColor(0xFF112233));
        scene.Canvas.BackgroundColor = 0; scene.LayerOpacity = .6;
        scene.Canvas.Grid.Visible = true; scene.Canvas.Grid.Color = 0xFF00FF00; scene.Canvas.Grid.Width = 2;
        scene.Elements = [Box(30, 40, 70, 60, 0x90FF0000)];
        using var cache = new OverlaySceneCache(renderer);
        using var surface = SKSurface.Create(new SKImageInfo(240, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent); cache.DrawOnClearedRasterTarget(surface, scene, assets, 1, 0);
        using (var cached = surface.Snapshot())
        using (var cachedPixels = SKBitmap.FromImage(cached))
        using (var direct = Pixels(renderer, scene, assets))
            check("projected grid and shapes match the ordinary renderer through screen tile cache", Difference(cachedPixels, direct) <= 2);
        long builds = cache.BuildCount;
        surface.Canvas.Clear(SKColors.Transparent); cache.DrawOnClearedRasterTarget(surface, scene, assets, 1, 0);
        check("unchanged projected grid reuses its screen-pixel raster", cache.BuildCount == builds && cache.HitCount > 0);
        scene.Canvas.Projection.Matrix = Projective2D.Identity;
        surface.Canvas.Clear(SKColors.Transparent); cache.DrawOnClearedRasterTarget(surface, scene, assets, 1, 0);
        check("projection preview invalidates cached pixels even before revision commit", cache.BuildCount > builds);
        scene.Canvas.Projection.Matrix = h;
        scene.Elements = []; scene.LayerOpacity = 1;
        using (var grid = Pixels(renderer, scene, assets))
        using (var plane = SKBitmap.Decode(renderer.ExportPng(scene, assets, true, SceneRenderSpace.PlaneContent)))
            check("grid projects throughout the viewport but does not become part of object baking", grid.GetPixel(58, 16).Green > 0 && plane.GetPixel(58, 16).Alpha == 0);

        scene.Canvas.Grid.Visible = false;
        var filter = new CanvasElement { Kind = ElementKind.LivePixelate, Width = 60, Height = 60,
            Transform = Affine2D.Translation(60, 60), Effects = [new() { Kind = ImageEffectKind.Pixelate, Amount = 1 }], Style = new() { StrokeWidth = 0 } };
        scene.Elements = [filter];
        using var backdrop = DesktopBackdrop.FromEncoded(scene.Viewport, Image(240, 180, (x, _) => x < 75 ? SKColors.Red : SKColors.Blue));
        renderer.LiveBackdrop = backdrop;
        using (var pixels = Pixels(renderer, scene, assets))
            check("live mask follows H while sampling the actual screen location", pixels.GetPixel(65, 65) == SKColors.Red && pixels.GetPixel(85, 65) == SKColors.Blue && pixels.GetPixel(112, 112).Alpha == 0);
        var roi = SceneRenderer.LiveEffectSamplingBounds(scene, filter);
        check("live capture ROI uses projected bounds rather than original plane rectangle", roi.Left < 60 && roi.Top < 60 && roi.Right < 120 && roi.Bottom < 120 && SceneRenderer.HasLiveEffects(scene));
        scene.Elements.Insert(0, Box(60, 60, 60, 60, 0xFF00FF00));
        using (var pixels = Pixels(renderer, scene, assets))
            check("live lower-prefix sampling includes already projected plane objects", pixels.GetPixel(65, 65) == SKColors.Lime);
        renderer.LiveBackdrop = null; renderer.ScreenBackdrop = backdrop; scene.Elements = [];
        using (var pixels = SKBitmap.Decode(renderer.ExportPng(scene, assets)))
            check("temporary screen background is neither projected nor clipped to the calibration frame", pixels.GetPixel(74, 150) == SKColors.Red && pixels.GetPixel(76, 150) == SKColors.Blue);
        renderer.ScreenBackdrop = null;
        scene.Background = new() { Kind = ElementKind.Image, AssetId = id, Width = 48, Height = 32, Transform = Affine2D.Translation(60, 100) };
        using (var pixels = SKBitmap.Decode(renderer.ExportPng(scene, assets)))
            check("persistent captured background follows canvas projection", pixels.GetPixel(65, 90).Alpha == 255 && pixels.GetPixel(65, 125).Alpha == 0);
    }

    private static void GridCountSweep(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer(); var assets = new AssetLibrary();
        var scene = new SceneDocument { Viewport = new(0, 0, 3840, 2160) };
        PointD[] quad = [new(1056, 432), new(2753, 395), new(2631, 1615), new(1349, 1340)];
        var settings = CanvasCalibration.CreateInitial(scene.Viewport);
        foreach (var pair in new[] { (7,7), (8,7), (8,8), (9,9), (12,12), (32,32), (64,64), (128,128), (256,256) })
        {
            int count = pair.Item1, rows = pair.Item2;
            settings.Columns = count; settings.Rows = rows;
            check($"calibrate grid {count}x{rows}", CanvasCalibration.TryCalibrate(settings, quad, scene.Viewport, out var calibrated, out _));
            scene.Canvas.Projection = calibrated; settings = calibrated;
            var expectedCorner = calibrated.Matrix.TransformPoint(calibrated.Origin + new PointD(count * calibrated.CellSize, rows * calibrated.CellSize));
            check($"grid {count}x{rows} matrix matches calibration quad", expectedCorner.DistanceTo(quad[2]) < .001);
            check($"grid {count}x{rows} preserves reference corners", calibrated.Quad.SequenceEqual(quad));
            scene.Canvas.Grid.Visible = true; scene.Canvas.Grid.Color = 0xFFFF0000; scene.Canvas.Grid.Width = 1;
            using var pixels = Pixels(renderer, scene, assets);
            if (count > 12) continue; // Dense cases still exercise bounded rendering and recalibration.
            int missing = 0;
            CanvasCalibration.TryGridToScreen(count, rows, quad, out var grid);
            for (int n = 1; n < count; n++)
            {
                var point = grid.TransformPoint(new(n, rows * .37));
                int x = (int)point.X, y = (int)point.Y;
                if (!Enumerable.Range(-2, 5).Any(dx => Enumerable.Range(-2, 5).Any(dy => pixels.GetPixel(x + dx, y + dy).Alpha > 30))) missing++;
            }
            for (int n = 1; n < rows; n++)
            {
                var point = grid.TransformPoint(new(count * .37, n));
                int x = (int)point.X, y = (int)point.Y;
                if (!Enumerable.Range(-2, 5).Any(dx => Enumerable.Range(-2, 5).Any(dy => pixels.GetPixel(x + dx, y + dy).Alpha > 30))) missing++;
            }
            check($"grid {count}x{rows} retains both requested families in reference region ({missing} missing)", missing == 0);
        }
    }

    private static void CalibrationReference(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer();
        var assets = new AssetLibrary();
        var scene = new SceneDocument { Viewport = new(0, 0, 240, 180) };
        scene.Canvas.Projection = CanvasCalibration.CreateInitial(scene.Viewport, 6, 4);
        scene.Canvas.Projection.Enabled = true;
        scene.Canvas.Grid.Visible = false;
        // Opaque content must not conceal calibration guides.
        scene.Elements = [Box(-1000, -1000, 3000, 3000, 0xFFFFFFFF)];
        using var surface = SKSurface.Create(new SKImageInfo(240, 180));
        SKBitmap Draw(bool reference)
        {
            surface.Canvas.Clear(SKColors.Transparent);
            renderer.Draw(surface.Canvas, scene, assets, calibrationReference: reference);
            using var image = surface.Snapshot(); return SKBitmap.FromImage(image);
        }
        var mapping = CanvasMapping.Create(scene);
        var anchor = mapping.ToScreen(mapping.GridOrigin);
        int x = (int)Math.Round(anchor.X);
        // The reference quad is inset, but its grid must reach the viewport edge.
        using (var reference = Draw(true))
            check("calibration reference extends outside quad over opaque white content with hidden grid", Enumerable.Range(Math.Max(0, x - 3), 7).Any(px => Enumerable.Range(1, 14).Any(y => reference.GetPixel(px, y).Red < 240)));
        scene.Elements[0].Style.Fill = 0xFF000000;
        using (var reference = Draw(true))
            check("calibration white center remains visible over opaque dark content", Enumerable.Range(Math.Max(0, x - 2), 5).Any(px => Enumerable.Range(1, 14).Any(y => reference.GetPixel(px, y).Red > 30)));
        scene.LayerOpacity = 0;
        using (var reference = Draw(true))
            check("calibration reference ignores zero annotation opacity", Enumerable.Range(Math.Max(0, x - 3), 7).Any(px => Enumerable.Range(1, 14).Any(y => reference.GetPixel(px, y).Alpha > 40)));
        using (var ended = Draw(false))
            check("ending calibration removes all reference pixels", ended.Bytes.All(b => b == 0));
        scene.Elements = []; scene.LayerOpacity = 1;
        byte MaximumGuideAlpha(SKBitmap bitmap) => Enumerable.Range(x - 2, 5)
            .SelectMany(px => Enumerable.Range(1, 14).Select(y => bitmap.GetPixel(px, y).Alpha)).Max();
        byte standaloneAlpha;
        using (var standalone = Draw(true))
        {
            standaloneAlpha = MaximumGuideAlpha(standalone);
            check("calibration reference remains a single screen pixel plus antialiasing", Enumerable.Range(x - 3, 7).Count(px => standalone.GetPixel(px, 3).Alpha > 0) <= 2);
        }
        scene.Canvas.Grid.Visible = true; scene.Canvas.Grid.Color = 0xFFFF0000; scene.Canvas.Grid.Width = .01;
        using (var subdued = Draw(true))
            check("visible configured grid further subdues reference opacity", MaximumGuideAlpha(subdued) + 10 < standaloneAlpha);
        scene.Canvas.Grid.Width = 3;
        using (var configured = Draw(true))
            check("configured grid draws above calibration reference", Enumerable.Range(Math.Max(0, x - 2), 5).Any(px => { var p = configured.GetPixel(px, 3); return p.Red > 200 && p.Green < 30; }));
        scene.Canvas.Grid.Visible = false;
        scene.Canvas.Projection = CanvasCalibration.CreateInitial(scene.Viewport, 256, 256);
        scene.Canvas.Projection.Enabled = true;
        using (var dense = Draw(true))
        {
            int openPixels = 0;
            for (int y = 0; y < dense.Height; y++) for (int px = 0; px < dense.Width; px++)
                if (dense.GetPixel(px, y).Alpha < 20) openPixels++;
            check("locally crowded dense reference retains open background instead of covering every pixel", openPixels > dense.Width * dense.Height / 100);
        }
    }

    private static SceneDocument Scene() => new()
    {
        Viewport = new(0, 0, 240, 180),
        Canvas = new() { Projection = new() { Enabled = true, CellSize = 20, Quad = [new(0, 0), new(200, 0), new(200, 160), new(0, 160)],
            Matrix = new(1, 0, 0, 0, 1, 0, 0, .002, 1) } }
    };
    private static CanvasElement Box(double x, double y, double w, double h, uint color) => new()
    { Kind = ElementKind.Rectangle, Width = w, Height = h, Transform = Affine2D.Translation(x, y), Style = new() { Fill = color, StrokeWidth = 0 } };
    private static SKBitmap Pixels(SceneRenderer renderer, SceneDocument scene, AssetLibrary assets) => SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
    private static byte[] Image(int width, int height, Func<int, int, SKColor> pixel)
    {
        using var bitmap = new SKBitmap(width, height);
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) bitmap.SetPixel(x, y, pixel(x, y));
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
    }
    private static int Difference(SKBitmap a, SKBitmap b)
    {
        var aa = a.Bytes; var bb = b.Bytes; int difference = 0;
        for (int i = 0; i < aa.Length; i++) difference = Math.Max(difference, Math.Abs(aa[i] - bb[i]));
        return difference;
    }
}
