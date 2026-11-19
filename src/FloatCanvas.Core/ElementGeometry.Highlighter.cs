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
        if (points.Count == 1 || style.StrokePattern == StrokePattern.Solid)
            yield return new(points);
        else if (style.StrokePattern == StrokePattern.Dashed)
        {
            bool hasRun = false;
            foreach (var run in DashRuns(points, false, Math.Max(.1, style.StrokeWidth)))
            {
                yield return new(run.Points, !hasRun, run.EndCap);
                hasRun = true;
            }
            if (!hasRun) yield return new(new[] { points[0] }, StartCap: true);
        }
        else
        {
            foreach (var center in DotStations(points, false, Math.Max(.1, style.StrokeWidth)))
                yield return new(new[] { center });
        }
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
