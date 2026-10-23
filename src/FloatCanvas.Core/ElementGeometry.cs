namespace FloatCanvas.Core;

public static class ElementGeometry
{
    public static Projective2D LocalToPlane(CanvasElement element) => element.IsBillboard
        ? BillboardGeometry.LocalPlacement(element, element.BillboardAnchor, 1)
        : Projective2D.FromAffine(element.Transform).Compose(element.ProjectivePlacement ?? Projective2D.Identity);
    public static RectD LocalBounds(CanvasElement element)
    {
        if (UsesPoints(element.Kind) && element.Points.Count > 0)
        {
            double x = element.Points.Min(p => p.X), y = element.Points.Min(p => p.Y);
            return new(x, y, element.Points.Max(p => p.X) - x, element.Points.Max(p => p.Y) - y);
        }
        return new(0, 0, Math.Max(0, element.Width), Math.Max(0, element.Height));
    }
    public static RectD Bounds(CanvasElement element, bool includeStroke = true)
        => LocalToPlane(element).TryTransformBounds(StrokedLocalBounds(element, includeStroke), out var bounds) ? bounds : default;
    public static RectD StrokedLocalBounds(CanvasElement element, bool includeStroke = true)
    {
        var bounds = LocalBounds(element);
        double stroke = includeStroke && element.Kind is not (ElementKind.Image or ElementKind.Text or ElementKind.Number) && !IsLiveEffect(element.Kind) ? element.Style.StrokeWidth / 2 : 0;
        if (IsLine(element.Kind))
        {
            foreach (var point in GetArrowHeads(element).SelectMany(head => head))
                bounds = RectD.Union(bounds, new(point.X, point.Y, 0, 0));
        }
        return bounds.Inflate(stroke);
    }
    public static bool UsesPoints(ElementKind kind) => kind is ElementKind.Freehand or ElementKind.Highlighter or ElementKind.Line or ElementKind.Arrow or ElementKind.DoubleArrow;
    public static bool IsLine(ElementKind kind) => kind is ElementKind.Line or ElementKind.Arrow or ElementKind.DoubleArrow;
    public static bool IsLiveEffect(ElementKind kind) => kind is ElementKind.LivePixelate or ElementKind.LiveBlur;
    public static bool IsBoxShape(ElementKind kind) => kind is ElementKind.Rectangle or ElementKind.RoundedRectangle or ElementKind.Ellipse or ElementKind.Triangle or ElementKind.Symbol;
    public static bool PreservesAspect(CanvasElement element) => element.ConstrainAspect ??
        (!IsBoxShape(element.Kind) && element.Kind != ElementKind.Text);

