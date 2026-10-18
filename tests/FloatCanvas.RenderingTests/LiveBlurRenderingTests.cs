using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class LiveBlurRenderingTests
{
    internal static void Run(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer(); var assets = new AssetLibrary();
        var view = new RectD(0, 0, 160, 120);
        var blur = Mask(50, 30, 50, 50, 6);
        var scene = new SceneDocument { Viewport = view, Elements = [blur] };
        check("blur is a dynamic filter and its strength expands the desktop ROI by three sigma", SceneRenderer.HasLiveEffects(scene) &&
            SceneRenderer.LiveEffectSamplingBounds(blur) == new RectD(32, 12, 86, 86));
        using var split = Backdrop(view, (x, _) => x < 50 ? SKColors.White : SKColors.Black);
        renderer.LiveBackdrop = split;
        using var rendered = Pixels(renderer, scene, assets);
        using var reference = FullBlur(160, 120, 6);
        var edge = rendered.GetPixel(51, 50);
        check("live blur samples outside the mask instead of clamping the mask edge", edge.Red is > 50 and < 180 && Math.Abs(edge.Red - reference.GetPixel(51, 50).Red) <= 2);
        check("blur writes only inside the visible mask despite padded sampling", rendered.GetPixel(48, 50).Alpha == 0 && rendered.GetPixel(101, 50).Alpha == 0 && rendered.GetPixel(80, 50).Alpha == 255);
        scene.Elements.Insert(0, Box(44, 30, 6, 50, 0xFFFF0000));
        scene.Elements.Add(Box(70, 35, 3, 35, 0xFF00FF00));
        using var lower = Pixels(renderer, scene, assets);
        check("blur samples lower vectors beyond its edge but upper vectors remain sharp", lower.GetPixel(51, 50).Red > lower.GetPixel(51, 50).Green && lower.GetPixel(71, 50) == SKColors.Lime);
        scene.Elements = [blur]; renderer.LiveBackdrop = null;
        using var missing = Pixels(renderer, scene, assets);
        check("missing live blur capture fails closed with opaque neutral coverage", missing.GetPixel(70, 50) == new SKColor(0xFF50545B));
        using var red = Backdrop(view, (_, _) => SKColors.Red); using var blue = Backdrop(view, (_, _) => SKColors.Blue);
        using var cache = new OverlaySceneCache(renderer);
        using var surface = SKSurface.Create(new SKImageInfo(160, 120, SKColorType.Bgra8888, SKAlphaType.Premul));
        renderer.LiveBackdrop = red; surface.Canvas.Clear(SKColors.Transparent); cache.DrawOnClearedRasterTarget(surface, scene, assets, 1, 1);
        using var first = surface.Snapshot(); using var firstPixels = SKBitmap.FromImage(first);
        renderer.LiveBackdrop = blue; surface.Canvas.Clear(SKColors.Transparent); cache.DrawOnClearedRasterTarget(surface, scene, assets, 1, 1);
        using var second = surface.Snapshot(); using var secondPixels = SKBitmap.FromImage(second);
        check("a fixed scene revision displays fresh blur backdrop frames without PNG or raster cache", firstPixels.GetPixel(70, 50) == SKColors.Red && secondPixels.GetPixel(70, 50) == SKColors.Blue && cache.CachedRasterBytes == 0 && assets.Count == 0);
        renderer.LiveBackdrop = split; blur.Transform = Affine2D.Translation(20, 30);
        using var moved = Pixels(renderer, scene, assets);
        check("moving a blur region resamples its new desktop location", moved.GetPixel(30, 50).Red > 245 && moved.GetPixel(65, 50).Red < 30);
        blur.Transform = Affine2D.Translation(50, 30); blur.Style.Opacity = .5; scene.LayerOpacity = .5;
        using var opacity = Pixels(renderer, scene, assets);
        check("live blur obeys element and whole-layer opacity once each", opacity.GetPixel(70, 50).Alpha is >= 63 and <= 65);
        blur.Style.Opacity = 1; scene.LayerOpacity = 1;
        var pixelate = new CanvasElement { Kind = ElementKind.LivePixelate, Width = 80, Height = 60, Transform = Affine2D.Translation(20, 20), Style = new() { StrokeWidth = 0 },
            Effects = [new() { Kind = ImageEffectKind.Pixelate, Amount = 17, Bounds = new(0, 0, 1, 1) }] };
        scene.Elements = [pixelate, blur, Mask(40, 40, 70, 50, 3)]; long builds = renderer.LiveEffectBuildCount;
        using var nested = Pixels(renderer, scene, assets);
        check("mixed pixelation and blur build each lower-prefix sample once per frame", renderer.LiveEffectBuildCount - builds == 3 && nested.GetPixel(60, 50).Alpha == 255);
        using var backgroundSurface = SKSurface.Create(new SKImageInfo(160, 120)); backgroundSurface.Canvas.Clear(SKColors.Red);
        using var backgroundImage = backgroundSurface.Snapshot(); using var encoded = backgroundImage.Encode(SKEncodedImageFormat.Png, 100);
        string id = assets.Add(encoded.ToArray());
        scene = new SceneDocument { Kind = DocumentKind.FrozenCapture, Viewport = view, Background = new() { Kind = ElementKind.Image, AssetId = id, Width = 160, Height = 120 }, Elements = [blur] };
        renderer.LiveBackdrop = blue;
        using var frozen = Pixels(renderer, scene, assets);
        check("frozen and transparent exports sample the frozen background instead of the live desktop", frozen.GetPixel(70, 50) == SKColors.Red && frozen.GetPixel(5, 5).Alpha == 0);
        scene.Elements.Insert(0, Box(40, 20, 70, 70, 0xFF00FF00));
        renderer.LiveEffectContext = scene;
        var selection = new SceneDocument { Viewport = view, Elements = [blur.Clone()] };
        using var selected = Pixels(renderer, selection, assets);
        check("isolated blur selection and stamps retain lower document context", selected.GetPixel(70, 50).Green > 245 && selected.GetPixel(70, 50).Red < 10 && selected.GetPixel(5, 5).Alpha == 0);
        renderer.LiveEffectContext = null; renderer.LiveBackdrop = null;
        blur.Style.StrokeWidth = 100;
        blur.Transform = Affine2D.Translation(-80, -40).Compose(Affine2D.Rotation(25)).Compose(Affine2D.Scale(1.5, .75));
        var center = blur.Transform.TransformPoint(new(25, 25));
        check("blur geometry remains editable transformed mask geometry without a visual stroke", ElementGeometry.Bounds(blur) == ElementGeometry.Bounds(blur, false) && ElementGeometry.HitTest(blur, center, 0));
        blur.Visible = false; check("hidden blur does not keep desktop capture active", !SceneRenderer.HasLiveEffects(new() { Viewport = new(-200, -200, 400, 400), Elements = [blur] }));
    }
    private static CanvasElement Mask(double x, double y, double width, double height, double amount) => new()
    { Kind = ElementKind.LiveBlur, Width = width, Height = height, Transform = Affine2D.Translation(x, y), Style = new() { StrokeWidth = 0 }, Effects = [new() { Kind = ImageEffectKind.Blur, Amount = amount, Bounds = new(0, 0, 1, 1) }] };
    private static CanvasElement Box(double x, double y, double width, double height, uint fill) => new()
    { Kind = ElementKind.Rectangle, Width = width, Height = height, Transform = Affine2D.Translation(x, y), Style = new() { Fill = fill, StrokeWidth = 0 } };
    private static SKBitmap Pixels(SceneRenderer renderer, SceneDocument scene, AssetLibrary assets) => SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
    private static DesktopBackdrop Backdrop(RectD bounds, Func<int, int, SKColor> color)
    {
        using var bitmap = new SKBitmap((int)bounds.Width, (int)bounds.Height);
        for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++) bitmap.SetPixel(x, y, color(x, y));
        bitmap.SetImmutable(); return new([new(bounds, SKImage.FromBitmap(bitmap))]);
    }
    private static SKBitmap FullBlur(int width, int height, float sigma)
    {
        using var raw = SKSurface.Create(new SKImageInfo(width, height)); raw.Canvas.Clear(SKColors.Black);
        using var white = new SKPaint { Color = SKColors.White }; raw.Canvas.DrawRect(0, 0, 50, height, white);
        using var image = raw.Snapshot(); using var result = SKSurface.Create(new SKImageInfo(width, height));
        using var filter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp); using var paint = new SKPaint { ImageFilter = filter };
        result.Canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), paint);
        using var final = result.Snapshot(); return SKBitmap.FromImage(final);
    }
}
