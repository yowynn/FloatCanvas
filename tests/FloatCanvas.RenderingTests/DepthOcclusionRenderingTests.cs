using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class DepthOcclusionRenderingTests
{
    public static void Run(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer(); var assets = new AssetLibrary();
        var scene = Scene(); var mapping = CanvasMapping.Create(scene);
        var near = Billboard(scene, 30, 0xFFFF0000); var far = Billboard(scene, 90, 0xFF0000FF);
        scene.Elements = [near, far]; var ids = scene.Elements.Select(e => e.Id).ToArray();
        using (var pixels = Pixels(renderer, scene, assets)) check("near billboard wins despite lower layer index", pixels.GetPixel(80, 60) == SKColors.Red);
        check("depth ordering never mutates persisted element order", scene.Elements.Select(e => e.Id).SequenceEqual(ids));
        check("point ordering shares anchor depth rather than screen center height", CanvasDepthOrder.BackToFrontAt(scene, mapping, new(80, 60)).SequenceEqual([1, 0]));
        scene.Canvas.DepthPriorityOcclusion = false;
        using (var pixels = Pixels(renderer, scene, assets)) check("disabled depth mode preserves original z-order", pixels.GetPixel(80, 60) == SKColors.Blue);
        scene.Canvas.DepthPriorityOcclusion = true;
        far.BillboardAnchor = near.BillboardAnchor; far.BillboardHeight = near.BillboardHeight; far.Width = near.Width; far.Height = near.Height;
        using (var pixels = Pixels(renderer, scene, assets)) check("exact equal depth uses original layer index", pixels.GetPixel(80, 60) == SKColors.Blue);
        far.BillboardAnchor += new PointD(0, 1e-7);
        using (var pixels = Pixels(renderer, scene, assets)) check("almost equal depths are not collapsed into a z-order tolerance", pixels.GetPixel(80, 60) == SKColors.Red);
        var affineScene = Scene(); affineScene.Canvas.Projection.Matrix = Projective2D.Identity;
        affineScene.Elements = [Billboard(affineScene, 60, 0xFFFF0000), Plane(affineScene, 0xFF00FF00)];
        using (var pixels = Pixels(renderer, affineScene, assets)) check("affine shared depth preserves interleaved plane and billboard layer order", pixels.GetPixel(80, 60) == SKColors.Lime);

        scene = Scene(); var board = Billboard(scene, 60, 0xFFFF0000); var plane = Plane(scene, 0xFF00FF00);
        scene.Elements = [board, plane];
        using (var pixels = Pixels(renderer, scene, assets))
            check("one plane and billboard intersect with opposite occlusion in the same frame", pixels.GetPixel(80, 40) == SKColors.Lime && pixels.GetPixel(80, 80) == SKColors.Red);
        scene.Elements.Reverse();
        using (var pixels = Pixels(renderer, scene, assets))
            check("crossing plane order is spatial regardless of layer sequence", pixels.GetPixel(80, 40) == SKColors.Lime && pixels.GetPixel(80, 80) == SKColors.Red);
        scene.Elements = [board]; scene.Canvas.BackgroundColor = 0xFF00FF00;
        using (var pixels = Pixels(renderer, scene, assets, false))
            check("opaque canvas color participates at plane depth", pixels.GetPixel(80, 40) == SKColors.Lime && pixels.GetPixel(80, 80) == SKColors.Red);
        using (var pixels = Pixels(renderer, scene, assets)) check("transparent export excludes canvas color as before", pixels.GetPixel(80, 40) == SKColors.Red && pixels.GetPixel(5, 5).Alpha == 0);
        scene.Canvas.BackgroundColor = 0x8000FF00;
        using (var pixels = Pixels(renderer, scene, assets, false))
        {
            var mixed = pixels.GetPixel(80, 40);
            check("translucent canvas color mixes with farther billboard", mixed.Alpha == 255 && mixed.Green is >= 127 and <= 129 && mixed.Red is >= 126 and <= 128);
        }
        scene.Canvas.BackgroundColor = 0; scene.Background = Plane(scene, 0xFF00FF00);
        using (var pixels = Pixels(renderer, scene, assets, false)) check("document photograph remains behind billboards rather than becoming a plane occluder", pixels.GetPixel(80, 40) == SKColors.Red);
        scene.Background = null; scene.Canvas.BackgroundColor = 0x8000FF00;
        scene.LayerOpacity = .5;
        using (var pixels = Pixels(renderer, scene, assets, false))
        {
            var bare = pixels.GetPixel(5, 5); var nearBoard = pixels.GetPixel(80, 80);
            check("global opacity preserves translucent canvas color alpha", bare.Alpha is >= 127 and <= 129 && bare.Green == 255);
            check("global annotation opacity is applied once over plane base", nearBoard.Alpha is >= 190 and <= 193 && nearBoard.Red > nearBoard.Green);
        }
        scene.LayerOpacity = 0;
        using (var pixels = Pixels(renderer, scene, assets, false)) check("zero annotation opacity retains canvas background", pixels.GetPixel(80, 80).Alpha is >= 127 and <= 129 && pixels.GetPixel(80, 80).Green == 255);
        scene.LayerOpacity = 1; scene.Canvas.BackgroundColor = 0; scene.Canvas.Grid = new() { Visible = true, Width = 6, Color = 0xFF00FF00 };
        using (var pixels = Pixels(renderer, scene, assets))
            check("grid strokes occlude behind-plane billboard only at their covered pixels", pixels.GetPixel(80, 40).Green > 240 && pixels.GetPixel(80, 80) == SKColors.Red && pixels.GetPixel(83, 43) == SKColors.Red);

        scene = Scene(); board = Billboard(scene, 60, 0x80FF0000); plane = Plane(scene, 0x8000FF00); scene.Elements = [plane, board];
        scene.LayerOpacity = .5;
        using var direct = Pixels(renderer, scene, assets);
        using var cache = new OverlaySceneCache(renderer);
        using var surface = SKSurface.Create(new SKImageInfo(160, 120, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent); cache.DrawOnClearedRasterTarget(surface, scene, assets, 1, 0);
        using (var image = surface.Snapshot()) using (var pixels = SKBitmap.FromImage(image))
            check("CPU overlay depth mode matches export with no stale z-order cache", Equal(pixels, direct) && cache.CachedRasterBytes == 0 && cache.InPlaceOpacityFrameCount == 0);
        using (var pixels = SKBitmap.Decode(renderer.ExportPng(scene, assets, true, SceneRenderSpace.PlaneContent)))
        {
            scene.Canvas.DepthPriorityOcclusion = false;
            using var expected = SKBitmap.Decode(renderer.ExportPng(scene, assets, true, SceneRenderSpace.PlaneContent));
            check("PlaneContent export retains logical plane semantics", Equal(pixels, expected));
        }
        scene.Canvas.DepthPriorityOcclusion = true; scene.Canvas.Projection.Enabled = false;
        using (var disabled = Pixels(renderer, scene, assets))
        {
            scene.Canvas.DepthPriorityOcclusion = false; using var expected = Pixels(renderer, scene, assets);
            check("perspective-off ignores saved depth preference", Equal(disabled, expected));
        }
        scene = Scene(); scene.Elements = [Plane(scene, 0xFF00FF00)];
        surface.Canvas.Clear(SKColors.Transparent); cache.DrawOnClearedRasterTarget(surface, scene, assets, 2, 0);
        check("enabled preference with only plane content retains ordinary raster cache", cache.CachedRasterBytes > 0);
        FilterEvents(renderer, assets, check);
        SlantedBoundaries(renderer, assets, check);
        FlattenedPlaneEquivalence(renderer, assets, check);
    }

    private static void FilterEvents(SceneRenderer renderer, AssetLibrary assets, Action<string, bool> check)
    {
        var scene = Scene();
        var farther = Billboard(scene, 100, 0xFFFF0000);
        var effect = Billboard(scene, 60, 0xFFFFFFFF); effect.Kind = ElementKind.LivePixelate; effect.Effects = [new() { Kind = ImageEffectKind.Pixelate, Amount = 1 }];
        scene.Elements = [effect, farther];
        using (var pixels = Pixels(renderer, scene, assets)) check("live filter samples farther billboard even when it is later in layer list", pixels.GetPixel(80, 75) == SKColors.Red);
        farther.Visible = false; scene.Canvas.BackgroundColor = 0xFF00FF00;
        using (var pixels = Pixels(renderer, scene, assets))
            check("transparent filter export samples the original far canvas color without outputting its bare background", pixels.GetPixel(80, 75) == SKColors.Lime && pixels.GetPixel(5, 5).Alpha == 0);
        scene.Canvas.BackgroundColor = 0;
        farther.BillboardAnchor = CanvasMapping.Create(scene).ScreenToPlane.TransformPoint(new(80, 30));
        farther.Visible = false;
        using (var pixels = Pixels(renderer, scene, assets)) check("filter without a completed lower source retains fail-closed desktop cover", pixels.GetPixel(80, 75) == new SKColor(0xFF50545B));
        farther.Visible = true;
        var planarBlur = Plane(scene, 0); planarBlur.Kind = ElementKind.LiveBlur; planarBlur.Effects = [new() { Kind = ImageEffectKind.Blur, Amount = 3 }];
        effect.Kind = ElementKind.LiveBlur; effect.Effects = [new() { Kind = ImageEffectKind.Blur, Amount = 3 }];
        scene.Elements = [planarBlur, effect, farther];
        long before = renderer.LiveEffectBuildCount;
        using (var pixels = Pixels(renderer, scene, assets))
            check("intersecting plane and billboard filters compose as a finite event prefix", pixels.GetPixel(80, 60).Alpha == 255 && renderer.LiveEffectBuildCount - before is >= 3 and <= 5);
        var context = Scene(); context.Elements = [Billboard(context, 100, 0xFFFF0000), effect];
        var selection = Scene(); selection.Elements = [effect.Clone()]; renderer.LiveEffectContext = context;
        using (var pixels = Pixels(renderer, selection, assets))
            check("isolated filter export reads full depth context without outputting unselected layers", pixels.GetPixel(80, 75).Red > 245 && pixels.GetPixel(5, 5).Alpha == 0);
        renderer.LiveEffectContext = null;
        var affine = Scene(); affine.Canvas.Projection.Matrix = Projective2D.Identity;
        var a = Plane(affine, 0xFFFF0000); var b = Plane(affine, 0); b.Kind = ElementKind.LivePixelate; b.Effects = [new() { Kind = ImageEffectKind.Pixelate, Amount = 1 }];
        affine.Elements = [a, b];
        using (var pixels = Pixels(renderer, affine, assets)) check("affine equal-depth filter retains same-plane layer sequence", pixels.GetPixel(80, 75) == SKColors.Red);
    }

    private static void SlantedBoundaries(SceneRenderer renderer, AssetLibrary assets, Action<string, bool> check)
    {
        var scene = Scene(); scene.Canvas.Projection.Matrix = new(1, 0, 0, 0, 1, 0, .001, .002, 1);
        scene.Elements = [Plane(scene, 0x8000FF00)];
        var a = Billboard(scene, 60, 0); a.Style.Opacity = 1; scene.Elements.Add(a);
        var b = Billboard(scene, 80, 0); b.Style.Opacity = 1; scene.Elements.Add(b);
        using var pixels = Pixels(renderer, scene, assets);
        bool seamless = true;
        for (int y = 2; y < pixels.Height - 2; y++) for (int x = 2; x < pixels.Width - 2; x++) seamless &= pixels.GetPixel(x, y).Alpha is >= 127 and <= 129;
        check("slanted adjacent depth clips neither double alpha nor leave uncovered seams", seamless);
    }

    private static void FlattenedPlaneEquivalence(SceneRenderer renderer, AssetLibrary assets, Action<string, bool> check)
    {
        var scene = Scene(); scene.Canvas.Projection.Matrix = new(1, 0, 0, 0, 1, 0, .001, .002, 1);
        scene.Canvas.BackgroundColor = 0x80082040; scene.Canvas.Grid = new() { Visible = true, Color = 0x8060A0E0, Width = 1.5 };
        scene.Elements = [Plane(scene, 0x8050D070), Billboard(scene, 40, 0xA0FF6040), Billboard(scene, 80, 0xB07080FF)];
        scene.Elements.Add(new() { Kind = ElementKind.Ellipse, Width = 90, Height = 80, Transform = Affine2D.Translation(20, 25),
            Style = new() { Fill = 0xA04090E0, Stroke = 0xD0F0C060, StrokeWidth = 3, Opacity = .7 } });
        scene.LayerOpacity = .55;
        long before = renderer.RasterIntermediateSurfaceCount;
        long reused = renderer.DepthSurfaceReuseCount;
        using var flattened = Pixels(renderer, scene, assets, false);
        check("normal depth scene rents two working images independent of billboard count",
            renderer.RasterIntermediateSurfaceCount - before + renderer.DepthSurfaceReuseCount - reused == 2);
        // An entirely offscreen planar filter selects the event path without changing any output pixel.
        scene.Elements.Add(new() { Kind = ElementKind.LivePixelate, Width = 1, Height = 1, Style = new() { StrokeWidth = 0 },
            ProjectivePlacement = CanvasMapping.Create(scene).ScreenToPlane.Compose(Projective2D.FromAffine(Affine2D.Translation(10000, 10000))),
            Effects = [new() { Kind = ImageEffectKind.Pixelate, Amount = 1 }] });
        using var eventPath = Pixels(renderer, scene, assets, false);
        bool equivalent = Equal(flattened, eventPath);
        if (!Equal(flattened, eventPath))
        {
            int changed = 0, maximum = 0, maximumAlpha = 0, aboveEight = 0; long totalDifference = 0;
            var examples = new List<string>();
            for (int y = 0; y < flattened.Height; y++) for (int x = 0; x < flattened.Width; x++)
            {
                var first = flattened.GetPixel(x, y); var second = eventPath.GetPixel(x, y);
                if (first == second) continue;
                int red = Math.Abs(first.Red - second.Red), green = Math.Abs(first.Green - second.Green), blue = Math.Abs(first.Blue - second.Blue), alpha = Math.Abs(first.Alpha - second.Alpha);
                changed++; maximum = Math.Max(maximum, Math.Max(red, Math.Max(green, blue))); maximumAlpha = Math.Max(maximumAlpha, alpha);
                if (Math.Max(red, Math.Max(green, Math.Max(blue, alpha))) > 8) aboveEight++;
                totalDifference += red + green + blue + alpha;
                if (examples.Count < 8) examples.Add($"({x},{y}) {first} vs {second}");
            }
            double mean = totalDifference / (double)(flattened.Width * flattened.Height * 4);
            // Skia's antialiased element-opacity SaveLayer is clipped before rasterization in the
            // event path, and after rasterization in the shared texture. Bound the sparse edge
            // differences; all ordering, exact color and no-seam alpha checks remain separate.
            double aboveEightRatio = aboveEight / (double)(flattened.Width * flattened.Height);
            equivalent = maximum <= 24 && maximumAlpha <= 5 && mean < .15 && aboveEightRatio < .001;
            Console.WriteLine($"Plane texture comparison: changed={changed}, maximum RGB difference={maximum}, maximum alpha difference={maximumAlpha}, mean RGBA difference={mean:F6}, pixels over 8={aboveEight} ({aboveEightRatio:P4}); {string.Join("; ", examples)}");
            if (Environment.GetEnvironmentVariable("FLOATCANVAS_DEPTH_DIAGNOSTICS") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                using var first = SKImage.FromBitmap(flattened); using var second = SKImage.FromBitmap(eventPath);
                using var firstData = first.Encode(SKEncodedImageFormat.Png, 100); using var secondData = second.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(Path.Combine(directory, "flattened.png"), firstData.ToArray()); File.WriteAllBytes(Path.Combine(directory, "events.png"), secondData.ToArray());
                File.WriteAllText(Path.Combine(directory, "comparison.json"), System.Text.Json.JsonSerializer.Serialize(new { ChangedPixels = changed, MaximumRgbDifference = maximum,
                    MaximumAlphaDifference = maximumAlpha, MeanRgbaDifference = mean, PixelsAboveEight = aboveEight, AboveEightRatio = aboveEightRatio, WithinEdgeRasterizationTolerance = equivalent }));
            }
        }
        check("shared plane texture preserves event composition within bounded edge rasterization differences", equivalent);
    }

    public static void WriteEvidence(string directory)
    {
        Directory.CreateDirectory(directory); using var renderer = new SceneRenderer(); var assets = new AssetLibrary();
        var scene = Scene(); scene.Viewport = new(0, 0, 640, 480); scene.Canvas.Projection.Matrix = new(1, 0, 0, 0, 1, 0, -.0003, -.0008, 1);
        scene.Canvas.Grid = new() { Visible = true, Color = 0x8050B0FF, Width = 1.5 }; scene.Canvas.BackgroundColor = 0xFF19232C;
        var mapping = CanvasMapping.Create(scene); mapping.TryToPlane(new(320, 300), out var anchor); mapping.TryGetDepthScale(anchor, out double scale);
        var board = new CanvasElement { Kind = ElementKind.Rectangle, Width = 220 / scale, Height = 280 / scale, IsBillboard = true,
            BillboardAnchor = anchor, BillboardHeight = 40 / scale, Style = new() { Fill = 0xE0FBBF24, Stroke = 0xFFFFFFFF, StrokeWidth = 2 / scale } };
        scene.Elements = [Plane(scene, 0x9038BDF8), board];
        foreach (bool enabled in new[] { false, true })
        {
            scene.Canvas.DepthPriorityOcclusion = enabled;
            File.WriteAllBytes(Path.Combine(directory, enabled ? "depth-on.png" : "depth-off.png"), renderer.ExportPng(scene, assets));
        }
        scene.Elements = [Plane(scene, 0x7038BDF8)];
        foreach (var placement in new[] { (new PointD(250, 240), 0xE0FBBF24u), (new PointD(370, 320), 0xC0EC4899u), (new PointD(420, 390), 0xA080E050u) })
        {
            mapping.TryToPlane(placement.Item1, out var ground);
            scene.Elements.Add(new() { Kind = ElementKind.Rectangle, Width = 180, Height = 240, IsBillboard = true, BillboardAnchor = ground,
                BillboardHeight = 70, Style = new() { Fill = placement.Item2, Stroke = 0xFFFFFFFF, StrokeWidth = 2 } });
        }
        File.WriteAllBytes(Path.Combine(directory, "depth-multi-transparent.png"), renderer.ExportPng(scene, assets));
        scene.Elements[^1].BillboardHeight = -80;
        File.WriteAllBytes(Path.Combine(directory, "depth-negative-height.png"), renderer.ExportPng(scene, assets));
        scene.Canvas.BackgroundColor = 0; scene.Canvas.Grid.Visible = false;
        scene.Elements = [Plane(scene, 0x8000FF00), board]; board.Style.Fill = 0; board.Style.StrokeWidth = 0;
        File.WriteAllBytes(Path.Combine(directory, "depth-slanted-alpha-seams.png"), renderer.ExportPng(scene, assets, true));
        WritePerformance(directory);
    }

    private static void WritePerformance(string directory)
    {
        var results = new List<object>(); var assets = new AssetLibrary();
        foreach (var sample in new[] { (640, 480, 12, 24), (1920, 1080, 12, 24), (1920, 1080, 40, 60) })
        {
            using var renderer = new SceneRenderer(); var scene = Scene(); scene.Viewport = new(0, 0, sample.Item1, sample.Item2);
            scene.Canvas.Projection.Matrix = new(1, 0, 0, 0, 1, 0, -.15 / sample.Item1, -.3 / sample.Item2, 1);
            scene.Canvas.BackgroundColor = 0xFF19232C; var mapping = CanvasMapping.Create(scene);
            for (int i = 0; i < sample.Item4; i++)
                scene.Elements.Add(new() { Kind = ElementKind.Rectangle, Width = 130, Height = 80,
                    Transform = Affine2D.Translation((i * 173) % sample.Item1, (i * 127) % sample.Item2), Style = new() { Fill = 0x805080C0, Stroke = 0xFFB0D0F0, StrokeWidth = 2 } });
            for (int i = 0; i < sample.Item3; i++)
            {
                mapping.TryToPlane(new((i * 179 + 180) % sample.Item1, (i * 97 + 120) % sample.Item2), out var anchor);
                scene.Elements.Add(new() { Kind = ElementKind.Ellipse, IsBillboard = true, BillboardAnchor = anchor, BillboardHeight = 30,
                    Width = 90, Height = 130, Style = new() { Fill = 0xC0FFB050, Stroke = 0xFFFFFFFF, StrokeWidth = 2 } });
            }
            using var surface = SKSurface.Create(new SKImageInfo(sample.Item1, sample.Item2, SKColorType.Bgra8888, SKAlphaType.Premul));
            renderer.Draw(surface.Canvas, scene, assets);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int frame = 0; frame < 5; frame++) { surface.Canvas.Clear(SKColors.Transparent); renderer.Draw(surface.Canvas, scene, assets); }
            watch.Stop();
            results.Add(new { Width = sample.Item1, Height = sample.Item2, BillboardCount = sample.Item3, PlaneCount = sample.Item4,
                Frames = 5, MeanMilliseconds = watch.Elapsed.TotalMilliseconds / 5, Backend = "CPU raster", Scope = "Small local sample; not a device or performance acceptance test" });
        }
        File.WriteAllText(Path.Combine(directory, "depth-cpu-samples.json"), System.Text.Json.JsonSerializer.Serialize(results, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static SceneDocument Scene() => new() { Viewport = new(0, 0, 160, 120), Canvas = new() { DepthPriorityOcclusion = true,
        Projection = new() { Enabled = true, CellSize = 20, Matrix = new(1, 0, 0, 0, 1, 0, 0, .005, 1), Quad = [new(0, 0), new(160, 0), new(160, 120), new(0, 120)] } } };
    private static CanvasElement Billboard(SceneDocument scene, double anchorY, uint fill)
    {
        var mapping = CanvasMapping.Create(scene); mapping.TryToPlane(new(80, anchorY), out var anchor); mapping.TryGetDepthScale(anchor, out double scale);
        return new() { Kind = ElementKind.Rectangle, IsBillboard = true, BillboardAnchor = anchor, BillboardHeight = (anchorY - 60) / scale,
            Width = 100 / scale, Height = 80 / scale, Style = new() { Fill = fill, StrokeWidth = 0 } };
    }
    private static CanvasElement Plane(SceneDocument scene, uint fill) => new() { Kind = ElementKind.Rectangle, Width = scene.Viewport.Width, Height = scene.Viewport.Height,
        ProjectivePlacement = CanvasMapping.Create(scene).ScreenToPlane, Style = new() { Fill = fill, StrokeWidth = 0 } };
    private static SKBitmap Pixels(SceneRenderer renderer, SceneDocument scene, AssetLibrary assets, bool transparent = true) => SKBitmap.Decode(renderer.ExportPng(scene, assets, transparent));
    private static bool Equal(SKBitmap first, SKBitmap second) => first.Width == second.Width && first.Height == second.Height && first.Bytes.SequenceEqual(second.Bytes);
}
