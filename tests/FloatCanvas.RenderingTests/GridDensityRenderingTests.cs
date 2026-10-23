using System.Diagnostics;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class GridDensityRenderingTests
{
    internal static void Run(Action<string, bool> check)
    {
        EdgeAlignment(check);
        var scene = new SceneDocument { Viewport = new(0, 0, 3840, 2160) };
        PointD[] quad = [new(1870, 700), new(1970, 700), new(3600, 2000), new(240, 2000)];
        check("dense-grid fixture calibrates with the horizon inside the 4K viewport", CanvasCalibration.TryCalibrate(
            CanvasCalibration.CreateInitial(scene.Viewport, 256, 256), quad, scene.Viewport, out var projection, out _));
        scene.Canvas.Projection = projection;
        check("dense-grid fixture exercises a visible horizon", !CanvasCalibration.IsStableViewport(projection.Matrix, scene.Viewport));
        using var renderer = new SceneRenderer(); var assets = new AssetLibrary();
        using var surface = SKSurface.Create(new SKImageInfo(3840, 2160));
        foreach (bool reference in new[] { true, false })
        foreach (var pattern in reference ? new[] { StrokePattern.Solid } : new[] { StrokePattern.Solid, StrokePattern.Dashed, StrokePattern.Dotted })
        {
            scene.Canvas.Grid.Visible = !reference; scene.Canvas.Grid.Color = 0xFF00CC00; scene.Canvas.Grid.Width = 1;
            scene.Canvas.Grid.Pattern = pattern;
            surface.Canvas.Clear(SKColors.Transparent);
            var timer = Stopwatch.StartNew();
            renderer.Draw(surface.Canvas, scene, assets, calibrationReference: reference); surface.Canvas.Flush();
            timer.Stop();
            using var image = surface.Snapshot(); using var pixels = SKBitmap.FromImage(image);
            int ink = 0, samples = 0;
            for (int y = 720; y < 780; y++) for (int x = 1840; x < 2000; x++)
            { samples++; if (pixels.GetPixel(x, y).Alpha > 16) ink++; }
            Console.WriteLine($"GRID density reference={reference}/{pattern}: ink={ink}/{samples}; candidates={renderer.GridCandidateCount}; lines={renderer.GridLineCount}; dashIntervals={renderer.GridDashIntervalCount:F0}/{renderer.UnresolvedGridDashIntervalCount:F0}; ms={timer.Elapsed.TotalMilliseconds:F2}");
            check($"{(reference ? "reference" : "custom")} grid leaves resolvable gaps in the compressed far region", ink < samples * .35);
            check("grid generation and drawing stay bounded", renderer.GridCandidateCount <= 5500 && renderer.GridLineCount <= 1800 && timer.Elapsed < TimeSpan.FromMilliseconds(500));
            if (!reference && pattern != StrokePattern.Solid)
                check("unresolvable far dash expansion is eliminated before Skia draws it", renderer.GridDashIntervalCount < 50_000 && renderer.GridDashIntervalCount < renderer.UnresolvedGridDashIntervalCount / 10);
        }
    }

    private static void EdgeAlignment(Action<string, bool> check)
    {
        var scene = new SceneDocument { Viewport = new(0, 0, 1000, 800) };
        PointD[] quad = [new(230, 150), new(740, 80), new(920, 710), new(90, 630)];
        check("edge alignment fixture has a nonaffine even-count calibration", CanvasCalibration.TryCalibrate(
            CanvasCalibration.CreateInitial(scene.Viewport, 8, 6), quad, scene.Viewport, out var projection, out _));
        scene.Canvas.Projection = projection;
        scene.Canvas.Grid = new() { Visible = true, Color = 0xFF00CC00, Width = 2 };
        using var renderer = new SceneRenderer(); var assets = new AssetLibrary();
        using var surface = SKSurface.Create(new SKImageInfo(1000, 800));
        foreach (bool reference in new[] { false, true })
        {
            scene.Canvas.Grid.Visible = !reference;
            surface.Canvas.Clear(SKColors.Transparent);
            renderer.Draw(surface.Canvas, scene, assets, calibrationReference: reference);
            using var image = surface.Snapshot(); using var pixels = SKBitmap.FromImage(image);
            for (int edge = 0; edge < 4; edge++)
            {
                bool present = true;
                foreach (double fraction in new[] { .03, .19, .43, .71, .97 })
                {
                    var point = quad[edge] + (quad[(edge + 1) % 4] - quad[edge]) * fraction;
                    int x = (int)Math.Round(point.X), y = (int)Math.Round(point.Y);
                    present &= Enumerable.Range(-2, 5).Any(dx => Enumerable.Range(-2, 5).Any(dy => pixels.GetPixel(x + dx, y + dy).Alpha > 40));
                }
                check($"{(reference ? "reference" : "custom")} even-count edge {edge} and its corner neighborhoods lie on the rendered grid", present);
            }
        }
    }
}
