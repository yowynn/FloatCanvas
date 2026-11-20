using FloatCanvas.Core;
using FloatCanvas.Rendering;

namespace FloatCanvas.App;

internal sealed partial class EditorController
{
    internal RectD? SelectionViewingBounds
    {
        get
        {
            var mapping=CurrentCanvasMapping;
            var bounds=SelectedEditingWorldElements().Where(element=>element.Visible && element.Style.Opacity>0)
                .Select(element=>mapping.ProjectedBounds(element)).Where(b=>!b.IsEmpty && double.IsFinite(b.X+b.Y+b.Width+b.Height)).ToArray();
            return bounds.Length==0?null:bounds.Aggregate(RectD.Union);
        }
    }
    // All adorner points use the output coordinate system, before CanvasOrigin is added.
    internal IReadOnlyList<PointD> SelectionScreenCorners
    {
        get
        {
            var elements = SelectedEditingWorldElements(editableOnly: true).ToArray();
            if (elements.Length == 0) return [];
            var selected = elements.Where(e => e.Visible).ToArray();
            var mapping = CurrentCanvasMapping;
            if (selected is [var single])
            {
                if (single.IsBillboard && !mapping.IsElementSafe(single)) return [];
                var frame = SingleSelectionFrame(single, mapping);
                return MapCorners(frame.Bounds, frame.ToScreen);
            }
            if (selected.Any(e => e.IsBillboard))
            {
                var displayed = selected.Where(e => !e.IsBillboard || mapping.IsElementSafe(e)).ToArray();
                if (displayed.Length == 0) return [];
                var screenBounds = displayed.Select(e => mapping.ProjectedBounds(e)).Aggregate(RectD.Union);
                return Corners(screenBounds);
            }
            return ToScreenPoints(Corners(elements.Select(e => ElementGeometry.Bounds(e)).Aggregate(RectD.Union)));
        }
    }

    private static (RectD Bounds, Projective2D ToScreen, bool AfterProjection) SingleSelectionFrame(CanvasElement element, CanvasMapping mapping)
    {
        if (!element.IsBillboard && element.Kind == ElementKind.Image && (element.ProjectivePlacement is not null || element.ImageFrame is not null))
            return (ElementGeometry.ImagePlacementBounds(element),
                mapping.PlaneToScreen.Compose(Projective2D.FromAffine(element.Transform)), true);
        var bounds = element.IsBillboard && element.Kind != ElementKind.Image
            ? ElementGeometry.LocalBounds(element) : ElementGeometry.StrokedLocalBounds(element);
        return (bounds, mapping.ElementToScreen(element), false);
    }

    private static IReadOnlyList<PointD> MapCorners(RectD bounds, Projective2D transform)
    {
        var corners = Corners(bounds);
        for (int i = 0; i < corners.Length; i++)
        {
            var point = corners[i];
            if (transform.Denominator(point) <= 0 || !transform.TryTransformPoint(point, out corners[i])) return [];
        }
        return corners;
    }

    internal PointD? SelectionScreenRotationAnchor => RotationAnchor(SelectionScreenCorners);
    internal static PointD? RotationAnchor(IReadOnlyList<PointD> points) => points is { Count: 4 }
        ? (points[0] + points[1]) * .5 : null;

    internal PointD? SelectionScreenRotationHandle
    {
        get => RotationHandle(SelectionScreenCorners);
    }
    internal static PointD? RotationHandle(IReadOnlyList<PointD> points)
    {
        if (points.Count != 4) return null;
        var anchor = (points[0] + points[1]) * .5;
        var edge = points[1] - points[0];
        double length = Math.Sqrt(edge.X * edge.X + edge.Y * edge.Y);
        if (length < 1e-9) return null;
        var normal = new PointD(edge.Y / length, -edge.X / length);
        var center = (points[0] + points[1] + points[2] + points[3]) * .25;
        if ((anchor.X - center.X) * normal.X + (anchor.Y - center.Y) * normal.Y < 0) normal *= -1;
        return anchor + normal * 28;
    }

    internal IReadOnlyList<PointD> SelectionScreenEndpoints
    {
        get
        {
            if (HasNodeSelection || SelectedEditingWorldElements().ToArray() is not [var line] || !ElementGeometry.IsLine(line.Kind)) return [];
            var mapping = CurrentCanvasMapping;
            if (line.IsBillboard && !mapping.IsElementSafe(line)) return [];
            return line.Points.Select(mapping.ElementToScreen(line).TransformPoint).ToArray();
        }
    }

    internal PointD? SelectionScreenArcHandle
    {
        get
        {
            if (HasNodeSelection || SelectedEditingWorldElements(editableOnly: true).ToArray() is not [var line])
                return null;
            return ScreenArcHandle(line);
        }
    }

    private PointD? ScreenArcHandle(CanvasElement line)
    {
        if (ElementGeometry.LineArcHandle(line) is not { } local) return null;
        var mapping = CurrentCanvasMapping;
        var transform = mapping.ElementToScreen(line);
        return mapping.IsElementSafe(line) && transform.TryTransformPoint(local, out var point) ? point : null;
    }

    internal IReadOnlyList<PointD> MarqueeScreenPolygon => Marquee is not { } bounds ? [] :
        gesture == "marquee" ? Corners(bounds) : gesture == "desktop-effect" && billboardDepth is { } depth ?
        Corners(bounds).Select(p => p * depth.Scale).ToArray() : ToScreenPoints(Corners(bounds));

