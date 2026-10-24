using System.Text;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class AnimatedImageRenderingTests
{
    public static void Run(Action<string, bool> check)
    {
        byte[] gif = Gif([
            new(0, 3, [1, 1, 1], 10, 1),
            new(1, 1, [2], 20, 3),
            new(2, 1, [3], 10, 2),
            new(0, 1, [4], 10, 1)
        ], 0);
        var normalized = AssetImport.Normalize(gif);
        check("GIF import retains original encoded animation and media type", normalized.MediaType == "image/gif" && normalized.PngBytes.SequenceEqual(gif));
        check("GIF import reports logical dimensions without aliasing caller data", normalized.Width == 3 && normalized.Height == 1 && !ReferenceEquals(gif, normalized.PngBytes));
        var assets = new AssetLibrary(); string id = assets.Add(normalized.PngBytes, normalized.MediaType);
        var document = Scene(id);
        using var renderer = new SceneRenderer();
        check("Animation detection reads metadata without frame decoding", renderer.HasAnimatedImages(document, assets) && renderer.AnimationFrameDecodeCount == 0);
        check("Animation damage stays within the projected image", renderer.AnimatedImageBounds(document, assets).SequenceEqual([SceneRenderer.ProjectedBounds(document, document.Elements[0]).Intersect(document.Viewport)]));
        Expect(0, [SKColors.Red, SKColors.Red, SKColors.Red], 100);
        Expect(99, [SKColors.Red, SKColors.Red, SKColors.Red], 1);
        check("Repeated sampling within one frame reuses its decoded pixels", renderer.AnimationFrameDecodeCount == 1);
        Expect(100, [SKColors.Red, SKColors.Lime, SKColors.Red], 200);
        Expect(299, [SKColors.Red, SKColors.Lime, SKColors.Red], 1);
        Expect(300, [SKColors.Red, SKColors.Red, SKColors.Blue], 100);
        Expect(400, [SKColors.Yellow, SKColors.Red, SKColors.Transparent], 100);
        Expect(500, [SKColors.Red, SKColors.Red, SKColors.Red], 100);
        Expect(400, [SKColors.Yellow, SKColors.Red, SKColors.Transparent], 100);
        check("Arbitrary seek re-composes required frames without retaining all decoded frames", renderer.AnimationFrameDecodeCount == 6);
        void Expect(int ms, SKColor[] expected, int delay)
        {
            renderer.AnimationTime = TimeSpan.FromMilliseconds(ms);
            using var bitmap = SKBitmap.Decode(renderer.ExportPng(document, assets, true));
            var actual = Enumerable.Range(0, 3).Select(x => bitmap.GetPixel(x, 0)).ToArray();
            check($"GIF composite at {ms}ms obeys local frame disposal and transparency ({string.Join(',', actual)})",
                actual.Zip(expected, (a, e) => e.Alpha == 0 ? a.Alpha == 0 : a == e).All(equal => equal));
            check($"GIF delay at {ms}ms respects frame duration", renderer.NextAnimationDelay == TimeSpan.FromMilliseconds(delay));
        }

        byte[] finite = Gif([new(0, 3, [1, 1, 1], 10, 1), new(0, 3, [3, 3, 3], 10, 1)], null);
        var finiteAssets = new AssetLibrary(); var finiteScene = Scene(finiteAssets.Add(finite, "image/gif"));
        using var finiteRenderer = new SceneRenderer { AnimationTime = TimeSpan.FromMilliseconds(200) };
        using (var stopped = SKBitmap.Decode(finiteRenderer.ExportPng(finiteScene, finiteAssets, true)))
            check("A finite animation stops at its last frame without scheduling endless redraws", stopped.GetPixel(0, 0) == SKColors.Blue && finiteRenderer.NextAnimationDelay is null);
        finiteRenderer.AnimationTime = TimeSpan.Zero;
        using (var restarted = SKBitmap.Decode(finiteRenderer.ExportPng(finiteScene, finiteAssets, true)))
            check("Caller-controlled time can restart a finite animation", restarted.GetPixel(0, 0) == SKColors.Red);
        byte[] repeat = Gif([new(0, 3, [1, 1, 1], 10, 1), new(0, 3, [3, 3, 3], 10, 1)], 1);
        var repeated = new AssetLibrary(); var repeatedScene = Scene(repeated.Add(repeat, "image/gif"));
        using var repeatRenderer = new SceneRenderer { AnimationTime = TimeSpan.FromMilliseconds(200) };
        using (var again = SKBitmap.Decode(repeatRenderer.ExportPng(repeatedScene, repeated, true)))
            check("Finite repetition count means one additional complete cycle", again.GetPixel(0, 0) == SKColors.Red && repeatRenderer.NextAnimationDelay is not null);
        repeatRenderer.AnimationTime = TimeSpan.FromMilliseconds(400);
        using (var stopped = SKBitmap.Decode(repeatRenderer.ExportPng(repeatedScene, repeated, true)))
            check("Finite repeats stop after their specified cycles", stopped.GetPixel(0, 0) == SKColors.Blue && repeatRenderer.NextAnimationDelay is null);

        // Decode the same GIF underneath a cached image effect. The effect must change with the frame.
        document.Elements[0].Effects.Add(new() { Kind = ImageEffectKind.Blur, Bounds = new(0, 0, 1, 1), Amount = .1 });
        renderer.AnimationTime = TimeSpan.Zero;
        byte[] firstEffect = renderer.ExportPng(document, assets, true);
        renderer.AnimationTime = TimeSpan.FromMilliseconds(100);
        byte[] secondEffect = renderer.ExportPng(document, assets, true);
        check("Image-effect cache includes animation frame identity", !firstEffect.SequenceEqual(secondEffect));
        document.Elements[0].Effects.Clear();
        using var cache = new OverlaySceneCache(renderer);
        using var target = SKSurface.Create(new SKImageInfo(3, 1));
        renderer.AnimationTime = TimeSpan.Zero; cache.Draw(target.Canvas, document, assets, 1, 0);
        target.Canvas.Clear(); renderer.AnimationTime = TimeSpan.FromMilliseconds(100); cache.Draw(target.Canvas, document, assets, 1, 0);
        using (var frame = SKBitmap.FromImage(target.Snapshot()))
            check("Unchanged document revision does not freeze animated images in raster tiles", frame.GetPixel(1, 0) == SKColors.Lime && cache.CachedRasterBytes == 0);
        check("Animation playback does not mutate element geometry or asset bytes", document.Elements[0].Width == 3 && assets.Get(id).Data.Span.SequenceEqual(gif));
        document.Elements[0].Visible = false;
        renderer.AnimationTime = TimeSpan.FromMilliseconds(300);
        using (var invisible = SKBitmap.Decode(renderer.ExportPng(document, assets, true)))
            check("Hidden animated objects do not schedule frame updates", renderer.NextAnimationDelay is null && !renderer.HasAnimatedImages(document, assets));
        document.Elements[0].Visible = true;
        renderer.CacheByteLimit = 0;
        _ = renderer.ExportPng(document, assets, true);
        check("Image memory budget evicts animation codec, buffers and display frame together", renderer.CachedImageBytes == 0);

        // A partially transparent WebP uses the same decoder and preserves its encoded source.
        using var red = new SKBitmap(3, 1); red.Erase(SKColors.Red);
        using var blue = new SKBitmap(3, 1); blue.Erase(SKColors.Transparent); blue.SetPixel(1, 0, SKColors.Blue);
        var webpFrames = new[] { new SKWebpEncoderFrame(red, TimeSpan.FromMilliseconds(70)), new SKWebpEncoderFrame(blue, TimeSpan.FromMilliseconds(130)) };
        using var encoded = SKWebpEncoder.EncodeAnimated(webpFrames, new(SKWebpEncoderCompression.Lossless, 100))
            ?? throw new InvalidOperationException("Animated WebP fixture encoding failed.");
        byte[] webp = encoded.ToArray(); var webpImport = AssetImport.Normalize(webp);
        check("Animated WebP import preserves all encoded frames and media type", webpImport.MediaType == "image/webp" && webpImport.PngBytes.SequenceEqual(webp));
        var webpAssets = new AssetLibrary(); var webpScene = Scene(webpAssets.Add(webp, webpImport.MediaType));
        using var webpRenderer = new SceneRenderer();
        using (var before = SKBitmap.Decode(webpRenderer.ExportPng(webpScene, webpAssets, true)))
            check("Animated WebP initially displays its first frame", before.GetPixel(0, 0) == SKColors.Red && webpRenderer.NextAnimationDelay == TimeSpan.FromMilliseconds(70));
        webpRenderer.AnimationTime = TimeSpan.FromMilliseconds(70);
        byte[] webpStill = webpRenderer.ExportPng(webpScene, webpAssets, true);
        using (var after = SKBitmap.Decode(webpStill))
            check("Animated WebP advances with source timing and real alpha", after.GetPixel(1, 0) == SKColors.Blue && after.GetPixel(0, 0).Alpha == 0 && webpRenderer.NextAnimationDelay == TimeSpan.FromMilliseconds(130));
        check("A static export at fixed playback time is deterministic", webpStill.SequenceEqual(webpRenderer.ExportPng(webpScene, webpAssets, true)));

        var together = Scene(id); string webpId = assets.Add(webp, "image/webp");
        together.Elements.Add(new() { Kind = ElementKind.Image, AssetId = webpId, Width = 3, Height = 1 });
        using var scheduling = new SceneRenderer();
        check("Whole-scene scheduling finds the earliest source frame boundary without decoding pixels",
            scheduling.GetNextAnimationDelay(together, assets, TimeSpan.FromMilliseconds(70)) == TimeSpan.FromMilliseconds(30) && scheduling.AnimationFrameDecodeCount == 0);
        using var emptySurface = SKSurface.Create(new SKImageInfo(3, 1));
        scheduling.Draw(emptySurface.Canvas, new() { Viewport = new(0, 0, 3, 1) }, assets);
        check("A later monitor draw with no image cannot erase whole-scene animation scheduling",
            scheduling.NextAnimationDelay is null && scheduling.GetNextAnimationDelay(together, assets, TimeSpan.FromMilliseconds(70)) == TimeSpan.FromMilliseconds(30));
        check("Finite finished sources have no next scheduled frame",
            scheduling.GetNextAnimationDelay(finiteScene, finiteAssets, TimeSpan.MaxValue) is null);
        together.Elements.Add(new() { Kind = ElementKind.LivePixelate, Width = 3, Height = 1, Effects = [new() { Kind = ImageEffectKind.Pixelate, Amount = 1 }] });
        scheduling.AnimationTime = TimeSpan.FromMilliseconds(70);
        _ = scheduling.ExportPng(together, assets, true);
        check("Repeated lower-scene sampling for live effects accumulates delays at one fixed time",
            scheduling.NextAnimationDelay == TimeSpan.FromMilliseconds(30));
        together.Elements.ForEach(element => element.Visible = false);
        check("Hidden scene sources need no animation timer", scheduling.GetNextAnimationDelay(together, assets, TimeSpan.Zero) is null);
        together.Elements[0].Visible = true; together.Elements[0].Transform = Affine2D.Translation(10, 0);
        check("Animation completely outside the viewport does not drive a redraw timer",
            !scheduling.HasAnimatedImages(together, assets) && scheduling.GetNextAnimationDelay(together, assets, TimeSpan.Zero) is null);
        byte[] zeroDelay = Gif([new(0, 3, [1, 1, 1], 0, 1), new(0, 3, [3, 3, 3], 0, 1)], 0);
        var zeroAssets = new AssetLibrary(); var zeroScene = Scene(zeroAssets.Add(zeroDelay, "image/gif"));
        check("Zero-duration source frames receive a bounded fallback instead of busy spinning",
            scheduling.GetNextAnimationDelay(zeroScene, zeroAssets, TimeSpan.Zero) == TimeSpan.FromMilliseconds(100));
        using var stillWebp = red.Encode(SKEncodedImageFormat.Webp, 100);
        var stillImport = AssetImport.Normalize(stillWebp.ToArray());
        check("Static WebP keeps the existing normalized PNG contract", stillImport.MediaType == "image/png" && stillImport.PngBytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71 }));
        bool incomplete = false;
        try { AssetImport.Normalize(gif[..^3]); } catch (InvalidDataException) { incomplete = true; }
        check("A declared later GIF frame with truncated pixels cannot pass frame-zero validation", incomplete);
        var oversized = gif.ToArray(); BitConverter.GetBytes((ushort)5000).CopyTo(oversized, 6); BitConverter.GetBytes((ushort)5000).CopyTo(oversized, 8);
        bool overBudget = false;
        try { AssetImport.Normalize(oversized); } catch (InvalidDataException) { overBudget = true; }
        check("Animation dimensions are bounded before allocating compositing surfaces", overBudget);

        // Metadata scales with frame count, decoded surfaces do not.
        byte[] many = Gif(Enumerable.Range(0, 150).Select(i => new GifFrame(0, 3, [(byte)(i % 3 + 1), 0, 0], 1, 1)).ToArray(), 0);
        var manyAssets = new AssetLibrary(); var manyScene = Scene(manyAssets.Add(many, "image/gif"));
        using var bounded = new SceneRenderer { AnimationTime = TimeSpan.FromMilliseconds(1490) };
        _ = bounded.ExportPng(manyScene, manyAssets, true);
        check("Seeking a long animation retains one displayed target rather than preloading every frame", bounded.AnimationFrameDecodeCount == 1);
        check("Animation memory is encoded bytes plus metadata and bounded frame buffers", bounded.CachedImageBytes <= many.Length + 150 * 72 + 3 * 3 * 4);
        bounded.AnimationTime = TimeSpan.FromMilliseconds(100);
        using (var earlier = SKBitmap.Decode(bounded.ExportPng(manyScene, manyAssets, true)))
            check("Rewinding a dependent-frame chain reconstructs the selected composite", earlier.GetPixel(0, 0) == SKColors.Lime && earlier.GetPixel(1, 0).Alpha == 0);

    }

    private static SceneDocument Scene(string id) => new()
    {
        Viewport = new(0, 0, 3, 1), Elements = [new() { Kind = ElementKind.Image, AssetId = id, Width = 3, Height = 1 }]
    };
    private sealed record GifFrame(int Left, int Width, byte[] Pixels, ushort Delay, int Disposal);

    // Independent tiny GIF fixture encoder. Repeated clear codes keep LZW at 4 bits, so no decoder
    // or production normalization is involved in defining the test frame/disposal expectations.
    private static byte[] Gif(GifFrame[] frames, ushort? repeat)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        writer.Write("GIF89a"u8); writer.Write((ushort)3); writer.Write((ushort)1); writer.Write((byte)0xF2); writer.Write((byte)0); writer.Write((byte)0);
        writer.Write(new byte[] { 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0, 255, 255, 255, 0, 255, 255, 255, 0, 255 });
        if (repeat is { } loops)
        { writer.Write(new byte[] { 0x21, 0xFF, 11 }); writer.Write("NETSCAPE2.0"u8); writer.Write(new byte[] { 3, 1 }); writer.Write(loops); writer.Write((byte)0); }
        foreach (var frame in frames)
        {
            writer.Write(new byte[] { 0x21, 0xF9, 4, (byte)((frame.Disposal << 2) | 1) }); writer.Write(frame.Delay); writer.Write((byte)0); writer.Write((byte)0);
            writer.Write((byte)0x2C); writer.Write((ushort)frame.Left); writer.Write((ushort)0); writer.Write((ushort)frame.Width); writer.Write((ushort)1); writer.Write((byte)0);
            var codes = new List<int>(); foreach (byte pixel in frame.Pixels) { codes.Add(8); codes.Add(pixel); } codes.Add(9);
            var packed = new byte[(codes.Count + 1) / 2]; for (int i = 0; i < codes.Count; i++) packed[i / 2] |= (byte)(codes[i] << ((i % 2) * 4));
            writer.Write((byte)3); writer.Write((byte)packed.Length); writer.Write(packed); writer.Write((byte)0);
        }
        writer.Write((byte)0x3B); writer.Flush(); return stream.ToArray();
    }
}
