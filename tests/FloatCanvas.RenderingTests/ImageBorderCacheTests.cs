using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class ImageBorderCacheTests
{
    internal static void Run(Action<string, bool> check)
    {
        StaticAndParameters(check);
        IntegerTranslation(check);
        AnimatedFrames(check);
        EmptyFrames(check);
        MemoryAndLifetime(check);
        PixelAccuracy(check);
        QualityNegativeControls(check);
        OutputTilesAndSmallSamples(check);
        FragmentedReuse(check);
    }

    private static void StaticAndParameters(Action<string, bool> check)
    {
        var assets = new AssetLibrary();
        var scene = Scene(assets.Add(Png(64, 48, Subject)));
        var image = scene.Elements[0];
        using var renderer = new SceneRenderer();
        using (var first = Draw(renderer, scene, assets))
            check("first image draw bakes an owned border mask lazily", Count(renderer, "ImageBorderMaskBuildCount") > 0 &&
                Count(renderer, "ImageBorderSupportBuildCount") > 0 && Count(renderer, "CachedImageBorderBytes") > 0 && first.GetPixel(53, 54).Alpha > 0);
        long builds = Count(renderer, "ImageBorderMaskBuildCount"), support = Count(renderer, "ImageBorderSupportBuildCount");
        for (int frame = 0; frame < 12; frame++) using (Draw(renderer, scene, assets)) { }
        check("unchanged static image redraws reuse baked pixels without rescanning source alpha", Count(renderer, "ImageBorderMaskBuildCount") == builds &&
            Count(renderer, "ImageBorderSupportBuildCount") == support && Count(renderer, "ImageBorderMaskHitCount") >= 12);
        image.Style.Stroke = 0x8020DD55;
        using (var tinted = Draw(renderer, scene, assets))
        using (var expected = Reference(scene, assets))
            check("changing border color tints the same baked mask without regeneration (builds " + Count(renderer, "ImageBorderMaskBuildCount") + ", expected " + builds + ")", Count(renderer, "ImageBorderMaskBuildCount") == builds && Diagnose("border-color", tinted, expected) <= 1);
        image.Style.Opacity = .5;
        using (var faded = Draw(renderer, scene, assets))
        using (var expected = Reference(scene, assets))
            check("changing element opacity reuses the border mask and composes alpha once (builds " + Count(renderer, "ImageBorderMaskBuildCount") + ", expected " + builds + ")", Count(renderer, "ImageBorderMaskBuildCount") == builds && Diagnose("element-opacity", faded, expected) <= 1);
        image.Style.Opacity = 1;
        image.Style.Stroke = 0xFFFF4050;

        Change("border width", () => image.ImageBorderWidth = 6, false);
        Change("displayed crop", () => image.ImageCrop = new(.1, .05, .8, .85), true);
        Change("body dimensions", () => { image.Width = 72; image.Height = 44; }, true);
        Change("source image", () => image.AssetId = assets.Add(Png(64, 48, (x, y) => x is >= 28 and < 56 && y is >= 7 and < 38 ? SKColors.Cyan : SKColors.Transparent)), true);
        Change("image effect", () => image.Effects = [new() { Kind = ImageEffectKind.Redact, Bounds = new(.1, .1, .2, .4), Color = 0xFF102030 }], true);
        Change("effect parameters", () => image.Effects[0].Bounds = new(.4, .1, .2, .5), true);
        check("all border cache memory remains included in the image budget", renderer.CachedImageBytes >= Count(renderer, "CachedImageBorderBytes") && renderer.CachedImageBytes <= renderer.CacheByteLimit);

        void Change(string name, Action change, bool changesSupport)
        {
            long before = Count(renderer, "ImageBorderMaskBuildCount"), beforeSupport = Count(renderer, "ImageBorderSupportBuildCount");
            change();
            check(name + " invalidation is lazy until the next draw", Count(renderer, "ImageBorderMaskBuildCount") == before && Count(renderer, "ImageBorderSupportBuildCount") == beforeSupport);
            using var actual = Draw(renderer, scene, assets);
            using var expected = Reference(scene, assets);
            var quality = CheckQuality(name, actual, expected);
            check(name + " regenerates the required baked border with current pixels (mask builds " + before + "→" + Count(renderer, "ImageBorderMaskBuildCount") + ", max channel delta " + quality.Maximum + ")", Count(renderer, "ImageBorderMaskBuildCount") > before && quality.Passed);
            check(name + " only regenerates alpha support when its geometry or source changes", changesSupport
                ? Count(renderer, "ImageBorderSupportBuildCount") > beforeSupport : Count(renderer, "ImageBorderSupportBuildCount") == beforeSupport);
        }
    }

    private static void AnimatedFrames(Action<string, bool> check)
    {
        var assets = new AssetLibrary();
        byte[] data = Animation();
        var scene = Scene(assets.Add(data, "image/webp"));
        using var renderer = new SceneRenderer();
        using var first = Draw(renderer, scene, assets);
        long firstBuilds = Count(renderer, "ImageBorderMaskBuildCount"), firstSupport = Count(renderer, "ImageBorderSupportBuildCount");
        check("animation initially bakes only its currently requested frame", firstBuilds > 0 && firstSupport == 1);
        renderer.AnimationTime = TimeSpan.FromMilliseconds(60);
        using var second = Draw(renderer, scene, assets);
        check("a new animation frame gets its own lazy mask and correct silhouette", Count(renderer, "ImageBorderMaskBuildCount") > firstBuilds &&
            Count(renderer, "ImageBorderSupportBuildCount") == firstSupport + 1 && Difference(first, second) > 20);
        renderer.AnimationTime = TimeSpan.FromMilliseconds(140);
        using var third = Draw(renderer, scene, assets);
        long allBuilds = Count(renderer, "ImageBorderMaskBuildCount"), allSupport = Count(renderer, "ImageBorderSupportBuildCount");
        check("third requested animation frame is baked without eager unrelated work", allSupport == 3 && Difference(second, third) > 20);
        for (int loop = 0; loop < 4; loop++)
        {
            renderer.AnimationTime = TimeSpan.Zero;
            using (var repeated = Draw(renderer, scene, assets)) check("loop " + loop + " reuses the first frame's baked mask", Difference(first, repeated) == 0);
            renderer.AnimationTime = TimeSpan.FromMilliseconds(60);
            using (var repeated = Draw(renderer, scene, assets)) check("loop " + loop + " reuses the second frame's baked mask", Difference(second, repeated) == 0);
            renderer.AnimationTime = TimeSpan.FromMilliseconds(140);
            using (var repeated = Draw(renderer, scene, assets)) check("loop " + loop + " reuses the third frame's baked mask", Difference(third, repeated) == 0);
        }
        check("replaying retained animation frames does not rebuild masks or rescan alpha", Count(renderer, "ImageBorderMaskBuildCount") == allBuilds &&
            Count(renderer, "ImageBorderSupportBuildCount") == allSupport && Count(renderer, "ImageBorderMaskHitCount") >= 12);
        scene.Elements[0].ImageBorderWidth = 7;
        renderer.AnimationTime = TimeSpan.Zero;
        using (Draw(renderer, scene, assets)) { }
        long changed = Count(renderer, "ImageBorderMaskBuildCount");
        renderer.AnimationTime = TimeSpan.FromMilliseconds(60);
        using (Draw(renderer, scene, assets)) { }
        check("an animated width edit invalidates each frame's baked mask on demand", changed > allBuilds && Count(renderer, "ImageBorderMaskBuildCount") > changed);
        check("animated mask caching preserves the encoded source bytes", assets.Get(scene.Elements[0].AssetId!).Data.Span.SequenceEqual(data));
    }

    private static void MemoryAndLifetime(Action<string, bool> check)
    {
        var assets = new AssetLibrary();
        var scene = Scene(assets.Add(Animation(), "image/webp"));
        using var renderer = new SceneRenderer();
        using (Draw(renderer, scene, assets)) { }
        renderer.AnimationTime = TimeSpan.FromMilliseconds(60);
        using (Draw(renderer, scene, assets)) { }
        long twoFrames = Count(renderer, "CachedImageBorderBytes");
        Limit(renderer, twoFrames);
        renderer.AnimationTime = TimeSpan.FromMilliseconds(140);
        using (Draw(renderer, scene, assets)) { }
        check("animation frame retention obeys a budget that cannot hold every requested frame", Count(renderer, "CachedImageBorderBytes") <= twoFrames);
        long beforeRecent = Count(renderer, "ImageBorderMaskBuildCount");
        renderer.AnimationTime = TimeSpan.FromMilliseconds(60);
        using (Draw(renderer, scene, assets)) { }
        check("memory pressure retains recently used animation frame masks (builds " + Count(renderer, "ImageBorderMaskBuildCount") + ", expected " + beforeRecent + ")", Count(renderer, "ImageBorderMaskBuildCount") == beforeRecent);
        long beforeOld = Count(renderer, "ImageBorderMaskBuildCount");
        renderer.AnimationTime = TimeSpan.Zero;
        using (Draw(renderer, scene, assets)) { }
        check("evicted old animation frames regenerate safely when requested again", Count(renderer, "ImageBorderMaskBuildCount") > beforeOld && Count(renderer, "CachedImageBorderBytes") <= twoFrames);

        Limit(renderer, 1);
        using (var limitedBorder = Draw(renderer, scene, assets))
        using (var expected = Reference(scene, assets))
            check("the dedicated border budget bounds its own support and mask allocations (bytes " + Count(renderer, "CachedImageBorderBytes") + ")", Count(renderer, "CachedImageBorderBytes") <= 1 && Diagnose("dedicated-one-byte-budget", limitedBorder, expected) <= 1);
        Limit(renderer, 64L * 1024 * 1024);

        renderer.CacheByteLimit = 1;
        using (var limited = Draw(renderer, scene, assets))
        using (var expected = Reference(scene, assets))
            check("a tiny total budget falls back without changing image pixels or retaining oversized masks (bytes " + renderer.CachedImageBytes + ")", renderer.CachedImageBytes <= 1 && Diagnose("global-one-byte-budget", limited, expected) <= 1);
        renderer.CacheByteLimit = 0;
        using (var uncached = Draw(renderer, scene, assets))
        using (var expected = Reference(scene, assets))
            check("zero budget releases all image and mask caches and still draws correctly (bytes " + renderer.CachedImageBytes + ")", renderer.CachedImageBytes == 0 && Count(renderer, "CachedImageBorderBytes") == 0 && Diagnose("zero-byte-budget", uncached, expected) <= 1);
        renderer.CacheByteLimit = 8 * 1024 * 1024;
        scene.Elements[0].Effects = [new() { Kind = ImageEffectKind.Blur, Bounds = new(0, 0, 1, 1), Amount = 2 }];
        using (Draw(renderer, scene, assets)) { }
        renderer.CacheByteLimit = 1;
        using (var constrained = Draw(renderer, scene, assets))
            check("effect and mask allocations under pressure do not retain disposed source resources", renderer.CachedImageBytes <= 1 && constrained.Pixels.Any(pixel => pixel.Alpha > 0));
        renderer.Dispose();
        check("renderer disposal releases all retained border masks and frame supports", Count(renderer, "CachedImageBorderBytes") == 0 && renderer.CachedImageBytes == 0);
    }

    private static void EmptyFrames(Action<string, bool> check)
    {
        var assets = new AssetLibrary();
        var scene = Scene(assets.Add(Png(32, 24, (_, _) => SKColors.Transparent)));
        using (var renderer = new SceneRenderer())
        {
            using (var first = Draw(renderer, scene, assets))
                check("transparent static images cache an empty support without baking blank mask pixels", first.Pixels.All(pixel => pixel.Alpha == 0) &&
                    renderer.ImageBorderSupportBuildCount == 1 && renderer.ImageBorderMaskBuildCount == 0 && renderer.CachedImageBorderBytes > 0 && renderer.CachedImageBorderBytes < 32 * 1024);
            for (int repeat = 0; repeat < 6; repeat++) using (Draw(renderer, scene, assets)) { }
            check("repeated transparent static draws do not rescan alpha or allocate empty masks", renderer.ImageBorderSupportBuildCount == 1 &&
                renderer.ImageBorderMaskBuildCount == 0 && renderer.CachedImageBorderBytes <= renderer.ImageBorderCacheByteLimit);
            renderer.ImageBorderCacheByteLimit = 0;
            using (Draw(renderer, scene, assets)) { }
            check("empty-support metadata is released when the border budget is disabled", renderer.CachedImageBorderBytes == 0 && renderer.ImageBorderMaskBuildCount == 0);
        }

        using var blank = new SKBitmap(32, 24); blank.Erase(SKColors.Transparent);
        using var visible = new SKBitmap(32, 24); visible.Erase(SKColors.Transparent);
        using (var canvas = new SKCanvas(visible))
        { using var paint = new SKPaint { Color = SKColors.Blue }; canvas.DrawRect(8, 6, 16, 12, paint); }
        using var animation = SKWebpEncoder.EncodeAnimated([new(blank, TimeSpan.FromMilliseconds(50)), new(visible, TimeSpan.FromMilliseconds(90))],
            new(SKWebpEncoderCompression.Lossless, 100)) ?? throw new InvalidOperationException("Transparent-frame cache animation encoding failed.");
        scene.Elements[0].AssetId = assets.Add(animation.ToArray(), "image/webp");
        using var animated = new SceneRenderer();
        using (var first = Draw(animated, scene, assets))
            check("a transparent first animation frame creates no eager mask for its future visible frame", first.Pixels.All(pixel => pixel.Alpha == 0) &&
                animated.ImageBorderSupportBuildCount == 1 && animated.ImageBorderMaskBuildCount == 0);
        animated.AnimationTime = TimeSpan.FromMilliseconds(50);
        using var visibleFrame = Draw(animated, scene, assets);
        long builds = animated.ImageBorderMaskBuildCount;
        check("the first requested visible frame is baked after its transparent predecessor", visibleFrame.GetPixel(68, 52) == SKColors.Blue &&
            animated.ImageBorderSupportBuildCount == 2 && builds > 0);
        for (int loop = 0; loop < 3; loop++)
        {
            animated.AnimationTime = TimeSpan.Zero;
            using (var transparentFrame = Draw(animated, scene, assets))
                check("transparent animation loop " + loop + " reuses empty support without stray border pixels", transparentFrame.Pixels.All(pixel => pixel.Alpha == 0));
            animated.AnimationTime = TimeSpan.FromMilliseconds(50);
            using (var repeated = Draw(animated, scene, assets))
                check("visible animation loop " + loop + " reuses its retained baked mask", Difference(visibleFrame, repeated) == 0);
        }
        check("alternating transparent and visible frames retain only their bounded requested caches", animated.ImageBorderSupportBuildCount == 2 &&
            animated.ImageBorderMaskBuildCount == builds && animated.ImageBorderMaskHitCount > 0 && animated.CachedImageBorderBytes <= animated.ImageBorderCacheByteLimit);
    }

    private static void PixelAccuracy(Action<string, bool> check)
    {
        var assets = new AssetLibrary(); var scene = Scene(assets.Add(Png(64, 48, Subject)));
        scene.Elements[0].Style.Stroke = 0x89FF4050;
        using var cached = new SceneRenderer();
        using var reference = Uncached();
        Compare("native pixel phase", SKMatrix.Identity, null);
        Compare("fractional device translation", new(1, 0, .375f, 0, 1, .625f, 0, 0, 1), null);
        Compare("fractional rectangle clip applied once", SKMatrix.Identity, new(48.375f, 33.625f, 80.875f, 70.125f));
        using (var builder = new SKPathBuilder())
        {
            builder.AddCircle(62.375f, 51.625f, 23.5f);
            using var shapeClip = builder.Detach();
            using var actual = Draw(cached, scene, assets, shapeClip: shapeClip, sizeX: 512, sizeY: 384);
            using var expected = Draw(reference, scene, assets, shapeClip: shapeClip, sizeX: 512, sizeY: 384);
            int difference = Diagnose("antialiased-path-clip", actual, expected);
            check("antialiased path clipping is applied only once to the cached border (max channel delta " + difference + ")", difference <= 1);
        }
        scene.LayerOpacity = .6; scene.Elements[0].Style.Opacity = .65;
        Compare("nested canvas and element opacity", SKMatrix.Identity, null);
        scene.LayerOpacity = 1; scene.Elements[0].Style.Opacity = 1;
        scene.Elements[0].Transform = Affine2D.Translation(90.375, 45.625).Compose(Affine2D.Rotation(23));
        Compare("rotated alpha edge", SKMatrix.Identity, null);
        scene.Elements[0].Transform = Affine2D.Translation(2.375, 1.625);
        Compare("twenty-fold magnification", new(20, 0, 0, 0, 20, 0, 0, 0, 1), null);
        Compare("anisotropic magnification", new(7, 0, .25f, 0, 3, .75f, 0, 0, 1), null);
        scene.Elements[0].Transform = Affine2D.Translation(26, 21);
        scene.Elements[0].ProjectivePlacement = new(1.3, .1, 0, .03, 1.2, 0, .002, .0006, 1);
        Compare("projective image placement", SKMatrix.Identity, null);
        scene.Elements[0].ProjectivePlacement = null;
        scene.Elements[0].Width = 260; scene.Elements[0].Height = 190;
        scene.Elements[0].Transform = Affine2D.Translation(180.375, 75.625);
        scene.Viewport = new(0, 0, 512, 384);
        Compare("mask tile boundaries under fractional placement", SKMatrix.Identity, null);

        void Compare(string name, SKMatrix matrix, SKRect? clip)
        {
            using var actual = Draw(cached, scene, assets, matrix, clip, 512, 384);
            using var expected = Draw(reference, scene, assets, matrix, clip, 512, 384);
            bool exact = name is "twenty-fold magnification" or "nested canvas and element opacity";
            var quality = exact ? AssessQuality(actual, expected, 1) : CheckQuality(name, actual, expected,
                name == "fractional rectangle clip applied once" ? 12 : 6);
            if (exact)
            {
                Console.WriteLine($"BORDER_CACHE_QUALITY {name}: max={quality.Maximum} stable-violations={quality.StableViolations} edge-violations={quality.EdgeViolations} edge-limit=1");
                if (!quality.Passed) Diagnose(name, actual, expected);
            }
            check(name + " preserves uncached border sharpness (max channel delta " + quality.Maximum + ")", quality.Passed);
            long builds = Count(cached, "ImageBorderMaskBuildCount");
            using var repeated = Draw(cached, scene, assets, matrix, clip, 512, 384);
            check(name + " reuses the correctly resolved device mask (builds " + Count(cached, "ImageBorderMaskBuildCount") + ", expected " + builds + ")", Difference(actual, repeated) == 0 && Count(cached, "ImageBorderMaskBuildCount") == builds);
        }
    }

    private static void OutputTilesAndSmallSamples(Action<string, bool> check)
    {
        var assets = new AssetLibrary(); var scene = Scene(assets.Add(Png(64, 48, Subject)));
        var image = scene.Elements[0]; image.Width = 100; image.Height = 80;
        image.Transform = Affine2D.Translation(212.375, 60.625);
        scene.Viewport = new(0, 0, 512, 160);
        using var renderer = new SceneRenderer();
        using var firstOutput = Draw(renderer, scene, assets, sizeX: 256);
        var secondOrigin = new SKMatrix(1, 0, -256, 0, 1, 0, 0, 0, 1);
        using (var secondOutput = Draw(renderer, scene, assets, secondOrigin, sizeX: 256))
        using (var reference = Uncached())
        using (var expected = Draw(reference, scene, assets, secondOrigin, sizeX: 256))
            check("separate physical output origins retain the same edge pixels", Diagnose("second-output", secondOutput, expected) <= 1);
        long built = Count(renderer, "ImageBorderMaskBuildCount");
        using (var firstAgain = Draw(renderer, scene, assets, sizeX: 256))
            check("returning to a retained physical output reuses its correctly phased masks", Count(renderer, "ImageBorderMaskBuildCount") == built && Difference(firstOutput, firstAgain) == 0);

        image.Width = 5_000_000; image.Height = 4_000_000;
        image.Transform = Affine2D.Identity; image.ImageBorderWidth = 100_000;
        scene.Viewport = new(0, 0, image.Width, image.Height);
        using var small = new SceneRenderer();
        using var direct = Uncached();
        var shrink = new SKMatrix(.00002f, 0, 0, 0, .00002f, 0, 0, 0, 1);
        var onePixel = new SKRect(25, 24, 26, 25);
        using var sampled = Draw(small, scene, assets, shrink, onePixel, 64, 64);
        using var expectedSample = Draw(direct, scene, assets, shrink, onePixel, 64, 64);
        check("a one-pixel sample of a huge logical image allocates only a bounded device mask", Count(small, "CachedImageBorderBytes") < 128 * 1024 &&
            sampled.GetPixel(25, 24).Alpha > 0 && Difference(sampled, expectedSample) <= 1);
    }

    private static void IntegerTranslation(Action<string, bool> check)
    {
        var assets = new AssetLibrary(); var scene = Scene(assets.Add(Png(64, 48, Subject)));
        scene.Elements[0].Transform = Affine2D.Translation(36.375, 28.625);
        var originalScene = scene.Clone();
        using var renderer = new SceneRenderer();
        using var first = Draw(renderer, scene, assets);
        long builds = Count(renderer, "ImageBorderMaskBuildCount"), supports = Count(renderer, "ImageBorderSupportBuildCount");
        scene.Elements[0].Transform = Affine2D.Translation(44.375, 34.625);
        using var translated = Draw(renderer, scene, assets);
        long afterBuilds = Count(renderer, "ImageBorderMaskBuildCount"), afterSupports = Count(renderer, "ImageBorderSupportBuildCount");
        using var nativeFirst = Reference(originalScene, assets);
        using var expected = Reference(scene, assets);
        int shiftDifference = ShiftDifference(first, translated, 8, 6), nativeShiftDifference = ShiftDifference(nativeFirst, expected, 8, 6);
        Console.WriteLine($"BORDER_CACHE_INTEGER_TRANSLATION mask={builds}→{afterBuilds} support={supports}→{afterSupports} cached-shift-delta={shiftDifference} native-shift-delta={nativeShiftDifference}");
        check("integer device translation reuses a fully visible baked border at the same fractional phase (mask " + builds + "→" + afterBuilds + ", support " + supports + "→" + afterSupports + ")", afterBuilds == builds && afterSupports == supports);
        check("integer device translation adds no shift error beyond native image sampling (cached " + shiftDifference + ", native " + nativeShiftDifference + ")", shiftDifference <= nativeShiftDifference);
        check("integer device translation retains direct-render edge coverage", Diagnose("integer-translation", translated, expected) <= 1);
    }

    private static void FragmentedReuse(Action<string, bool> check)
    {
        var assets = new AssetLibrary();
        string id = assets.Add(Png(256, 256, (x, y) => ((x + y) & 1) == 0 ? new SKColor(0, 70, 230, 128) : SKColors.Transparent));
        var scene = Scene(id); scene.Viewport = new(0, 0, 300, 300);
        var image = scene.Elements[0]; image.Width = image.Height = 256; image.Transform = Affine2D.Translation(18, 18);
        image.Style.Stroke = 0x80FF3050;
        using var cached = new SceneRenderer(); using var reference = Uncached();
        using (var actual = Draw(cached, scene, assets, sizeX: 300, sizeY: 300))
        using (var expected = Draw(reference, scene, assets, sizeX: 300, sizeY: 300))
            check("fragmented alpha bakes the same overlapping translucent envelope as direct rendering", CheckQuality("fragmented-alpha", actual, expected).Passed);
        long builds = Count(cached, "ImageBorderMaskBuildCount"), supports = Count(cached, "ImageBorderSupportBuildCount");
        using var target = SKSurface.Create(new SKImageInfo(300, 300, SKColorType.Bgra8888, SKAlphaType.Premul));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 8; i++) { target.Canvas.Clear(SKColors.Transparent); cached.Draw(target.Canvas, scene, assets, false); }
        double cachedMs = watch.Elapsed.TotalMilliseconds / 8;
        watch.Restart();
        for (int i = 0; i < 3; i++) { target.Canvas.Clear(SKColors.Transparent); reference.Draw(target.Canvas, scene, assets, false); }
        double directMs = watch.Elapsed.TotalMilliseconds / 3;
        check("fragmented alpha redraws reuse baked masks without rebuilding row-run expansion", Count(cached, "ImageBorderMaskBuildCount") == builds && Count(cached, "ImageBorderSupportBuildCount") == supports);
        Console.WriteLine($"ALPHA_BORDER_BAKED redraw-only fragmented 32768 runs: cached={cachedMs:F3}ms direct={directMs:F3}ms");
    }

    private static void QualityNegativeControls(Action<string, bool> check)
    {
        var assets = new AssetLibrary(); var scene = Scene(assets.Add(Png(64, 48, Subject)));
        scene.Elements[0].Transform = Affine2D.Translation(36.375, 28.625);
        scene.Elements[0].Style.Stroke = 0x89FF4050;
        using var reference = Reference(scene, assets);
        using var original = SKImage.FromBitmap(reference);
        using var blurSurface = SKSurface.Create(new SKImageInfo(reference.Width, reference.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        blurSurface.Canvas.Clear(SKColors.Transparent);
        using (var filter = SKImageFilter.CreateBlur(1.25f, 1.25f))
        using (var paint = new SKPaint { ImageFilter = filter })
            blurSurface.Canvas.DrawImage(original, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), paint);
        using var blurredImage = blurSurface.Snapshot(); using var blurred = SKBitmap.FromImage(blurredImage);
        var blurQuality = AssessQuality(blurred, reference, 6);
        check("quality guard rejects a genuinely blurred border rather than accepting every antialiased edge", !blurQuality.Passed && blurQuality.StableViolations > 0);

        using var half = SKSurface.Create(new SKImageInfo(reference.Width / 2, reference.Height / 2, SKColorType.Bgra8888, SKAlphaType.Premul));
        half.Canvas.Clear(SKColors.Transparent);
        half.Canvas.DrawImage(original, new SKRect(0, 0, reference.Width, reference.Height),
            new SKRect(0, 0, half.Canvas.DeviceClipBounds.Width, half.Canvas.DeviceClipBounds.Height), new SKSamplingOptions(SKFilterMode.Linear));
        using var halfImage = half.Snapshot();
        using var restored = SKSurface.Create(new SKImageInfo(reference.Width, reference.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        restored.Canvas.Clear(SKColors.Transparent);
        restored.Canvas.DrawImage(halfImage, new SKRect(0, 0, halfImage.Width, halfImage.Height),
            new SKRect(0, 0, reference.Width, reference.Height), new SKSamplingOptions(SKFilterMode.Linear));
        using var upsampledImage = restored.Snapshot(); using var upsampled = SKBitmap.FromImage(upsampledImage);
        var downsampleQuality = AssessQuality(upsampled, reference, 6);
        check("quality guard rejects a half-resolution mask enlarged back to its original size", !downsampleQuality.Passed && downsampleQuality.StableViolations > 0);

        using var shiftedSurface = SKSurface.Create(new SKImageInfo(reference.Width, reference.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        shiftedSurface.Canvas.Clear(SKColors.Transparent);
        shiftedSurface.Canvas.DrawImage(original, 1, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        using var shiftedImage = shiftedSurface.Snapshot(); using var shifted = SKBitmap.FromImage(shiftedImage);
        var shiftQuality = AssessQuality(shifted, reference, 6);
        check("quality guard rejects a one-pixel displacement of the cached border", !shiftQuality.Passed && shiftQuality.StableViolations > 0);
        Console.WriteLine($"BORDER_CACHE_NEGATIVE_CONTROLS blur:max={blurQuality.Maximum},stable={blurQuality.StableViolations},edge={blurQuality.EdgeViolations}; downsample:max={downsampleQuality.Maximum},stable={downsampleQuality.StableViolations},edge={downsampleQuality.EdgeViolations}; shift:max={shiftQuality.Maximum},stable={shiftQuality.StableViolations},edge={shiftQuality.EdgeViolations}");
    }

    private static SceneDocument Scene(string id) => new()
    {
        Viewport = new(0, 0, 224, 160), Elements = [new() { Kind = ElementKind.Image, AssetId = id, Width = 64, Height = 48,
            Transform = Affine2D.Translation(36, 28), ImageBorderWidth = 3, Style = new() { Stroke = 0xFFFF4050 } }]
    };

    private static SKColor Subject(int x, int y) => x is >= 14 and < 50 && y is >= 10 and < 38 && !(x is >= 26 and < 38 && y is >= 18 and < 30)
        ? new SKColor(35, 130, 220, x < 32 ? (byte)255 : (byte)112) : SKColors.Transparent;

    private static byte[] Png(int width, int height, Func<int, int, SKColor> pixel)
    {
        using var bitmap = new SKBitmap(width, height);
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) bitmap.SetPixel(x, y, pixel(x, y));
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
    }

    private static byte[] Animation()
    {
        using var first = new SKBitmap(64, 48); first.Erase(SKColors.Transparent);
        using var second = new SKBitmap(64, 48); second.Erase(SKColors.Transparent);
        using var third = new SKBitmap(64, 48); third.Erase(SKColors.Transparent);
        using (var canvas = new SKCanvas(first)) { using var paint = new SKPaint { Color = SKColors.Blue }; canvas.DrawRect(7, 12, 18, 20, paint); }
        using (var canvas = new SKCanvas(second)) { using var paint = new SKPaint { Color = SKColors.Lime }; canvas.DrawCircle(43, 24, 11, paint); }
        using (var canvas = new SKCanvas(third)) { using var paint = new SKPaint { Color = new SKColor(240, 180, 30, 112) }; canvas.DrawRect(24, 7, 17, 34, paint); }
        using var encoded = SKWebpEncoder.EncodeAnimated([new(first, TimeSpan.FromMilliseconds(60)), new(second, TimeSpan.FromMilliseconds(80)),
            new(third, TimeSpan.FromMilliseconds(100))], new(SKWebpEncoderCompression.Lossless, 100));
        return encoded?.ToArray() ?? throw new InvalidOperationException("Border-cache animation encoding failed.");
    }

    private static SKBitmap Draw(SceneRenderer renderer, SceneDocument scene, AssetLibrary assets, SKMatrix? transform = null, SKRect? clip = null, int sizeX = 224, int sizeY = 160, SKPath? shapeClip = null)
    {
        using var surface = SKSurface.Create(new SKImageInfo(sizeX, sizeY, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas; canvas.Clear(SKColors.Transparent); int saved = canvas.Save();
        if (clip is { } clipping) canvas.ClipRect(clipping, antialias: true);
        if (shapeClip is not null) canvas.ClipPath(shapeClip, antialias: true);
        if (transform is { } matrix) canvas.Concat(matrix);
        renderer.Draw(canvas, scene, assets, false);
        canvas.RestoreToCount(saved);
        using var image = surface.Snapshot(); return SKBitmap.FromImage(image);
    }

    private static SKBitmap Reference(SceneDocument scene, AssetLibrary assets)
    { using var renderer = Uncached(); return Draw(renderer, scene, assets); }

    private static SceneRenderer Uncached()
        => new() { CacheImageBorders = false };

    private static long Count(SceneRenderer renderer, string name) => name switch
    {
        "ImageBorderMaskBuildCount" => renderer.ImageBorderMaskBuildCount,
        "ImageBorderMaskHitCount" => renderer.ImageBorderMaskHitCount,
        "ImageBorderSupportBuildCount" => renderer.ImageBorderSupportBuildCount,
        "CachedImageBorderBytes" => renderer.CachedImageBorderBytes,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown border-cache counter.")
    };

    private static void Limit(SceneRenderer renderer, long bytes)
        => renderer.ImageBorderCacheByteLimit = bytes;

    private static int ShiftDifference(SKBitmap original, SKBitmap translated, int dx, int dy)
    {
        var first = original.GetPixelSpan(); var shifted = translated.GetPixelSpan(); int maximum = 0;
        for (int y = 0; y < original.Height; y++)
            for (int x = 0; x < original.Width; x++)
            {
                int target = y * translated.RowBytes + x * 4;
                int source = y >= dy && x >= dx ? (y - dy) * original.RowBytes + (x - dx) * 4 : -1;
                for (int channel = 0; channel < 4; channel++)
                {
                    int expected = source < 0 ? 0 : first[source + channel];
                    maximum = Math.Max(maximum, Math.Abs(shifted[target + channel] - expected));
                }
            }
        return maximum;
    }

    private static int Difference(SKBitmap a, SKBitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return 255;
        var first = a.GetPixelSpan(); var second = b.GetPixelSpan(); int maximum = 0;
        for (int i = 0; i < first.Length; i++) maximum = Math.Max(maximum, Math.Abs(first[i] - second[i]));
        return maximum;
    }

    private readonly record struct QualityResult(int Maximum, int StableViolations, int EdgeViolations)
    { internal bool Passed => StableViolations == 0 && EdgeViolations == 0; }

    private static QualityResult CheckQuality(string name, SKBitmap actual, SKBitmap expected, int edgeLimit = 6)
    {
        var quality = AssessQuality(actual, expected, edgeLimit);
        Console.WriteLine($"BORDER_CACHE_QUALITY {name}: max={quality.Maximum} stable-violations={quality.StableViolations} edge-violations={quality.EdgeViolations} edge-limit={edgeLimit}");
        if (!quality.Passed) Diagnose(name, actual, expected);
        return quality;
    }

    // Baking resolves coverage before tint/clip composition. Only already varying AA
    // fringes may differ by a few quantization levels. Transparent/opaque endpoints and
    // flat premultiplied neighborhoods retain a one-level limit, so blur or displacement
    // cannot hide behind the fringe allowance. The fractional rectangular clip alone has
    // a wider bound for separate geometry-coverage and clip-coverage multiplication.
    private static QualityResult AssessQuality(SKBitmap actual, SKBitmap expected, int edgeLimit)
    {
        if (actual.Width != expected.Width || actual.Height != expected.Height) return new(255, 1, 0);
        var first = actual.GetPixelSpan(); var second = expected.GetPixelSpan();
        int maximum = 0, stableViolations = 0, edgeViolations = 0;
        for (int y = 0; y < expected.Height; y++)
            for (int x = 0; x < expected.Width; x++)
            {
                int actualOffset = y * actual.RowBytes + x * 4, expectedOffset = y * expected.RowBytes + x * 4;
                int delta = 0;
                for (int channel = 0; channel < 4; channel++) delta = Math.Max(delta, Math.Abs(first[actualOffset + channel] - second[expectedOffset + channel]));
                maximum = Math.Max(maximum, delta);
                if (delta <= 1) continue;
                bool stable = first[actualOffset + 3] is 0 or 255 || second[expectedOffset + 3] is 0 or 255 || FlatNeighborhood(second, expected.RowBytes, expected.Width, expected.Height, x, y);
                if (stable) stableViolations++;
                else if (delta > edgeLimit) edgeViolations++;
            }
        return new(maximum, stableViolations, edgeViolations);
    }

    private static bool FlatNeighborhood(ReadOnlySpan<byte> pixels, int rowBytes, int width, int height, int x, int y)
    {
        int center = y * rowBytes + x * 4;
        for (int ny = Math.Max(0, y - 1); ny <= Math.Min(height - 1, y + 1); ny++)
            for (int nx = Math.Max(0, x - 1); nx <= Math.Min(width - 1, x + 1); nx++)
            {
                int index = ny * rowBytes + nx * 4;
                for (int channel = 0; channel < 4; channel++) if (pixels[index + channel] != pixels[center + channel]) return false;
            }
        return true;
    }

    private static int Diagnose(string name, SKBitmap actual, SKBitmap expected)
    {
        int difference = Difference(actual, expected);
        if (difference <= 1) return difference;
        var first = actual.GetPixelSpan(); var second = expected.GetPixelSpan();
        int offset = 0;
        for (int i = 0; i < first.Length; i++)
            if (Math.Abs(first[i] - second[i]) == difference) { offset = i; break; }
        int x = offset / 4 % actual.Width, y = offset / actual.RowBytes;
        Console.WriteLine($"BORDER_CACHE_PIXEL_DELTA {name}: max={difference} at({x},{y}) actual={actual.GetPixel(x, y)} expected={expected.GetPixel(x, y)}");
        var worst = new List<(int Delta, int X, int Y)>();
        for (int py = 0; py < actual.Height; py++)
            for (int px = 0; px < actual.Width; px++)
            {
                int index = py * actual.RowBytes + px * 4, delta = 0;
                for (int channel = 0; channel < 4; channel++) delta = Math.Max(delta, Math.Abs(first[index + channel] - second[index + channel]));
                if (delta <= 1 || worst.Count == 10 && delta <= worst[^1].Delta) continue;
                int rank = worst.FindIndex(pixel => pixel.Delta < delta);
                if (rank < 0) worst.Add((delta, px, py)); else worst.Insert(rank, (delta, px, py));
                if (worst.Count > 10) worst.RemoveAt(10);
            }
        foreach (var pixel in worst)
            Console.WriteLine($"BORDER_CACHE_PIXEL_WORST delta={pixel.Delta} at({pixel.X},{pixel.Y}) actual={actual.GetPixel(pixel.X, pixel.Y)} expected={expected.GetPixel(pixel.X, pixel.Y)}");
        if (Environment.GetEnvironmentVariable("FLOATCANVAS_BORDER_CACHE_FAILURE_OUTPUT") is not { Length: > 0 } folder) return difference;
        Directory.CreateDirectory(folder);
        string slug = string.Concat(name.Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-'));
        Save(actual, Path.Combine(folder, "image-border-cache-actual.png"));
        Save(expected, Path.Combine(folder, "image-border-cache-reference.png"));
        Save(actual, Path.Combine(folder, "image-border-cache-" + slug + "-actual.png"));
        Save(expected, Path.Combine(folder, "image-border-cache-" + slug + "-reference.png"));
        using var heat = new SKBitmap(actual.Width, actual.Height);
        for (int py = 0; py < actual.Height; py++)
            for (int px = 0; px < actual.Width; px++)
            {
                int index = py * actual.RowBytes + px * 4;
                int maximum = 0;
                for (int channel = 0; channel < 4; channel++) maximum = Math.Max(maximum, Math.Abs(first[index + channel] - second[index + channel]));
                heat.SetPixel(px, py, new SKColor((byte)Math.Min(255, maximum * 16), 0, 0));
            }
        Save(heat, Path.Combine(folder, "image-border-cache-diff.png"));
        Save(heat, Path.Combine(folder, "image-border-cache-" + slug + "-diff.png"));
        return difference;

        static void Save(SKBitmap bitmap, string path)
        {
            using var image = SKImage.FromBitmap(bitmap); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, png.ToArray());
        }
    }
}
