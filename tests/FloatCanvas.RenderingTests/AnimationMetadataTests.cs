using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class AnimationMetadataTests
{
    internal static void Run(Action<string, bool> check)
    {
        using var bitmap = new SKBitmap(2048, 2048);
        bitmap.Erase(SKColors.Red);
        using var still = bitmap.Encode(SKEncodedImageFormat.Webp, 90);
        var assets = new AssetLibrary();
        string id = assets.Add(still.ToArray(), "image/webp");
        var document = new SceneDocument { Viewport = new(0, 0, 3840, 2160),
            Elements = [new() { Kind = ElementKind.Image, AssetId = id, Width = 2048, Height = 2048 }] };
        using var renderer = new SceneRenderer();
        check("static WebP animation detection allocates no decoded image or animation pixels",
            !renderer.HasAnimatedImages(document, assets) && renderer.ImageDecodeCount == 0 &&
            renderer.AnimationFrameDecodeCount == 0 && renderer.CachedImageBytes == 0);
        long reads = renderer.AnimationMetadataReadCount;
        var stillSchedule = renderer.CaptureAnimationSchedule(document, assets, TimeSpan.Zero);
        check("static WebP metadata is cached by resource identity without another codec query",
            !stillSchedule.HasAnimations && stillSchedule.NextDelay is null && stillSchedule.DirtyBounds.Count == 0 &&
            renderer.AnimationMetadataReadCount == reads && renderer.CachedAnimationMetadataBytes == 0 && renderer.CachedImageBytes == 0);

        using var red = new SKBitmap(8, 8); red.Erase(SKColors.Red);
        using var blue = new SKBitmap(8, 8); blue.Erase(SKColors.Blue);
        using var animated = SKWebpEncoder.EncodeAnimated(
            [new(red, TimeSpan.FromMilliseconds(70)), new(blue, TimeSpan.FromMilliseconds(130))],
            new(SKWebpEncoderCompression.Lossless, 100))!;
        string animationId = assets.Add(animated.ToArray(), "image/webp");
        document.Elements = [new() { Kind = ElementKind.Image, AssetId = animationId, Width = 80, Height = 40,
            Transform = Affine2D.Translation(20, 30) }];
        var published = renderer.CaptureAnimationSchedule(document, assets, TimeSpan.FromMilliseconds(70));
        check("published animation schedule carries exact source timing and projected damage without pixels",
            published.HasAnimations && published.SampleTime == TimeSpan.FromMilliseconds(70) &&
            published.NextDelay == TimeSpan.FromMilliseconds(130) && published.DirtyBounds.SequenceEqual([new RectD(20, 30, 80, 40)]) &&
            renderer.ImageDecodeCount == 0 && renderer.AnimationFrameDecodeCount == 0 && renderer.CachedAnimationMetadataBytes == 16);
        document.Elements[0].Visible = false;
        var hidden = renderer.CaptureAnimationSchedule(document, assets, TimeSpan.FromMilliseconds(80));
        check("later live scene changes cannot mutate an already published schedule",
            published.HasAnimations && published.NextDelay == TimeSpan.FromMilliseconds(130) &&
            published.DirtyBounds.SequenceEqual([new RectD(20, 30, 80, 40)]) &&
            !hidden.HasAnimations && hidden.NextDelay is null && hidden.DirtyBounds.Count == 0);
        assets.Prune([]);
        _ = renderer.CaptureAnimationSchedule(document, assets, TimeSpan.Zero);
        check("timing metadata releases resources no longer retained by the worker asset snapshot",
            renderer.CachedAnimationMetadataBytes == 0);
    }
}
