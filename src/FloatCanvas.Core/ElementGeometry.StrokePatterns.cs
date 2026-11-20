using System.Runtime.CompilerServices;

namespace FloatCanvas.Core;

public static partial class ElementGeometry
{
    private static readonly ConditionalWeakTable<CanvasElement, PatternCache> PatternCaches = new();
    private const double ClosedDotSpacingInRadii = 5;

    /// <summary>Flat dash masks and complete circles, measured along the middle of the solid stroke band.</summary>
    public static StrokePatternGeometry StrokePatternGeometry(CanvasElement element)
    {
        var cache = PatternCaches.GetValue(element, _ => new());
        lock (cache)
        {
            if (!cache.Matches(element)) cache.Update(element);
            return cache.Geometry!;
        }
    }

    private sealed class PatternCache
    {
        private CanvasElement? _snapshot;
        public StrokePatternGeometry? Geometry { get; private set; }
        public bool Matches(CanvasElement e) => _snapshot is { } s && s.Kind == e.Kind && s.Width == e.Width && s.Height == e.Height &&
            s.CornerRadius == e.CornerRadius && s.Symbol == e.Symbol && s.CustomSymbol == e.CustomSymbol && s.Style.StrokeWidth == e.Style.StrokeWidth &&
            s.Style.StrokePattern == e.Style.StrokePattern && s.Style.ArrowHeadSize == e.Style.ArrowHeadSize &&
            s.Style.ArrowHeadStyle == e.Style.ArrowHeadStyle && s.Style.StartArrowHead == e.Style.StartArrowHead &&
            s.Style.EndArrowHead == e.Style.EndArrowHead && s.Style.LineArcBulge == e.Style.LineArcBulge &&
            s.Style.LineArcOffset == e.Style.LineArcOffset && e.Points.ContentEquals(s.Points);
        public void Update(CanvasElement e) { Geometry = BuildPattern(e); _snapshot = e.Clone(); }
    }

    private static StrokePatternGeometry BuildPattern(CanvasElement element)
    {
        if (element.Kind != ElementKind.Symbol) return BuildPattern(element, null, false);
        var contours = SymbolContours(element);
        if (contours.Count == 1) return BuildPattern(element, contours[0], false);
        var shape = SymbolGeometry.Shape(element.Symbol, element.CustomSymbol);
        var parts = contours.Select((c, i) => BuildPattern(element, c, shape.IsHole(i))).ToArray();
        return new([], parts.SelectMany(p => p.DashPolygons).ToArray(), parts.SelectMany(p => p.Dots).ToArray(), parts[0].Radius, true);
    }

    private static StrokePatternGeometry BuildPattern(CanvasElement element, IReadOnlyList<PointD>? contour, bool hole)
    {
        double width = Math.Max(.1, element.Style.StrokeWidth), radius = width / 2;
        if (element.Kind == ElementKind.Ellipse && element.Style.StrokePattern == StrokePattern.Dotted)
            return DottedEllipse(element.Width / 2, element.Height / 2, radius);
        bool closed = IsBoxShape(element.Kind);
        bool roundJoins = IsLine(element.Kind);
        if (roundJoins && element.Style.StrokePattern is StrokePattern.Dashed or StrokePattern.DashDot)
            radius = element.Style.StrokeWidth / 2;
        bool rectangle = element.Kind is ElementKind.Rectangle or ElementKind.RoundedRectangle;
        var outer = closed && !rectangle ? CleanContour(contour ?? Outline(element)) : [];
        var center = rectangle ? InsetRectangle(element, radius) :
            closed ? InsetContour(outer, hole ? -radius : radius) : CleanContour(IsLine(element.Kind) ? LineShaftPoints(element) : element.Points, false);
        // For a closed shape the solid inner band supplies the width. Extend the
        // dash cuts across it so its outer antialias edge is not masked twice.
        double cutRadius = closed ? width : radius;
        var polygons = new List<IReadOnlyList<PointD>>(); var dots = new List<PointD>();
        if (element.Style.StrokePattern == StrokePattern.Dotted)
        {
            if (rectangle) return new(center, [], RectangleDots(element, center, radius), radius, true);
            foreach (var station in DotStations(center, closed, closed ? ClosedDotSpacingInRadii * radius : 2 * width))
                if (!closed) dots.Add(station);
                else if (FitDot(outer, station, radius, hole, out var point)) dots.Add(point);
        }
        else if (element.Style.StrokePattern == StrokePattern.DashDot)
        {
            if (closed && center.Count > 0) center.Add(center[0]);
            foreach (var mark in DashDotMarks(center, width, squareCaps: closed))
            {
                if (mark.Count > 1) AddDashPolygons(mark, cutRadius, polygons, dots, roundJoins);
                if (!closed)
                {
                    dots.Add(mark[0]);
                    if (mark.Count > 1) dots.Add(mark[^1]);
                }
            }
            if (closed && center.Count > 0) center.RemoveAt(center.Count - 1);
        }
        else
        {
            var chunks = DashRuns(center, closed, width, roundCaps: !closed).Select(run => run.Points).ToList();
            if (closed && chunks.Count > 1 && chunks[0][0].DistanceTo(chunks[^1][^1]) < 1e-8)
            {
                var merged = chunks[^1]; merged.AddRange(chunks[0].Skip(1)); chunks.RemoveAt(chunks.Count - 1); chunks[0] = merged;
            }
            foreach (var points in chunks)
            {
                AddDashPolygons(points, cutRadius, polygons, dots, roundJoins);
                if (!closed)
                {
                    dots.Add(points[0]);
                    dots.Add(points[^1]);
                }
            }
            if (!closed && chunks.Count == 0 && center.Count > 0) dots.Add(center[0]);
        }
        return new(center, polygons, dots, radius, closed);
    }