    /// <summary>Resize vector dimensions around their center, retaining circular corners and uniform strokes.</summary>
    public static void ResizeShapeGeometry(CanvasElement element, double scaleX, double scaleY)
    {
        if (!IsBoxShape(element.Kind)) throw new ArgumentException(nameof(element));
        if (!double.IsFinite(scaleX) || !double.IsFinite(scaleY) || scaleX <= 0 || scaleY <= 0) throw new ArgumentOutOfRangeException(nameof(scaleX));
        if (element.ProjectivePlacement is not null)
        {
            element.Transform = Affine2D.Around(Bounds(element, false).Center, Affine2D.Scale(scaleX, scaleY)).Compose(element.Transform);
            return;
        }
        var transform = element.Transform;
        double basisX = Math.Sqrt(transform.M11 * transform.M11 + transform.M21 * transform.M21);
        double basisY = Math.Sqrt(transform.M12 * transform.M12 + transform.M22 * transform.M22);
        var center = transform.TransformPoint(new(element.Width / 2, element.Height / 2));
        element.Width *= scaleX * basisX; element.Height *= scaleY * basisY;
        if (element.CornerRadius is { } radius) element.CornerRadius = radius * Math.Min(basisX, basisY);
        var unit = new Affine2D(transform.M11 / basisX, transform.M12 / basisY, transform.M21 / basisX, transform.M22 / basisY, 0, 0);
        var half = unit.TransformPoint(new(element.Width / 2, element.Height / 2));
        element.Transform = unit with { OffsetX = center.X - half.X, OffsetY = center.Y - half.Y };
    }
    public static double CornerRadius(CanvasElement element) => Math.Clamp(element.CornerRadius ??
        (element.Kind == ElementKind.RoundedRectangle ? Math.Min(element.Width, element.Height) * .2 : 0), 0, Math.Min(element.Width, element.Height) / 2);
    public static bool HitTest(CanvasElement element, PointD worldPoint, double tolerance = 4)
        => HitTestProjected(element, worldPoint, Projective2D.Identity, tolerance);
    /// <summary>Tests fill, stroke and tolerance in the target coordinate space, preserving local stroke widths under perspective.</summary>
    public static bool HitTestProjected(CanvasElement element, PointD worldPoint, Projective2D planeToTarget, double tolerance = 4)
    {
        var transform = BillboardGeometry.ElementToTarget(element, planeToTarget);
        if (!element.Visible || !transform.TryInverse(out var inverse) || !inverse.TryTransformPoint(worldPoint, out var local)) return false;
        if (transform.Denominator(local) <= 0) return false;
        if (transform.TryTransformBounds(StrokedLocalBounds(element), out var projectedBounds) && !projectedBounds.Inflate(tolerance).Contains(worldPoint)) return false;
        double strokeRadius = Math.Max(0, element.Style.StrokeWidth / 2);
        if (UsesPoints(element.Kind))
        {
            if (element.Style.StrokeWidth <= 0) return false;
            if (element.Points.Count == 1) return StrokeSegmentHit(transform, worldPoint, local, element.Points[0], element.Points[0], strokeRadius, tolerance);
            var shaft = IsLine(element.Kind) ? LineShaftPoints(element) : element.Points;
            if (StrokePathHit(element, transform, shaft, false, worldPoint, local, strokeRadius, tolerance)) return true;
            if (IsLine(element.Kind) && element.Points.Count >= 2)
            {
                foreach (var head in GetStyledArrowHeads(element))
                    if (HitArrowHead(transform, head.Points, head.Style, worldPoint, local, strokeRadius, Math.Max(0, tolerance))) return true;
            }
            return false;
        }
        var bounds = LocalBounds(element);
        if (element.Kind is ElementKind.Image or ElementKind.Text || IsLiveEffect(element.Kind))
        {
            if (bounds.Contains(local)) return true;
            PointD[] corners = [new(bounds.Left, bounds.Top), new(bounds.Right, bounds.Top), new(bounds.Right, bounds.Bottom), new(bounds.Left, bounds.Bottom)];
            return ProjectedContourHit(transform, corners, worldPoint, tolerance);
        }
        var outline = Outline(element);
        if (element.Kind == ElementKind.Number)
        {
            if (PointInPolygon(local, outline)) return true;
            return ProjectedContourHit(transform, outline, worldPoint, tolerance);
        }
        if (((element.Style.LinkColors ? element.Style.Stroke : element.Style.Fill) >> 24) != 0 && (PointInPolygon(local, outline) || ProjectedContourHit(transform, outline, worldPoint, tolerance))) return true;
        return element.Style.StrokeWidth > 0 && StrokePathHit(element, transform, outline, true, worldPoint, local, strokeRadius, tolerance);
    }
    public static IReadOnlyList<PointD> Outline(CanvasElement element)
    {
        double w = element.Width, h = element.Height;
        if (element.Kind == ElementKind.Symbol) return SymbolGeometry.Outline(element.Symbol, w, h);
        if (element.Kind == ElementKind.Triangle) return [new(w / 2, 0), new(w, h), new(0, h)];
        if (element.Kind is ElementKind.Ellipse or ElementKind.Number)
        {
            int steps = CurveQuarterSteps(Math.Max(w, h) / 2) * 4;
            return Enumerable.Range(0, steps).Select(i => new PointD(w / 2 + Math.Cos(i * Math.Tau / steps) * w / 2,
                h / 2 + Math.Sin(i * Math.Tau / steps) * h / 2)).ToArray();
        }
        if (element.Kind is ElementKind.Rectangle or ElementKind.RoundedRectangle && CornerRadius(element) > 0)
        {
            double radius = CornerRadius(element);
            int steps = CurveQuarterSteps(radius);
            PointD[] centers = [new(w - radius, radius), new(w - radius, h - radius), new(radius, h - radius), new(radius, radius)];
            return Enumerable.Range(0, 4).SelectMany(corner => Enumerable.Range(0, steps + 1).Select(step =>
            {
                double angle = (-90 + corner * 90 + step * 90.0 / steps) * Math.PI / 180;
                return new PointD(centers[corner].X + Math.Cos(angle) * radius, centers[corner].Y + Math.Sin(angle) * radius);
            })).ToArray();
        }
        return [new(0, 0), new(w, 0), new(w, h), new(0, h)];
    }
    // The renderer uses this same ordered contour for patterned strokes. Keeping its
    // starting point and segment lengths identical also keeps every dash and dot hittable.
    // Limit curve-to-chord deviation to .05 local pixels without unbounded point counts.
    private static int CurveQuarterSteps(double radius) => Math.Clamp((int)Math.Ceiling(Math.PI / 2 /
        (2 * Math.Acos(1 - Math.Min(1, .05 / Math.Max(.05, radius))))), 8, 1024);
    private static bool HitArrowHead(Projective2D transform, IReadOnlyList<PointD> localPoints, ArrowHeadStyle style, PointD worldPoint,
        PointD localPoint, double strokeRadius, double tolerance)
    {
        if (style == ArrowHeadStyle.Open)
        {
            for (var i = 1; i < localPoints.Count; i++)
                if (StrokeSegmentHit(transform, worldPoint, localPoint, localPoints[i - 1], localPoints[i], strokeRadius, tolerance)) return true;
            return false;
        }
        return PointInPolygon(localPoint, localPoints) || ProjectedContourHit(transform, localPoints, worldPoint, tolerance);
    }
    private static bool ProjectedContourHit(Projective2D transform, IReadOnlyList<PointD> points, PointD target, double tolerance)
    {
        if (tolerance <= 0) return false;
        for (int i = 0; i < points.Count; i++)
        {
            var first = points[i]; var last = points[(i + 1) % points.Count];
            if (transform.Denominator(first) > 0 && transform.Denominator(last) > 0 &&
                transform.TryTransformPoint(first, out var a) && transform.TryTransformPoint(last, out var b) &&
                DistanceToSegment(target, a, b) <= tolerance) return true;
        }
        return false;
    }
    private static bool StrokePathHit(CanvasElement element, Projective2D transform, IReadOnlyList<PointD> points, bool closed,
        PointD worldPoint, PointD localPoint, double radius, double tolerance)
    {
        double offset = 0, width = Math.Max(.1, element.Style.StrokeWidth);
        double ink = element.Style.StrokePattern == StrokePattern.Dotted ? width * .001 : width * 4;
        double gap = element.Style.StrokePattern == StrokePattern.Dotted ? width * 2.5 : width * 3;
        int segments = closed ? points.Count : points.Count - 1;
        for (int i = 0; i < segments; i++)
        {
            var a = points[i]; var b = points[(i + 1) % points.Count]; double length = a.DistanceTo(b);
            if (length < 1e-12)
            {
                // Adjacent arcs can share a tangent point when a straight side collapses.
                // A zero-length segment must not turn a patterned gap into a solid dot.
                if ((element.Style.StrokePattern == StrokePattern.Solid || offset % (ink + gap) <= ink) &&
                    StrokeSegmentHit(transform, worldPoint, localPoint, a, b, radius, tolerance)) return true;
            }
            else if (element.Style.StrokePattern == StrokePattern.Solid)
            {
                if (StrokeSegmentHit(transform, worldPoint, localPoint, a, b, radius, tolerance)) return true;
            }
            else
            {
                double cycle = ink + gap;
                for (double start = -offset % cycle; start < length; start += cycle)
                {
                    double first = Math.Max(0, start), last = Math.Min(length, start + ink);
                    if (last < first) continue;
                    var direction = (b - a) * (1 / length);
                    if (StrokeSegmentHit(transform, worldPoint, localPoint, a + direction * first, a + direction * last, radius, tolerance)) return true;
                }
            }
            offset += length;
        }
        return false;
    }
    // Test the actual transformed stroke, rather than inflating by maximum matrix scale.
    // Maximum-scale inflation would select far outside a thin stroke after nonuniform scaling.
    private static bool StrokeSegmentHit(Projective2D transform, PointD worldPoint, PointD localPoint, PointD a, PointD b, double radius, double tolerance)
    {
        if (DistanceToSegment(localPoint, a, b) <= radius) return true;
        if (tolerance <= 0) return false;
        if (radius <= 0) return transform.Denominator(a) > 0 && transform.Denominator(b) > 0 && transform.TryTransformPoint(a, out var startPoint) &&
            transform.TryTransformPoint(b, out var endPoint) && DistanceToSegment(worldPoint, startPoint, endPoint) <= tolerance;
        double direction = Math.Atan2(b.Y - a.Y, b.X - a.X);
        // A capsule in local space becomes a segment with elliptical caps under an affine matrix.
        PointD? previous = null, first = null;
        for (int cap = 0; cap < 2; cap++)
        {
            var center = cap == 0 ? b : a;
            for (int step = 0; step <= 16; step++)
            {
                double angle = direction - Math.PI / 2 + cap * Math.PI + step * Math.PI / 16;
                var localCap = center + new PointD(Math.Cos(angle), Math.Sin(angle)) * radius;
                if (transform.Denominator(localCap) <= 0 || !transform.TryTransformPoint(localCap, out var point)) { previous = null; continue; }
                first ??= point;
                if (previous is { } p && DistanceToSegment(worldPoint, p, point) <= tolerance) return true;
                previous = point;
            }
        }
        return previous is { } last && first is { } start && DistanceToSegment(worldPoint, last, start) <= tolerance;
    }
    public static IReadOnlyList<PointD> ArrowHead(CanvasElement element, bool reverse = false)
    {
        if (element.Points.Count < 2) return [];
        var style = ArrowStyle(element, reverse);
        if (style == ArrowHeadStyle.None) return [];
        PointD tip = reverse ? element.Points[0] : element.Points[^1], previous = reverse ? element.Points[1] : element.Points[^2];
        double length = tip.DistanceTo(previous);
        if (length < .001) return style == ArrowHeadStyle.Diamond ? [tip, tip, tip, tip] : [tip, tip, tip];
        var direction = (tip - previous) * (1 / length);
        double size = ArrowHeadLength(element);
        var basePoint = tip - direction * size;
        var normal = new PointD(-direction.Y, direction.X) * (size * .45);
        return style switch
        {
            ArrowHeadStyle.Open => [basePoint + normal, tip, basePoint - normal],
            ArrowHeadStyle.Diamond => [tip, tip - direction * (size / 2) + normal, basePoint, tip - direction * (size / 2) - normal],
            _ => [tip, basePoint + normal, basePoint - normal]
        };
    }
    private static double ArrowHeadLength(CanvasElement element) => element.Style.ArrowHeadSize > 0 ? element.Style.ArrowHeadSize : Math.Max(10, element.Style.StrokeWidth * 4);

