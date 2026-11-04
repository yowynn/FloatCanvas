namespace FloatCanvas.Core;

public sealed record CanvasRebaseResult(SceneNodeGraph Graph, SubprojectCanvasSettings Canvas);

/// <summary>Prepares an owned graph's real content and calibration together. The caller
/// publishes the candidate atomically and retains responsibility for definitions, permissions and history.</summary>
public static class CanvasRebase
{
    public static CanvasRebaseResult Prepare(SceneNodeGraph source, SubprojectCanvasSettings header, CanvasViewTransform view)
    {
        var graph = SceneGraphClone.CloneGraph(source).Graph; var canvas = header.Clone();
        TranslatePlane(graph, canvas, view.PlaneOffset, new SceneGraph(graph).EnumerateChildren(null));
        return Prepare(graph, canvas, view.Matrix);
    }

    internal static void TranslatePlane(SceneNodeGraph graph, SubprojectCanvasSettings header, PointD offset, IEnumerable<Guid> roots)
    {
        if (!header.Canvas.Projection.Enabled || offset == default) return;
        var translation = new LayerPlacement(Affine2D.Translation(offset.X, offset.Y), 1);
        if (!translation.IsValid) throw new ArgumentException("Invalid plane translation.", nameof(offset));
        var tree = new SceneGraph(graph);
        foreach (var id in roots)
        {
            if (tree.FindElement(id) is { } element) SceneGraph.ApplyPlacement(element, translation);
            else if (tree.FindGroup(id) is { } group) group.Transform = translation.PlaneTransform.Compose(group.Transform);
            else if (tree.FindInstance(id) is { } instance) instance.Transform = translation.PlaneTransform.Compose(instance.Transform);
        }
        var settings = header.Canvas.ResolveReference(header.Viewport);
        settings.Grid.PlaneOrigin = (settings.Grid.PlaneOrigin ?? settings.Projection.PinPoint) + offset;
        header.Canvas = settings;
        if (header.Background is { } background) SceneGraph.ApplyPlacement(background, translation);
    }

    internal static CanvasRebaseResult Prepare(SceneNodeGraph source, SubprojectCanvasSettings header, Affine2D transform)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(header);
        double scale = transform.M11;
        if (!LayerPlacement.Finite(transform) || scale <= 0 || transform.M22 != scale || transform.M12 != 0 || transform.M21 != 0 || !transform.TryInverse(out _))
            throw new ArgumentException("Canvas rebasing requires a finite positive uniform transform.", nameof(transform));
        var graph = SceneGraphClone.CloneGraph(source).Graph;
        var placement = new LayerPlacement(transform, scale);
        foreach (var element in graph.Elements.Where(element => element.GroupId is null)) SceneGraph.ApplyPlacement(element, placement);
        foreach (var group in graph.Groups.Where(group => group.ParentId is null))
        {
            var combined = placement.Compose(new(group.Transform, group.ElevationScale));
            group.Transform = combined.PlaneTransform; group.ElevationScale = combined.ElevationScale;
        }
        foreach (var instance in graph.Instances.Where(instance => instance.ParentId is null))
        {
            var combined = placement.Compose(new(instance.Transform, instance.ElevationScale));
            instance.Transform = combined.PlaneTransform; instance.ElevationScale = combined.ElevationScale;
        }
        return new(graph, RebaseHeader(header, transform, scale));
    }

    private static SubprojectCanvasSettings RebaseHeader(SubprojectCanvasSettings source, Affine2D c, double scale)
    {
        var result = source.Clone();
        var oldDocument = new SceneDocument(); source.ApplyTo(oldDocument);
        var oldMapping = CanvasMapping.Create(oldDocument);
        var matrix = Projective2D.FromAffine(c).Compose(oldMapping.PlaneToScreen)
            .Compose(Projective2D.FromAffine(Affine2D.Translation(-source.Origin.X, -source.Origin.Y)))
            .Compose(Projective2D.FromAffine(c.Inverse()));
        // The new header uses author coordinates directly. Fold the independent origin
        // into the complete calibration rather than losing it or applying it twice.
        result.Origin = default;
        PointD SourcePoint(PointD point) => c.TransformPoint(point + source.Origin);
        RectD SourceBounds(RectD bounds) => c.TransformBounds(new(bounds.X + source.Origin.X, bounds.Y + source.Origin.Y, bounds.Width, bounds.Height));
        var oldProjection = source.Canvas.Projection.Quad.Count == 0
            ? CanvasCalibration.CreateInitial(source.Viewport, source.Canvas.Projection.Columns, source.Canvas.Projection.Rows)
            : source.Canvas.Projection.Clone();
        var projection = result.Canvas.Projection;
        // Preserve inactive calibration too, so enabling perspective after applying a
        // flat view does not resurrect a stale source-coordinate reference.
        var bounds = SourceBounds(CanvasProjectionGeometry.ResolvePlaneBounds(oldProjection));
        projection.PlaneBounds = bounds; projection.Origin = new(bounds.X, bounds.Y);
        projection.ReferenceBounds = SourceBounds(oldProjection.ReferenceBounds ?? CanvasProjectionGeometry.ResolvePlaneBounds(oldProjection));
        projection.PinPlanePoint = SourcePoint(oldProjection.PinPoint);
        projection.CellSize = oldProjection.CellSize * scale;
        projection.LockedGridCellSize = oldProjection.LockedGridCellSize is { } locked ? locked * scale : null;
        if (source.Canvas.Projection.Enabled)
        {
            projection.Matrix = matrix;
            projection.Quad = [matrix.TransformPoint(new(bounds.Left, bounds.Top)), matrix.TransformPoint(new(bounds.Right, bounds.Top)),
                matrix.TransformPoint(new(bounds.Right, bounds.Bottom)), matrix.TransformPoint(new(bounds.Left, bounds.Bottom))];
        }
        else
        {
            projection.Matrix = Projective2D.FromAffine(c).Compose(oldProjection.Matrix)
                .Compose(Projective2D.FromAffine(Affine2D.Translation(-source.Origin.X, -source.Origin.Y)))
                .Compose(Projective2D.FromAffine(c.Inverse()));
            projection.Quad = oldProjection.Quad.Select(c.TransformPoint).ToList();
            result.Canvas.Grid.FlatTranslation = new(matrix.M13 / matrix.M33, matrix.M23 / matrix.M33);
        }
        var grid = result.Canvas.Grid;
        grid.Width *= scale;
        double cell = CanvasGridGeometry.ResolveCellSize(source.Canvas, source.Viewport);
        grid.FlatCellSize = (source.Canvas.Grid.FlatCellSize ?? cell) * scale;
        grid.PlaneCellSize = (source.Canvas.Grid.PlaneCellSize ?? cell) * scale;
        var flatPlaneOrigin = (source.Canvas.Grid.FlatOrigin ?? source.Viewport.Center) - source.Canvas.Grid.FlatTranslation;
        grid.FlatOrigin = SourcePoint(flatPlaneOrigin) + grid.FlatTranslation;
        grid.PlaneOrigin = SourcePoint(source.Canvas.Grid.PlaneOrigin ?? oldProjection.PinPoint);
        if (result.Background is { } background)
            SceneGraph.ApplyPlacement(background, new(c.Compose(Affine2D.Translation(source.Origin.X, source.Origin.Y)), scale));
        return result;
    }
}
