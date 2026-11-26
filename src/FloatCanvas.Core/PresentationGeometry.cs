namespace FloatCanvas.Core;

/// <summary>Finite authored-region clipping shared by live rendering and explicit trail baking.</summary>
public static class PresentationGeometry
{
    public static bool TryScreenRegion(CanvasElement worldSource, CanvasMapping mapping, RectD viewport, out IReadOnlyList<PointD> polygon, bool contentOverflow = false)
    {
        polygon = [];
        if (worldSource is null || worldSource.Kind != ElementKind.Presentation || !Finite(viewport) || viewport.IsEmpty ||
            !double.IsFinite(worldSource.Width) || !double.IsFinite(worldSource.Height) || worldSource.Width <= 0 || worldSource.Height <= 0) return false;
        var transform = worldSource.IsBillboard ? mapping.ElementToScreen(worldSource) : ElementGeometry.LocalToPlane(worldSource);
        double left = 0, right = worldSource.Width, top = 0, bottom = worldSource.Height;
        if (contentOverflow)
        {
            var screenTransform = mapping.ElementToScreen(worldSource);
            if (screenTransform.TryInverse(out var screenToLocal))
            {
                PointD[] viewportCorners = [new(viewport.Left, viewport.Top), new(viewport.Right, viewport.Top), new(viewport.Right, viewport.Bottom), new(viewport.Left, viewport.Bottom)];
                var visible = viewportCorners.Select(point => screenToLocal.TryTransformPoint(point, out var localPoint) ? localPoint : new PointD(double.NaN, double.NaN)).Where(Finite).ToArray();
                if (visible.Length == 4)
                {
                    left = Math.Min(0, visible.Min(point => point.X) - worldSource.Width);
                    right = Math.Max(worldSource.Width, visible.Max(point => point.X) + worldSource.Width);
                    top = Math.Min(0, visible.Min(point => point.Y) - worldSource.Height);
                    bottom = Math.Max(worldSource.Height, visible.Max(point => point.Y) + worldSource.Height);
                }
            }
        }
        PointD[] local = [new(left, top), new(right, top), new(right, bottom), new(left, bottom)];
        if (!transform.TryInverse(out _) || worldSource.IsBillboard && !mapping.TryGetDepthScale(worldSource.BillboardAnchor, out _)) return false;
        var points = new List<PointD>(4);
        foreach (var point in local)
        {
            if (transform.Denominator(point) <= 0 || !transform.TryTransformPoint(point, out var projected)) return false;
            points.Add(projected);
        }
        if (!worldSource.IsBillboard)
        {
            // Intersect in the finite plane before projection. A region crossing the
            // canvas horizon retains its visible safe fragment, rather than disappearing.
            points = ClipPolygon(points, mapping.VisiblePlanePolygon);
            for (int i = 0; i < points.Count; i++)
            {
                if (!mapping.IsVisible(points[i]) || !mapping.PlaneToScreen.TryTransformPoint(points[i], out var projected)) return false;
                points[i] = projected;
            }
        }
        PointD[] viewportPolygon = [new(viewport.Left, viewport.Top), new(viewport.Right, viewport.Top), new(viewport.Right, viewport.Bottom), new(viewport.Left, viewport.Bottom)];
        points = ClipPolygon(points, viewportPolygon);
        if (points.Count < 3 || !double.IsFinite(Area(points)) || Math.Abs(Area(points)) <= 1e-10) return false;
        polygon = points.AsReadOnly(); return true;
    }

    public static bool TryClipSegment(PointD first, PointD last, IReadOnlyList<PointD> convexPolygon, out PointD clippedFirst, out PointD clippedLast)
    {
        clippedFirst = clippedLast = default;
        if (!Finite(first) || !Finite(last) || convexPolygon is null || convexPolygon.Count < 3) return false;
        double area = Area(convexPolygon); if (!double.IsFinite(area) || Math.Abs(area) <= 1e-10) return false;
        double orientation = Math.Sign(area), enter = 0, leave = 1;
        var direction = last - first;
        for (int i = 0; i < convexPolygon.Count; i++)
        {
            var edgeStart = convexPolygon[i]; var edge = convexPolygon[(i + 1) % convexPolygon.Count] - edgeStart;
            double distance = Cross(edge, first - edgeStart) * orientation, change = Cross(edge, direction) * orientation;
            if (!double.IsFinite(distance) || !double.IsFinite(change)) return false;
            if (Math.Abs(change) <= 1e-12) { if (distance < -1e-8) return false; continue; }
            double crossing = -distance / change;
            if (change > 0) enter = Math.Max(enter, crossing); else leave = Math.Min(leave, crossing);
            if (enter > leave) return false;
        }
        clippedFirst = first + direction * Math.Clamp(enter, 0, 1); clippedLast = first + direction * Math.Clamp(leave, 0, 1);
        return Finite(clippedFirst) && Finite(clippedLast);
    }
    private static List<PointD> ClipPolygon(IReadOnlyList<PointD> subject, IReadOnlyList<PointD> clip)
    {
        var output = subject.ToList(); double area = Area(clip);
        if (clip.Count < 3 || !double.IsFinite(area) || Math.Abs(area) <= 1e-10) return [];
        double orientation = Math.Sign(area);
        for (int i = 0; i < clip.Count && output.Count > 0; i++)
        {
            var input = output; output = [];
            var start = clip[i]; var edge = clip[(i + 1) % clip.Count] - start;
            var previous = input[^1]; double before = Cross(edge, previous - start) * orientation;
            foreach (var current in input)
            {
                double after = Cross(edge, current - start) * orientation;
                if (!double.IsFinite(before) || !double.IsFinite(after)) return [];
                bool beforeInside = before >= -1e-8, afterInside = after >= -1e-8;
                if (beforeInside != afterInside)
                {
                    double denominator = before - after;
                    if (Math.Abs(denominator) <= 1e-20) return [];
                    var intersection = previous + (current - previous) * Math.Clamp(before / denominator, 0, 1);
                    if (!Finite(intersection)) return [];
                    output.Add(intersection);
                }
                if (afterInside) output.Add(current);
                previous = current; before = after;
            }
        }
        return output;
    }
    private static double Area(IReadOnlyList<PointD> points)
    {
        double sum = 0; for (int i = 0; i < points.Count; i++) sum += Cross(points[i], points[(i + 1) % points.Count]); return sum;
    }
    private static double Cross(PointD a, PointD b) => a.X * b.Y - a.Y * b.X;
    private static bool Finite(PointD value) => double.IsFinite(value.X) && double.IsFinite(value.Y);
    private static bool Finite(RectD value) => Finite(new PointD(value.Left, value.Top)) && Finite(new PointD(value.Right, value.Bottom));
}
