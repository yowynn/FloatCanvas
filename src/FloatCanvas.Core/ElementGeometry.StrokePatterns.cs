using System.Runtime.CompilerServices;

namespace FloatCanvas.Core;

public static partial class ElementGeometry
{
    private static readonly ConditionalWeakTable<CanvasElement, PatternCache> PatternCaches = new();

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
            s.CornerRadius == e.CornerRadius && s.Symbol == e.Symbol && s.Style.StrokeWidth == e.Style.StrokeWidth &&
            s.Style.StrokePattern == e.Style.StrokePattern && s.Style.ArrowHeadSize == e.Style.ArrowHeadSize &&
            s.Style.ArrowHeadStyle == e.Style.ArrowHeadStyle && s.Style.StartArrowHead == e.Style.StartArrowHead &&
            s.Style.EndArrowHead == e.Style.EndArrowHead && e.Points.ContentEquals(s.Points);
        public void Update(CanvasElement e) { Geometry = BuildPattern(e); _snapshot = e.Clone(); }
    }

    private static StrokePatternGeometry BuildPattern(CanvasElement element)
    {
        double width = Math.Max(.1, element.Style.StrokeWidth), radius = width / 2;
        bool closed = IsBoxShape(element.Kind);
        var outer = closed ? CleanContour(Outline(element)) : [];
        var center = closed ? InsetContour(outer, radius) : CleanContour(IsLine(element.Kind) ? LineShaftPoints(element) : element.Points, false);
        var polygons = new List<IReadOnlyList<PointD>>(); var dots = new List<PointD>();
        int segments = closed ? center.Count : Math.Max(0, center.Count - 1);
        double total = 0;
        for (int i = 0; i < segments; i++) total += center[i].DistanceTo(center[(i + 1) % center.Count]);
        if (element.Style.StrokePattern == StrokePattern.Dotted)
        {
            foreach (var station in DotStations(center, closed, width * (closed ? 2.5 : 2), total))
                if (!closed) dots.Add(station);
                else if (FitDot(element, outer, station, radius, out var point)) dots.Add(point);
        }
        else if (element.Style.StrokePattern == StrokePattern.DashDot)
        {
            if (closed && center.Count > 0) center.Add(center[0]);
            foreach (var mark in DashDotMarks(center, width, squareCaps: closed))
            {
                if (mark.Count > 1) AddDashPolygons(mark, radius, polygons, dots);
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
                AddDashPolygons(points, radius, polygons, dots);
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

    private static IEnumerable<PointD> DotStations(IReadOnlyList<PointD> center, bool closed, double spacing, double total = 0)
    {
        if (!closed && center.Count > 0) yield return center[0];
        double station = closed ? 0 : spacing, offset = 0;
        int segments = closed ? center.Count : center.Count - 1;
        for (int i = 0; i < segments; i++)
        {
            var a = center[i]; var b = center[(i + 1) % center.Count]; double length = a.DistanceTo(b);
            if (length <= 1e-9) continue;
            while (station <= offset + length + 1e-9 && (!closed || station < total - 1e-9))
            {
                yield return a + (b - a) * Math.Clamp((station - offset) / length, 0, 1);
                station += spacing;
            }
            offset += length;
        }
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
        double offset = 0, ink = width * (closed ? 4 : roundCaps ? 2 : 3), cycle = width * (closed ? 7 : 4);
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

    private static void AddDashPolygons(IReadOnlyList<PointD> points, double radius, List<IReadOnlyList<PointD>> polygons, List<PointD> caps)
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
            var p = points[i]; var previous = points[i == 0 ? points.Count - 2 : i - 1]; var next = points[i + 1];
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

    private static bool FitDot(CanvasElement element, IReadOnlyList<PointD> outer, PointD candidate, double radius, out PointD point)
    {
        point = candidate;
        // Segment interpolation on a curved inset is slightly inside its exact offset.
        // Put the circle back on the true boundary-distance contour before checking fit.
        for (int i = 0; i < 3; i++)
        {
            var nearest = NearestOutlinePoint(element, outer, point); double distance = point.DistanceTo(nearest);
            if (distance < 1e-9 || !PointInPolygon(point, outer)) return false;
            point = nearest + (point - nearest) * (radius / distance);
        }
        return PointInPolygon(point, outer) && point.DistanceTo(NearestOutlinePoint(element, outer, point)) >= radius - 1e-5;
    }

    private static PointD NearestOutlinePoint(CanvasElement e, IReadOnlyList<PointD> outer, PointD point)
    {
        if (e.Kind == ElementKind.Ellipse)
        {
            double cx = e.Width / 2, cy = e.Height / 2, best = 0, distance = double.MaxValue, step = Math.Tau / 32;
            PointD At(double angle) => new(cx + cx * Math.Cos(angle), cy + cy * Math.Sin(angle));
            for (int i = 0; i < 32; i++) { double d = point.DistanceTo(At(i * step)); if (d < distance) { distance = d; best = i * step; } }
            double first = best - step, last = best + step;
            for (int i = 0; i < 48; i++) { double a = (first * 2 + last) / 3, b = (first + last * 2) / 3; if (point.DistanceTo(At(a)) < point.DistanceTo(At(b))) last = b; else first = a; }
            return At((first + last) / 2);
        }
        PointD nearest = default; double minimum = double.MaxValue;
        void Consider(PointD q) { double d = point.DistanceTo(q); if (d < minimum) { minimum = d; nearest = q; } }
        if (e.Kind is ElementKind.Rectangle or ElementKind.RoundedRectangle && CornerRadius(e) > 0)
        {
            double r = CornerRadius(e), w = e.Width, h = e.Height;
            Consider(new(Math.Clamp(point.X, r, w - r), 0)); Consider(new(Math.Clamp(point.X, r, w - r), h));
            Consider(new(0, Math.Clamp(point.Y, r, h - r))); Consider(new(w, Math.Clamp(point.Y, r, h - r)));
            PointD[] centers = [new(w-r,r), new(w-r,h-r), new(r,h-r), new(r,r)];
            for (int i = 0; i < 4; i++)
            {
                double first = -Math.PI / 2 + i * Math.PI / 2;
                double angle = Math.Atan2(point.Y - centers[i].Y, point.X - centers[i].X);
                while (angle < first - Math.PI) angle += Math.Tau;
                while (angle > first + Math.PI) angle -= Math.Tau;
                angle = Math.Clamp(angle, first, first + Math.PI / 2);
                Consider(centers[i] + new PointD(Math.Cos(angle), Math.Sin(angle)) * r);
            }
        }
        else for (int i = 0; i < outer.Count; i++)
        {
            var a = outer[i]; var b = outer[(i + 1) % outer.Count]; var v = b - a;
            double length = v.X * v.X + v.Y * v.Y;
            Consider(a + v * (length <= 1e-12 ? 0 : Math.Clamp(((point.X-a.X)*v.X+(point.Y-a.Y)*v.Y)/length, 0, 1)));
        }
        return nearest;
    }

    private static bool PatternHit(CanvasElement element, Projective2D transform, PointD target, PointD local, double tolerance)
    {
        var geometry = StrokePatternGeometry(element);
        if (element.Style.StrokePattern == StrokePattern.Dashed)
        {
            var contour = geometry.Closed ? Outline(element) : IsLine(element.Kind) ? LineShaftPoints(element) : element.Points;
            double radius = element.Style.StrokeWidth * (geometry.Closed ? 1 : .5);
            if (!StrokePathHit(transform, contour, geometry.Closed, target, local, radius, tolerance) &&
                (!geometry.Closed || !InsideMiterHit(element, transform, contour, target, local, tolerance))) return false;
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
