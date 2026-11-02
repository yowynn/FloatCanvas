using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Core;

/// <summary>Original local placements for one transform gesture. Geometry remains in the source graph.</summary>
public sealed class SelectionTransformSnapshot
{
    internal Dictionary<Guid, CanvasElement> Elements { get; } = [];
    internal Dictionary<Guid, CanvasGroup> Groups { get; } = [];
    internal Dictionary<Guid, PrefabInstance> Instances { get; } = [];
    internal Dictionary<Guid, PointD> Centers { get; } = [];
    internal PointD CommonCenter { get; set; }
    internal Guid[] Ids => Elements.Keys.Concat(Groups.Keys).Concat(Instances.Keys).ToArray();
}

public sealed partial class EditorSession
{
    private Guid? editScopeId;
    private Guid[] editScopeAncestors = [];
    public Guid? EditScopeId => editScopeId;
    public long EditScopeVersion { get; private set; }
    public bool CanInsertInEditScope => !IsReadOnly && (editScopeId is not { } scope || !IsNodeLocked(scope));
    public void RequireWritableScope()
    {
        if (IsReadOnly) throw new InvalidOperationException(L.Get("GroupCanvas.ReadOnly"));
        EnsureWritableParent(editScopeId);
    }
    public IReadOnlySet<Guid> SelectedNodeIds => Selection;
    public bool TransformIndividually { get; set; }
    public IReadOnlyList<CanvasGroup> EditScopePath
    {
        get
        {
            if (editScopeId is not { } scope) return [];
            var graph = new SceneGraph(Document);
            return graph.AncestorIds(scope).Reverse().Append(scope).Select(id => graph.FindGroup(id)!).ToArray();
        }
    }

