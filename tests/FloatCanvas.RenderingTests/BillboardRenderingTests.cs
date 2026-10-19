using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;
using System.Text.Json;

internal static class BillboardRenderingTests
{
    public static void Run(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer(); var assets = new AssetLibrary();
        var h = new Projective2D(1, 0, 0, 0, 1, 0, 0, .005, 1);
        var scene = new SceneDocument { Viewport = new(0, 0, 180, 150), Canvas = new() { Projection = new() { Enabled = true,
            Matrix = h, Quad = [new(0, 0), new(100, 0), new(100 / 1.5, 100 / 1.5), new(0, 100 / 1.5)] } } };
        var box = new CanvasElement { Kind = ElementKind.Rectangle, Width = 400, Height = 200, IsBillboard = true,
            BillboardAnchor = new(280, 600), BillboardHeight = 200, Style = new() { Fill = 0xFFFF0000, StrokeWidth = 0 } };
        scene.Elements = [box];
        using (var pixels = SKBitmap.Decode(renderer.ExportPng(scene, assets, true)))
        {
            check("billboard projection draws expected screen-facing rectangle", pixels.GetPixel(21, 76) == SKColors.Red && pixels.GetPixel(118, 123) == SKColors.Red);
            check("billboard width and height uniformly shrink by far depth", pixels.GetPixel(19, 100).Alpha == 0 && pixels.GetPixel(70, 74).Alpha == 0 && pixels.GetPixel(120, 100).Alpha == 0);
        }
        var bounds = SceneRenderer.ProjectedBounds(scene, box);
        check("render bounds share billboard screen transform", Math.Abs(bounds.X - 20) < 1e-7 && Math.Abs(bounds.Y - 75) < 1e-7 && Math.Abs(bounds.Width - 100) < 1e-7 && Math.Abs(bounds.Height - 50) < 1e-7);
        using var cache = new OverlaySceneCache(renderer);
        using var target = SKSurface.Create(new SKImageInfo(180, 150, SKColorType.Bgra8888, SKAlphaType.Premul));
        target.Canvas.Clear(SKColors.Transparent); cache.DrawOnClearedRasterTarget(target, scene, assets, 1, 0);
        using (var image = target.Snapshot()) using (var cached = SKBitmap.FromImage(image))
            check("overlay tile cache renders same screen-facing transform", cached.GetPixel(21, 76) == SKColors.Red && cached.GetPixel(19, 100).Alpha == 0);
        var capture = renderer.ExportSelectionPng(scene, assets, new(CaptureShape.Rectangle, CaptureSource.CanvasOnly, new(20, 75, 100, 50), [], 0, 0), false);
        using (var pixels = SKBitmap.Decode(capture.PngBytes))
            check("screenshot composition shares billboard projection", pixels.Width == 100 && pixels.Height == 50 && pixels.GetPixel(50, 25) == SKColors.Red);
        box.Transform = Affine2D.Rotation(90);
        bounds = SceneRenderer.ProjectedBounds(scene, box);
        check("own rotation retains screen-facing uniform depth scale", Math.Abs(bounds.Width - 50) < 1e-7 && Math.Abs(bounds.Height - 100) < 1e-7 && bounds.Center.DistanceTo(new(70, 100)) < 1e-7);
        box.Transform = Affine2D.Identity; scene.Canvas.Projection.Enabled = false;
        scene.Viewport = new(0, 0, 600, 600);
        using (var pixels = SKBitmap.Decode(renderer.ExportPng(scene, assets, true)))
            check("perspective off renders true logical dimensions and height", pixels.GetPixel(81, 301) == SKColors.Red && pixels.GetPixel(479, 499) == SKColors.Red && pixels.GetPixel(480, 400).Alpha == 0);
        scene.Canvas.Projection.Enabled = true; scene.Viewport = new(0, 0, 180, 150);
        box.Style.Fill = 0; box.Style.Stroke = 0xFF00FF00; box.Style.StrokeWidth = 8;
        using (var pixels = SKBitmap.Decode(renderer.ExportPng(scene, assets, true)))
            check("logical stroke shrinks with same depth as geometry", pixels.GetPixel(20, 100).Green > 0 && pixels.GetPixel(23, 100).Alpha == 0 && box.Style.StrokeWidth == 8);
        box.Kind = ElementKind.LivePixelate; box.Style.StrokeWidth = 0; box.Width = 160; box.Height = 160;
        box.Effects = [new() { Kind = ImageEffectKind.Pixelate, Amount = 1 }];
        using var backdropImage = SKSurface.Create(new SKImageInfo(180, 150)); backdropImage.Canvas.Clear(SKColors.Blue);
        using var backdropSnapshot = backdropImage.Snapshot(); using var backdropBytes = backdropSnapshot.Encode(SKEncodedImageFormat.Png, 100);
        using var backdrop = DesktopBackdrop.FromEncoded(scene.Viewport, backdropBytes.ToArray()); renderer.LiveBackdrop = backdrop;
        var roi = SceneRenderer.LiveEffectSamplingBounds(scene, box);
        using (var pixels = SKBitmap.Decode(renderer.ExportPng(scene, assets, true)))
            check("billboard live effect sampling and rendered mask agree", roi.Center.DistanceTo(new(70, 100)) < 1e-7 && Math.Abs(roi.Width - 40) < 1e-7 && pixels.GetPixel(70, 100) == SKColors.Blue && pixels.GetPixel(45, 100).Alpha == 0);
        // A captured bitmap keeps its original pixels while the logical frame is larger at depth.
        using var checker = SKSurface.Create(new SKImageInfo(40, 24));
        using var checkerPaint = new SKPaint();
        for (int y = 0; y < 24; y++) for (int x = 0; x < 40; x++)
        { checkerPaint.Color = (x + y) % 2 == 0 ? SKColors.Black : SKColors.White; checker.Canvas.DrawRect(x, y, 1, 1, checkerPaint); }
        using var checkerImage = checker.Snapshot(); using var checkerData = checkerImage.Encode(SKEncodedImageFormat.Png, 100);
        var imageElement = new CanvasElement { Kind = ElementKind.Image, Width = 160, Height = 96, AssetId = assets.Add(checkerData.ToArray()),
            Style = new() { StrokeWidth = 0 } };
        var mapping = CanvasMapping.Create(scene); mapping.TryGetEqualDepthLine(new(70, 150), out var depthLine);
        check("captured bitmap placed using fixed depth", BillboardGeometry.TryPlaceFromScreen(imageElement, mapping, depthLine,
            Projective2D.FromAffine(Affine2D.Translation(50, 50).Compose(Affine2D.Scale(.25, .25)))));
        scene.Elements = [imageElement];
        using (var pixels = SKBitmap.Decode(renderer.ExportPng(scene, assets, true)))
        using (var expected = SKBitmap.Decode(checkerData.ToArray()))
        {
            bool exact = true;
            for (int y = 0; y < 24; y++) for (int x = 0; x < 40; x++) exact &= pixels.GetPixel(x + 50, y + 50) == expected.GetPixel(x, y);
            check("far billboard snapshot samples original checker pixels once with no rebaking", exact);
        }
    }