    internal IReadOnlyList<PointD> EffectScreenPreview => effectTarget is { IsBillboard: true } item && imageRegion is { } region ?
        Corners(region).Select(CurrentCanvasMapping.ElementToScreen(new SceneGraph(Session.Document).ToWorld(item)).TransformPoint).ToArray() :
        EffectPreviewPolygon is { } points ? ToScreenPoints(points) : [];

    private IReadOnlyList<PointD> ToScreenPoints(IEnumerable<PointD> points)
    {
        var matrix = CurrentCanvasMapping.PlaneToScreen;
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
            return id is null ? null : CurrentEditableLeaf(id.Value);
        var document = PickingDocument;
        var hit = !CanvasDepthOrder.IsEnabled(document) ? document.Elements
            .LastOrDefault(element => SceneCanvasRoots.Resolve(document, element.Id) == PickingRootCanvas && IsShownInkSelectable(document, element) && ElementHitTestScreenCore(document, element, point, tolerance)) :
            SelectionRenderer.PickDepth(document, PickingAssets, canvasView.ToCanvas(point), tolerance / canvasView.Zoom, rootCanvas: PickingRootCanvas);
        // The shown snapshot may lag a removal. Never resurrect or mutate its frozen nodes.
        return hit is null ? null : CurrentEditableLeaf(hit.Id);
    }
    private CanvasElement? CurrentEditableLeaf(Guid id)
    {
        var evaluation = CurrentPickingEvaluation;
        if (evaluation.FindOccurrence(id) is not { } occurrence || !IsPickingOccurrenceSelectable(evaluation, id) || ResolveHitSelectableNode(id) is null) return null;
        var graph = new SceneGraph(Session.Document);
        return occurrence.Source.DefinitionId is null ? graph.FindElement(occurrence.HostNodeId) : occurrence.RenderElement;
    }

    private Guid? ResolveHitHostNode(Guid runtimeId) => CurrentPickingEvaluation.HostNodeOfOccurrence(runtimeId) ??
        (Session.Document.Instances.Any(instance => instance.Id == runtimeId) ? runtimeId : null);
    private bool IsInstanceHit(Guid runtimeId) => ResolveHitHostNode(runtimeId) is { } host && Session.Document.Instances.Any(instance => instance.Id == host);
    private Guid? ResolveHitSelectableNode(Guid runtimeId, bool individual = false)
    {
        var evaluation = CurrentPickingEvaluation;
        if (evaluation.RootCanvasOf(runtimeId) != Session.ActiveRootCanvas || !IsPickingOccurrenceSelectable(evaluation, runtimeId) || evaluation.ResolveSelectableNode(runtimeId, Session.EditScopeId) is not { } selectable) return null;
        return individual ? evaluation.HostNodeOfOccurrence(runtimeId) : selectable;
    }
    private bool IsPickingOccurrenceSelectable(EvaluatedScene evaluation, Guid id) => evaluation.IsPickableOccurrence(id,
        independentCanvasView ? PickingDocument.Elements.FirstOrDefault(element => element.Id == id)?.Visible == true : null);
    private static bool IsShownInkSelectable(SceneDocument document, CanvasElement element) =>
        SceneEvaluation.RuntimeView(document)?.IsPickableOccurrence(element.Id) ?? (element.Visible && !element.Locked);

    private bool ElementHitTestScreen(CanvasElement element, PointD point, double tolerance)
    {
        if (_pointPreflight is { } preflight && preflight.Point == point && preflight.Ink.TryGetValue((element.Id, tolerance), out bool ink)) return ink;
        var document = PickingDocument;
        var shown = document.Elements.FirstOrDefault(e => e.Id == element.Id);
        return shown is not null && ElementHitTestScreenCore(document, shown, point, tolerance);
    }

    private bool ElementHitTestScreenCore(SceneDocument document, CanvasElement element, PointD point, double tolerance)
        => ElementHitTestScreenCore(document, PickingAssets, SelectionRenderer, element, canvasView.ToCanvas(point), tolerance / canvasView.Zoom);

    private static bool ElementHitTestScreenCore(SceneDocument document, AssetLibrary assets, SceneRenderer renderer,
        CanvasElement element, PointD point, double tolerance, CancellationToken cancellationToken = default)
    {
        var mapping = CanvasMapping.Create(document, SceneCanvasRoots.Resolve(document, element.Id));
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
        ApplyRectangleHits(ScreenRectangleHits(PickingDocument, PickingAssets, SelectionRenderer,
            canvasView.Matrix.Inverse().TransformBounds(rectangle), rootCanvas: PickingRootCanvas), additive, Session.Selection.ToArray());
    }

    private void ApplyRectangleHits(IEnumerable<Guid> hits, bool additive, IEnumerable<Guid> previous)
    {
        var selected = additive ? new HashSet<Guid>(previous) : [];
        foreach (var id in hits)
            if (ResolveHitSelectableNode(id) is { } node) selected.Add(node);
        Session.SetSelection(selected);
    }

    private static IReadOnlyList<Guid> ScreenRectangleHits(SceneDocument document, AssetLibrary assets, SceneRenderer renderer,
        RectD rectangle, CancellationToken cancellationToken = default, RootCanvasKind rootCanvas = RootCanvasKind.Main)
    {
        var mapping = CanvasMapping.Create(document, rootCanvas);
        var selected = new List<Guid>();
        foreach (var element in document.Elements.Where(e => IsShownInkSelectable(document, e)))
        {
            if (SceneCanvasRoots.Resolve(document, element.Id) != rootCanvas) continue;
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
            bool intersects = element.Kind == ElementKind.Image || ElementGeometry.HasFlatHighlighterTip(element) ||
                ProjectedContourIntersectsRectangle(local, transform, rectangle, closed);
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
