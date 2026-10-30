using FloatCanvas.Core;
using FloatCanvas.Rendering;

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
            var mapping = CanvasMapping.Create(Session.Document);
            if (selected is [var billboard] && billboard.IsBillboard)
            {
                if (!mapping.IsElementSafe(billboard)) return [];
                var local = billboard.Kind == ElementKind.Image ? ElementGeometry.StrokedLocalBounds(billboard) : ElementGeometry.LocalBounds(billboard);
                return Corners(local).Select(mapping.ElementToScreen(billboard).TransformPoint).ToArray();
            }
            if (selected.Any(e => e.IsBillboard))
            {
                var displayed = selected.Where(e => !e.IsBillboard || mapping.IsElementSafe(e)).ToArray();
                if (displayed.Length == 0) return [];
                var screenBounds = displayed.Select(e => mapping.ProjectedBounds(e)).Aggregate(RectD.Union);
                return Corners(screenBounds);
            }
            // Captures use the plane envelope of their contour, rather than the PNG allocation rectangle.
            var plane = selected is [var single] && !(single.Kind == ElementKind.Image &&
                (single.ProjectivePlacement is not null || single.ImageFrame is not null))
                ? Corners(ElementGeometry.StrokedLocalBounds(single)).Select(ElementGeometry.LocalToPlane(single).TransformPoint)
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

    internal IReadOnlyList<PointD> SelectionScreenEndpoints
    {
        get
        {
            if (Session.SelectedElements().ToArray() is not [var line] || !ElementGeometry.IsLine(line.Kind)) return [];
            var mapping = CanvasMapping.Create(Session.Document);
            if (line.IsBillboard && !mapping.IsElementSafe(line)) return [];
            return line.Points.Select(mapping.ElementToScreen(line).TransformPoint).ToArray();
        }
    }

    internal IReadOnlyList<PointD> MarqueeScreenPolygon => Marquee is not { } bounds ? [] :
        gesture == "marquee" ? Corners(bounds) : gesture == "desktop-effect" && billboardDepth is { } depth ?
        Corners(bounds).Select(p => p * depth.Scale).ToArray() : ToScreenPoints(Corners(bounds));

    internal IReadOnlyList<PointD> EffectScreenPreview => effectTarget is { IsBillboard: true } item && imageRegion is { } region ?
        Corners(region).Select(CanvasMapping.Create(Session.Document).ElementToScreen(item).TransformPoint).ToArray() :
        EffectPreviewPolygon is { } points ? ToScreenPoints(points) : [];

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

    private CanvasElement? HitTestScreen(PointD point, double tolerance)
    {
        if (CanvasContentHidden) return null;
        if (_pointPreflight is { } preflight && preflight.Point == point && preflight.Hits.TryGetValue(tolerance, out var id))
            return id is null ? null : Session.Document.Elements.FirstOrDefault(e => e.Id == id && e.Visible && !e.Locked);
        var document = PickingDocument;
        var hit = !CanvasDepthOrder.IsEnabled(document) ? document.Elements
            .LastOrDefault(element => !element.Locked && ElementHitTestScreenCore(document, element, point, tolerance)) :
            SelectionRenderer.PickDepth(document, PickingAssets, point, tolerance);
        // The shown snapshot may lag a removal. Never resurrect or mutate its frozen nodes.
        return hit is null ? null : Session.Document.Elements.FirstOrDefault(e => e.Id == hit.Id && e.Visible && !e.Locked);
    }

    private bool ElementHitTestScreen(CanvasElement element, PointD point, double tolerance)
    {
        if (_pointPreflight is { } preflight && preflight.Point == point && preflight.Ink.TryGetValue((element.Id, tolerance), out bool ink)) return ink;
        var document = PickingDocument;
        var shown = document.Elements.FirstOrDefault(e => e.Id == element.Id);
        return shown is not null && ElementHitTestScreenCore(document, shown, point, tolerance);
    }

    private bool ElementHitTestScreenCore(SceneDocument document, CanvasElement element, PointD point, double tolerance)
        => ElementHitTestScreenCore(document, PickingAssets, SelectionRenderer, element, point, tolerance);

    private static bool ElementHitTestScreenCore(SceneDocument document, AssetLibrary assets, SceneRenderer renderer,
        CanvasElement element, PointD point, double tolerance, CancellationToken cancellationToken = default)
    {
        var mapping = CanvasMapping.Create(document);
        return (!element.IsBillboard || mapping.IsElementSafe(element)) &&
            ElementGeometry.HitTestProjected(element, point, mapping.PlaneToScreen, tolerance) &&
            renderer.IntersectsElementInk(document, assets, element,
                new(point.X - Math.Max(.5, tolerance), point.Y - Math.Max(.5, tolerance), Math.Max(1, tolerance * 2), Math.Max(1, tolerance * 2)), cancellationToken);
    }

    private void SelectScreenRectangle(RectD rectangle, bool additive)
    {
        if (CanvasContentHidden) return;
        if (BackgroundPickingEnabled)
        {
            PendingPicking = SelectScreenRectangleAsync(rectangle, additive);
            return;
        }
        ApplyRectangleHits(ScreenRectangleHits(PickingDocument, PickingAssets, SelectionRenderer, rectangle), additive, Session.Selection.ToArray());
    }

    private void ApplyRectangleHits(IEnumerable<Guid> hits, bool additive, IEnumerable<Guid> previous)
    {
        var selected = additive ? new HashSet<Guid>(previous) : [];
        foreach (var id in hits)
            if (Session.Document.Elements.Any(e => e.Id == id && e.Visible && !e.Locked)) selected.UnionWith(Session.SelectionUnitIds(id));
        Session.SetSelection(selected);
    }

    private static IReadOnlyList<Guid> ScreenRectangleHits(SceneDocument document, AssetLibrary assets, SceneRenderer renderer,
        RectD rectangle, CancellationToken cancellationToken = default)
    {
        var mapping = CanvasMapping.Create(document);
        var selected = new List<Guid>();
        foreach (var element in document.Elements.Where(e => e.Visible && !e.Locked))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (element.IsBillboard && !mapping.IsElementSafe(element)) continue;
            if (!mapping.ProjectedBounds(element).Intersects(rectangle)) continue;
            // The box is a screen gesture. Test the projected contour, rather than the
            // inverse-mapped AABB, which selects unrelated objects beside a trapezoid.
            var transform = mapping.ElementToScreen(element);
            var local = ElementGeometry.IsLine(element.Kind) ? ElementGeometry.LineShaftPoints(element) :
                ElementGeometry.UsesPoints(element.Kind) ? element.Points : ElementGeometry.Outline(element);
            bool closed = !ElementGeometry.UsesPoints(element.Kind);
            // Image alpha and its editable border can extend beyond the body contour.
            // The full envelope above is a broad phase; the ink sample below is exact.
            bool intersects = element.Kind == ElementKind.Image || ProjectedContourIntersectsRectangle(local, transform, rectangle, closed);
            bool hasStroke = element.Kind is not (ElementKind.Image or ElementKind.Text or ElementKind.Number) && !ElementGeometry.IsLiveEffect(element.Kind);
            if (!intersects && hasStroke)
                intersects = ProjectedStrokeIntersectsRectangle(local, transform, rectangle, closed, element.Style.StrokeWidth);
            if (!intersects && ElementGeometry.IsLine(element.Kind))
                intersects = ElementGeometry.GetStyledArrowHeads(element).Any(head =>
                    ProjectedContourIntersectsRectangle(head.Points, transform, rectangle, true) ||
                    head.Style == ArrowHeadStyle.Open && ProjectedStrokeIntersectsRectangle(head.Points, transform, rectangle, false, element.Style.StrokeWidth));
            if (intersects && renderer.IntersectsElementInk(document, assets, element, rectangle, cancellationToken)) selected.Add(element.Id);
        }
        return selected;
    }

    private static bool ProjectedContourIntersectsRectangle(IReadOnlyList<PointD> points, Projective2D transform,
        RectD rectangle, bool closed)
    {
        if (points.Count == 0) return false;
        // Clip before homogeneous division. Connecting projected endpoints from opposite
        // sides of the horizon would invent a segment through the visible screen.
        var boundaries = ProjectedRectangleBoundaries(transform, rectangle);
        if (!closed)
        {
            if (points.Count == 1) return boundaries.All(boundary => BoundarySide(points[0], boundary) >= 0);
            for (int i = 1; i < points.Count; i++)
            {
                double first = 0, last = 1;
                foreach (var boundary in boundaries)
                {
                    double a = BoundarySide(points[i - 1], boundary), b = BoundarySide(points[i], boundary);
                    if (a < 0 && b < 0) { last = -1; break; }
                    if (a < 0) first = Math.Max(first, a / (a - b));
                    else if (b < 0) last = Math.Min(last, a / (a - b));
                }
                if (first <= last) return true;
            }
            return false;
        }
        return ClipProjectedPolygon(points, boundaries).Count > 0;
    }

    private static bool ProjectedStrokeIntersectsRectangle(IReadOnlyList<PointD> points, Projective2D transform,
        RectD rectangle, bool closed, double width)
    {
        if (points.Count == 0 || width <= 0) return false;
        double radius = width / 2;
        var bounds = new RectD(points.Min(p => p.X), points.Min(p => p.Y),
            points.Max(p => p.X) - points.Min(p => p.X), points.Max(p => p.Y) - points.Min(p => p.Y)).Inflate(radius);
        // Pull the marquee's positive branch into local space and clip it to bounded
        // stroke extents. Distances there retain round caps/joins and nonuniform or
        // projective widths without inflating the screen rectangle by a maximum scale.
        var polygon = ClipProjectedPolygon(Corners(bounds), ProjectedRectangleBoundaries(transform, rectangle));
        if (polygon.Count == 0) return false;
        int segments = closed ? points.Count : Math.Max(1, points.Count - 1);
        for (int i = 0; i < segments; i++)
        {
            var a = points[i]; var b = points[closed ? (i + 1) % points.Count : Math.Min(i + 1, points.Count - 1)];
            if (ElementGeometry.PointInPolygon(a, polygon) || ElementGeometry.PointInPolygon(b, polygon)) return true;
            // The centerline intersection has already been tested. The remaining
            // capsule overlap occurs at a vertex of either segment/polygon boundary.
            for (int edge = 0; edge < polygon.Count; edge++)
            {
                var first = polygon[edge]; var last = polygon[(edge + 1) % polygon.Count];
                if (ElementGeometry.DistanceToSegment(first, a, b) <= radius ||
                    ElementGeometry.DistanceToSegment(a, first, last) <= radius ||
                    ElementGeometry.DistanceToSegment(b, first, last) <= radius) return true;
            }
        }
        return false;
    }

    private static (double X, double Y, double Offset)[] ProjectedRectangleBoundaries(Projective2D transform, RectD rectangle) =>
    [
        (transform.M31, transform.M32, transform.M33 - 1e-8),
        (transform.M11 - rectangle.Left * transform.M31, transform.M12 - rectangle.Left * transform.M32, transform.M13 - rectangle.Left * transform.M33),
        (rectangle.Right * transform.M31 - transform.M11, rectangle.Right * transform.M32 - transform.M12, rectangle.Right * transform.M33 - transform.M13),
        (transform.M21 - rectangle.Top * transform.M31, transform.M22 - rectangle.Top * transform.M32, transform.M23 - rectangle.Top * transform.M33),
        (rectangle.Bottom * transform.M31 - transform.M21, rectangle.Bottom * transform.M32 - transform.M22, rectangle.Bottom * transform.M33 - transform.M23)
    ];

    private static double BoundarySide(PointD point, (double X, double Y, double Offset) boundary) =>
        point.X * boundary.X + point.Y * boundary.Y + boundary.Offset;

    private static List<PointD> ClipProjectedPolygon(IReadOnlyList<PointD> points, (double X, double Y, double Offset)[] boundaries)
    {
        var polygon = points.ToList();
        foreach (var boundary in boundaries)
        {
            if (polygon.Count == 0) return polygon;
            var input = polygon; polygon = [];
            var previous = input[^1]; double before = BoundarySide(previous, boundary);
            foreach (var current in input)
            {
                double after = BoundarySide(current, boundary);
                if ((before >= 0) != (after >= 0))
                    polygon.Add(previous + (current - previous) * (before / (before - after)));
                if (after >= 0) polygon.Add(current);
                previous = current; before = after;
            }
        }
        return polygon;
    }
}
