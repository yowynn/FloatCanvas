using FloatCanvas.Core;

namespace FloatCanvas.App;

internal sealed partial class EditorController
{
    // All adorner points use the output coordinate system, before ScreenOrigin is added.
    internal IReadOnlyList<PointD> SelectionScreenCorners
    {
        get
        {
            if (Session.SelectionBounds() is not { } bounds) return [];
            var selected = Session.SelectedElements().Where(e => e.Visible && !e.Locked).ToArray();
            var plane = selected is [var image] && image.Kind == ElementKind.Image
                ? Corners(ElementGeometry.LocalBounds(image)).Select(ElementGeometry.LocalToPlane(image).TransformPoint)
                : Corners(bounds);
            return ToScreenPoints(plane);
        }
    }

    internal PointD? SelectionScreenRotationAnchor => SelectionScreenCorners is { Count: 4 } points
        ? (points[0] + points[1]) * .5 : null;

    internal PointD? SelectionScreenRotationHandle
    {
        get
        {
            if (SelectionScreenCorners is not { Count: 4 } points) return null;
            var anchor = (points[0] + points[1]) * .5;
            var edge = points[1] - points[0];
            double length = Math.Sqrt(edge.X * edge.X + edge.Y * edge.Y);
            if (length < 1e-9) return null;
            var normal = new PointD(edge.Y / length, -edge.X / length);
            var center = (points[0] + points[1] + points[2] + points[3]) * .25;
            if ((anchor.X - center.X) * normal.X + (anchor.Y - center.Y) * normal.Y < 0) normal *= -1;
            return anchor + normal * 28;
        }
    }

    internal IReadOnlyList<PointD> SelectionScreenEndpoints => Session.SelectedElements().ToArray() is [var line] &&
        ElementGeometry.IsLine(line.Kind) ? ToScreenPoints(line.Points.Select(line.Transform.TransformPoint)) : [];

    internal IReadOnlyList<PointD> MarqueeScreenPolygon => Marquee is not { } bounds ? [] :
        gesture == "marquee" ? Corners(bounds) : ToScreenPoints(Corners(bounds));

    internal IReadOnlyList<PointD> EffectScreenPreview => EffectPreviewPolygon is { } points ? ToScreenPoints(points) : [];

    private IReadOnlyList<PointD> ToScreenPoints(IEnumerable<PointD> points)
    {
        var matrix = CanvasMapping.Create(Session.Document).PlaneToScreen;
        var result = new List<PointD>();
        foreach (var point in points)
        {
            if (matrix.Denominator(point) <= 1e-8 || !matrix.TryTransformPoint(point, out var mapped)) return [];
            result.Add(mapped);
        }
        return result;
    }

    private CanvasElement? HitTestScreen(PointD point, double tolerance) => Session.Document.Elements
        .LastOrDefault(element => !element.Locked && ElementHitTestScreen(element, point, tolerance));

    private bool ElementHitTestScreen(CanvasElement element, PointD point, double tolerance) =>
        ElementGeometry.HitTestProjected(element, point, CanvasMapping.Create(Session.Document).PlaneToScreen, tolerance);

    private void SelectScreenRectangle(RectD rectangle, bool additive)
    {
        var mapping = CanvasMapping.Create(Session.Document);
        var selected = additive ? new HashSet<Guid>(Session.Selection) : [];
        foreach (var element in Session.Document.Elements.Where(e => e.Visible && !e.Locked))
        {
            if (!mapping.ProjectedBounds(element).Intersects(rectangle)) continue;
            // The box is a screen gesture. Test the projected contour, rather than the
            // inverse-mapped AABB, which selects unrelated objects beside a trapezoid.
            var transform = mapping.ElementToScreen(element);
            var local = ElementGeometry.UsesPoints(element.Kind) ? element.Points : ElementGeometry.Outline(element);
            bool closed = !ElementGeometry.UsesPoints(element.Kind);
            bool intersects = ProjectedContourIntersectsRectangle(local, transform, rectangle, closed);
            if (!intersects && ElementGeometry.IsLine(element.Kind))
                intersects = ElementGeometry.GetArrowHeads(element).Any(head =>
                    ProjectedContourIntersectsRectangle(head, transform, rectangle, true));
            if (intersects) selected.UnionWith(Session.SelectionUnitIds(element.Id));
        }
        Session.SetSelection(selected);
    }

    private static bool ProjectedContourIntersectsRectangle(IReadOnlyList<PointD> points, Projective2D transform,
        RectD rectangle, bool closed)
    {
        if (points.Count == 0) return false;
        // Clip before homogeneous division. Connecting projected endpoints from opposite
        // sides of the horizon would invent a segment through the visible screen.
        (double X, double Y, double Offset)[] boundaries =
        [
            (transform.M31, transform.M32, transform.M33 - 1e-8),
            (transform.M11 - rectangle.Left * transform.M31, transform.M12 - rectangle.Left * transform.M32, transform.M13 - rectangle.Left * transform.M33),
            (rectangle.Right * transform.M31 - transform.M11, rectangle.Right * transform.M32 - transform.M12, rectangle.Right * transform.M33 - transform.M13),
            (transform.M21 - rectangle.Top * transform.M31, transform.M22 - rectangle.Top * transform.M32, transform.M23 - rectangle.Top * transform.M33),
            (rectangle.Bottom * transform.M31 - transform.M21, rectangle.Bottom * transform.M32 - transform.M22, rectangle.Bottom * transform.M33 - transform.M23)
        ];
        static double Side(PointD p, (double X, double Y, double Offset) boundary) =>
            p.X * boundary.X + p.Y * boundary.Y + boundary.Offset;
        if (!closed)
        {
            if (points.Count == 1) return boundaries.All(boundary => Side(points[0], boundary) >= 0);
            for (int i = 1; i < points.Count; i++)
            {
                double first = 0, last = 1;
                foreach (var boundary in boundaries)
                {
                    double a = Side(points[i - 1], boundary), b = Side(points[i], boundary);
                    if (a < 0 && b < 0) { last = -1; break; }
                    if (a < 0) first = Math.Max(first, a / (a - b));
                    else if (b < 0) last = Math.Min(last, a / (a - b));
                }
                if (first <= last) return true;
            }
            return false;
        }
        var polygon = points.ToList();
        foreach (var boundary in boundaries)
        {
            if (polygon.Count == 0) return false;
            var input = polygon; polygon = [];
            var previous = input[^1]; double before = Side(previous, boundary);
            foreach (var current in input)
            {
                double after = Side(current, boundary);
                if ((before >= 0) != (after >= 0))
                    polygon.Add(previous + (current - previous) * (before / (before - after)));
                if (after >= 0) polygon.Add(current);
                previous = current; before = after;
            }
        }
        return polygon.Count > 0;
    }
}