    private static double ClosedPerimeter(IReadOnlyList<PointD> points)
    {
        double length = 0;
        for (int i = 0; i < points.Count; i++) length += points[i].DistanceTo(points[(i + 1) % points.Count]);
        return length;
    }

    private static double ClosedRepeatCount(double perimeter, double nominalPeriod) =>
        Math.Max(1, Math.Round(perimeter / nominalPeriod, MidpointRounding.AwayFromZero));

    private static IEnumerable<PointD> DotStations(IReadOnlyList<PointD> center, bool closed, double spacing,
        Func<int, double, PointD>? pointOnCurve = null)
    {
        if (center.Count == 0) yield break;
        yield return center[0];
        double limit = double.PositiveInfinity;
        if (closed)
        {
            double perimeter = ClosedPerimeter(center);
            limit = ClosedRepeatCount(perimeter, spacing);
            spacing = perimeter / limit;
        }
        long index = 1;
        double offset = 0;
        int segments = closed ? center.Count : center.Count - 1;
        for (int i = 0; i < segments && index < limit; i++)
        {
            var a = center[i]; var b = center[(i + 1) % center.Count]; double length = a.DistanceTo(b);
            if (length <= 1e-9) continue;
            while (index < limit && index * spacing <= offset + length + 1e-9)
            {
                double fraction = Math.Clamp((index * spacing - offset) / length, 0, 1);
                yield return pointOnCurve is null ? a + (b - a) * fraction : pointOnCurve(i, fraction);
                index++;
            }
            offset += length;
        }
    }

    private static StrokePatternGeometry DottedEllipse(double a, double b, double radius)
    {
        double major = Math.Max(a, b), minor = Math.Min(a, b);
        var center = new PointD(a, b);
        if (radius >= minor)
            return new([center], [], radius == minor ? [center] : [], radius, true);

        // A complete dot touches the ellipse along its inward unit normal. If the
        // offset folds near a sharp tip, keep only normals up to the medial axis.
        double aspect = minor / major, inset = radius / minor;
        double trim = inset <= aspect ? 0 : Math.Asin(Math.Sqrt(Math.Clamp(
            (inset - aspect) * (inset + aspect) / ((1 - aspect) * (1 + aspect)), 0, 1)));
        int steps = CurveQuarterSteps(major);
        double quarter = Math.PI / 2, step = (quarter - trim) / steps;
        PointD At(int segment, double fraction)
        {
            int quadrant = segment / steps;
            double start = quadrant * quarter + ((quadrant % 2 == 0) == (a >= b) ? trim : 0);
            var (sin, cos) = Math.SinCos(start + (segment % steps + fraction) * step);
            double speed = double.Hypot(b * cos, a * sin);
            return new(a + cos * (a - radius * (b / speed)), b + sin * (b - radius * (a / speed)));
        }
        var contour = new PointD[4 * steps];
        for (int i = 0; i < contour.Length; i++) contour[i] = At(i, 0);
        // Measure along the inset contour, then evaluate the true tangent point at
        // each station. Drawing and picking consume the same complete circles.
        var dots = DotStations(contour, true, ClosedDotSpacingInRadii * radius, At).ToArray();
        return new(contour, [], dots, radius, true);
    }

