using System.Security.Cryptography;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class Program
{
    private static int _checks;
    private static int Main()
    {
        try
        {
            ShapesAndCoordinates();
            AlphaAndBackground();
            ImagesAndEffects();
            TextAndNumbers();
            OrientationAndFormats();
            AssetImportLosslessTests.Run(Assert);
            CacheAndCanvasState();
            ArrowHeadRenderingTests.Run(Assert);
            OverlaySceneCacheTests.Run(Assert);
            ClearedLiveOpacityTests.Run(Assert);
            Console.WriteLine($"PASS {_checks} real render/import/export checks.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void ShapesAndCoordinates()
    {
        using var renderer = new SceneRenderer();
        var assets = new AssetLibrary();
        var document = Document(160, 100);
        document.Elements.Add(Shape(ElementKind.Rectangle, 30, 20, 0xFFFF0000,
            Affine2D.Translation(80, 20).Compose(Affine2D.Rotation(90))));
        using var result = Decode(renderer.ExportPng(document, assets, true));
        Assert("rotated rectangle exact physical position", result.GetPixel(70, 30) == SKColors.Red && result.GetPixel(85, 30).Alpha == 0);
        Assert("export preserves dimensions", result.Width == 160 && result.Height == 100);
        document.Viewport = new(-100, -50, 160, 100);
        document.Elements[0].Transform = Affine2D.Translation(-95, -45);
        using var negative = Decode(renderer.ExportPng(document, assets, true));
        Assert("negative viewport translated once", negative.GetPixel(10, 10) == SKColors.Red && negative.GetPixel(100, 50).Alpha == 0);

        foreach (var kind in new[] { ElementKind.Freehand, ElementKind.Highlighter, ElementKind.Line, ElementKind.Arrow, ElementKind.DoubleArrow,
                     ElementKind.Rectangle, ElementKind.RoundedRectangle, ElementKind.Ellipse, ElementKind.Triangle })
        {
            var item = new CanvasElement { Kind = kind, Width = 50, Height = 40, Points = [new(5, 20), new(25, 10), new(45, 20)],
                Transform = Affine2D.Translation(15, 15).Compose(Affine2D.Rotation(12)),
                Style = new() { Stroke = 0xFFFF0000, Fill = 0x800000FF, StrokeWidth = 4 } };
            var scene = Document(100, 100); scene.Elements.Add(item);
            using var bitmap = Decode(renderer.ExportPng(scene, assets, true));
            Assert($"{kind} draws transformed pixels", CountOpaque(bitmap) > 15);
        }
        var dot = Document(40, 40);
        dot.Elements.Add(new() { Kind = ElementKind.Freehand, Points = [new(20, 20)], Style = new() { StrokeWidth = 6 } });
        using var dotBitmap = Decode(renderer.ExportPng(dot, assets, true));
        Assert("single point stroke renders a round dot", dotBitmap.GetPixel(20, 20).Alpha > 200);
    }

    private static void AlphaAndBackground()
    {
        using var renderer = new SceneRenderer();
        var assets = new AssetLibrary();
        var background = AssetImport.Normalize(ImageBytes(64, 64, (_, _) => SKColors.Blue));
        var document = Document(64, 64);
        document.Background = new() { Kind = ElementKind.Image, Width = 64, Height = 64, AssetId = assets.Add(background.PngBytes) };
        document.LayerOpacity = .5;
        document.Elements.Add(Shape(ElementKind.Rectangle, 40, 40, 0xFFFF0000, Affine2D.Identity));
        document.Elements.Add(Shape(ElementKind.Rectangle, 40, 40, 0xFFFF0000, Affine2D.Translation(20, 20)));
        using var composed = Decode(renderer.ExportPng(document, assets));
        Assert("background unaffected by layer opacity", composed.GetPixel(63, 63) == SKColors.Blue);
        var single = composed.GetPixel(10, 10); var overlap = composed.GetPixel(30, 30);
        Assert("global opacity applied once to overlapping annotations", single == overlap && Math.Abs(single.Red - 128) <= 1 && Math.Abs(single.Blue - 127) <= 1);
        using var transparent = Decode(renderer.ExportPng(document, assets, true));
        Assert("transparent export excludes background", transparent.GetPixel(63, 63).Alpha == 0 && Math.Abs(transparent.GetPixel(30, 30).Alpha - 128) <= 1);
        document.LayerOpacity = 0;
        using var onlyBackground = Decode(renderer.ExportPng(document, assets));
        Assert("zero layer opacity still draws background", onlyBackground.GetPixel(30, 30) == SKColors.Blue);
        using var jpeg = Decode(renderer.ExportJpeg(document, assets));
        Assert("JPEG output is decodable and opaque", jpeg.Width == 64 && jpeg.GetPixel(30, 30).Alpha == 255 && jpeg.GetPixel(30, 30).Blue > 240);
    }

    private static void ImagesAndEffects()
    {
        using var renderer = new SceneRenderer();
        var assets = new AssetLibrary();
        byte[] png = ImageBytes(64, 32, (x, y) => x < 32 ? new SKColor(255, 0, 0, (byte)(y < 8 ? 0 : 255)) : SKColors.Blue);
        var normalized = AssetImport.Normalize(png);
        Assert("normalization retains real RGBA alpha", DecodePixel(normalized.PngBytes, 4, 2).Alpha == 0);
        string id = assets.Add(normalized.PngBytes);
        var beforeHash = SHA256.HashData(assets.Get(id).Data.Span);
        var document = Document(128, 100);
        var image = new CanvasElement { Kind = ElementKind.Image, AssetId = id, Width = 64, Height = 32, Transform = Affine2D.Translation(10, 10) };
        document.Elements.Add(image);
        using (var bitmap = Decode(renderer.ExportPng(document, assets, true)))
            Assert("image transparency displayed without black matte", bitmap.GetPixel(14, 12).Alpha == 0 && bitmap.GetPixel(50, 30) == SKColors.Blue);
        image.ImageCrop = new(.5, 0, .5, 1);
        using (var bitmap = Decode(renderer.ExportPng(document, assets, true)))
            Assert("normalized crop maps selected source area to object frame", bitmap.GetPixel(15, 20) == SKColors.Blue && bitmap.GetPixel(65, 30) == SKColors.Blue);
        image.ImageCrop = null;
        image.Effects.Add(new() { Kind = ImageEffectKind.Redact, Bounds = new(.5, .25, .5, .5), Color = 0xFF00FF00 });
        using (var bitmap = Decode(renderer.ExportPng(document, assets, true)))
            Assert("redaction changes only target original-image coordinates", bitmap.GetPixel(60, 25) == SKColors.Lime && bitmap.GetPixel(60, 12) == SKColors.Blue);
        image.Transform = Affine2D.Translation(90, 10).Compose(Affine2D.Rotation(90));
        using (var bitmap = Decode(renderer.ExportPng(document, assets, true)))
            Assert("effects rotate with target image", bitmap.GetPixel(75, 60) == SKColors.Lime);
        document.Elements.Add(Shape(ElementKind.Rectangle, 10, 10, 0xFFFFFF00, Affine2D.Translation(70, 55)));
        using (var bitmap = Decode(renderer.ExportPng(document, assets, true)))
            Assert("vector object above image effects follows z order", bitmap.GetPixel(75, 60) == SKColors.Yellow);
        Assert("effects never mutate original resource", beforeHash.SequenceEqual(SHA256.HashData(assets.Get(id).Data.Span)));

        string checkerId = assets.Add(ImageBytes(64, 32, (x, y) => ((x / 2 + y / 2) & 1) == 0 ? SKColors.Black : SKColors.White));
        var effectScene = Document(64, 32);
        var checker = new CanvasElement { Kind = ElementKind.Image, AssetId = checkerId, Width = 64, Height = 32,
            Effects = [new() { Kind = ImageEffectKind.Pixelate, Bounds = new(0, 0, .5, 1), Amount = 8 }] };
        effectScene.Elements.Add(checker);
        using (var bitmap = Decode(renderer.ExportPng(effectScene, assets, true)))
            Assert("pixelation builds nearest-neighbor blocks within region", bitmap.GetPixel(1, 1) == bitmap.GetPixel(6, 6) && bitmap.GetPixel(40, 1) != bitmap.GetPixel(42, 1));
        checker.Effects[0].Kind = ImageEffectKind.Blur;
        checker.Effects[0].Amount = 3;
        using (var bitmap = Decode(renderer.ExportPng(effectScene, assets, true)))
            Assert("blur smooths target region without touching rest", bitmap.GetPixel(12, 12).Red is > 70 and < 185 && bitmap.GetPixel(40, 1) == SKColors.Black);
        checker.Effects.Add(new() { Kind = ImageEffectKind.Redact, Bounds = new(.1, .1, .2, .5), Color = 0xFFFF0000 });
        using (var bitmap = Decode(renderer.ExportPng(effectScene, assets, true)))
            Assert("effects applied in saved order", bitmap.GetPixel(10, 8) == SKColors.Red);
    }

    private static void TextAndNumbers()
    {
        using var renderer = new SceneRenderer();
        var style = new ElementStyle { Stroke = 0xFF111111, FontFamily = "Microsoft YaHei UI", FontSize = 28 };
        var metrics = renderer.MeasureText("浮绘 FloatCanvas\n中文标注", style);
        Assert("HarfBuzz returns real Chinese glyphs", metrics.GlyphCount >= 16 && metrics.MissingGlyphCount == 0 && metrics.LineCount == 2 && metrics.Width > 100);
        var document = Document(400, 180);
        document.Elements.Add(new() { Kind = ElementKind.Text, Text = "浮绘 FloatCanvas\n中文标注", Width = 360, Height = 100, Style = style, Transform = Affine2D.Translation(10, 10) });
        using var bitmap = Decode(renderer.ExportPng(document, new(), true));
        Assert("Chinese text actually rasterizes on both lines", CountOpaque(bitmap, 10, 10, 370, 48) > 100 && CountOpaque(bitmap, 10, 48, 370, 90) > 100);
        var fallback = style.Clone(); fallback.FontFamily = "Arial";
        Assert("Latin font falls back for Chinese glyphs", renderer.MeasureText("浮绘中文", fallback).MissingGlyphCount == 0);
        var wrapped = renderer.MeasureText("中文测试自动换行", style, 60);
        Assert("text wrapping respects local width", wrapped.LineCount >= 4 && wrapped.Width <= 60);
        var number = Document(80, 80);
        number.Elements.Add(new() { Kind = ElementKind.Number, Width = 60, Height = 60, Number = 27, Style = new() { Stroke = 0xFFEE3333, FontSize = 30 }, Transform = Affine2D.Translation(10, 10) });
        using var label = Decode(renderer.ExportPng(number, new(), true));
        Assert("number badge renders circle and white digits", label.GetPixel(40, 14).Red > 200 && label.GetPixel(40, 14).Alpha > 200 &&
            Enumerable.Range(20, 40).Any(y => Enumerable.Range(20, 40).Any(x => { var c = label.GetPixel(x, y); return c.Red > 245 && c.Green > 245 && c.Blue > 245 && c.Alpha > 200; })));
    }

    private static void OrientationAndFormats()
    {
        byte[] jpeg = ImageBytes(40, 20, (x, y) => x < 20 ? (y < 10 ? SKColors.Red : SKColors.Blue) : (y < 10 ? SKColors.Lime : SKColors.Yellow), SKEncodedImageFormat.Jpeg);
        for (byte orientation = 1; orientation <= 8; orientation++)
        {
            var normalized = AssetImport.Normalize(AddExif(jpeg, orientation));
            Assert($"EXIF {orientation} has correct oriented dimensions", normalized.Width == (orientation >= 5 ? 20 : 40) && normalized.Height == (orientation >= 5 ? 40 : 20));
            var originalRed = orientation switch
            {
                2 => new PointD(34, 4), 3 => new PointD(34, 14), 4 => new PointD(5, 14),
                5 => new PointD(4, 5), 6 => new PointD(14, 5), 7 => new PointD(14, 34), 8 => new PointD(4, 34), _ => new PointD(5, 4)
            };
            var pixel = DecodePixel(normalized.PngBytes, (int)originalRed.X, (int)originalRed.Y);
            Assert($"EXIF {orientation} pixel mapping", pixel.Red > 220 && pixel.Green < 35 && pixel.Blue < 35);
        }
        var webp = AssetImport.Normalize(ImageBytes(16, 8, (_, _) => SKColors.Cyan, SKEncodedImageFormat.Webp));
        Assert("WebP import normalizes to decodable PNG", webp.Width == 16 && DecodePixel(webp.PngBytes, 5, 5).Green > 240);
        byte[] bmp = [0x42, 0x4D, 58, 0, 0, 0, 0, 0, 0, 0, 54, 0, 0, 0, 40, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0,
            1, 0, 24, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 255, 0];
        var normalizedBmp = AssetImport.Normalize(bmp);
        Assert("BMP import decodes bottom-up pixels", normalizedBmp.Width == 1 && DecodePixel(normalizedBmp.PngBytes, 0, 0) == SKColors.Red);
        bool invalid = false;
        try { AssetImport.Normalize([1, 2, 3, 4]); } catch (InvalidDataException) { invalid = true; }
        Assert("invalid image rejected explicitly", invalid);
    }

    private static void CacheAndCanvasState()
    {
        using var renderer = new SceneRenderer();
        var assets = new AssetLibrary();
        var document = Document(60, 60);
        document.Elements.Add(Shape(ElementKind.Rectangle, 20, 20, 0xFFFF0000, Affine2D.Identity));
        var image = new CanvasElement { Kind = ElementKind.Image, Width = 20, Height = 20, AssetId = assets.Add(ImageBytes(20, 20, (_, _) => SKColors.Blue)), Transform = Affine2D.Translation(30, 30) };
        document.Elements.Add(image);
        renderer.ExportPng(document, assets, true);
        long decodes = renderer.ImageDecodeCount, paths = renderer.PathBuildCount;
        for (int i = 0; i < 10; i++) renderer.ExportPng(document, assets, true);
        Assert("steady draws reuse decoded images and paths", decodes == renderer.ImageDecodeCount && paths == renderer.PathBuildCount);
        document.Elements[0].Transform = Affine2D.Rotation(12);
        renderer.ExportPng(document, assets, true);
        Assert("transform changes do not rebuild local geometry", paths == renderer.PathBuildCount);
        document.Elements[0].Width = 30;
        renderer.ExportPng(document, assets, true);
        Assert("geometry mutation invalidates path cache", paths + 1 == renderer.PathBuildCount);
        using var surface = SKSurface.Create(new SKImageInfo(100, 100));
        surface.Canvas.Translate(7, 9); surface.Canvas.ClipRect(new(0, 0, 80, 70));
        var matrix = surface.Canvas.TotalMatrix; var clip = surface.Canvas.DeviceClipBounds; int saves = surface.Canvas.SaveCount;
        renderer.Draw(surface.Canvas, document, assets);
        Assert("renderer preserves caller matrix clip and save stack", matrix == surface.Canvas.TotalMatrix && clip == surface.Canvas.DeviceClipBounds && saves == surface.Canvas.SaveCount);
        renderer.CacheByteLimit = 0; renderer.Draw(surface.Canvas, document, assets);
        Assert("image cache budget releases native image memory", renderer.CachedImageBytes == 0);
        renderer.Dispose();
        bool disposed = false;
        try { renderer.Draw(surface.Canvas, document, assets); } catch (ObjectDisposedException) { disposed = true; }
        Assert("disposed renderer fails explicitly", disposed);
    }

    private static SceneDocument Document(int width, int height) => new() { Viewport = new(0, 0, width, height) };
    private static CanvasElement Shape(ElementKind kind, int width, int height, uint fill, Affine2D transform) =>
        new() { Kind = kind, Width = width, Height = height, Style = new() { Fill = fill, StrokeWidth = 0 }, Transform = transform };
    private static SKBitmap Decode(byte[] data) => SKBitmap.Decode(data) ?? throw new Exception("Real encoded output failed decoding.");
    private static SKColor DecodePixel(byte[] data, int x, int y) { using var bitmap = Decode(data); return bitmap.GetPixel(x, y); }
    private static int CountOpaque(SKBitmap bitmap, int left = 0, int top = 0, int right = -1, int bottom = -1)
    {
        int count = 0;
        for (int y = top; y < (bottom < 0 ? bitmap.Height : bottom); y++)
            for (int x = left; x < (right < 0 ? bitmap.Width : right); x++) if (bitmap.GetPixel(x, y).Alpha > 0) count++;
        return count;
    }
    private static byte[] ImageBytes(int width, int height, Func<int, int, SKColor> pixel, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(width, height);
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) bitmap.SetPixel(x, y, pixel(x, y));
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(format, 100); return data.ToArray();
    }
    private static byte[] AddExif(byte[] jpeg, byte orientation)
    {
        byte[] exif = [69, 120, 105, 102, 0, 0, 73, 73, 42, 0, 8, 0, 0, 0, 1, 0, 18, 1, 3, 0, 1, 0, 0, 0, orientation, 0, 0, 0, 0, 0, 0, 0];
        using var output = new MemoryStream(); output.Write(jpeg, 0, 2); output.Write([255, 225, 0, (byte)(exif.Length + 2)]); output.Write(exif); output.Write(jpeg, 2, jpeg.Length - 2); return output.ToArray();
    }
    private static void Assert(string name, bool condition)
    {
        if (!condition) throw new InvalidOperationException("FAIL " + name);
        _checks++; Console.WriteLine("PASS " + name);
    }
}
