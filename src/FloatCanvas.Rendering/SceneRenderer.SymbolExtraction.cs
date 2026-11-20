using System.Collections.Immutable;
using FloatCanvas.Core;
using SkiaSharp;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Rendering;

public sealed partial class SceneRenderer
{
    public SymbolShape? ExtractSymbol(SceneDocument selection, AssetLibrary assets) =>
        PrepareSymbolExtraction(selection, assets).Trace()?.Shape;

    /// <summary>Freezes the selected artwork once so interactive tracing only needs its alpha coverage.</summary>
    public SymbolTraceSource PrepareSymbolExtraction(SceneDocument selection, AssetLibrary assets)
    {
        var doc = SceneEvaluation.Build(selection).RenderDocument;
        var elements = doc.Elements.Where(e => e.Visible && e.Style.Opacity > 0).ToArray();
        if (elements.Length == 0) return new SymbolTraceSource(null);
        if (TryVectorSymbol(doc, elements, out var vector))
        {
            using (vector)
            {
                if (vector is null) return new SymbolTraceSource(null);
                try { return new SymbolTraceSource(ReadSymbolPath(vector)); }
                catch (InvalidDataException) { /* Trace oversized vector paths from coverage so detail can still be reduced. */ }
            }
        }

        var bounds = elements.Select(e => ProjectedBounds(doc, e, true)).Aggregate(RectD.Union);
        double extent = Math.Max(bounds.Width, bounds.Height);
        if (!double.IsFinite(extent) || extent <= 0) return new SymbolTraceSource(null);
        // Sample ordinary 4K images at native resolution. Odd integer supersampling also
        // places an original pixel center on a sample when magnifying smaller assets.
        double limit = Math.Min(4096 / extent, Math.Sqrt(9_000_000 / Math.Max(1, bounds.Width * bounds.Height)));
        double scale = limit >= 3 ? 3 : Math.Min(1, limit);
        var view = bounds.Inflate(2 / scale);
        doc = SceneEvaluation.WithViewport(doc, view, doc.Canvas.ResolveReference(doc.Viewport));
        using var bitmap = new SKBitmap(new SKImageInfo((int)Math.Ceiling(view.Width * scale), (int)Math.Ceiling(view.Height * scale),
            SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale((float)scale); canvas.Translate((float)-view.X, (float)-view.Y);
            Draw(canvas, doc, assets, includeBackground: false, includeCanvasBackground: false, includeGrid: false);
        }
        var pixels = bitmap.GetPixelSpan();
        // One transparent sample around the bitmap closes contours even at clipped image edges.
        int stride = bitmap.Width + 2;
        var alpha = new byte[checked(stride * (bitmap.Height + 2))];
        for (int y = 0; y < bitmap.Height; y++)
        {
            var row = pixels.Slice(y * bitmap.RowBytes, bitmap.Width * 4);
            var output = alpha.AsSpan((y + 1) * stride + 1, bitmap.Width);
            for (int x = 0; x < output.Length; x++) output[x] = row[x * 4 + 3];
        }
        return new SymbolTraceSource(alpha, stride, bitmap.Height + 2);
    }

    private bool TryVectorSymbol(SceneDocument doc, CanvasElement[] elements, out SKPath? union)
    {
        union = null;
        if (doc.LayerOpacity != 1 || elements.Length > 256 || elements.Any(e => e.Style.Opacity != 1)) return false;
        SKPath? result = null;
        try
        {
            foreach (var e in elements)
            {
                if (!TryGetOpaqueSymbolInk(e, out var ink)) return false;
                using var local = ink;
                if (local is null || local.IsEmpty) continue;
                var mapping = ElementMapping(doc, e);
                if (!mapping.IsElementSafe(e)) return false;
                using var transformed = new SKPathBuilder();
                transformed.AddPath(local, ToMatrix(mapping.ElementToScreen(e)));
                transformed.FillType = local.FillType;
                using var path = transformed.Detach();
                var merged = result is null ? path.Simplify() : result.Op(path, SKPathOp.Union);
                if (merged is null) return false;
                result?.Dispose(); result = merged;
            }
            union = result; result = null; return true;
        }
        finally { result?.Dispose(); }
    }

    private bool TryGetOpaqueSymbolInk(CanvasElement e, out SKPath? ink)
    {
        ink = null;
        if (ElementGeometry.IsBoxShape(e.Kind))
        {
            uint fill = ElementAppearance.ShapeFill(e.Style) >> 24;
            uint stroke = ElementAppearance.HasShapeStroke(e.Style) && e.Style.StrokeWidth > 0 ? e.Style.PrimaryColor >> 24 : fill;
            if (fill == 0 && stroke == 0) return true;
            if (fill is not (0 or 255) || stroke is not (0 or 255)) return false;
            var shape = GetPath(e);
            if (stroke == fill) ink = new(shape.Path);
            else if (shape.DirectInsideBand() is { } band)
                ink = stroke == 255 ? new SKPath(band) : shape.Path.Op(band, SKPathOp.Difference);
            return ink is not null;
        }
        if (e.Kind != ElementKind.Freehand && !ElementGeometry.IsLine(e.Kind)) return false;
        if (e.Style.StrokeWidth <= 0 || e.Style.PrimaryColor >> 24 == 0 || e.Points.Count == 0) return true;
        if (e.Style.PrimaryColor >> 24 != 255 || e.Style.StrokePattern != StrokePattern.Solid) return false;
        if (e.Kind == ElementKind.Freehand && e.Points.Count == 1)
        {
            var p = e.Points[0]; using var dot = new SKPathBuilder();
            dot.AddCircle((float)p.X, (float)p.Y, (float)e.Style.StrokeWidth / 2); ink = dot.Detach();
        }
        else
        {
            var path = GetPath(e); ink = new(path.HasArrowHeads ? path.ArrowInk : path.SolidStroke);
        }
        return true;
    }

    private static InvalidDataException TooComplex() => new(L.Get("Symbol.ExtractComplex"));

    internal static SymbolShape? ReadSymbolPath(SKPath path)
    {
        var box = path.TightBounds;
        if (box.Width <= 0 || box.Height <= 0) return null;
        PointD Normalize(SKPoint p) => new(Math.Round((p.X - box.Left) / (double)box.Width, 7), Math.Round((p.Y - box.Top) / (double)box.Height, 7));
        var contours = ImmutableArray.CreateBuilder<ImmutableArray<SymbolVertex>>();
        var vertices = new List<SymbolVertex>();
        int total = 0;
        void Line(SKPoint p) { var n = Normalize(p); vertices.Add(new(n.X, n.Y)); }
        void Quad(SKPoint start, SKPoint control, SKPoint end)
        {
            var a = Normalize(start); var b = Normalize(control); var c = Normalize(end);
            vertices.Add(new(c.X, c.Y, a + (b - a) * (2d / 3), c + (b - c) * (2d / 3)));
        }
        void Finish()
        {
            if (vertices.Count > 1 && vertices[^1].Control1 is null && vertices[^1].Point == vertices[0].Point) vertices.RemoveAt(vertices.Count - 1);
            if (vertices.Count >= 2)
            {
                var finished = vertices.ToImmutableArray();
                total += finished.Length;
                if (total > SymbolShape.MaximumVertices) throw TooComplex();
                if (finished.Length >= 2) contours.Add(finished);
            }
            vertices.Clear();
        }
        using var iterator = path.CreateRawIterator();
        var pts = new SKPoint[4];
        SKPathVerb verb;
        while ((verb = iterator.Next(pts)) != SKPathVerb.Done)
        {
            if (vertices.Count > SymbolShape.MaximumVertices * 4) throw TooComplex();
            switch (verb)
            {
                case SKPathVerb.Move: Finish(); Line(pts[0]); break;
                case SKPathVerb.Line: Line(pts[1]); break;
                case SKPathVerb.Quad: Quad(pts[0], pts[1], pts[2]); break;
                case SKPathVerb.Conic:
                    var quads = SKPath.ConvertConicToQuads(pts[0], pts[1], pts[2], iterator.ConicWeight(), 3);
                    for (int i = 0; i + 2 < quads.Length; i += 2) Quad(quads[i], quads[i + 1], quads[i + 2]);
                    break;
                case SKPathVerb.Cubic:
                    var end = Normalize(pts[3]); vertices.Add(new(end.X, end.Y, Normalize(pts[1]), Normalize(pts[2]))); break;
                case SKPathVerb.Close: Finish(); break;
            }
        }
        Finish();
        if (contours.Count == 0) return null;
        var result = new SymbolShape { AspectRatio = box.Width / box.Height, Contours = contours.ToImmutable() };
        if (!result.IsValid()) throw TooComplex();
        return result;
    }
}
