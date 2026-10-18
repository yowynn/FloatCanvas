using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class IntermediateSurfaceRenderingTests
{
    internal static void Run(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer();
        var assets = new AssetLibrary();
        var scene = new SceneDocument
        {
            Viewport = new(0, 0, 32, 24),
            Elements = [Mask(ElementKind.LivePixelate, 0, 16), Mask(ElementKind.LiveBlur, 16, 16)]
        };
        using var red = Backdrop(SKColors.Red);
        using var blue = Backdrop(SKColors.Blue);
        renderer.LiveBackdrop = red;
        using var first = SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
        check("raster exports keep both live filter types on CPU and preserve their pixels",
            renderer.GpuIntermediateSurfaceCount == 0 && renderer.RasterIntermediateSurfaceCount == 3 &&
            first.GetPixel(8, 12) == SKColors.Red && first.GetPixel(24, 12) == SKColors.Red);
        renderer.LiveBackdrop = blue;
        using var second = SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
        check("CPU live intermediates are rebuilt for the new backdrop instead of reusing the prior frame",
            renderer.RasterIntermediateSurfaceCount == 6 && second.GetPixel(8, 12) == SKColors.Blue && second.GetPixel(24, 12) == SKColors.Blue);

        using var raw = SKSurface.Create(new SKImageInfo(32, 24));
        raw.Canvas.Clear(SKColors.Red);
        using var sourceImage = raw.Snapshot();
        using var encoded = sourceImage.Encode(SKEncodedImageFormat.Png, 100);
        var image = new CanvasElement
        {
            Kind = ElementKind.Image, AssetId = assets.Add(encoded.ToArray()), Width = 32, Height = 24,
            Effects = [new() { Kind = ImageEffectKind.Blur, Bounds = new(0, 0, 1, 1), Amount = 2 },
                new() { Kind = ImageEffectKind.Pixelate, Bounds = new(0, 0, 1, 1), Amount = 4 }]
        };
        scene.Elements = [image];
        long previousAllocations = renderer.RasterIntermediateSurfaceCount;
        using var effected = SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
        long cachedBytes = renderer.CachedImageBytes;
        using var repeated = SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
        check("ordinary raster image effects retain their existing cross-frame cache",
            renderer.RasterIntermediateSurfaceCount == previousAllocations + 2 && cachedBytes == renderer.CachedImageBytes &&
            effected.GetPixel(16, 12) == SKColors.Red && repeated.GetPixel(16, 12) == SKColors.Red);
        image.Effects[1].Amount = 8;
        using var modified = SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
        check("changing image filter parameters invalidates CPU intermediates without enabling a GPU",
            renderer.RasterIntermediateSurfaceCount == previousAllocations + 4 && renderer.GpuIntermediateSurfaceCount == 0 &&
            modified.GetPixel(16, 12) == SKColors.Red);
        renderer.LiveBackdrop = null;
    }

    private static CanvasElement Mask(ElementKind kind, double left, double width) => new()
    {
        Kind = kind, Width = width, Height = 24, Transform = Affine2D.Translation(left, 0),
        Effects = [new() { Kind = kind == ElementKind.LiveBlur ? ImageEffectKind.Blur : ImageEffectKind.Pixelate,
            Bounds = new(0, 0, 1, 1), Amount = 2 }]
    };

    private static DesktopBackdrop Backdrop(SKColor color)
    {
        using var surface = SKSurface.Create(new SKImageInfo(32, 24));
        surface.Canvas.Clear(color);
        return new([new(new(0, 0, 32, 24), surface.Snapshot())]);
    }
}
