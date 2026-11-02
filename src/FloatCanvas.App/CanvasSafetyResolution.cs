using FloatCanvas.Core;

namespace FloatCanvas.App;

internal enum CanvasSafetyAction { Cancel, Relocate, Delete, Keep }

/// <summary>Resolves whole editing units without changing geometry, styles or group membership.</summary>
internal static class CanvasSafetyResolution
{
    internal static IReadOnlyList<Guid> AffectedIds(SceneDocument document)
    {
        var mapping = CanvasMapping.Create(document);
        var graph = new SceneGraph(document);
        var affectedRoots = SceneEvaluation.Build(document).Leaves.Where(leaf => leaf.RootCanvas == RootCanvasKind.Main && !mapping.IsElementSafe(leaf.RenderElement))
            .Select(leaf => RootNode(graph, leaf.HostNodeId)).ToHashSet();
        return document.Elements.Select(element => element.Id).Concat(document.Instances.Select(instance => instance.Id))
            .Where(id => affectedRoots.Contains(RootNode(graph, id))).ToArray();
    }

    internal static IReadOnlyList<Guid> RootNodes(SceneDocument document, IEnumerable<Guid> elementIds)
    {
        var graph = new SceneGraph(document);
        return elementIds.Select(id => RootNode(graph, id)).Distinct().ToArray();
    }

    private static Guid RootNode(SceneGraph graph, Guid id) => graph.AncestorIds(id).LastOrDefault() is var root && root != Guid.Empty ? root : id;

    internal static bool TryRelocate(SceneDocument document, IReadOnlyList<Guid> affectedIds)
    {
        var mapping = CanvasMapping.Create(document);
        if (mapping.SafeScreenPolygon.Count < 3) return false;
        var graph = new SceneGraph(document);
        var affected = affectedIds.Select(id => RootNode(graph, id)).ToHashSet();
        var units = SceneEvaluation.Build(document).Leaves
            .Where(leaf => leaf.RootCanvas == RootCanvasKind.Main && affected.Contains(RootNode(graph, leaf.HostNodeId))).GroupBy(leaf => RootNode(graph, leaf.HostNodeId)).ToArray();
        var replacements = new Dictionary<Guid, CanvasElement>();
        var groupMoves = new Dictionary<Guid, Affine2D>();
        var reference = document.Canvas.Projection;
        var referenceCenter = mapping.ToScreen(CanvasProjectionGeometry.ResolvePlaneBounds(reference).Center);
        var safeCenter = mapping.SafeScreenPolygon.Aggregate(new PointD(), (sum, p) => sum + p) * (1.0 / mapping.SafeScreenPolygon.Count);
        int index = 0;
        foreach (var unit in units)
        {
            var originals = unit.Select(leaf => leaf.RenderElement).ToArray();
            var support = originals.Select(e => e.IsBillboard
                ? new RectD(e.BillboardAnchor.X, e.BillboardAnchor.Y, 0, 0)
                : ElementGeometry.Bounds(e)).ToArray();
            var center = new PointD((support.Min(b => b.Left) + support.Max(b => b.Right)) / 2,
                (support.Min(b => b.Top) + support.Max(b => b.Bottom)) / 2);
            CanvasElement[]? relocated = null;
            PointD translation = default;
            double minimumVisibleSize = Math.Min(12, originals.Max(e =>
            { var bounds = ElementGeometry.StrokedLocalBounds(e); return Math.Max(bounds.Width, bounds.Height); }));
            var stagger = new PointD((index % 5) * 28, (index / 5 % 5) * 28);
            foreach (var target in Targets(referenceCenter + stagger, safeCenter + stagger, document.Viewport))
            {
                if (!mapping.TryToPlane(target, out var targetPlane)) continue;
                var delta = targetPlane - center;
                var candidates = originals.Select(e => e.Clone()).ToArray();
                foreach (var candidate in candidates) BillboardGeometry.ApplyPlaneTransform(candidate, Affine2D.Translation(delta.X, delta.Y));
                if (!candidates.All(e => mapping.IsElementSafe(e) && HasInteriorMargin(mapping, e))) continue;
                var bounds = candidates.Select(e => mapping.ProjectedBounds(e)).Aggregate(RectD.Union);
                var visible = bounds.Intersect(document.Viewport);
                // Choose a useful interior position, rather than stranding content on the horizon.
                if (visible.IsEmpty || Math.Max(visible.Width, visible.Height) < minimumVisibleSize) continue;
                relocated = candidates; translation = delta; break;
            }
            if (relocated is null) return false;
            if (graph.FindGroup(unit.Key) is { } group)
                groupMoves.Add(group.Id, Affine2D.Translation(translation.X, translation.Y).Compose(group.Transform));
            else if (graph.FindInstance(unit.Key) is { } instance)
                groupMoves.Add(instance.Id, Affine2D.Translation(translation.X, translation.Y).Compose(instance.Transform));
            else foreach (var element in relocated) replacements.Add(element.Id, element);
            index++;
        }
        // Nothing is changed until every unit has a valid destination.
        foreach (var element in document.Elements)
            if (replacements.TryGetValue(element.Id, out var replacement))
            { element.Transform = replacement.Transform; element.BillboardAnchor = replacement.BillboardAnchor; }
        foreach (var group in document.Groups)
            if (groupMoves.TryGetValue(group.Id, out var transform)) group.Transform = transform;
        foreach (var instance in document.Instances)
            if (groupMoves.TryGetValue(instance.Id, out var transform)) instance.Transform = transform;
        return true;
    }

    private static bool HasInteriorMargin(CanvasMapping mapping, CanvasElement element)
    {
        var inverse = mapping.ScreenToPlane;
        double normal = Math.Sqrt(inverse.M31 * inverse.M31 + inverse.M32 * inverse.M32);
        if (normal < 1e-15) return true;
        if (element.IsBillboard) return inverse.Denominator(mapping.ToScreen(element.BillboardAnchor)) / normal >= 32;
        var bounds = mapping.ProjectedBounds(element);
        PointD[] corners = [new(bounds.Left, bounds.Top), new(bounds.Right, bounds.Top), new(bounds.Right, bounds.Bottom), new(bounds.Left, bounds.Bottom)];
        return corners.All(p => inverse.Denominator(p) / normal >= 32);
    }

    private static IEnumerable<PointD> Targets(PointD referenceCenter, PointD safeCenter, RectD viewport)
    {
        yield return referenceCenter; yield return safeCenter;
        foreach (double fraction in new[] { .5, .35, .65, .2, .8 })
            foreach (double other in new[] { .5, .35, .65, .2, .8 })
                yield return new(viewport.Left + viewport.Width * fraction, viewport.Top + viewport.Height * other);
    }
}