    /// <summary>Filled heads replace the end of the shaft, rather than allowing its round cap to poke through the tip.</summary>
    public static IReadOnlyList<PointD> LineShaftPoints(CanvasElement element)
    {
        if (!IsLine(element.Kind) || element.Points.Count < 2) return element.Points;
        var points = element.Points.ToList();
        foreach (bool reverse in new[] { true, false })
        {
            if (ArrowStyle(element, reverse) is not (ArrowHeadStyle.FilledTriangle or ArrowHeadStyle.Diamond)) continue;
            double remaining = Math.Max(ArrowHeadLength(element), element.Style.StrokeWidth / 2);
            if (!reverse) points.Reverse();
            while (remaining > 0 && points.Count >= 2)
            {
                var first = points[0]; var next = points[1]; double length = first.DistanceTo(next);
                if (length <= remaining) { remaining -= length; points.RemoveAt(0); }
                else { points[0] = first + (next - first) * (remaining / length); remaining = 0; }
            }
            if (!reverse) points.Reverse();
            if (points.Count < 2) return [];
        }
        return points;
    }
    /// <summary>Ordered local head vertices. Open heads contain two V edges; other heads are closed filled polygons.</summary>
    public static IReadOnlyList<IReadOnlyList<PointD>> GetArrowHeads(CanvasElement element)
    {
        return GetStyledArrowHeads(element).Select(h => h.Points).ToArray();
    }
    public static ArrowHeadStyle ArrowStyle(CanvasElement element, bool reverse = false) => reverse
        ? element.Style.StartArrowHead ?? (element.Kind == ElementKind.DoubleArrow ? element.Style.ArrowHeadStyle : ArrowHeadStyle.None)
        : element.Style.EndArrowHead ?? (element.Kind is ElementKind.Arrow or ElementKind.DoubleArrow ? element.Style.ArrowHeadStyle : ArrowHeadStyle.None);
    public static IReadOnlyList<ArrowHeadGeometry> GetStyledArrowHeads(CanvasElement element)
    {
        if (!IsLine(element.Kind) || element.Points.Count < 2) return [];
        var result = new List<ArrowHeadGeometry>();
        foreach (bool reverse in new[] { false, true })
            if (ArrowStyle(element, reverse) is var style && style != ArrowHeadStyle.None)
                result.Add(new(style, ArrowHead(element, reverse)));
        return result;
    }
    public static double DistanceToSegment(PointD p, PointD a, PointD b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, lengthSquared = dx * dx + dy * dy;
        if (lengthSquared < 1e-12) return p.DistanceTo(a);
        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1);
        return p.DistanceTo(new(a.X + dx * t, a.Y + dy * t));
    }
    public static bool PointInPolygon(PointD p, IReadOnlyList<PointD> polygon)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i]; var b = polygon[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }
}

public sealed record ArrowHeadGeometry(ArrowHeadStyle Style, IReadOnlyList<PointD> Points);