    public static void WriteEvidence(string directory)
    {
        Directory.CreateDirectory(directory);
        using var renderer = new SceneRenderer(); var assets = new AssetLibrary();
        var scene = new SceneDocument { Viewport = new(0, 0, 1000, 700), Canvas = new() { BackgroundColor = 0xFF111827,
            Grid = new() { Visible = true, Color = 0x3038BDF8, Width = 1 } } };
        var reference = CanvasCalibration.CreateInitial(scene.Viewport, 8, 6);
        if (!CanvasCalibration.TryCalibrate(reference, [new(300, 120), new(700, 160), new(850, 560), new(150, 520)], scene.Viewport, out var calibrated, out var error))
            throw new InvalidOperationException(error);
        scene.Canvas.Projection = calibrated;
        var mapping = CanvasMapping.Create(scene);
        var anchors = new[] { new PointD(390, 270), new PointD(650, 500) };
        var evidence = new List<object>();
        foreach (var screenAnchor in anchors)
        {
            if (!mapping.TryToPlane(screenAnchor, out var ground)) throw new InvalidOperationException("Invalid evidence anchor.");
            var element = new CanvasElement { Kind = ElementKind.Rectangle, Width = 220, Height = 150,
                IsBillboard = true, BillboardAnchor = ground, BillboardHeight = 100,
                Style = new() { Stroke = 0xFF38BDF8, Fill = 0xB025638D, StrokeWidth = 5 } };
            scene.Elements.Add(element);
            mapping.TryGetDepthScale(ground, out double scale);
            var bounds = mapping.ProjectedBounds(element, false);
            if (Math.Abs(bounds.Width - 220 * scale) > 1e-7 || Math.Abs(bounds.Height - 150 * scale) > 1e-7) throw new InvalidOperationException("Evidence geometry mismatch.");
            evidence.Add(new { GroundAnchor = ground, ScreenAnchor = screenAnchor, Scale = scale, LogicalWidth = 220, LogicalHeight = 150, LogicalStroke = 5, LogicalHeightAboveGround = 100, ScreenBounds = bounds });
        }
        using var surface = SKSurface.Create(new SKImageInfo(1000, 700)); surface.Canvas.Clear(SKColors.Transparent);
        renderer.Draw(surface.Canvas, scene, assets);
        using var ink = new SKPaint { IsAntialias = true, Color = SKColors.White };
        using var guide = new SKPaint { IsAntialias = true, Color = new SKColor(0xFFFBBF24), StrokeWidth = 1.5f, Style = SKPaintStyle.Stroke };
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 19);
        using var smallFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 15);
        surface.Canvas.DrawText("Billboard placement - production SceneRenderer", 36, 42, SKTextAlign.Left, font, ink);
        surface.Canvas.DrawText("Same logical size 220 x 150 / stroke 5 / height 100. Tilted ground, screen-facing rectangles.", 36, 70, SKTextAlign.Left, smallFont, ink);
        for (int i = 0; i < scene.Elements.Count; i++)
        {
            var element = scene.Elements[i]; var bounds = mapping.ProjectedBounds(element, false); var ground = mapping.ToScreen(element.BillboardAnchor);
            surface.Canvas.DrawLine((float)bounds.Center.X, (float)bounds.Center.Y, (float)ground.X, (float)ground.Y, guide);
            surface.Canvas.DrawCircle((float)ground.X, (float)ground.Y, 4, guide);
            surface.Canvas.DrawCircle((float)bounds.Center.X, (float)bounds.Center.Y, 3, guide);
            mapping.TryGetDepthScale(element.BillboardAnchor, out double scale);
            surface.Canvas.DrawText($"{(i == 0 ? "Far" : "Near")}: scale {scale:F3} / screen {bounds.Width:F1} x {bounds.Height:F1}",
                (float)bounds.Left - 30, (float)bounds.Top - 16, SKTextAlign.Left, smallFont, ink);
        }
        surface.Canvas.DrawText("Geometry and raster tests verified. Guides added for explanation; not an app UI screenshot.", 36, 666, SKTextAlign.Left, smallFont, ink);
        using var snapshot = surface.Snapshot(); using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(directory, "billboard-production-render.png"), data.ToArray());
        File.WriteAllText(Path.Combine(directory, "billboard-production-render.json"), JsonSerializer.Serialize(new { Renderer = "FloatCanvas.Rendering.SceneRenderer", Calibration = calibrated, Objects = evidence }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