    public void EnterScope(Guid groupId) => SetEditScope(groupId);
    public void LeaveScope()
    {
        if (editScopeId is not { } scope) return;
        SetEditScope(new SceneGraph(Document).ParentId(scope));
    }
    public void SetEditScope(Guid? groupId)
    {
        if (IsTransactionActive) Cancel();
        var graph = new SceneGraph(Document);
        if (groupId is { } id && (graph.FindGroup(id) is null || !IsReadOnly && graph.IsLocked(id))) throw new InvalidOperationException(L.Get("Inspector.LayerMissing"));
        if (editScopeId == groupId) return;
        if (groupId is { } rootScope) ActiveRootCanvas = SceneCanvasRoots.Resolve(Document, rootScope, graph);
        editScopeId = groupId;
        EditScopeVersion++;
        editScopeAncestors = groupId is { } value ? graph.AncestorIds(value).ToArray() : [];
        Selection.Clear(); OnChanged();
    }
    private void ValidateEditScope()
    {
        if (editScopeId is not { } scope) return;
        var parents = Document.Groups.ToDictionary(group => group.Id, group => group.ParentId);
        if (parents.ContainsKey(scope) && SceneCanvasRoots.Resolve(Document, scope) == ActiveRootCanvas)
        {
            var ancestors = new List<Guid>(); var visited = new HashSet<Guid>(); Guid? parent = parents[scope];
            while (parent is { } ancestorId)
            {
                if (!visited.Add(ancestorId) || ancestors.Count >= SceneLimits.MaximumGroupDepth || !parents.TryGetValue(ancestorId, out var next)) throw new InvalidDataException(L.Get("Scene.InvalidElementId"));
                ancestors.Add(ancestorId); parent = next;
            }
            editScopeAncestors = ancestors.ToArray(); return;
        }
        var existing = Document.Groups.Select(group => group.Id).ToHashSet();
        editScopeId = editScopeAncestors.Where(id => existing.Contains(id) && SceneCanvasRoots.Resolve(Document, id) == ActiveRootCanvas).Cast<Guid?>().FirstOrDefault();
        EditScopeVersion++;
        editScopeAncestors = editScopeId is { } id ? new SceneGraph(Document).AncestorIds(id).ToArray() : [];
    }
    private HashSet<Guid> ExpandedSelectionIds(SceneGraph graph, bool editableOnly = false) => graph.NormalizeSelection(Selection)
        .Where(id => !editableOnly || !graph.IsLocked(id))
        .SelectMany(graph.DescendantElementIds).ToHashSet();
    public IReadOnlySet<Guid> SelectedElementIds() => ExpandedSelectionIds(new SceneGraph(Document));
    public IEnumerable<CanvasElement> SelectedWorldElements(bool editableOnly = false)
    {
        var graph = new SceneGraph(Document);
        if (Document.Instances.Count > 0)
        {
            var evaluated = SceneEvaluation.Build(Document);
            return graph.NormalizeSelection(Selection).Where(id => !editableOnly || !graph.IsLocked(id))
                .SelectMany(evaluated.OccurrencesOfHostNode).Select(leaf => leaf.RenderElement.Clone()).ToArray();
        }
        var ids = ExpandedSelectionIds(graph, editableOnly);
        return graph.OrderedLeafIds.Where(ids.Contains).Select(id => graph.ToWorld(graph.FindElement(id)!));
    }
    public bool IsNodeVisible(Guid id, bool inherited = true)
    {
        var graph = new SceneGraph(Document);
        return inherited ? graph.IsVisible(id) : graph.FindGroup(id)?.Visible ?? graph.FindInstance(id)?.Visible ?? graph.FindElement(id)?.Visible ?? false;
    }
    public bool IsNodeLocked(Guid id, bool inherited = true)
    {
        var graph = new SceneGraph(Document);
        return inherited ? graph.IsLocked(id) : graph.FindGroup(id)?.Locked ?? graph.FindInstance(id)?.Locked ?? graph.FindElement(id)?.Locked ?? false;
    }
    public RectD? NodeBounds(Guid id)
    {
        var graph = new SceneGraph(Document);
        return NodeBounds(graph, id, Document.Instances.Count > 0 ? SceneEvaluation.Build(Document) : null);
    }
    private static RectD? NodeBounds(SceneGraph graph, Guid id, EvaluatedScene? evaluated = null)
    {
        if (evaluated is not null)
        {
            var occurrences = evaluated.OccurrencesOfHostNode(id);
            if (occurrences.Count > 0) return occurrences.Select(leaf => WorldBounds(leaf.RenderElement)).Aggregate(RectD.Union);
            var point = graph.NodeWorldPlacement(id).PlaneTransform.TransformPoint(default); return new(point.X, point.Y, 0, 0);
        }
        if (graph.FindElement(id) is { } element) return WorldBounds(graph.ToWorld(element));
        var leaves = graph.DescendantElementIds(id).Select(leaf => graph.ToWorld(graph.FindElement(leaf)!)).ToArray();
        return leaves.Length == 0 ? graph.FindGroup(id) is not null
            ? new RectD(graph.NodeWorldPlacement(id).PlaneTransform.OffsetX, graph.NodeWorldPlacement(id).PlaneTransform.OffsetY, 0, 0) : null
            : leaves.Select(WorldBounds).Aggregate(RectD.Union);
    }
    private static RectD WorldBounds(CanvasElement element) => element.IsBillboard
        ? BillboardGeometry.ElementToTarget(element, Projective2D.Identity).TransformBounds(ElementGeometry.StrokedLocalBounds(element))
        : ElementGeometry.Bounds(element);
    public void SetNodeVisible(Guid id, bool value) => Execute(L.Get(value ? "History.ShowElements" : "History.HideElements"), _ => SetNodeFlag(id, value, locking: false));
    public void SetNodeLocked(Guid id, bool value) => Execute(L.Get(value ? "History.LockElements" : "History.UnlockElements"), _ => SetNodeFlag(id, value, locking: true));
    private void SetNodeFlag(Guid id, bool value, bool locking)
    {
        var graph = new SceneGraph(Document);
        if (graph.FindGroup(id) is { } group) { if (locking) group.Locked = value; else group.Visible = value; }
        else if (graph.FindInstance(id) is { } instance) { if (locking) instance.Locked = value; else instance.Visible = value; }
        else if (graph.FindElement(id) is { } element) { if (locking) element.Locked = value; else element.Visible = value; }
    }
    public Guid CreateEmptyGroup(string? name = null)
    {
        EnsureWritableParent(EditScopeId);
        var group = new CanvasGroup { Name = NormalizeInsertionName(name) ?? NextGroupName(), OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] };
        Execute(L.Get("History.GroupElements"), document =>
        {
            new SceneGraph(document).InsertGroup(group, EditScopeId, rootCanvas: ActiveRootCanvas); SetSelectionCore([group.Id]);
        });
        return group.Id;
    }
    public void CollectLegacyGroup(Guid groupId) => Execute(L.Get("History.ReorderElements"), document => new SceneGraph(document).CollectLegacyGroup(groupId));
    public void MoveNodes(IEnumerable<Guid> ids, Guid? parentId, Guid? beforeNodeId = null)
    {
        EnsureWritableParent(parentId);
        var requested = ids.ToArray();
        var initial = new SceneGraph(Document); var normalized = initial.NormalizeSelection(requested).Where(id => !initial.IsLocked(id)).ToArray();
        var destinationRoot = parentId is { } destination ? SceneCanvasRoots.Resolve(Document, destination, initial) : ActiveRootCanvas;
        if (normalized.Any(id => SceneCanvasRoots.Resolve(Document, id, initial) != destinationRoot))
            throw new InvalidOperationException("Use an explicit root canvas transfer to move between coordinate spaces.");
        if (beforeNodeId is { } beforeTarget && SceneCanvasRoots.Resolve(Document, beforeTarget, initial) != destinationRoot)
            throw new InvalidOperationException("The destination slot belongs to another root canvas.");
        if (normalized.Length == 0 || beforeNodeId is { } own && normalized.Contains(own)) return;
        if (normalized.All(id => initial.ParentId(id) == parentId && initial.FindGroup(id)?.OrderMode != GroupOrderMode.LegacyPassThrough))
        {
            var context = ScopePaintContext(initial, parentId); var previous = initial.PaintOrder(context);
            var moving = normalized.ToHashSet(); var block = previous.Where(moving.Contains).ToArray();
            var next = previous.Where(id => !moving.Contains(id)).ToList();
            int insertion = beforeNodeId is { } before ? next.IndexOf(before) : next.Count;
            next.InsertRange(insertion < 0 ? next.Count : insertion, block);
            if (previous.SequenceEqual(next)) return;
        }
        Execute(L.Get("History.ReorderElements"), document =>
        {
            var graph = new SceneGraph(document);
            var selected = graph.NormalizeSelection(requested).Where(id => !graph.IsLocked(id)).ToArray();
            if (selected.Length == 0 || beforeNodeId is { } before && selected.Contains(before)) return;
            foreach (var id in selected)
                if (graph.FindGroup(id)?.OrderMode == GroupOrderMode.LegacyPassThrough) graph.CollectLegacyGroup(id);
            graph = new(document);
            Guid? context = ScopePaintContext(graph, parentId);
            var order = graph.PaintOrder(context);
            int index = -1;
            if (beforeNodeId is { } target)
            {
                var targetTokens = graph.DescendantElementIds(target).Append(target).ToHashSet();
                index = Enumerable.Range(0, order.Count).FirstOrDefault(i => targetTokens.Contains(order[i]), -1);
                var moving = selected.ToHashSet();
                if (index >= 0) index -= order.Take(index).Count(token => moving.Contains(token) || graph.AncestorIds(token).Any(moving.Contains));
            }
            graph.ReparentPreservingWorld(selected, parentId, index);
            SynchronizeElementOrder(document); SetSelectionCore(selected);
        });
    }
    private void EnsureWritableParent(Guid? parentId)
    {
        if (IsReadOnly) throw new InvalidOperationException(L.Get("GroupCanvas.ReadOnly"));
        if (parentId is { } id && new SceneGraph(Document).IsLocked(id)) throw new InvalidOperationException(L.Get("Inspector.LockedSuffix"));
    }
    private static Guid? ScopePaintContext(SceneGraph graph, Guid? scope) => scope is not { } id ? null :
        graph.FindGroup(id)!.OrderMode == GroupOrderMode.AtomicBlock ? id : graph.PaintContext(id);
    private static void SynchronizeElementOrder(SceneDocument document)
    {
        var graph = new SceneGraph(document);
        document.Elements = graph.OrderedLeafIds.Select(id => graph.FindElement(id)!).ToList();
    }
    private void ReorderNodes(SceneDocument document, LayerOrder direction)
    {
        var graph = new SceneGraph(document);
        var selected = graph.NormalizeSelection(Selection).Where(id => !graph.IsLocked(id)).ToArray();
        if (selected.Length == 0) return;
        foreach (var id in selected)
            if (graph.FindGroup(id)?.OrderMode == GroupOrderMode.LegacyPassThrough) graph.CollectLegacyGroup(id);
        graph.MaterializeOrder(); graph = new(document);
        foreach (var bucket in selected.GroupBy(graph.PaintContext))
        {
            var order = bucket.Key is { } parent ? graph.FindGroup(parent)!.ChildOrder! : document.RootOrder!;
            var moving = bucket.ToHashSet();
            ReorderRootTokens(document, graph, order, moving, direction);
        }
        SynchronizeElementOrder(document);
    }
    private bool WouldReorder(LayerOrder direction)
    {
        var graph = new SceneGraph(Document); var selected = graph.NormalizeSelection(Selection).Where(id => !graph.IsLocked(id)).ToArray();
        if (selected.Any(id => graph.FindGroup(id)?.OrderMode == GroupOrderMode.LegacyPassThrough)) return true;
        foreach (var bucket in selected.GroupBy(graph.PaintContext))
        {
            var before = graph.PaintOrder(bucket.Key); var after = before.ToList();
            ReorderRootTokens(Document, graph, after, bucket.ToHashSet(), direction);
            if (!before.SequenceEqual(after)) return true;
        }
        return false;
    }
    private static void ReorderTokens(List<Guid> order, HashSet<Guid> moving, LayerOrder direction)
    {
        if (direction is LayerOrder.Front or LayerOrder.Back)
        {
            var block = order.Where(moving.Contains).ToArray(); order.RemoveAll(moving.Contains);
            order.InsertRange(direction == LayerOrder.Front ? order.Count : 0, block);
        }
        else if (direction == LayerOrder.Forward)
        {
            for (int i = order.Count - 2; i >= 0; i--) if (moving.Contains(order[i]) && !moving.Contains(order[i + 1])) (order[i], order[i + 1]) = (order[i + 1], order[i]);
        }
        else for (int i = 1; i < order.Count; i++) if (moving.Contains(order[i]) && !moving.Contains(order[i - 1])) (order[i], order[i - 1]) = (order[i - 1], order[i]);
    }

    private static void ReorderRootTokens(SceneDocument document, SceneGraph graph, List<Guid> order, HashSet<Guid> moving, LayerOrder direction)
    {
        if (moving.Count == 0) return;
        var root = SceneCanvasRoots.Resolve(document, moving.First(), graph);
        var slots = Enumerable.Range(0, order.Count).Where(index => SceneCanvasRoots.Resolve(document, order[index], graph) == root).ToArray();
        var sameRoot = slots.Select(index => order[index]).ToList();
        ReorderTokens(sameRoot, moving, direction);
        for (int index = 0; index < slots.Length; index++) order[slots[index]] = sameRoot[index];
    }

    public SelectionTransformSnapshot CaptureSelectionTransform()
    {
        var result = new SelectionTransformSnapshot(); var graph = new SceneGraph(Document);
        var evaluated = Document.Instances.Count > 0 ? SceneEvaluation.Build(Document) : null;
        RectD? combined = null;
        foreach (var id in graph.NormalizeSelection(Selection).Where(id => !graph.IsLocked(id)))
        {
            if (graph.FindGroup(id) is { } group) result.Groups.Add(id, group.Clone());
            else if (graph.FindInstance(id) is { } instance) result.Instances.Add(id, instance.Clone());
            else result.Elements.Add(id, graph.FindElement(id)!.Clone());
            var bounds = NodeBounds(graph, id, evaluated);
            result.Centers[id] = bounds?.Center ?? default;
            if (bounds is { } value) combined = combined is { } previous ? RectD.Union(previous, value) : value;
        }
        result.CommonCenter = combined?.Center ?? default;
        return result;
    }
    private SelectionTransformSnapshot CapturePlacements(IEnumerable<Guid> ids)
    {
        var result = new SelectionTransformSnapshot(); var graph = new SceneGraph(Document);
        foreach (var id in ids)
            if (graph.FindGroup(id) is { } group) result.Groups.Add(id, group.Clone());
            else if (graph.FindInstance(id) is { } instance) result.Instances.Add(id, instance.Clone());
            else if (graph.FindElement(id) is { } element) result.Elements.Add(id, element.Clone());
        return result;
    }
    private void RestorePlacements(SelectionTransformSnapshot state)
    {
        // A rejected candidate can be non-finite. Restore by entity ID before rebuilding a graph.
        var groups = Document.Groups.ToDictionary(group => group.Id);
        var elements = Document.Elements.ToDictionary(element => element.Id);
        var instances = Document.Instances.ToDictionary(instance => instance.Id);
        foreach (var (id, saved) in state.Groups)
            if (groups.TryGetValue(id, out var group)) { group.Transform = saved.Transform; group.ElevationScale = saved.ElevationScale; }
        foreach (var (id, saved) in state.Elements)
            if (elements.TryGetValue(id, out var element)) CopyPlacement(element, saved);
        foreach (var (id, saved) in state.Instances)
            if (instances.TryGetValue(id, out var instance)) { instance.Transform = saved.Transform; instance.ElevationScale = saved.ElevationScale; }
    }
    public static void CopyPlacement(CanvasElement target, CanvasElement source)
    {
        target.Transform = source.Transform; target.ProjectivePlacement = source.ProjectivePlacement;
        target.IsBillboard = source.IsBillboard; target.BillboardAnchor = source.BillboardAnchor; target.BillboardHeight = source.BillboardHeight;
    }
    public bool TryApplySelectionTransform(SelectionTransformSnapshot original, Affine2D delta, double elevationScale = 1, bool? individually = null,
        CanvasMapping? displayMapping = null)
    {
        if (IsReadOnly) return false;
        var displacement = new LayerPlacement(delta, elevationScale);
        if (!displacement.IsValid) return false;
        var current = CapturePlacements(original.Ids);
        try
        {
            RestorePlacements(original); var graph = new SceneGraph(Document);
            var ids = original.Ids.ToHashSet();
            foreach (var id in displacement == LayerPlacement.Identity ? Array.Empty<Guid>() : original.Ids)
            {
                var operation = displacement;
                if (individually ?? TransformIndividually)
                {
                    var linear = delta with { OffsetX = 0, OffsetY = 0 };
                    var shift = delta.TransformPoint(original.CommonCenter) - original.CommonCenter;
                    operation = displacement with { PlaneTransform = Affine2D.Translation(shift.X, shift.Y).Compose(Affine2D.Around(original.Centers.GetValueOrDefault(id), linear)) };
                }
                var parent = graph.ParentWorldPlacement(id);
                if (graph.FindGroup(id) is { } group)
                {
                    var local = parent.Inverse().Compose(operation).Compose(parent).Compose(new(group.Transform, group.ElevationScale));
                    group.Transform = local.PlaneTransform; group.ElevationScale = local.ElevationScale;
                }
                else if (graph.FindInstance(id) is { } instance)
                {
                    var local = parent.Inverse().Compose(operation).Compose(parent).Compose(new(instance.Transform, instance.ElevationScale));
                    instance.Transform = local.PlaneTransform; instance.ElevationScale = local.ElevationScale;
                }
                else
                {
                    var element = graph.FindElement(id)!; var world = graph.ToWorld(element);
                    SceneGraph.ApplyPlacement(world, operation); CopyPlacement(element, graph.ToLocal(world, element.GroupId));
                }
            }
            graph = new(Document); var mapping = displayMapping ?? CanvasMapping.Create(Document, ActiveRootCanvas);
            var worlds = Document.Instances.Count > 0
                ? SceneEvaluation.Build(Document).Leaves.Where(leaf => ids.Contains(leaf.HostNodeId) || graph.AncestorIds(leaf.HostNodeId).Any(ids.Contains)).Select(leaf => leaf.RenderElement)
                : graph.OrderedLeafIds.Where(id => ids.Contains(id) || graph.AncestorIds(id).Any(ids.Contains)).Select(id => graph.ToWorld(graph.FindElement(id)!));
            foreach (var world in worlds)
            {
                if (!LayerPlacement.Finite(world.Transform) || !world.Transform.TryInverse(out _) || !double.IsFinite(world.BillboardHeight) ||
                    (displayMapping is not null || ActiveRootCanvas == RootCanvasKind.Main && Document.Canvas.Projection.Enabled) && !mapping.IsElementSafe(world)) throw new InvalidOperationException();
            }
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or ArgumentException)
        { RestorePlacements(current); return false; }
        OnChanged(contentChanged: true); return true;
    }
}
