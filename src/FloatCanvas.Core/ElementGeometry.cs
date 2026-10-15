namespace FloatCanvas.Core;

public static class ElementGeometry
{
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
    {
        var bounds = LocalBounds(element);
        double stroke = includeStroke && element.Kind is not (ElementKind.Image or ElementKind.Text) ? element.Style.StrokeWidth / 2 : 0;
        if (element.Kind is ElementKind.Arrow or ElementKind.DoubleArrow)
        {
            foreach (var point in GetArrowHeads(element).SelectMany(head => head))
                bounds = RectD.Union(bounds, new(point.X, point.Y, 0, 0));
        }
        return element.Transform.TransformBounds(bounds.Inflate(stroke));
    }
    public static bool UsesPoints(ElementKind kind) => kind is ElementKind.Freehand or ElementKind.Highlighter or ElementKind.Line or ElementKind.Arrow or ElementKind.DoubleArrow;
    public static bool HitTest(CanvasElement element, PointD worldPoint, double tolerance = 4)
    {
        if (!element.Visible || !element.Transform.TryInverse(out var inverse)) return false;
        if (!Bounds(element).Inflate(tolerance).Contains(worldPoint)) return false;
        PointD local = inverse.TransformPoint(worldPoint);
        double strokeRadius = Math.Max(0, element.Style.StrokeWidth / 2);
        if (UsesPoints(element.Kind))
        {
            if (element.Style.StrokeWidth <= 0) return false;
            if (element.Points.Count == 1) return StrokeSegmentHit(element.Transform, worldPoint, local, element.Points[0], element.Points[0], strokeRadius, tolerance);
            for (int i = 1; i < element.Points.Count; i++)
                if (StrokeSegmentHit(element.Transform, worldPoint, local, element.Points[i - 1], element.Points[i], strokeRadius, tolerance)) return true;
            if (element.Kind is ElementKind.Arrow or ElementKind.DoubleArrow && element.Points.Count >= 2)
            {
                foreach (var head in GetArrowHeads(element))
                    if (HitArrowHead(element, head, worldPoint, local, strokeRadius, Math.Max(0, tolerance))) return true;
            }
            return false;
        }
        var bounds = LocalBounds(element);
        if (element.Kind is ElementKind.Image or ElementKind.Text)
        {
            if (bounds.Contains(local)) return true;
            var corners = new PointD[] { new(bounds.Left, bounds.Top), new(bounds.Right, bounds.Top), new(bounds.Right, bounds.Bottom), new(bounds.Left, bounds.Bottom) }.Select(element.Transform.TransformPoint).ToArray();
            return Enumerable.Range(0, 4).Any(i => DistanceToSegment(worldPoint, corners[i], corners[(i + 1) % 4]) <= tolerance);
        }
        var outline = Outline(element);
        if ((element.Kind == ElementKind.Number || (element.Style.Fill >> 24) != 0) && PointInPolygon(local, outline)) return true;
        for (int i = 0; i < outline.Count; i++)
            if (StrokeSegmentHit(element.Transform, worldPoint, local, outline[i], outline[(i + 1) % outline.Count], strokeRadius, tolerance)) return true;
        return false;
    }
    public static IReadOnlyList<PointD> Outline(CanvasElement element)
    {
        double w = element.Width, h = element.Height;
        if (element.Kind == ElementKind.Triangle) return [new(w / 2, 0), new(w, h), new(0, h)];
        if (element.Kind is ElementKind.Ellipse or ElementKind.Number)
            return Enumerable.Range(0, 64).Select(i => new PointD(w / 2 + Math.Cos(i * Math.PI / 32) * w / 2, h / 2 + Math.Sin(i * Math.PI / 32) * h / 2)).ToArray();
        if (element.Kind == ElementKind.RoundedRectangle)
        {
            double radius = Math.Min(w, h) * .2;
            PointD[] centers = [new(w - radius, radius), new(w - radius, h - radius), new(radius, h - radius), new(radius, radius)];
            return Enumerable.Range(0, 4).SelectMany(corner => Enumerable.Range(0, 9).Select(step =>
            {
                double angle = (-90 + corner * 90 + step * 90.0 / 8) * Math.PI / 180;
                return new PointD(centers[corner].X + Math.Cos(angle) * radius, centers[corner].Y + Math.Sin(angle) * radius);
            })).ToArray();
        }
        return [new(0, 0), new(w, 0), new(w, h), new(0, h)];
    }
    private static bool HitArrowHead(CanvasElement element, IReadOnlyList<PointD> localPoints, PointD worldPoint,
        PointD localPoint, double strokeRadius, double tolerance)
    {
        if (element.Style.ArrowHeadStyle == ArrowHeadStyle.Open)
        {
            for (var i = 1; i < localPoints.Count; i++)
                if (StrokeSegmentHit(element.Transform, worldPoint, localPoint, localPoints[i - 1], localPoints[i], strokeRadius, tolerance)) return true;
            return false;
        }
        var points = localPoints.Select(element.Transform.TransformPoint).ToArray();
        return PointInPolygon(worldPoint, points) || Enumerable.Range(0, points.Length)
            .Any(i => DistanceToSegment(worldPoint, points[i], points[(i + 1) % points.Length]) <= tolerance);
    }
    // Test the actual transformed stroke, rather than inflating by maximum matrix scale.
    // Maximum-scale inflation would select far outside a thin stroke after nonuniform scaling.
    private static bool StrokeSegmentHit(Affine2D transform, PointD worldPoint, PointD localPoint, PointD a, PointD b, double radius, double tolerance)
    {
        if (DistanceToSegment(localPoint, a, b) <= radius) return true;
        if (tolerance <= 0) return false;
        if (radius <= 0) return DistanceToSegment(worldPoint, transform.TransformPoint(a), transform.TransformPoint(b)) <= tolerance;
        double direction = Math.Atan2(b.Y - a.Y, b.X - a.X);
        // A capsule in local space becomes a segment with elliptical caps under an affine matrix.
        PointD? previous = null, first = null;
        for (int cap = 0; cap < 2; cap++)
        {
            var center = cap == 0 ? b : a;
            for (int step = 0; step <= 16; step++)
            {
                double angle = direction - Math.PI / 2 + cap * Math.PI + step * Math.PI / 16;
                var point = transform.TransformPoint(center + new PointD(Math.Cos(angle), Math.Sin(angle)) * radius);
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
        PointD tip = reverse ? element.Points[0] : element.Points[^1], previous = reverse ? element.Points[1] : element.Points[^2];
        double length = tip.DistanceTo(previous);
        if (length < .001) return element.Style.ArrowHeadStyle == ArrowHeadStyle.Diamond ? [tip, tip, tip, tip] : [tip, tip, tip];
        var direction = (tip - previous) * (1 / length);
        double size = element.Style.ArrowHeadSize > 0 ? element.Style.ArrowHeadSize : Math.Max(10, element.Style.StrokeWidth * 4);
        var basePoint = tip - direction * size;
        var normal = new PointD(-direction.Y, direction.X) * (size * .45);
        return element.Style.ArrowHeadStyle switch
        {
            ArrowHeadStyle.Open => [basePoint + normal, tip, basePoint - normal],
            ArrowHeadStyle.Diamond => [tip, tip - direction * (size / 2) + normal, basePoint, tip - direction * (size / 2) - normal],
            _ => [tip, basePoint + normal, basePoint - normal]
        };
    }
    /// <summary>Ordered local head vertices. Open heads contain two V edges; other heads are closed filled polygons.</summary>
    public static IReadOnlyList<IReadOnlyList<PointD>> GetArrowHeads(CanvasElement element)
    {
        if (element.Kind is not (ElementKind.Arrow or ElementKind.DoubleArrow) || element.Points.Count < 2) return [];
        return element.Kind == ElementKind.DoubleArrow ? [ArrowHead(element), ArrowHead(element, true)] : [ArrowHead(element)];
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