    private static List<PointD> InsetRectangle(CanvasElement box, double radius)
    {
        var inset = new PointD(Math.Min(radius, box.Width / 2), Math.Min(radius, box.Height / 2));
        double corner = Math.Max(0, CornerRadius(box) - radius);
        var outline = RectangleOutline(box.Width - 2 * inset.X, box.Height - 2 * inset.Y, corner);
        // Use the top-left tangent as the seam, also when the corner shrinks to zero.
        int start = corner > 0 ? outline.Count - 1 : 0;
        var points = new PointD[outline.Count];
        for (int i = 0; i < points.Length; i++) points[i] = inset + outline[(start + i) % points.Length];
        return CleanContour(points);
    }

    private static IReadOnlyList<PointD> RectangleDots(CanvasElement box, IReadOnlyList<PointD> center, double radius)
    {
        if (2 * radius > Math.Min(box.Width, box.Height)) return [];
        double spacing = ClosedDotSpacingInRadii * radius;
        if (center.Count == 2)
        {
            // At exactly one dot diameter thick, both sides share one row.
            var span = center[1] - center[0];
            double length = span.DistanceTo(default);
            int intervals = (int)Math.Floor(length / spacing);
            double first = (length - intervals * spacing) / 2;
            return Enumerable.Range(0, intervals + 1).Select(i => center[0] + span * ((first + i * spacing) / length)).ToArray();
        }
        var stations = DotStations(center, true, spacing);
        double corner = CornerRadius(box);
        if (corner <= radius) return stations.ToArray();
        return stations.Select(p =>
        {
            var origin = new PointD(Math.Clamp(p.X, corner, box.Width - corner), Math.Clamp(p.Y, corner, box.Height - corner));
            if (p.X == origin.X || p.Y == origin.Y) return p;
            var radial = p - origin;
            return origin + radial * ((corner - radius) / radial.DistanceTo(default));
        }).ToArray();
    }

    private static IEnumerable<IReadOnlyList<PointD>> DashDotMarks(
        IReadOnlyList<PointD> points, double width, ElementStyle? flatStyle = null, bool squareCaps = false)
    {
        if (points.Count == 0) yield break;
        var axes = flatStyle is null ? default : FlatHighlighterAxes(flatStyle);
        var position = points[0];
        int next = 1;
        bool first = true;
        while (true)
        {
            var tangent = NextDirection();
            if (!first && next == points.Count) yield break;
            var dash = new List<PointD> { position };
            double leadingReach = Reach(tangent);
            double remaining = Advance(3 * width - (first ? leadingReach : 0), dash);
            // Locate the nib centers inside the visible dash envelope.
            double trailingInset = dash.Count > 1 ? Math.Max(0, Reach(dash[^1] - dash[^2]) - remaining) : 0;
            var trimmed = TrimDash(dash, first ? 0 : leadingReach, trailingInset);
            if (trimmed.Count > 0) yield return trimmed;
            if (remaining > 0 || Advance(width) > 0) yield break;
            if (squareCaps)
            {
                var dot = new List<PointD> { position };
                double dotRemaining = Advance(width, dot);
                if (dot.Count > 1) yield return dot;
                if (dotRemaining > 0) yield break;
            }
            else
            {
                var direction = NextDirection();
                if (next == points.Count) yield break;
                double dotReach = Reach(direction);
                if (Advance(dotReach) > 0) yield break;
                yield return new[] { position };
                if (Advance(dotReach) > 0) yield break;
            }
            if (Advance(width) > 0) yield break;
            first = false;
        }

        double Reach(PointD tangent) => squareCaps ? 0 : flatStyle is null ? width / 2 : FlatReach(tangent, axes);

        PointD NextDirection()
        {
            while (next < points.Count && position.DistanceTo(points[next]) <= 1e-9) next++;
            return next < points.Count ? points[next] - position : default;
        }

        double Advance(double distance, List<PointD>? path = null)
        {
            while (distance > 1e-9)
            {
                var delta = NextDirection();
                if (next == points.Count) return distance;
                double length = delta.DistanceTo(default);
                if (distance < length)
                {
                    position += delta * (distance / length);
                    path?.Add(position);
                    return 0;
                }
                position = points[next++];
                path?.Add(position);
                distance -= length;
            }
            return 0;
        }
    }

