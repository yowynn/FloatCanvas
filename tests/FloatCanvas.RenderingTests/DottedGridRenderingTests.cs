using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class DottedGridRenderingTests
{
    public static void Run(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer();
        var assets = new AssetLibrary();
        foreach (bool perspective in new[] { false, true })
        {
            var scene = Scene(perspective);
            string mode = perspective ? "perspective" : "flat";
            using var dotted = Pixels(renderer, scene, assets);
            scene.Canvas.Grid.Pattern = StrokePattern.Solid;
            using var solid = Pixels(renderer, scene, assets);
            scene.Canvas.Grid.Pattern = StrokePattern.Dotted;
            int dottedInk = GreenInk(dotted), solidInk = GreenInk(solid);
            check($"{mode} dotted grid renders substantial visible colored pixels", dottedInk > 250);
            check($"{mode} dotted grid retains gaps instead of becoming solid", dottedInk < solidInk * .8);
            var mapping = CanvasMapping.Create(scene);
            check($"{mode} dotted grid draws both horizontal and vertical families", BothFamilies(dotted, mapping));

            using var cached = CachedPixels(renderer, scene, assets);
            check($"{mode} cached dotted grid agrees with direct rendering", Difference(dotted, cached) <= 2);
            using var reference = Pixels(renderer, scene, assets, reference: true);
            check($"{mode} colored dotted grid remains visible over calibration guides", GreenInk(reference) > 250);
        }

        var defaultWidth = Scene(false);
        defaultWidth.Canvas.Grid.Width = 1;
        using (var pixels = Pixels(renderer, defaultWidth, assets))
            check("default one-pixel dotted grid remains visibly dotted", GreenInk(pixels) > 250 && BothFamilies(pixels, CanvasMapping.Create(defaultWidth)));

        var depth = Scene(true);
        depth.Canvas.DepthPriorityOcclusion = true;
        var depthMapping = CanvasMapping.Create(depth);
        depth.Elements.Add(new()
        {
            Kind = ElementKind.Rectangle, IsBillboard = true,
            BillboardAnchor = depthMapping.GridOrigin + new PointD(4 * depthMapping.GridCellSize, 4 * depthMapping.GridCellSize),
            BillboardHeight = 5, Width = 12, Height = 10,
            Style = new() { Stroke = 0, StrokeWidth = 0, Fill = 0xFFFF0000 }
        });
        using var depthPixels = Pixels(renderer, depth, assets);
        check("depth composition retains visible dotted grid pixels", GreenInk(depthPixels) > 250);
        check("depth composition retains both dotted grid families", BothFamilies(depthPixels, depthMapping));

        depth.Canvas.Grid.Visible = false;
        using var hidden = Pixels(renderer, depth, assets);
        check("hidden dotted grid does not leave colored pixels", GreenInk(hidden) == 0);
        using var hiddenReference = Pixels(renderer, depth, assets, reference: true);
        check("hidden dotted grid still allows neutral calibration guides", GreenInk(hiddenReference) == 0 && hiddenReference.Bytes.Any(value => value != 0));
    }

    public static void WriteEvidence(string directory)
    {
        Directory.CreateDirectory(directory);
        using var renderer = new SceneRenderer();
        var assets = new AssetLibrary();
        foreach (bool perspective in new[] { false, true })
        {
            var scene = Scene(perspective);
            string mode = perspective ? "perspective" : "flat";
            foreach (var pattern in new[] { StrokePattern.Dotted, StrokePattern.Dashed, StrokePattern.Solid })
            {
                scene.Canvas.Grid.Pattern = pattern;
                File.WriteAllBytes(Path.Combine(directory, $"grid-{mode}-{pattern.ToString().ToLowerInvariant()}.png"), renderer.ExportPng(scene, assets, true));
            }
        }
    }

    private static SceneDocument Scene(bool perspective)
    {
        var scene = new SceneDocument { Viewport = new(0, 0, 240, 180) };
        var projection = CanvasCalibration.CreateInitial(scene.Viewport);
        if (perspective)
        {
            PointD[] quad = [new(60, 35), new(180, 25), new(215, 150), new(25, 150)];
            if (!CanvasCalibration.TryCalibrate(projection, quad, scene.Viewport, out projection, out var error))
                throw new InvalidOperationException(error);
        }
        scene.Canvas.Projection = projection;
        scene.Canvas.Grid = new() { Visible = true, Width = 2, Color = 0xFF00CC00, Pattern = StrokePattern.Dotted };
        return scene;
    }

    private static bool BothFamilies(SKBitmap pixels, CanvasMapping mapping)
    {
        var center = mapping.ScreenToPlane.TransformPoint(new(120, 90));
        var crossing = mapping.GridOrigin + new PointD(Math.Round((center.X - mapping.GridOrigin.X) / mapping.GridCellSize),
            Math.Round((center.Y - mapping.GridOrigin.Y) / mapping.GridCellSize)) * mapping.GridCellSize;
        bool Family(bool vertical)
        {
            int hits = 0;
            for (double offset = -1.8; offset < 1.8; offset += .025)
            {
                // Sample between crossings so one grid family cannot stand in for the other.
                double fraction = offset - Math.Floor(offset);
                if (fraction < .2 || fraction > .8) continue;
                var point = crossing + (vertical ? new PointD(0, offset) : new PointD(offset, 0)) * mapping.GridCellSize;
                var screen = mapping.ToScreen(point);
                int x = (int)Math.Round(screen.X), y = (int)Math.Round(screen.Y);
                if (x < 1 || x >= pixels.Width - 1 || y < 1 || y >= pixels.Height - 1) continue;
                if (Enumerable.Range(Math.Max(0, x - 1), Math.Min(pixels.Width - 1, x + 1) - Math.Max(0, x - 1) + 1)
                    .Any(px => Enumerable.Range(Math.Max(0, y - 1), Math.Min(pixels.Height - 1, y + 1) - Math.Max(0, y - 1) + 1)
                        .Any(py => IsGreen(pixels.GetPixel(px, py))))) hits++;
            }
            return hits > 8;
        }
        return Family(true) && Family(false);
    }

    private static int GreenInk(SKBitmap pixels)
    {
        int count = 0;
        for (int y = 0; y < pixels.Height; y++)
            for (int x = 0; x < pixels.Width; x++)
                if (IsGreen(pixels.GetPixel(x, y))) count++;
        return count;
    }
    private static bool IsGreen(SKColor color) => color.Green > 150 && color.Red < 20 && color.Blue < 20 && color.Alpha > 80;
    private static SKBitmap Pixels(SceneRenderer renderer, SceneDocument scene, AssetLibrary assets, bool reference = false)
    {
        using var surface = SKSurface.Create(new SKImageInfo(240, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        renderer.Draw(surface.Canvas, scene, assets, calibrationReference: reference);
        using var snapshot = surface.Snapshot();
        return SKBitmap.FromImage(snapshot);
    }
    private static SKBitmap CachedPixels(SceneRenderer renderer, SceneDocument scene, AssetLibrary assets)
    {
        using var cache = new OverlaySceneCache(renderer);
        using var surface = SKSurface.Create(new SKImageInfo(240, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        cache.DrawOnClearedRasterTarget(surface, scene, assets, 1, 0);
        using var snapshot = surface.Snapshot();
        return SKBitmap.FromImage(snapshot);
    }
    private static int Difference(SKBitmap first, SKBitmap second)
    {
        int maximum = 0;
        var a = first.Bytes; var b = second.Bytes;
        for (int i = 0; i < a.Length; i++) maximum = Math.Max(maximum, Math.Abs(a[i] - b[i]));
        return maximum;
    }
}
