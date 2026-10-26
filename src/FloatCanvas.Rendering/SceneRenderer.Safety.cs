using FloatCanvas.Core;
using SkiaSharp;

namespace FloatCanvas.Rendering;

public sealed partial class SceneRenderer
{
    /// <summary>Draws a transient editing hint in screen coordinates. Ordinary scene rendering and exports never call this.</summary>
    public static void DrawProjectionUnsafeRegion(SKCanvas canvas, SceneDocument document, RectD screenViewport)
    {
        var bounds = screenViewport.Intersect(document.Viewport);
        if (!document.Canvas.Projection.Enabled || bounds.IsEmpty || !Finite(bounds)) return;
        var mapping = CanvasMapping.Create(document);
        if (mapping.IsScreenSafe(new(bounds.Left, bounds.Top)) && mapping.IsScreenSafe(new(bounds.Right, bounds.Top)) &&
            mapping.IsScreenSafe(new(bounds.Right, bounds.Bottom)) && mapping.IsScreenSafe(new(bounds.Left, bounds.Bottom))) return;
        var safe = bounds == document.Viewport ? mapping.SafeScreenPolygon : mapping.Safety.ClipScreen(bounds);
        using var builder = new SKPathBuilder { FillType = SKPathFillType.EvenOdd };
        builder.AddRect(ToRect(bounds));
        if (safe.Count >= 3)
        {
            builder.MoveTo(ToPoint(safe[0]));
            foreach (var point in safe.Skip(1)) builder.LineTo(ToPoint(point));
            builder.Close();
        }
        using var path = builder.Detach();
        using var paint = new SKPaint { Color = new SKColor(220, 72, 72, 24), IsAntialias = true };
        canvas.DrawPath(path, paint);
    }

    private CanvasMapping MappingFor(SceneDocument document) =>
        ReferenceEquals(document, _drawingDocument) && _mapping is not null ? _mapping : CanvasMapping.Create(document);

    private bool ClipSafePlane(SKCanvas canvas, SceneDocument document)
    {
        if (_renderSpace == SceneRenderSpace.PlaneContent) return true;
        if (ReferenceEquals(document, _drawingDocument) && _mapping is not null && !_referenceOpacityAtHorizon) return true;
        var mapping = MappingFor(document);
        var bounds = document.Viewport;
        if (mapping.IsScreenSafe(new(bounds.Left, bounds.Top)) && mapping.IsScreenSafe(new(bounds.Right, bounds.Top)) &&
            mapping.IsScreenSafe(new(bounds.Right, bounds.Bottom)) && mapping.IsScreenSafe(new(bounds.Left, bounds.Bottom))) return true;
        return ClipDepthEvent(canvas, mapping.SafeScreenPolygon);
    }

    // A source path which straddles w=0 must never be submitted whole to Skia's projective
    // path rasterizer: even a device clip cannot repair its unbounded, joined contours.
    // Only that transient calibration case uses a bounded local raster. Its actual draw
    // primitive is the clipped, finite source polygon, whose interior cannot contain a pole.
    private void DrawClippedSource(SKCanvas canvas, CanvasElement element, AssetLibrary assets,
        Projective2D transform, CanvasMapping mapping)
    {
        var bounds = ElementGeometry.StrokedLocalBounds(element);
        if (bounds.IsEmpty || !Finite(bounds)) return;
        IReadOnlyList<PointD> polygon = [new(bounds.Left, bounds.Top), new(bounds.Right, bounds.Top),
            new(bounds.Right, bounds.Bottom), new(bounds.Left, bounds.Bottom)];
        var placement = ElementGeometry.LocalToPlane(element);
        polygon = ClipSource(polygon, p => placement.Denominator(p));
        polygon = ClipSource(polygon, p => transform.Denominator(p));
        var clip = mapping.SafeScreenPolygon;
        if (clip.Count < 3) return;
        double area = 0;
        for (int i = 0; i < clip.Count; i++) area += clip[i].X * clip[(i + 1) % clip.Count].Y - clip[(i + 1) % clip.Count].X * clip[i].Y;
        double sign = area >= 0 ? 1 : -1;
        for (int i = 0; i < clip.Count && polygon.Count >= 3; i++)
        {
            var a = clip[i]; var b = clip[(i + 1) % clip.Count];
            polygon = ClipSource(polygon, p => sign * ((b.X - a.X) *
                (transform.M21 * p.X + transform.M22 * p.Y + transform.M23 - a.Y * transform.Denominator(p)) -
                (b.Y - a.Y) * (transform.M11 * p.X + transform.M12 * p.Y + transform.M13 - a.X * transform.Denominator(p))));
        }
        if (polygon.Count < 3 || polygon.Any(p => !transform.TryTransformPoint(p, out _))) return;
        bounds = PointBounds(polygon);
        if (bounds.IsEmpty || !Finite(bounds)) return;
        double scale = 1;
        foreach (var point in polygon)
        {
            var derivative = transform.Jacobian(point);
            scale = Math.Max(scale, Math.Max(Math.Sqrt(derivative.M11 * derivative.M11 + derivative.M21 * derivative.M21),
                Math.Sqrt(derivative.M12 * derivative.M12 + derivative.M22 * derivative.M22)));
        }
        // Bound both dimension and total temporary work during a live calibration gesture.
        scale = Math.Min(scale, Math.Min(2048 / Math.Max(bounds.Width, bounds.Height), Math.Sqrt(4_194_304 / (bounds.Width * bounds.Height))));
        if (!double.IsFinite(scale) || scale <= 0) return;
        int width = Math.Clamp((int)Math.Ceiling(bounds.Width * scale), 1, 2048);
        int height = Math.Clamp((int)Math.Ceiling(bounds.Height * scale), 1, 2048);
        using var source = CreateIntermediateSurface(canvas, new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul),
            "Cannot allocate the finite perspective preview.");
        source.Canvas.Clear(SKColors.Transparent);
        source.Canvas.Scale((float)(width / bounds.Width), (float)(height / bounds.Height));
        source.Canvas.Translate(-(float)bounds.Left, -(float)bounds.Top);
        DrawLocalElement(source.Canvas, element, assets);
        using var image = source.Snapshot();
        var imageToLocal = ToMatrix(Projective2D.FromAffine(Affine2D.Translation(bounds.Left, bounds.Top)
            .Compose(Affine2D.Scale(bounds.Width / width, bounds.Height / height))));
        using var shader = image.ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal, Linear, imageToLocal);
        using var paint = new SKPaint { IsAntialias = true, Shader = shader };
        using var builder = new SKPathBuilder();
        builder.MoveTo(ToPoint(polygon[0]));
        foreach (var point in polygon.Skip(1)) builder.LineTo(ToPoint(point));
        builder.Close(); using var path = builder.Detach();
        var matrix = ToMatrix(transform); canvas.Concat(in matrix);
        canvas.DrawPath(path, paint);
    }

    private static IReadOnlyList<PointD> ClipSource(IReadOnlyList<PointD> input, Func<PointD, double> side)
    {
        var output = new List<PointD>();
        if (input.Count == 0) return output;
        var previous = input[^1]; double before = side(previous);
        foreach (var current in input)
        {
            double after = side(current);
            if ((before >= 0) != (after >= 0)) output.Add(previous + (current - previous) * (before / (before - after)));
            if (after >= 0) output.Add(current);
            previous = current; before = after;
        }
        return output;
    }
}
