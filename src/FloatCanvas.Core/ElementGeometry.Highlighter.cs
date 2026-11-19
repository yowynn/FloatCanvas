namespace FloatCanvas.Core;

public static partial class ElementGeometry
{
    public static bool HasFlatHighlighterTip(CanvasElement element) =>
        element.Kind == ElementKind.Highlighter && element.Style.HighlighterTip == HighlighterTip.Flat;

    public static PointD FlatHighlighterExtent(ElementStyle style)
    {
        var (longAxis, shortAxis) = FlatHighlighterAxes(style);
        return new(Math.Abs(longAxis.X) + Math.Abs(shortAxis.X), Math.Abs(longAxis.Y) + Math.Abs(shortAxis.Y));
    }

    private static (PointD Long, PointD Short) FlatHighlighterAxes(ElementStyle style)
    {
        double angle = style.HighlighterTipAngle * Math.PI / 180;
        double halfWidth = style.StrokeWidth / 2;
        var (sin, cos) = Math.SinCos(angle);
        return (new(-sin * halfWidth, cos * halfWidth), new(cos * halfWidth / 4, sin * halfWidth / 4));
    }

    /// <summary>Consistently wound contours of the full footprint, for bounds-independent picking and previews.</summary>
    public static IEnumerable<PointD[]> FlatHighlighterPolygons(IReadOnlyList<PointD> points, ElementStyle style)
    {
        foreach (var run in HighlighterRuns(points, style))
            foreach (var group in FlatHighlighterSweeps(run.Points, style))
                foreach (var polygon in group.Polygons) yield return polygon;
    }

    public static IEnumerable<HighlighterRun> HighlighterRuns(IReadOnlyList<PointD> points, ElementStyle style)
    {
        if (points.Count == 0 || style.StrokeWidth <= 0) yield break;
        double width = Math.Max(.1, style.StrokeWidth);
        bool flat = style.HighlighterTip == HighlighterTip.Flat;
        if (points.Count == 1 || style.StrokePattern == StrokePattern.Solid)
            yield return new(points);
        else if (style.StrokePattern == StrokePattern.DashDot)
        {
            foreach (var mark in DashDotMarks(points, width, flat ? style : null))
                yield return new(mark);
        }
        else if (style.StrokePattern == StrokePattern.Dashed)
        {
            bool hasRun = false;
            foreach (var run in DashRuns(points, false, width, roundCaps: !flat))
            {
                bool startCap = !hasRun;
                hasRun = true;
                var path = flat ? TrimFlatDash(run.Points, style, startCap, run.EndCap) : run.Points;
                if (path.Count > 0) yield return new(path, !flat || startCap, !flat || run.EndCap);
            }
            if (!hasRun) yield return new(new[] { points[0] }, StartCap: true);
        }
        else
        {
            var stations = flat ? FlatDotStations(points, style, width) : DotStations(points, false, width * 2);
            foreach (var center in stations)
                yield return new(new[] { center });
        }
    }

    private static IReadOnlyList<PointD> TrimFlatDash(List<PointD> points, ElementStyle style, bool startCap, bool endCap)
    {
        if (startCap && endCap || points.Count < 2) return points;
        var axes = FlatHighlighterAxes(style);
        // Bring the centers inward so the swept nib stops at each nominal dash boundary.
        double startInset = startCap ? 0 : FlatReach(points[1] - points[0], axes);
        double endInset = endCap ? 0 : FlatReach(points[^1] - points[^2], axes);
        return TrimDash(points, startInset, endInset);
    }

