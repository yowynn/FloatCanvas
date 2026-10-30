using System.Diagnostics;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class DashedFreehandPerformanceTests
{
    internal static void Run(Action<string, bool> check)
    {
        Coverage(check);
        using var surface = SKSurface.Create(new SKImageInfo(1280, 720));
        var assets = new AssetLibrary();
        foreach (var kind in new[] { ElementKind.Freehand, ElementKind.Highlighter })
        {
            using var renderer = new SceneRenderer();
            var element = new CanvasElement
            {
                Kind = kind, Points = Enumerable.Range(0, 24).Select(Point).ToList(),
                Style = new() { Stroke = kind == ElementKind.Highlighter ? 0x8000CC88u : 0xFF00CC88u,
                    StrokeWidth = kind == ElementKind.Highlighter ? 12 : 4, StrokePattern = StrokePattern.Dashed }
            };
            var scene = new SceneDocument { Viewport = new(0, 0, 1280, 720), Elements = [element] };
            Draw(); // Warm both native stroke construction and raster drawing.
            element.Points = Enumerable.Range(0, 500).Select(Point).ToList();
            element.Style.StrokePattern = StrokePattern.Solid;
            double solid = Time(Draw);
            element.Style.StrokePattern = StrokePattern.Dashed;
            double switched = Time(Draw);
            long built = renderer.PathBuildCount;
            Draw();
            check(kind + " unchanged dashed strokes reuse their cached path", renderer.PathBuildCount == built);
            var append = new List<double>();
            for (int i = 500; i < 503; i++)
            {
                element.Points.Add(Point(i));
                append.Add(Time(Draw));
            }
            check(kind + " appending points rebuilds the actual dashed stroke", renderer.PathBuildCount == built + 3);
            // Generous frame-cost guard: catches the second-long global path operation
            // while allowing scheduler noise and slower CI hardware.
            double limit = Math.Max(100, solid * 50), median = append.Order().ElementAt(1);
            Console.WriteLine($"DASH_COST {kind} solid={solid:F3}ms switch={switched:F3}ms appendMedian={median:F3}ms limit={limit:F3}ms");
            check(kind + " switching a 500-point curve to dashed avoids a long UI-thread stall", switched < limit);
            check(kind + " appending to a 500-point dashed curve avoids a long UI-thread stall", median < limit);
            element.Points = Enumerable.Range(0, 2000).Select(Point).ToList();
            Draw();
            using var pixels = surface.PeekPixels();
            check(kind + " long self-crossing dashed strokes render visible ink without a path-operation failure",
                pixels.GetPixelSpan().ContainsAnyExcept((byte)0));

            void Draw()
            {
                surface.Canvas.Clear(SKColors.Transparent);
                renderer.Draw(surface.Canvas, scene, assets, includeBackground: false, includeCanvasBackground: false, includeGrid: false);
                surface.Canvas.Flush();
            }
        }
    }

    private static void Coverage(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer();
        foreach (var kind in new[] { ElementKind.Freehand, ElementKind.Highlighter })
        foreach (byte alpha in new byte[] { 255, 89 })
        {
            byte expectedAlpha = kind == ElementKind.Highlighter ? (byte)Math.Round(alpha * .35) : alpha;
            var element = new CanvasElement
            {
                Kind = kind, Points = [new(20, 40), new(80, 40)],
                Style = new() { Stroke = (uint)alpha << 24 | 0x00CC3311u, StrokeWidth = 8, StrokePattern = StrokePattern.Dashed }
            };
            var scene = new SceneDocument { Viewport = new(0, 0, 120, 100), Elements = [element] };
            using (var ink = Render())
            {
                check($"{kind} alpha {alpha} retains flat dash cuts and transparent gaps", ink.GetPixel(51, 37).Alpha == expectedAlpha && ink.GetPixel(52, 40).Alpha == 0);
                check($"{kind} alpha {alpha} retains the original round endpoint caps", ink.GetPixel(17, 40).Alpha >= expectedAlpha * .8 && ink.GetPixel(82, 40).Alpha >= expectedAlpha * .8);
            }
            element.Points = [new(20, 20), new(40, 20), new(40, 60)];
            using (var ink = Render())
                check($"{kind} alpha {alpha} retains the round corner without filling its next dash gap", ink.GetPixel(41, 18).Alpha == expectedAlpha && ink.GetPixel(40, 34).Alpha == 0);
            element.Points = [new(20, 20), new(40, 20), new(20, 20)];
            using (var ink = Render())
                check($"{kind} alpha {alpha} retains the round join at a reversing corner", ink.GetPixel(42, 20).Alpha >= expectedAlpha * .8);
            element.Points = [new(20, 20), new(90, 70), new(20, 70), new(90, 20)];
            CheckCrossing("self-crossings");
            element.Transform = Affine2D.Translation(4, 12).Compose(Affine2D.Scale(.9, .65));
            element.ProjectivePlacement = new(1, .05, 0, -.03, 1, 0, .001, .0002, 1);
            CheckCrossing("projected nonuniform self-crossings");

            void CheckCrossing(string name)
            {
                using var ink = Render();
                int maximum = 0, missed = 0;
                for (int y = 0; y < ink.Height; y++)
                for (int x = 0; x < ink.Width; x++)
                {
                    int coverage = ink.GetPixel(x, y).Alpha;
                    maximum = Math.Max(maximum, coverage);
                    if (coverage >= expectedAlpha * .95 && !ElementGeometry.HitTest(element, new(x + .5, y + .5), 0)) missed++;
                }
                check($"{kind} alpha {alpha} {name} apply ink alpha only once", maximum == expectedAlpha);
                check($"{kind} alpha {alpha} {name} remain selectable with zero tolerance", missed == 0);
            }

            SKBitmap Render() => SKBitmap.Decode(renderer.ExportPng(scene, new(), true));
        }
    }

    private static PointD Point(int i) => new(640 + 500 * Math.Sin(i * .012), 360 + 250 * Math.Sin(i * .017));
    private static double Time(Action draw)
    {
        long start = Stopwatch.GetTimestamp(); draw();
        return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
}
