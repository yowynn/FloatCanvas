using System.Diagnostics;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class PerspectiveOpacityRenderingTests
{
    internal static void Run(Action<string, bool> check, bool performance = true)
    {
        foreach (var kind in new[] { ElementKind.Freehand, ElementKind.Highlighter, ElementKind.Rectangle, ElementKind.RoundedRectangle,
            ElementKind.Ellipse, ElementKind.Triangle, ElementKind.Symbol, ElementKind.Arrow, ElementKind.DoubleArrow })
        foreach (bool compound in new[] { false, true })
        foreach (double opacity in new[] { .01, .5, .99 })
        foreach (int clip in new[] { 0, 1, 2, 3 })
        {
            var scene = new SceneDocument { Viewport = new(0, 0, 256, 192) };
            scene.Elements.Add(new() { Kind = kind, Width = 100, Height = 64,
                Points = [new(0, 0), new(100, 60), new(0, 60), new(100, 0)],
                Transform = Affine2D.Translation(50.125, 40.75),
                ProjectivePlacement = new(1, .05, 0, -.03, 1, 0, .0003, .0002, 1),
                Style = new() { Stroke = 0xDDCC3311, Fill = compound ? 0x8833DD66 : 0, StrokeWidth = 9, Opacity = opacity } });
            int delta = Compare(scene, clip);
            bool single = kind is ElementKind.Freehand or ElementKind.Highlighter || ElementGeometry.IsBoxShape(kind) && !compound;
            check($"perspective {kind} compound {compound} opacity {opacity} clip {clip} delta {delta}", delta <= (single ? 2 : 0));
        }
        SafetyClips(check);
        HorizonOverlap(check);
        StrokeCacheChanges(check);
        OwnedRaster(check);
        if (performance) Performance(check);
    }

    private static void StrokeCacheChanges(Action<string, bool> check)
    {
        foreach (var kind in new[] { ElementKind.Freehand, ElementKind.Highlighter })
        foreach (uint stroke in new[] { 0xFFCC3311u, 0xDDCC3311u })
        {
            var scene = new SceneDocument { Viewport = new(0, 0, 256, 192) };
            var element = new CanvasElement { Kind = kind, Style = new() { Stroke = stroke, StrokeWidth = 4, Opacity = .5 },
                Points = [.. Enumerable.Range(0, 207).Select(n => new PointD(100 + 70 * Math.Sin(n * .2), 80 + 50 * Math.Sin(n * .37)))] };
            scene.Elements.Add(element); using var renderer = new SceneRenderer();
            for (int frame = 0; frame < 60; frame++)
            {
                int i = frame / 3;
                element.ProjectivePlacement = new(1 + i * .015, .05, .125 * frame, -.03, 1, -.25 * frame, .0003, .0002, 1);
                element.Style.StrokeWidth = i < 7 ? 4 : 9.25;
                element.Style.StrokePattern = i is >= 12 and < 16 ? StrokePattern.Dashed : StrokePattern.Solid;
                if (frame == 27) element.Points = [.. element.Points.Select((p, n) => n == 3 ? p + new PointD(4, -3) : p)];
                int delta = Compare(scene, i % 4, renderer);
                check($"207-point {kind} {stroke:X8} self-overlap keeps coverage across scale/width/pattern/point edit frame {frame} (delta {delta})", delta <= 2);
            }
        }
    }

    private static void HorizonOverlap(Action<string, bool> check)
    {
        foreach (var kind in new[] { ElementKind.Freehand, ElementKind.Highlighter, ElementKind.Rectangle })
        {
            var scene = new SceneDocument { Viewport = new(0, 0, 256, 192) };
            scene.Canvas.Projection = CanvasCalibration.CreateInitial(scene.Viewport); scene.Canvas.Projection.Enabled = true;
            scene.Canvas.Projection.Matrix = new(1, 0, 0, 0, 1, 200, 0, .01, 1);
            for (int i = 0; i < 24; i++) scene.Elements.Add(new() { Kind = kind, Width = 200, Height = 120,
                Points = [.. Enumerable.Range(0, 207).Select(n => new PointD(100 + 70 * Math.Sin(n * .2), 80 + 50 * Math.Sin(n * .37)))],
                Transform = Affine2D.Translation(i * .125, i * .25), Style = new() { Stroke = 0xDDCC3311, Fill = 0xDDCC3311,
                    StrokeWidth = kind == ElementKind.Rectangle ? 0 : 4, Opacity = .5 } });
            using var reference = new SceneRenderer { OptimizeElementOpacity = false }; using var renderer = new SceneRenderer();
            using var expected = SKSurface.Create(new SKImageInfo(256, 192, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var actual = SKSurface.Create(new SKImageInfo(256, 192, SKColorType.Bgra8888, SKAlphaType.Premul));
            expected.Canvas.Clear(SKColors.Transparent); actual.Canvas.Clear(SKColors.Transparent);
            reference.Draw(expected.Canvas, scene, new(), calibrationReference: true, includeGrid: false);
            renderer.DrawCalibrationOnClearedRasterTarget(actual, scene, new(), false);
            using var a = expected.PeekPixels(); using var b = actual.PeekPixels();
            check($"visible horizon {kind} overlap retains exact reference coverage", a.GetPixelSpan().SequenceEqual(b.GetPixelSpan()));
        }
    }

    private static void OwnedRaster(Action<string, bool> check)
    {
        foreach (double layerOpacity in new[] { .5, 1d })
        foreach (var format in new[] { SKColorType.Bgra8888, SKColorType.Rgba8888 })
        foreach (int clip in new[] { 0, 1, 2 })
        {
            var scene = new SceneDocument { Viewport = new(0, 0, 257, 193), LayerOpacity = layerOpacity };
            scene.Canvas.Projection = CanvasCalibration.CreateInitial(scene.Viewport); scene.Canvas.Projection.Enabled = true;
            scene.Canvas.Projection.Matrix = new(1, .05, 0, -.03, 1, 0, .0003, .0002, 1);
            for (int i = 0; i < 12; i++) scene.Elements.Add(new() { Kind = i % 3 == 0 ? ElementKind.Arrow : ElementKind.Freehand,
                Points = Enumerable.Range(0, 48).Select(n => new PointD(n * 3, 30 + Math.Sin(n * .55 + i) * 27)).ToList(),
                Transform = Affine2D.Translation(20 + i * .75, 35 + i * .5), Style = new() { Stroke = 0xFFCC3311, StrokeWidth = 4 + i % 2,
                    Opacity = new[] { .01, .25, .5, .99 }[i % 4], StrokePattern = (StrokePattern)(i % 3) } });
            using var reference = new SceneRenderer { OptimizeElementOpacity = false }; using var renderer = new SceneRenderer();
            using var expected = SKSurface.Create(new SKImageInfo(257, 193, format, SKAlphaType.Premul));
            using var actual = SKSurface.Create(new SKImageInfo(257, 193, format, SKAlphaType.Premul));
            void Prepare(SKCanvas canvas)
            {
                canvas.Clear(SKColors.Transparent);
                if (clip == 1) canvas.ClipRect(new(13, 9, 249, 185));
                if (clip == 2) { using var builder = new SKPathBuilder(); builder.AddCircle(120, 90, 75); using var path = builder.Detach(); canvas.ClipPath(path, antialias: true); }
            }
            Prepare(expected.Canvas); Prepare(actual.Canvas);
            reference.Draw(expected.Canvas, scene, new(), calibrationReference: true);
            renderer.DrawCalibrationOnClearedRasterTarget(actual, scene, new(), true);
            using var a = expected.PeekPixels(); using var b = actual.PeekPixels(); int delta = 0;
            var first = a.GetPixelSpan(); var second = b.GetPixelSpan();
            for (int i = 0; i < first.Length; i++) delta = Math.Max(delta, Math.Abs(first[i] - second[i]));
            check($"owned raster {format} group {layerOpacity} clip {clip} retains source-over and pixel tails (delta {delta})", delta <= 4);
        }
    }

    private static void SafetyClips(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer();
        var scene = new SceneDocument { Viewport = new(0, 0, 256, 192) };
        scene.Canvas.Projection = CanvasCalibration.CreateInitial(scene.Viewport); scene.Canvas.Projection.Enabled = true;
        scene.Canvas.Projection.Matrix = new(1, 0, 0, 0, 1, 200, .002, .01, 1);
        var shape = new CanvasElement { Kind = ElementKind.Rectangle, Width = 5000, Height = 1000,
            Style = new() { Stroke = 0xDDCC3311, Fill = 0x8833DD66, StrokeWidth = 9, Opacity = .5 } };
        scene.Elements.Add(shape);
        for (int i = 0; i < 6; i++)
        {
            shape.Transform = Affine2D.Translation(i * 3.25, i * 1.5);
            int delta = Compare(scene, 0, renderer);
            check($"hard safety polygon compound frame {i} retains reference pixels delta={delta}", delta == 0);
        }
        check("finite perspective compound uses reusable opacity scratch with a hard safety clip", renderer.CachedElementOpacityBytes > 0);
        shape.Transform = Affine2D.Translation(-1000, -300);
        check("source crossing a projective pole retains bounded reference rendering", Compare(scene, 0, renderer) == 0);
        scene.Canvas.Projection.Matrix = new(1, 0, 0, 0, 1, 0, 0, .01, 1);
        shape.ProjectivePlacement = new(1, 0, 0, 0, 1, 0, 0, -.002, 1);
        check("projective placement crossing a pole keeps the source clipping path", Compare(scene, 0, renderer) == 0);
    }

    internal static void Performance(Action<string, bool> check)
    {
        foreach (bool genuine in new[] { false, true })
        {
            var scene = new SceneDocument { Viewport = new(0, 0, 3840, 2160) };
            scene.Canvas.Projection = CanvasCalibration.CreateInitial(scene.Viewport); scene.Canvas.Projection.Enabled = true;
            scene.Canvas.Projection.Matrix = genuine ? new(1, .05, 0, -.03, 1, 0, .00003, .00002, 1) : new(1, 0, 0, 0, 1, 0, 5.354e-20, 0, 1);
            for (int i = 0; i < 425; i++) scene.Elements.Add(new() { Kind = i % 2 == 0 ? ElementKind.Highlighter : ElementKind.Rectangle,
                Width = 100, Height = 65, Points = [new(0, 0), new(100, 60), new(0, 60)],
                Transform = Affine2D.Translation(i % 25 * 140.25, i / 25 * 110.5), Style = new() { Stroke = 0xEEDF4D2D, StrokeWidth = 9, Opacity = .5 } });
            using var renderer = new SceneRenderer(); using var surface = SKSurface.Create(new SKImageInfo(3840, 2160, SKColorType.Bgra8888, SKAlphaType.Premul));
            var times = new List<double>();
            for (int i = 0; i < 2; i++) { surface.Canvas.Clear(SKColors.Transparent); var watch = Stopwatch.StartNew(); renderer.Draw(surface.Canvas, scene, new()); times.Add(watch.Elapsed.TotalMilliseconds); }
            Console.WriteLine($"425 perspective objects genuine={genuine}: {string.Join(", ", times.Select(t => t.ToString("F2")))} ms");
            check($"425 translucent perspective objects genuine={genuine} avoid full-target layers", times[1] < 500);
        }
    }

    private static int Compare(SceneDocument scene, int clip, SceneRenderer? candidate = null)
    {
        using var reference = new SceneRenderer { OptimizeElementOpacity = false }; using var owned = candidate is null ? new SceneRenderer() : null;
        var renderer = candidate ?? owned!;
        using var expected = SKSurface.Create(new SKImageInfo(256, 192, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var actual = SKSurface.Create(new SKImageInfo(256, 192, SKColorType.Bgra8888, SKAlphaType.Premul));
        void Prepare(SKCanvas canvas)
        {
            canvas.Clear(new(13, 17, 77, 255));
            if (clip == 1) canvas.ClipRect(new(42, 28, 179, 143));
            if (clip == 2) canvas.ClipRect(new(42.25f, 28.75f, 179.5f, 143.25f), SKClipOperation.Intersect, true);
            if (clip == 3) { using var builder = new SKPathBuilder(); builder.AddCircle(120, 90, 70); using var path = builder.Detach(); canvas.ClipPath(path, antialias: true); }
        }
        Prepare(expected.Canvas); Prepare(actual.Canvas); reference.Draw(expected.Canvas, scene, new()); renderer.Draw(actual.Canvas, scene, new());
        using var a = expected.PeekPixels(); using var b = actual.PeekPixels(); int delta = 0;
        var first = a.GetPixelSpan(); var second = b.GetPixelSpan(); for (int i = 0; i < first.Length; i++) delta = Math.Max(delta, Math.Abs(first[i] - second[i]));
        return delta;
    }
}
