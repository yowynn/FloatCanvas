using System.Diagnostics;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class ElementOpacityRenderingTests
{
    public static void Run(Action<string, bool> check)
    {
        foreach (var kind in new[] { ElementKind.Freehand, ElementKind.Highlighter, ElementKind.Rectangle, ElementKind.RoundedRectangle, ElementKind.Ellipse })
        foreach (double opacity in new[] { .01, .25, .5, .77, .99 })
        foreach (int clip in new[] { 0, 1, 2 })
        {
            var scene = Scene(Element(kind, opacity));
            int delta = Compare(scene, clip);
            check($"single {kind} opacity {opacity} clip {clip} keeps coverage within two 8-bit rounding levels (actual {delta})", delta <= 2);
        }
        foreach (var kind in new[] { ElementKind.Rectangle, ElementKind.RoundedRectangle, ElementKind.Arrow, ElementKind.DoubleArrow })
        foreach (double opacity in new[] { .01, .25, .5, .77, .99 })
        foreach (int clip in new[] { 0, 1, 2 })
        {
            var element = Element(kind, opacity); element.Style.Fill = 0x8833DD66;
            var scene = Scene(element);
            check($"compound {kind} opacity {opacity} clip {clip} keeps one compositing unit", Compare(scene, clip) == 0);
        }
        var shared = Scene(Element(ElementKind.Rectangle, .5));
        shared.Elements[0].Style.Fill = 0xAA3366CC;
        using var retained = new SceneRenderer();
        for (int frame = 0; frame < 8; frame++)
        {
            shared.Elements[0].Transform = Affine2D.Translation(30 + frame * 9.25, 22 - frame * 2.5);
            check($"reused compound opacity scratch clears prior pixels frame {frame}", Compare(shared, frame % 3, retained) == 0);
        }
        shared.Elements[0].ProjectivePlacement = new(1, .05, 0, -.03, 1, 0, .0003, .0002, 1);
        check("perspective opacity retains the reference layer path", Compare(shared) == 0);
        shared.Elements[0].ProjectivePlacement = null;
        retained.CacheByteLimit = 1;
        check("opacity scratch respects a reduced cache limit and keeps reference pixels", Compare(shared, 0, retained) == 0);
        check("reducing the cache budget releases the retained opacity surface", retained.CachedElementOpacityBytes == 0);
        retained.CacheByteLimit = 64 * 1024 * 1024;
        Compare(shared, 0, retained);
        retained.Dispose();
        check("renderer disposal releases the opacity surface", retained.CachedElementOpacityBytes == 0);
        var random = new Random(431);
        for (int i = 0; i < 32; i++)
        {
            var element = Element(i % 2 == 0 ? ElementKind.Freehand : ElementKind.Rectangle, random.NextDouble());
            element.Style.Stroke = (uint)random.NextInt64(0, 1L << 32);
            element.Style.StrokePattern = (StrokePattern)(i % 3);
            if (i % 4 == 0) element.Points = [new(32.25, 31.5)];
            if (i % 4 == 1) { element.Style.Fill = element.Style.Stroke; element.Style.StrokeWidth = 0; }
            check($"single primitive color/alpha/dash/dot sample {i} preserves opacity within rounding", Compare(Scene(element), i % 3) <= 2);
        }
        FilledStrokeOverlap(check);
    }

    public static void Performance(Action<string, bool> check)
    {
        var scene = new SceneDocument { Viewport = new(0, 0, 3840, 2160) };
        for (int i = 0; i < 425; i++)
        {
            var element = Element(i % 3 == 0 ? ElementKind.Highlighter : i % 3 == 1 ? ElementKind.Freehand : ElementKind.Rectangle, .5);
            element.Transform = Affine2D.Translation((i % 25) * 140.25, (i / 25) * 110.5);
            scene.Elements.Add(element);
        }
        using var surface = SKSurface.Create(new SKImageInfo(3840, 2160, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var renderer = new SceneRenderer();
        var times = new List<double>();
        for (int frame = 0; frame < 3; frame++)
        {
            surface.Canvas.Clear(SKColors.Transparent);
            var watch = Stopwatch.StartNew();renderer.Draw(surface.Canvas, scene, new());watch.Stop();
            times.Add(watch.Elapsed.TotalMilliseconds);
        }
        Console.WriteLine($"425 half-transparent elements at 4K: {string.Join(", ", times.Select(x => x.ToString("F2")))} ms");
        check("425 half-transparent 4K objects avoid second-long full-screen element layers", times.Skip(1).Max() < 500);
    }

    private static void FilledStrokeOverlap(Action<string, bool> check)
    {
        var scene = new SceneDocument { Viewport = new(0, 0, 100, 100), Elements = [new()
        { Kind = ElementKind.Rectangle, Width = 60, Height = 60, Transform = Affine2D.Translation(20, 20),
            Style = new() { Stroke = 0xFFFF0000, Fill = 0xFFFF0000, StrokeWidth = 12, Opacity = .5 } }] };
        using var renderer = new SceneRenderer();using var surface = SKSurface.Create(new SKImageInfo(100, 100, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);renderer.Draw(surface.Canvas, scene, new());using var image = surface.Snapshot();using var bitmap = SKBitmap.FromImage(image);
        check("fill and stroke overlap is not independently faded twice", bitmap.GetPixel(22, 50) == bitmap.GetPixel(50, 50) && bitmap.GetPixel(50, 50).Alpha == 128);
    }

    private static int Compare(SceneDocument scene, int clip = 0, SceneRenderer? candidate = null)
    {
        using var reference = new SceneRenderer { OptimizeElementOpacity = false };
        using var owned = candidate is null ? new SceneRenderer() : null;
        var actual = candidate ?? owned!;
        using var expectedSurface = SKSurface.Create(new SKImageInfo(256, 192, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var actualSurface = SKSurface.Create(new SKImageInfo(256, 192, SKColorType.Bgra8888, SKAlphaType.Premul));
        Prepare(expectedSurface.Canvas, clip);Prepare(actualSurface.Canvas, clip);
        reference.Draw(expectedSurface.Canvas, scene, new());actual.Draw(actualSurface.Canvas, scene, new());
        using var expected = expectedSurface.PeekPixels();using var pixels = actualSurface.PeekPixels();
        var a = expected.GetPixelSpan();var b = pixels.GetPixelSpan();int difference = 0;
        for (int i = 0; i < a.Length; i++) difference = Math.Max(difference, Math.Abs(a[i] - b[i]));
        return difference;
    }

    private static void Prepare(SKCanvas canvas, int clip)
    {
        canvas.Clear(new(13, 17, 77, 255));
        if (clip == 1) canvas.ClipRect(new(42, 28, 179, 143));
        else if (clip == 2) canvas.ClipRect(new(42.25f, 28.75f, 179.5f, 143.25f), SKClipOperation.Intersect, true);
    }

    private static SceneDocument Scene(CanvasElement element) => new() { Viewport = new(0, 0, 256, 192), Elements = [element] };
    private static CanvasElement Element(ElementKind kind, double opacity) => new()
    {
        Kind = kind, Width = 130, Height = 80, Points = [new(0, 0), new(125, 80), new(12, 90), new(120, 0), new(0, 0)],
        Transform = Affine2D.Translation(57.125, 41.25).Compose(Affine2D.Rotation(17.3)),
        Style = new() { Stroke = 0xEEDF4D2D, Fill = 0, StrokeWidth = 9, Opacity = opacity }
    };
}