    private static IReadOnlyList<PointD> TrimDash(List<PointD> points, double startInset, double endInset)
    {
        if (points.Count < 2) return startInset > 0 || endInset > 0 ? [] : points;
        var (first, start) = MoveInward(startInset, 0, 1);
        var (last, end) = MoveInward(endInset, points.Count - 1, -1);
        if (first >= points.Count || last < 0) return [];
        if (first > last)
        {
            // Both cut stations can fall in the same original segment.
            if (first != last + 1) return [];
            var direction = points[first] - points[last];
            var remaining = end - start;
            if (remaining.X * direction.X + remaining.Y * direction.Y < 0) return [];
            return [start, end];
        }
        var trimmed = new List<PointD> { start };
        for (int i = first; i <= last; i++)
            if (trimmed[^1] != points[i]) trimmed.Add(points[i]);
        if (trimmed[^1] != end) trimmed.Add(end);
        return trimmed;

        (int, PointD) MoveInward(double distance, int index, int step)
        {
            if (distance == 0) return (index, points[index]);
            for (int next = index + step; next >= 0 && next < points.Count; next += step)
            {
                double length = points[index].DistanceTo(points[next]);
                if (length >= distance && length > 0)
                    return (next, points[index] + (points[next] - points[index]) * (distance / length));
                distance -= length;
                index = next;
            }
            return (index + step, points[index]);
        }
    }

    private static IEnumerable<PointD> FlatDotStations(IReadOnlyList<PointD> points, ElementStyle style, double gap)
    {
        var axes = FlatHighlighterAxes(style);
        yield return points[0];
        double phase = 0;
        for (int i = 1; i < points.Count; i++)
        {
            var delta = points[i] - points[i - 1];
            double length = delta.DistanceTo(default);
            if (length <= 1e-9) continue;
            double spacing = gap + 2 * FlatReach(delta, axes);
            double advance = length / spacing;
            for (double next = 1 - phase; next <= advance + 1e-9; next++)
                yield return points[i - 1] + delta * Math.Min(1, next / advance);
            double traveled = phase + advance;
            phase = Math.Max(0, traveled - Math.Floor(traveled + 1e-9));
        }
    }

    private static double FlatReach(PointD direction, (PointD Long, PointD Short) axes)
    {
        double length = direction.DistanceTo(default);
        return length <= 1e-9 ? 0 : (Math.Abs(direction.X * axes.Long.X + direction.Y * axes.Long.Y) +
            Math.Abs(direction.X * axes.Short.X + direction.Y * axes.Short.Y)) / length;
    }

    public static StrokePatternGeometry RoundHighlighterDashMask(HighlighterRun run, double radius)
    {
        var polygons = new List<IReadOnlyList<PointD>>();
        var caps = new List<PointD>();
        AddDashPolygons(run.Points, radius, polygons, caps);
        if (run.StartCap) caps.Add(run.Points[0]);
        if (run.EndCap) caps.Add(run.Points[^1]);
        return new(run.Points, polygons, caps, radius, false);
    }

    /// <summary>Initial footprint followed by the area newly covered by each successive movement.</summary>
    public static IEnumerable<(PointD Movement, PointD[][] Polygons)> FlatHighlighterSweeps(IReadOnlyList<PointD> run, ElementStyle style)
    {
        var (longAxis, shortAxis) = FlatHighlighterAxes(style);
        PointD[] corners = [shortAxis + longAxis, longAxis - shortAxis, (shortAxis + longAxis) * -1, shortAxis - longAxis];

        yield return (default, [corners.Select(corner => run[0] + corner).ToArray()]);
        for (int i = 1; i < run.Count; i++)
        {
            var start = run[i - 1];
            var end = run[i];
            var delta = end - start;
            var polygons = new List<PointD[]>(2);
            for (int edge = 0; edge < corners.Length; edge++)
            {
                var a = corners[edge];
                var b = corners[(edge + 1) % corners.Length];
                var side = b - a;
                if (delta.X * side.Y - delta.Y * side.X > 0)
                    polygons.Add([start + b, start + a, end + a, end + b]);
            }
            if (polygons.Count > 0) yield return (delta, polygons.ToArray());
        }
    }
}

public readonly record struct HighlighterRun(IReadOnlyList<PointD> Points, bool StartCap = false, bool EndCap = false);