    private static IEnumerable<(List<PointD> Points, bool EndCap)> DashRuns(
        IReadOnlyList<PointD> center, bool closed, double width, bool roundCaps = false)
    {
        List<PointD>? chunk = null;
        // Two semicircular caps add one width to the visible dash.
        double offset = 0, cycle = 4 * width, ink = width * (roundCaps ? 2 : 3);
        if (closed)
        {
            double perimeter = ClosedPerimeter(center);
            if (perimeter <= 1e-9) yield break;
            cycle = perimeter / ClosedRepeatCount(perimeter, cycle);
            ink = .75 * cycle;
            offset = ink / 2; // Carry the middle of a dash through the closing corner.
        }
        int segments = closed ? center.Count : center.Count - 1;
        for (int i = 0; i < segments; i++)
        {
            var a = center[i]; var b = center[(i + 1) % center.Count]; double length = a.DistanceTo(b), along = 0;
            if (length <= 1e-9) continue;
            while (along < length - 1e-9)
            {
                double phase = (offset + along) % cycle;
                if (cycle - phase < 1e-8) phase = 0;
                bool visible = phase < ink - 1e-9;
                double end = Math.Min(length, along + (visible ? ink - phase : cycle - phase));
                if (end <= along + 1e-10) { along += 1e-8; continue; }
                if (visible)
                {
                    chunk ??= new();
                    var startPoint = a + (b - a) * (along / length);
                    if (chunk.Count == 0 || chunk[^1].DistanceTo(startPoint) > 1e-8) chunk.Add(startPoint);
                    chunk.Add(a + (b - a) * (end / length));
                }
                else if (chunk is not null) { yield return (chunk, false); chunk = null; }
                along = end;
            }
            offset += length;
        }
        if (chunk is not null) yield return (chunk, !closed && offset % cycle < ink - 1e-9);
    }

    private static List<PointD> CleanContour(IReadOnlyList<PointD> points, bool closed = true)
    {
        var result = new List<PointD>(points.Count);
        foreach (var p in points) if (result.Count == 0 || result[^1].DistanceTo(p) > 1e-9) result.Add(p);
        if (closed && result.Count > 1 && result[0].DistanceTo(result[^1]) < 1e-9) result.RemoveAt(result.Count - 1);
        return result;
    }

