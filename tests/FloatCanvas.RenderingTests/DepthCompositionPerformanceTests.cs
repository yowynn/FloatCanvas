using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class DepthCompositionPerformanceTests
{
    internal static void Run(Action<string, bool> check)
    {
        TilePixelsAndCalibration(check);
        ReuseClearsPixelsAndState(check);
        LiveSamplingAcrossTiles(check);
        RetentionBoundAndDispose(check);
    }

    private static void TilePixelsAndCalibration(Action<string, bool> check)
    {
        var assets = new AssetLibrary(); var scene = Scene();
        var originalViewport = scene.Viewport; var originalProjection = scene.Canvas.Projection.Matrix;
        using var renderer = new SceneRenderer(); using var reference = new SceneRenderer();
        RectD[] tiles = [new(-180, -90, 210, 140), new(80, 70, 220, 140),
            new(-180, -90, 240, 300), new(60, -90, 240, 300)];
        CompareTiles("negative-origin screens with a gap and adjacent tile seam", scene, tiles, false);
        check("depth target clipping preserves document viewport and projection", scene.Viewport == originalViewport && scene.Canvas.Projection.Matrix == originalProjection);

        var initial = CanvasCalibration.CreateInitial(scene.Viewport, 8, 6);
        for (int frame = 0; frame < 4; frame++)
        {
            // One whole edge moves: two corners change together, as in canvas adjustment.
            double offset = frame * 7;
            PointD[] quad = [new(-150, -60), new(250 + offset, -45 + offset), new(275 + offset, 180 + offset), new(-130, 190)];
            bool calibrated = CanvasCalibration.TryCalibrate(initial, quad, scene.Viewport, out var projection, out _);
            check($"edge-drag frame {frame} has valid perspective", calibrated);
            projection.Enabled = true; scene.Canvas.Projection = projection;
            CompareTiles($"edge-drag frame {frame} with reference grid", scene, tiles, true);
        }

        scene = Scene(); scene.Viewport = new(-179.75, -89.5, 479.5, 299.25);
        CompareTiles("fractional viewport preserves the original raster phase", scene,
            [new(-180, -90, 240, 300), new(60, -90, 240, 300)], false);
        CompareTiles("fractional caller translation retains full composition fallback", scene,
            [new(-180, -90, 240, 300), new(60, -90, 240, 300)], false, .25f, .5f);

        // Isolate the complementary non-AA depth clips from ordinary edge antialiasing.
        scene = Scene(); scene.Canvas.BackgroundColor = 0; scene.Canvas.Grid.Visible = false; scene.LayerOpacity = 1;
        scene.Elements = [ScreenBox(scene, scene.Viewport, 0x8000FF00), Board(scene, new(-40, 20), 0), Board(scene, new(130, 100), 0)];
        using var alpha = Draw(renderer, scene, assets, new(20, -90, 80, 300));
        bool seamless = true;
        for (int y = 3; y < alpha.Height - 3; y++) for (int x = 3; x < alpha.Width - 3; x++)
            seamless &= alpha.GetPixel(x, y).Alpha is >= 127 and <= 129;
        check("slanted equal-depth boundaries in clipped composition do not double alpha or open seams", seamless);

        scene = Scene(); scene.Canvas.BackgroundColor = 0; scene.Canvas.Grid.Visible = false; scene.LayerOpacity = 1;
        var mapping = CanvasMapping.Create(scene);
        var near = Board(scene, new(60, 100), 0xFFFF0000); var far = Board(scene, new(60, 30), 0xFF0000FF);
        mapping.TryGetDepthScale(near.BillboardAnchor, out double nearScale); mapping.TryGetDepthScale(far.BillboardAnchor, out double farScale);
        near.BillboardHeight = 40 / nearScale; far.BillboardHeight = -30 / farScale;
        scene.Elements = [near, far];
        using var ordered = Draw(renderer, scene, assets, new(30, 10, 120, 120));
        check("reused clipped composition retains exact near-billboard color despite opposite layer order", ordered.GetPixel(30, 50) == SKColors.Red);

        void CompareTiles(string label, SceneDocument document, IReadOnlyList<RectD> regions, bool calibrationReference, float dx = 0, float dy = 0)
        {
            var bounds = PixelBounds(document.Viewport);
            using var full = Draw(reference, document, assets, bounds, calibrationReference, dx, dy);
            bool matches = true;
            bool integerPhase = document.Viewport.X == Math.Truncate(document.Viewport.X) && document.Viewport.Y == Math.Truncate(document.Viewport.Y) && dx == 0 && dy == 0;
            foreach (var tile in regions)
            {
                using var pixels = Draw(renderer, document, assets, tile, calibrationReference, dx, dy);
                matches &= CompareCrop(label, full, bounds, pixels, tile, integerPhase, document);
            }
            if (!matches)
            {
                var control = document.Clone(); control.Canvas.DepthPriorityOcclusion = false;
                using var controlFull = Draw(reference, control, assets, bounds, calibrationReference, dx, dy);
                foreach (var tile in regions)
                {
                    using var controlTile = Draw(renderer, control, assets, tile, calibrationReference, dx, dy);
                    CompareCrop("ordinary raster control " + label, controlFull, bounds, controlTile, tile);
                }
            }
            check(label + (integerPhase ? " matches full-viewport crops within bounded edge rasterization" : " matches full-viewport crops exactly"), matches);
        }
    }

    private static void ReuseClearsPixelsAndState(Action<string, bool> check)
    {
        var assets = new AssetLibrary(); var scene = Scene();
        using var renderer = new SceneRenderer();
        RectD[] tiles = [new(-180, -90, 220, 130), new(80, 40, 220, 170)];
        foreach (var tile in tiles) using (Draw(renderer, scene, assets, tile)) { }
        long allocations = renderer.RasterIntermediateSurfaceCount, reused = renderer.DepthSurfaceReuseCount;
        for (int frame = 0; frame < 3; frame++)
            foreach (var tile in tiles) using (Draw(renderer, scene, assets, tile)) { }
        check("two screen sizes reuse depth scratch surfaces after warm-up without new allocations",
            renderer.RasterIntermediateSurfaceCount == allocations && renderer.DepthSurfaceReuseCount > reused);
        check("raster scratch reuse never creates GPU intermediates", renderer.GpuIntermediateSurfaceCount == 0);

        // Equal-sized targets at different origins exercise Save/clip/translation cleanup on return.
        RectD[] shiftedTiles = [new(-150, -75, 220, 130), new(35, 65, 220, 130)];
        for (int frame = 0; frame < 4; frame++)
        {
            scene.Canvas.BackgroundColor = frame % 2 == 0 ? 0x80408020 : 0;
            scene.Canvas.Grid.Visible = frame % 2 == 0;
            scene.Canvas.Projection.Matrix = Projective2D.FromAffine(Affine2D.Translation(frame * 9, -frame * 5))
                .Compose(new(1, .02, 0, -.01, 1, 0, -.0004, -.0007, 1));
            scene.Elements[0].Visible = frame % 2 == 0;
            scene.Elements[1].Style.Fill = frame % 2 == 0 ? 0xB0E06040 : 0x803070E0;
            scene.Elements[1].BillboardHeight += 8;
            using var fresh = new SceneRenderer(); using var full = Draw(fresh, scene, assets, PixelBounds(scene.Viewport));
            bool clean = true, exactReuse = true;
            foreach (var tile in shiftedTiles)
            {
                using var pixels = Draw(renderer, scene, assets, tile);
                using var freshTile = Draw(fresh, scene, assets, tile);
                exactReuse &= pixels.Bytes.SequenceEqual(freshTile.Bytes);
                clean &= CompareCrop($"scratch reset frame {frame}", full, PixelBounds(scene.Viewport), pixels, tile, true, scene);
            }
            check($"reused scratch frame {frame} matches a fresh renderer byte-for-byte at the same origin", exactReuse);
            check($"reused scratch frame {frame} matches full-viewport crop within bounded edge rasterization", clean);
        }
    }

    private static void LiveSamplingAcrossTiles(Action<string, bool> check)
    {
        var assets = new AssetLibrary(); var scene = Scene();
        scene.Canvas.BackgroundColor = 0; scene.Canvas.Grid.Visible = false; scene.LayerOpacity = 1;
        var blur = Board(scene, new(60, 80), 0); blur.Kind = ElementKind.LiveBlur;
        blur.Width = 170; blur.Height = 150; blur.BillboardHeight = 40;
        blur.Effects = [new() { Kind = ImageEffectKind.Blur, Amount = 8 }];
        scene.Elements = [blur, Board(scene, new(0, -10), 0)];
        using var bitmap = new SKBitmap(480, 300);
        for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++)
            bitmap.SetPixel(x, y, x < 240 ? SKColors.Red : SKColors.Blue);
        bitmap.SetImmutable();
        using var backdrop = new DesktopBackdrop([new(scene.Viewport, SKImage.FromBitmap(bitmap))]);
        using var renderer = new SceneRenderer { LiveBackdrop = backdrop };
        using var reference = new SceneRenderer { LiveBackdrop = backdrop };
        var tile = new RectD(60, -90, 240, 300); var bounds = PixelBounds(scene.Viewport);
        using (var full = Draw(reference, scene, assets, bounds))
        using (var cropped = Draw(renderer, scene, assets, tile))
        {
            check("live blur across a screen boundary preserves full-viewport sampling halo", CompareCrop("live blur sampling halo", full, bounds, cropped, tile, false));
            var mixed = cropped.GetPixel(1, 140);
            check("cross-screen blur actually reads red pixels outside the blue target tile", mixed.Red > 20 && mixed.Blue > 20 && mixed.Alpha == 255);
        }
        var selection = scene.Clone(); selection.Elements = [blur.Clone()];
        renderer.LiveEffectContext = scene; reference.LiveEffectContext = scene;
        using (var full = Draw(reference, selection, assets, bounds))
        using (var cropped = Draw(renderer, selection, assets, tile))
            check("isolated depth filter context keeps full sampling while output is clipped", CompareCrop("isolated context", full, bounds, cropped, tile, false));
        renderer.LiveEffectContext = null; reference.LiveEffectContext = null;
    }

    private static void RetentionBoundAndDispose(Action<string, bool> check)
    {
        const long limit = 128L * 1024 * 1024;
        var assets = new AssetLibrary(); var scene = Scene();
        scene.Viewport = new(0, 0, 2060, 2048); scene.Canvas.Grid.Visible = false;
        scene.Elements = [Board(scene, new(100, 100), 0x80F09020)];
        using var renderer = new SceneRenderer();
        bool bounded = true;
        for (int i = 0; i < 10; i++)
        {
            using var pixels = Draw(renderer, scene, assets, new(0, 0, 80 + i, 60 + i));
            bounded &= renderer.CachedDepthSurfaceBytes <= limit && renderer.CachedDepthSurfaceCount <= 8;
        }
        // Four distinct pairs are slightly larger than 128 MiB: byte limit must evict too.
        for (int i = 0; i < 4; i++)
        {
            using var pixels = Draw(renderer, scene, assets, new(0, 0, 2048 + i, 2048));
            bounded &= renderer.CachedDepthSurfaceBytes <= limit && renderer.CachedDepthSurfaceCount <= 8;
        }
        check("varying depth target sizes retain at most eight surfaces and 128 MiB", bounded && renderer.CachedDepthSurfaceBytes > 0);
        renderer.Dispose();
        check("renderer disposal releases all retained depth scratch surfaces", renderer.CachedDepthSurfaceBytes == 0 && renderer.CachedDepthSurfaceCount == 0);
    }

    private static SceneDocument Scene()
    {
        var scene = new SceneDocument { Viewport = new(-180, -90, 480, 300), LayerOpacity = .7, Canvas = new()
        {
            DepthPriorityOcclusion = true, BackgroundColor = 0x50204060,
            Grid = new() { Visible = true, Color = 0x8050B0F0, Width = 1.5 },
            Projection = new() { Enabled = true, CellSize = 20, Matrix = new(1, .02, 0, -.01, 1, 0, -.0004, -.0007, 1),
                Quad = [new(-160, -70), new(270, -60), new(280, 190), new(-150, 200)] }
        } };
        scene.Elements = [ScreenBox(scene, scene.Viewport, 0x5038BDF8), Board(scene, new(-50, 20), 0xB0FBBF24), Board(scene, new(150, 130), 0xA0EC4899),
            new() { Kind = ElementKind.Ellipse, Width = 170, Height = 130, Transform = Affine2D.Translation(-60, -10).Compose(Affine2D.Rotation(13)),
                Style = new() { Fill = 0x8040A070, Stroke = 0xC0F0E060, StrokeWidth = 3, Opacity = .75 } }];
        return scene;
    }

    private static CanvasElement Board(SceneDocument scene, PointD screenAnchor, uint fill)
    {
        var mapping = CanvasMapping.Create(scene); mapping.TryToPlane(screenAnchor, out var anchor);
        return new() { Kind = ElementKind.Rectangle, IsBillboard = true, BillboardAnchor = anchor, BillboardHeight = 35,
            Width = 180, Height = 210, Style = new() { Fill = fill, StrokeWidth = 0 } };
    }

    private static CanvasElement ScreenBox(SceneDocument scene, RectD bounds, uint fill) => new()
    {
        Kind = ElementKind.Rectangle, Width = bounds.Width, Height = bounds.Height,
        ProjectivePlacement = CanvasMapping.Create(scene).ScreenToPlane.Compose(Projective2D.FromAffine(Affine2D.Translation(bounds.X, bounds.Y))),
        Style = new() { Fill = fill, StrokeWidth = 0 }
    };

    private static RectD PixelBounds(RectD viewport) => new(Math.Floor(viewport.Left), Math.Floor(viewport.Top),
        Math.Ceiling(viewport.Right) - Math.Floor(viewport.Left), Math.Ceiling(viewport.Bottom) - Math.Floor(viewport.Top));

    private static SKBitmap Draw(SceneRenderer renderer, SceneDocument scene, AssetLibrary assets, RectD bounds,
        bool calibrationReference = false, float dx = 0, float dy = 0)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)bounds.Width, (int)bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent); surface.Canvas.Translate(-(float)bounds.X + dx, -(float)bounds.Y + dy);
        renderer.Draw(surface.Canvas, scene, assets, calibrationReference: calibrationReference);
        using var image = surface.Snapshot(); return SKBitmap.FromImage(image);
    }

    private static bool CompareCrop(string label, SKBitmap full, RectD fullBounds, SKBitmap tile, RectD tileBounds, bool allowEdgeRasterization = true, SceneDocument? document = null)
    {
        int changed = 0, maxAlpha = 0, maximum = 0; long total = 0; var examples = new List<string>();
        int maxPremultiplied = 0, overEight = 0, boundaryOutliers = 0; long totalPremultiplied = 0; string maximumExample = "";
        bool boundedNonBoundaryPixels = true;
        var fullBytes = full.Bytes; var tileBytes = tile.Bytes;
        int offsetX = (int)(tileBounds.X - fullBounds.X), offsetY = (int)(tileBounds.Y - fullBounds.Y);
        for (int y = 0; y < tile.Height; y++) for (int x = 0; x < tile.Width; x++)
        {
            var expected = full.GetPixel(x + offsetX, y + offsetY); var actual = tile.GetPixel(x, y);
            if (expected == actual) continue;
            changed++;
            int r = Math.Abs(expected.Red - actual.Red), g = Math.Abs(expected.Green - actual.Green), b = Math.Abs(expected.Blue - actual.Blue), a = Math.Abs(expected.Alpha - actual.Alpha);
            maxAlpha = Math.Max(maxAlpha, a); maximum = Math.Max(maximum, Math.Max(a, Math.Max(r, Math.Max(g, b)))); total += r + g + b + a;
            if (examples.Count < 5) examples.Add($"({x},{y}) {expected} vs {actual}");
            int fullOffset = (y + offsetY) * full.RowBytes + (x + offsetX) * 4, tileOffset = y * tile.RowBytes + x * 4;
            int pixelMaximum = 0;
            for (int channel = 0; channel < 4; channel++)
            {
                int difference = Math.Abs(fullBytes[fullOffset + channel] - tileBytes[tileOffset + channel]);
                pixelMaximum = Math.Max(pixelMaximum, difference); totalPremultiplied += difference;
            }
            if (pixelMaximum > maxPremultiplied)
            {
                maxPremultiplied = pixelMaximum;
                var screen = new PointD(tileBounds.X + x + .5, tileBounds.Y + y + .5);
                maximumExample = $"screen={screen}, distanceToDepthBoundary={(document is null ? double.PositiveInfinity : BoundaryDistance(document, screen)):F8}, {expected} vs {actual}";
            }
            if (pixelMaximum > 8) overEight++;
            if (pixelMaximum > 32)
            {
                var center = new PointD(tileBounds.X + x + .5, tileBounds.Y + y + .5);
                bool atBoundary = document is not null && BoundaryDistance(document, center) <= .05;
                boundedNonBoundaryPixels &= atBoundary && pixelMaximum <= 48;
                boundaryOutliers++;
            }
        }
        double meanPremultiplied = totalPremultiplied / (double)(tile.Width * tile.Height * 4), overEightRatio = overEight / (double)(tile.Width * tile.Height);
        bool equivalent = changed == 0 || allowEdgeRasterization && boundedNonBoundaryPixels && boundaryOutliers <= 4 && maxAlpha <= 24 && meanPremultiplied < .15 && overEightRatio < .005;
        string? directory = Environment.GetEnvironmentVariable("FLOATCANVAS_DEPTH_DIAGNOSTICS");
        if (changed > 0 && (!equivalent || !string.IsNullOrEmpty(directory)))
        {
            Console.WriteLine($"Depth ROI pixels {label}, tile={tileBounds}: changed={changed}; maxRGBA={maximum}; maxAlpha={maxAlpha}; meanRGBA={total / (double)(tile.Width * tile.Height * 4):F6}; maxPremultiplied={maxPremultiplied}; meanPremultiplied={meanPremultiplied:F6}; pixelsOverEight={overEight} ({overEightRatio:P4}); maximum={maximumExample}; {string.Join("; ", examples)}");
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
                string name = string.Concat(label.Select(c => char.IsLetterOrDigit(c) ? c : '-')) + $"-{tileBounds.X}-{tileBounds.Y}-{tile.Width}";
                using var expected = new SKBitmap(tile.Width, tile.Height); using var diff = new SKBitmap(tile.Width, tile.Height);
                for (int y = 0; y < tile.Height; y++) for (int x = 0; x < tile.Width; x++)
                {
                    var e = full.GetPixel(x + offsetX, y + offsetY); var a = tile.GetPixel(x, y); expected.SetPixel(x, y, e);
                    diff.SetPixel(x, y, e == a ? SKColors.Transparent : SKColors.Magenta);
                }
                Save(expected, Path.Combine(directory, name + "-full-crop.png")); Save(tile, Path.Combine(directory, name + "-tile.png")); Save(diff, Path.Combine(directory, name + "-diff.png"));
            }
        }
        // Ordinary (depth disabled) raster drawing has the same subpixel edge differences when
        // its target origin changes. Bound actual premultiplied output, rather than amplifying
        // near-transparent RGB via GetPixel. Hard clip samples within .05 screen pixels of a depth
        // boundary can change ownership: allow at most four such samples, with bounded color and
        // alpha. Exact same-origin reuse and flat-alpha seams are verified separately; fractional
        // phases and live-filter fallbacks must remain exact.
        return equivalent;
    }

    private static void Save(SKBitmap bitmap, string path)
    {
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100); File.WriteAllBytes(path, data.ToArray());
    }

    private static double BoundaryDistance(SceneDocument document, PointD screen)
    {
        var mapping = CanvasMapping.Create(document); var inverse = mapping.ScreenToPlane;
        double gradient = Math.Sqrt(inverse.M31 * inverse.M31 + inverse.M32 * inverse.M32);
        if (gradient == 0) return double.PositiveInfinity;
        return document.Elements.Where(e => e.IsBillboard && e.Visible).Select(e =>
            CanvasDepthOrder.TryAnchorDepth(mapping, e.BillboardAnchor, out double depth)
                ? Math.Abs(inverse.Denominator(screen) - 1 / depth) / gradient : double.PositiveInfinity).DefaultIfEmpty(double.PositiveInfinity).Min();
    }
}
