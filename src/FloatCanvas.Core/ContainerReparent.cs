using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Core;

internal static class ContainerReparent
{
    internal static SceneGraph? Prepare(SceneDocument document, SceneGraph graph, IReadOnlyList<Guid> nodes, Guid? parent, int index)
    {
        var target = graph.ScopeContainerPlacement(parent);
        // Ordinary placement already compensates planar ancestors. Nested cards also
        // carry a flattened mapping: equal outer depth owners alone are insufficient,
        // particularly for upright members whose height uses a separate scale.
        if (nodes.All(id =>
        {
            var source = graph.ScopeContainerPlacement(graph.ParentId(id));
            return source.Surface == target.Surface && source.FlatPrefix == target.FlatPrefix;
        })) return null;
        var sourceLeaves = SceneEvaluation.ResolveHostNodes(document, nodes, graph);
        var sourceElements = sourceLeaves.ToDictionary(leaf => leaf.RuntimeElementId);
        var candidate = document.CreatePlacementDraft(nodes.ToHashSet());
        var next = new SceneGraph(candidate);
        next.ReparentPreservingWorld(nodes, parent, index, preserveContainerAppearance: false);
        var root = SceneCanvasRoots.Resolve(document, nodes[0], graph);
        var mapping = CanvasMapping.Create(document, root);
        var basis = target.Surface is { } surface
            ? surface.ToTarget(mapping.ToScreen(surface.Anchor), Depth(mapping, surface.Anchor)).Compose(target.FlatPrefix)
            : mapping.PlaneToScreen;
        var localMapping = target.Surface is null ? mapping : CanvasMapping.Create(new SceneDocument());
        foreach (var id in nodes)
        {
            if (graph.FindElement(id) is { } source)
            {
                var rendered = sourceElements[id].RenderElement;
                var screen = mapping.ElementToScreen(rendered);
                if (target.Surface is not null) screen = basis.Inverse().Compose(screen);
                var converted = source.Clone();
                if (source.IsBillboard)
                {
                    if (!BillboardGeometry.TryResolveScreenBottom(converted, localMapping, screen, out var pose, out _)) throw Rejected();
                    converted.Transform = pose.Linear; converted.ProjectivePlacement = null;
                    converted.BillboardAnchor = pose.Anchor; converted.BillboardHeight = pose.Height;
                }
                else
                {
                    converted.Transform = Affine2D.Identity;
                    converted.ProjectivePlacement = localMapping.ScreenToPlane.Compose(screen);
                }
                SceneGraph.ApplyPlacement(converted, target.Placement.Inverse());
                EditorSession.CopyPlacement(next.FindElement(id)!, converted);
            }
            else
            {
                // Containers cannot carry an arbitrary projective compensation. Keep shared
                // definitions untouched and reject if an exact affine placement is unavailable.
                var oldGroup = graph.FindGroup(id); var oldInstance = graph.FindInstance(id);
                var oldTransform = oldGroup?.Transform ?? oldInstance!.Transform;
                double elevation = oldGroup?.ElevationScale ?? oldInstance!.ElevationScale;
                var card = oldGroup?.Billboard ?? oldInstance?.Billboard;
                var oldContext = graph.ScopeContainerPlacement(graph.ParentId(id)).Enter(oldTransform, elevation, card);
                Affine2D affine;
                ContainerBillboard? newCard = card;
                if (card is not null)
                {
                    var desired = oldContext.PlaneToTarget(mapping.PlaneToScreen);
                    if (target.Surface is not null) desired = basis.Inverse().Compose(desired);
                    // Use a symmetric probe around the stable reference; the content itself
                    // is validated below, including all shared occurrences and their strokes.
                    var reference = card.ReferencePoint;
                    var probe = new CanvasElement { Kind = ElementKind.Rectangle, Width = 2, Height = 2 };
                    probe.Style.StrokeWidth = 0;
                    var probeToScreen = desired.Compose(Projective2D.FromAffine(Affine2D.Translation(reference.X - 1, reference.Y - 1)));
                    var screenReference = desired.TransformPoint(reference);
                    var localBounds = ContainerBillboardGeometry.LocalBounds(document, id);
                    var ground = new PointD(screenReference.X, desired.TransformBounds(localBounds).Bottom);
                    if (!BillboardGeometry.TryResolveScreenPlacement(probe, localMapping, probeToScreen, ground, out var pose, out _)) throw Rejected();
                    var located = target.Placement.Inverse().Compose(new(pose.Linear with { OffsetX = pose.Anchor.X, OffsetY = pose.Anchor.Y }, 1));
                    affine = located.PlaneTransform; elevation = located.ElevationScale;
                    newCard = card with { Height = pose.Height };
                }
                else
                {
                    var desired = target.PlaneToTarget(mapping.PlaneToScreen).Inverse().Compose(oldContext.PlaneToTarget(mapping.PlaneToScreen));
                    if (!TryAffine(desired, out affine)) throw Rejected();
                    elevation = next.FindGroup(id)?.ElevationScale ?? next.FindInstance(id)!.ElevationScale;
                }
                if (next.FindGroup(id) is { } group) { group.Transform = affine; group.ElevationScale = elevation; group.Billboard = newCard; }
                else { var instance = next.FindInstance(id)!; instance.Transform = affine; instance.ElevationScale = elevation; instance.Billboard = newCard; }
            }
        }
        next = new(candidate);
        var final = SceneEvaluation.ResolveHostNodes(candidate, nodes, next).ToDictionary(leaf => leaf.OccurrencePath);
        foreach (var before in sourceLeaves)
        {
                if (!final.TryGetValue(before.OccurrencePath, out var after) || !mapping.IsElementSafe(after.RenderElement)) throw Rejected();
                var oldMatrix = mapping.ElementToScreen(before.RenderElement); var newMatrix = mapping.ElementToScreen(after.RenderElement);
                var bounds = ElementGeometry.StrokedLocalBounds(before.RenderElement);
                PointD[] samples = [new(bounds.Left, bounds.Top), new(bounds.Right, bounds.Top), new(bounds.Right, bounds.Bottom), new(bounds.Left, bounds.Bottom), bounds.Center];
                if (samples.Any(point => !oldMatrix.TryTransformPoint(point, out var a) || !newMatrix.TryTransformPoint(point, out var b) || a.DistanceTo(b) > .01)) throw Rejected();
        }
        return next;
    }
    private static double Depth(CanvasMapping mapping, PointD anchor) => mapping.TryGetDepthScale(anchor, out double scale) ? scale : throw Rejected();
    private static InvalidOperationException Rejected() => new(L.Get("Billboard.UnsafeMove"));
    internal static bool TryAffine(Projective2D matrix, out Affine2D affine)
    {
        affine = default;
        if (!matrix.IsFinite || Math.Abs(matrix.M33) < 1e-12 || Math.Abs(matrix.M31 / matrix.M33) > 1e-12 || Math.Abs(matrix.M32 / matrix.M33) > 1e-12) return false;
        double k = 1 / matrix.M33;
        affine = new(matrix.M11 * k, matrix.M12 * k, matrix.M21 * k, matrix.M22 * k, matrix.M13 * k, matrix.M23 * k);
        return LayerPlacement.Finite(affine) && affine.TryInverse(out _);
    }
}
