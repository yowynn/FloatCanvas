using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class SymbolTraceTests
{
    internal static void Run(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer();
        SymbolTraceSource FromBitmap(SKBitmap bitmap)
        {
            using var image = SKImage.FromBitmap(bitmap); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            var assets = new AssetLibrary();
            var layer = new CanvasElement { Kind = ElementKind.Image, AssetId = assets.Add(png.ToArray()), Width = bitmap.Width, Height = bitmap.Height };
            var document = new SceneDocument { Viewport = new(0, 0, bitmap.Width, bitmap.Height), Elements = [layer] };
            return renderer.PrepareSymbolExtraction(document, assets);
        }
        bool Contains(SymbolTraceResult? trace, SymbolTraceSource source, double x, double y)
        {
            if (trace is null) return false;
            var e = new CanvasElement
            {
                Kind = ElementKind.Symbol,
                Symbol = SymbolKind.Custom,
                CustomSymbol = trace.Shape,
                Style = new() { Variant = ElementAppearance.ShapeUniform, PrimaryColor = 0xFF000000 }
            };
            SymbolGeometry.PlaceInBox(e, trace.Bounds);
            return ElementGeometry.HitTest(e, new(source.FrameWidth * x, source.FrameHeight * y), 0);
        }

        using var bands = new SKBitmap(160, 60);
        byte[] alphas = [0, 1, 51, 127, 128, 200, 254, 255];
        for (int y = 0; y < bands.Height; y++) for (int x = 0; x < bands.Width; x++)
            bands.SetPixel(x, y, new SKColor(75, 140, 55, alphas[x / 20]));
        var bandSource = FromBitmap(bands);
        foreach (double threshold in new[] { .1, 20, 20.01, 50, 99.6, 100 })
        {
            var trace = bandSource.Trace(threshold);
            check($"threshold {threshold} percent includes exactly the qualifying alpha bands", Enumerable.Range(0, alphas.Length)
                .All(i => Contains(trace, bandSource, (i + .5) / alphas.Length, .5) == (alphas[i] * 100 >= threshold * 255)));
        }

        using var ring = new SKBitmap(200, 160);
        for (int y = 0; y < ring.Height; y++) for (int x = 0; x < ring.Width; x++)
        {
            double dx = x - 79, dy = y - 80, r = Math.Sqrt(dx * dx + dy * dy), a = Math.Atan2(dy, dx);
            bool body = r < 60 + 2.1 * Math.Sin(37 * a) && r > 23;
            bool dot = (x - 172) * (x - 172) + (y - 61) * (y - 61) < 49;
            ring.SetPixel(x, y, body || dot ? new SKColor(230, 125, 90, 254) : SKColors.Transparent);
        }
        var ringSource = FromBitmap(ring);
        var coarse = ringSource.Trace(50, 0)!; var normal = ringSource.Trace()!; var detailed = ringSource.Trace(50, 100)!;
        check("the default traces a nearly opaque raster that is empty at 100 percent", normal.Shape.IsValid() && ringSource.Trace(100) is null);
        check($"detail controls the number of vector vertices ({coarse.Shape.VertexCount}, {normal.Shape.VertexCount}, {detailed.Shape.VertexCount})",
            coarse.Shape.VertexCount < normal.Shape.VertexCount && normal.Shape.VertexCount < detailed.Shape.VertexCount);
        check("smoothed raster edges have native cubic segments", normal.Shape.Contours.SelectMany(c => c).Count(v => v.Control1.HasValue) >= 4 &&
            coarse.Shape.Contours.SelectMany(c => c).Any(v => v.Control1.HasValue));
        check("every detail level preserves a hole, the ring and the detached dot", new[] { coarse, normal, detailed }.All(t =>
            t.Shape.Contours.Length == 3 && !Contains(t, ringSource, 79d / 200, .5) && Contains(t, ringSource, 119d / 200, .5) &&
            Contains(t, ringSource, 172d / 200, 61d / 160) && !Contains(t, ringSource, 151d / 200, 61d / 160)));
        check("returning to the same settings reproduces identical vector data", ringSource.Trace(50, 0)!.Shape.Fingerprint == coarse.Shape.Fingerprint);
        using var rect = new SKBitmap(100, 100); rect.Erase(SKColors.Transparent);
        using (var c = new SKCanvas(rect)) using (var paint = new SKPaint { Color = SKColors.Black }) c.DrawRect(10, 20, 80, 60, paint);
        var rectangle = FromBitmap(rect); var smoothRect = rectangle.Trace(50, 0)!;
        check("straight edges and all four deliberate corners survive smoothing", new[] { (.12, .22), (.88, .22), (.88, .78), (.12, .78) }.All(p => Contains(smoothRect, rectangle, p.Item1, p.Item2)) &&
            !Contains(smoothRect, rectangle, .5, .15) && !Contains(smoothRect, rectangle, .04, .5));
        var exact = renderer.PrepareSymbolExtraction(new SceneDocument
        {
            Elements = [new CanvasElement{Kind=ElementKind.Ellipse,Width=80,Height=40,
            Style=new(){Variant=ElementAppearance.ShapeUniform,PrimaryColor=0xFF123456}}]
        }, new());
        check("native opaque vector geometry stays exact for every setting", !exact.IsRaster && ReferenceEquals(exact.Trace(.1, 0)?.Shape, exact.Trace(100, 100)?.Shape));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); bool cancelled = false;
        try { ringSource.Trace(50, 50, cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
        check("an obsolete trace can be cancelled before publishing", cancelled);
    }
}
