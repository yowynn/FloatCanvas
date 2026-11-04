namespace FloatCanvas.Core;

/// <summary>Detached authored candidate for applying a complete editor camera.
/// A null root means the composite; a specified root means its independent canvas.</summary>
public static class CanvasViewApplication
{
    public static SceneDocument Prepare(SceneDocument source, CanvasViewTransform view, RootCanvasKind? root = null, Guid? group = null)
        => Prepare(source, view.Matrix, root, group, view.PlaneOffset);

    internal static SceneDocument Prepare(SceneDocument source, Affine2D transform, RootCanvasKind? root = null, Guid? group = null, PointD planeOffset = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (group is not null && root is null) throw new ArgumentException("An entered group requires an independent canvas.", nameof(group));
        if (root is { } selectedRoot) _ = SceneCanvasRoots.PaintRank(selectedRoot);
        var sourceGraph = new SceneGraph(source);
        if (group is { } scope && (sourceGraph.FindGroup(scope) is null || SceneCanvasRoots.Resolve(source, scope, sourceGraph) != root))
            throw new ArgumentException("The group is not in the requested root canvas.", nameof(group));
        var ids = sourceGraph.EnumerateChildren(group).Where(id => group is not null || root is null || SceneCanvasRoots.Resolve(source, id, sourceGraph) == root).ToArray();
        var owned = Extract(source, ids);
        var header = group is { } groupId ? sourceGraph.FindGroup(groupId)!.PreviewCanvas?.Clone() ?? new() :
            root is null or RootCanvasKind.Main ? SubprojectCanvasSettings.FromDocument(source, root is null ? default : source.IndependentCanvasOrigin) :
                source.RootPreviewCanvases.GetValueOrDefault(root.Value)?.Clone() ?? new();
        CanvasRebase.TranslatePlane(owned, header, planeOffset,
            ids.Where(id => root is not null || SceneCanvasRoots.Resolve(source, id, sourceGraph) == RootCanvasKind.Main));
        var rebased = CanvasRebase.Prepare(owned, header, transform);
        var candidate = source.Clone();
        var target = new SceneGraph(candidate); var transformed = new SceneGraph(rebased.Graph);
        foreach (var id in ids)
        {
            if (target.FindElement(id) is { } element) EditorSession.CopyPlacement(element, transformed.FindElement(id)!);
            else if (target.FindGroup(id) is { } child)
            { var placed = transformed.FindGroup(id)!; child.Transform = placed.Transform; child.ElevationScale = placed.ElevationScale; }
            else if (target.FindInstance(id) is { } instance)
            { var placed = transformed.FindInstance(id)!; instance.Transform = placed.Transform; instance.ElevationScale = placed.ElevationScale; }
        }
        if (group is { } entered) target.FindGroup(entered)!.PreviewCanvas = rebased.Canvas;
        else if (root is null or RootCanvasKind.Main)
        { rebased.Canvas.ApplyTo(candidate); candidate.IndependentCanvasOrigin = rebased.Canvas.Origin; }
        else candidate.RootPreviewCanvases[root.Value] = rebased.Canvas;
        SceneLimits.ValidateCapacity(candidate);
        RequireSafeChange(source, candidate, root, group);
        return candidate;
    }

    private static void RequireSafeChange(SceneDocument before, SceneDocument after, RootCanvasKind? root, Guid? group)
    {
        var oldDisplay = Display(before); var newDisplay = Display(after);
        var oldScene = SceneEvaluation.Build(oldDisplay.Document).RenderDocument;
        var newScene = SceneEvaluation.Build(newDisplay.Document).RenderDocument;
        var newElements = newScene.Elements.ToDictionary(element => element.Id);
        foreach (var element in oldScene.Elements)
        {
            var oldMapping = Mapping(oldScene, element, oldDisplay.Origin);
            var changed = newElements[element.Id];
            var newMapping = Mapping(newScene, changed, newDisplay.Origin);
            if (oldMapping.IsElementSafe(element) && !newMapping.IsElementSafe(changed))
                throw new InvalidOperationException("Applying this view would move content outside the safe canvas domain.");
        }
        CanvasMapping Mapping(SceneDocument document, CanvasElement element, PointD origin) => root is null
            ? CanvasMapping.Create(document, SceneCanvasRoots.Resolve(document, element.Id))
            : CanvasMapping.CreateView(document, new(Affine2D.Translation(-origin.X, -origin.Y), 1));
        (SceneDocument Document, PointD Origin) Display(SceneDocument source)
        {
            if (root is null) return (source, default);
            var graph = new SceneGraph(source);
            var nodes = graph.EnumerateChildren(group).Where(id => group is not null || SceneCanvasRoots.Resolve(source, id, graph) == root).ToArray();
            var owned = Extract(source, nodes);
            var header = group is { } scope ? graph.FindGroup(scope)!.PreviewCanvas?.Clone() ?? new() :
                root == RootCanvasKind.Main ? SubprojectCanvasSettings.FromDocument(source, source.IndependentCanvasOrigin) :
                    source.RootPreviewCanvases.GetValueOrDefault(root.Value)?.Clone() ?? new();
            var document = new SceneDocument { ResourceId = source.ResourceId, Elements = owned.Elements, Groups = owned.Groups,
                Instances = owned.Instances, RootOrder = owned.RootOrder, Definitions = source.Definitions };
            header.ApplyTo(document); return (document, header.Origin);
        }
    }

    private static SceneNodeGraph Extract(SceneDocument source, IReadOnlyList<Guid> roots)
    {
        var graph = new SceneGraph(source);
        var retained = roots.SelectMany(graph.DescendantNodeIds).ToHashSet();
        var result = new SceneNodeGraph
        {
            Elements = source.Elements.Where(item => retained.Contains(item.Id)).Select(item => item.Clone()).ToList(),
            Groups = source.Groups.Where(item => retained.Contains(item.Id)).Select(item => item.Clone()).ToList(),
            Instances = source.Instances.Where(item => retained.Contains(item.Id)).Select(item => item.Clone()).ToList(),
            RootOrder = roots.ToList()
        };
        foreach (var item in result.Elements) if (item.GroupId is { } parent && !retained.Contains(parent)) item.GroupId = null;
        foreach (var item in result.Groups) if (item.ParentId is { } parent && !retained.Contains(parent)) item.ParentId = null;
        foreach (var item in result.Instances) if (item.ParentId is { } parent && !retained.Contains(parent)) item.ParentId = null;
        return result;
    }
}