    private static List<PointD> InsetContour(IReadOnlyList<PointD> points, double radius)
    {
        var result = new List<PointD>(points.Count); double area = 0;
        for (int i = 0; i < points.Count; i++) { var a = points[i]; var b = points[(i + 1) % points.Count]; area += a.X * b.Y - b.X * a.Y; }
        double side = area >= 0 ? 1 : -1;
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i]; var previous = points[(i + points.Count - 1) % points.Count]; var next = points[(i + 1) % points.Count];
            var a = (p - previous) * (1 / p.DistanceTo(previous)); var b = (next - p) * (1 / next.DistanceTo(p));
            double denominator = 1 + a.X * b.X + a.Y * b.Y;
            result.Add(denominator <= 1e-9 ? p : p + new PointD(-a.Y - b.Y, a.X + b.X) * (radius * side / denominator));
        }
        return result;
    }

    private static void AddDashPolygons(IReadOnlyList<PointD> points, double radius, List<IReadOnlyList<PointD>> polygons,
        List<PointD> caps, bool roundJoins = false)
    {
        for (int i = 1; i < points.Count; i++)
        {
            var a = points[i - 1]; var b = points[i]; double length = a.DistanceTo(b);
            if (length <= 1e-9) continue;
            var normal = new PointD(a.Y - b.Y, b.X - a.X) * (radius / length);
            polygons.Add([a + normal, a - normal, b - normal, b + normal]);
        }
        bool loop = points.Count > 2 && points[0].DistanceTo(points[^1]) < 1e-8;
        for (int i = loop ? 0 : 1; i < points.Count - 1; i++)
        {
            var p = points[i];
            if (roundJoins) { caps.Add(p); continue; }
            var previous = points[i == 0 ? points.Count - 2 : i - 1]; var next = points[i + 1];
            var a = (p - previous) * (1 / p.DistanceTo(previous)); var b = (next - p) * (1 / next.DistanceTo(p));
            var n1 = new PointD(-a.Y, a.X) * radius; var n2 = new PointD(-b.Y, b.X) * radius;
            double denominator = 1 + a.X * b.X + a.Y * b.Y;
            if (denominator <= 1e-9) { caps.Add(p); continue; }
            var miter = (n1 + n2) * (1 / denominator);
            for (int side = -1; side <= 1; side += 2)
                polygons.Add(miter.DistanceTo(default) <= radius * 4
                    ? [p, p + n1 * side, p + miter * side, p + n2 * side]
                    : [p, p + n1 * side, p + n2 * side]);
        }
    }

    private static bool FitDot(IReadOnlyList<PointD> outer, PointD candidate, double radius, bool hole, out PointD point)
    {
        point = candidate;
        // Segment interpolation on a curved inset is slightly inside its exact offset.
        // Put the circle back on the true boundary-distance contour before checking fit.
        for (int i = 0; i < 3; i++)
        {
            var nearest = NearestOutlinePoint(outer, point); double distance = point.DistanceTo(nearest);
            if (distance < 1e-9 || PointInPolygon(point, outer) == hole) return false;
            point = nearest + (point - nearest) * (radius / distance);
        }
        return PointInPolygon(point, outer) != hole && point.DistanceTo(NearestOutlinePoint(outer, point)) >= radius - 1e-5;
    }

    private static PointD NearestOutlinePoint(IReadOnlyList<PointD> outer, PointD point)
    {
        PointD nearest = default; double minimum = double.MaxValue;
        for (int i = 0; i < outer.Count; i++)
        {
            var a = outer[i]; var b = outer[(i + 1) % outer.Count]; var v = b - a;
            double length = v.X * v.X + v.Y * v.Y;
            var q = a + v * (length <= 1e-12 ? 0 : Math.Clamp(((point.X-a.X)*v.X+(point.Y-a.Y)*v.Y)/length, 0, 1));
            double distance = point.DistanceTo(q);
            if (distance < minimum) { minimum = distance; nearest = q; }
        }
        return nearest;
    }

    private static bool PatternHit(CanvasElement element, Projective2D transform, PointD target, PointD local, double tolerance)
    {
        var geometry = StrokePatternGeometry(element);
        if (geometry.Closed && element.Style.StrokePattern is StrokePattern.Dashed or StrokePattern.DashDot)
        {
            IReadOnlyList<IReadOnlyList<PointD>> contours = element.Kind == ElementKind.Symbol ? SymbolContours(element) : [Outline(element)];
            var shape = element.Kind == ElementKind.Symbol ? SymbolGeometry.Shape(element.Symbol, element.CustomSymbol) : null;
            if (!Enumerable.Range(0, contours.Count).Any(i => StrokePathHit(transform, contours[i], true, target, local, element.Style.StrokeWidth, tolerance) ||
                InsideMiterHit(element, transform, contours[i], target, local, tolerance, shape?.IsHole(i) == true))) return false;
        }
        foreach (var dot in geometry.Dots)
            if (StrokeSegmentHit(transform, target, local, dot, dot, geometry.Radius, tolerance)) return true;
        foreach (var polygon in geometry.DashPolygons)
            if (PointInPolygon(local, polygon) || ProjectedContourHit(transform, polygon, target, tolerance)) return true;
        return false;
    }
}

public sealed record StrokePatternGeometry(IReadOnlyList<PointD> Centerline, IReadOnlyList<IReadOnlyList<PointD>> DashPolygons,
    IReadOnlyList<PointD> Dots, double Radius, bool Closed);
