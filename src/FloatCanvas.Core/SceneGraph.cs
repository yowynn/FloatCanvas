using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Core;

/// <summary>Placement relative to a parent plane. Elevation is independent of in-plane rotation.</summary>
public readonly record struct LayerPlacement(Affine2D PlaneTransform, double ElevationScale)
{
    public static LayerPlacement Identity => new(Affine2D.Identity, 1);
    public LayerPlacement Compose(LayerPlacement other) => new(PlaneTransform.Compose(other.PlaneTransform), ElevationScale * other.ElevationScale);
    public bool TryInverse(out LayerPlacement inverse)
    {
        inverse = default;
        if (!IsValid || !PlaneTransform.TryInverse(out var plane)) return false;
        inverse = new(plane, 1 / ElevationScale);
        return inverse.IsValid;
    }
    public LayerPlacement Inverse() => TryInverse(out var inverse) ? inverse : throw new InvalidDataException(L.Get("Scene.InvalidElementTransform"));
    public bool IsValid => Finite(PlaneTransform) && PlaneTransform.TryInverse(out var inverse) && Finite(inverse) &&
        double.IsFinite(ElevationScale) && ElevationScale != 0 && double.IsFinite(1 / ElevationScale);
    internal static bool Finite(Affine2D value) => double.IsFinite(value.M11) && double.IsFinite(value.M12) &&
        double.IsFinite(value.M21) && double.IsFinite(value.M22) && double.IsFinite(value.OffsetX) && double.IsFinite(value.OffsetY);
}

/// <summary>One source document's ownership, paint slots and local placements. Rebuild after external mutations.</summary>
public sealed partial class SceneGraph
{
    private readonly SceneNodeGraph document;
    private Dictionary<Guid, CanvasElement> elements = [];
    private Dictionary<Guid, CanvasGroup> groups = [];
    private Dictionary<Guid, PrefabInstance> instances = [];
    private readonly Dictionary<Guid, LayerPlacement> world = [];
    private readonly Dictionary<Guid, bool> visible = [], locked = [];
    private readonly Dictionary<Guid, IReadOnlyList<Guid>> children = [];
    private readonly Dictionary<Guid, IReadOnlyList<Guid>> descendantLeaves = [];
    private Dictionary<Guid, int> paintRanks = [];
    private IReadOnlyList<Guid> rootChildren = [];
    private IReadOnlyList<Guid> orderedLeaves = [];
    private IReadOnlyList<Guid> orderedContent = [];

    public SceneGraph(SceneNodeGraph document) { this.document = document; Refresh(); }
    public SceneNodeGraph Document => document;
    public IReadOnlyList<Guid> OrderedLeafIds => orderedLeaves;
    public IReadOnlyList<Guid> OrderedContentNodeIds => orderedContent;
    public CanvasElement? FindElement(Guid id) => elements.GetValueOrDefault(id);
    public CanvasGroup? FindGroup(Guid id) => groups.GetValueOrDefault(id);
    public PrefabInstance? FindInstance(Guid id) => instances.GetValueOrDefault(id);
    public Guid? ParentId(Guid id) => groups.TryGetValue(id, out var group) ? group.ParentId :
        elements.TryGetValue(id, out var element) ? element.GroupId : instances.TryGetValue(id, out var instance) ? instance.ParentId : throw InvalidGraph();
    public IEnumerable<Guid> AncestorIds(Guid id)
    {
        var parent = ParentId(id);
        while (parent is { } value) { yield return value; parent = groups[value].ParentId; }
    }
    public bool IsAncestor(Guid ancestorId, Guid nodeId) => AncestorIds(nodeId).Contains(ancestorId);
    public IReadOnlyList<Guid> EnumerateChildren(Guid? parentId) => parentId is { } id ? children.GetValueOrDefault(id) ?? [] : rootChildren;
    /// <summary>Real ownership subtree including its root; instances are terminal source nodes.</summary>
    public IEnumerable<Guid> DescendantNodeIds(Guid nodeId)
    {
        if (!elements.ContainsKey(nodeId) && !groups.ContainsKey(nodeId) && !instances.ContainsKey(nodeId)) yield break;
        yield return nodeId;
        foreach (var child in EnumerateChildren(nodeId)) foreach (var descendant in DescendantNodeIds(child)) yield return descendant;
    }
    public IEnumerable<Guid> DescendantElementIds(Guid nodeId)
    {
        if (elements.ContainsKey(nodeId)) return [nodeId];
        if (!groups.ContainsKey(nodeId)) return [];
        if (descendantLeaves.TryGetValue(nodeId, out var cached)) return cached;
        // Ownership children can interleave in the legacy paint ledger. Visit only
        // this subtree, then sort by its real draw rank instead of scanning all leaves
        // separately for every selected node.
        var leaves = new List<Guid>();
        foreach (var child in EnumerateChildren(nodeId)) leaves.AddRange(DescendantElementIds(child));
        leaves.Sort((first, second) => paintRanks[first].CompareTo(paintRanks[second]));
        var result = leaves.AsReadOnly(); descendantLeaves.Add(nodeId, result); return result;
    }
    public IReadOnlyList<Guid> NormalizeSelection(IEnumerable<Guid> ids)
    {
        var selected = ids.Where(id => groups.ContainsKey(id) || elements.ContainsKey(id) || instances.ContainsKey(id)).ToHashSet();
        return selected.Where(id => !AncestorIds(id).Any(selected.Contains)).ToArray();
    }
    public LayerPlacement ScopeWorldPlacement(Guid? parentId) => parentId is { } id ? NodeWorldPlacement(id) : LayerPlacement.Identity;
    private readonly Dictionary<Guid, ContainerPlacementContext> containerContexts = [];
    public ContainerPlacementContext ScopeContainerPlacement(Guid? parentId)
    {
        if (parentId is not { } id) return ContainerPlacementContext.Identity;
        if (containerContexts.TryGetValue(id, out var cached)) return cached;
        var group = groups[id];
        var result = ScopeContainerPlacement(group.ParentId).Enter(group.Transform, group.ElevationScale, group.Billboard);
        containerContexts.Add(id, result); return result;
    }
    public ContainerBillboard? ContainerBillboard(Guid id) => groups.TryGetValue(id, out var group) ? group.Billboard : instances.GetValueOrDefault(id)?.Billboard;
    public LayerPlacement ParentWorldPlacement(Guid nodeId) => ScopeWorldPlacement(ParentId(nodeId));
    public LayerPlacement NodeWorldPlacement(Guid nodeId)
    {
        if (world.TryGetValue(nodeId, out var result)) return result;
        var parent = ParentWorldPlacement(nodeId);
        result = groups.TryGetValue(nodeId, out var group)
            ? parent.Compose(new(group.Billboard?.FlatPlacement(new(group.Transform, group.ElevationScale)) ?? group.Transform, group.ElevationScale))
            : instances.TryGetValue(nodeId, out var instance) ? parent.Compose(new(instance.Billboard?.FlatPlacement(new(instance.Transform, instance.ElevationScale)) ?? instance.Transform, instance.ElevationScale))
            : parent.Compose(new(elements[nodeId].Transform, 1));
        if (!result.IsValid) throw new InvalidDataException(L.Get("Scene.InvalidElementTransform"));
        world.Add(nodeId, result); return result;
    }
    public bool IsVisible(Guid nodeId)
    {
        if (visible.TryGetValue(nodeId, out bool value)) return value;
        bool own = groups.TryGetValue(nodeId, out var group) ? group.Visible : instances.TryGetValue(nodeId, out var instance) ? instance.Visible : elements[nodeId].Visible;
        value = own && (ParentId(nodeId) is not { } parent || IsVisible(parent));
        visible.Add(nodeId, value); return value;
    }
    public bool IsLocked(Guid nodeId)
    {
        if (locked.TryGetValue(nodeId, out bool value)) return value;
        bool own = groups.TryGetValue(nodeId, out var group) ? group.Locked : instances.TryGetValue(nodeId, out var instance) ? instance.Locked : elements[nodeId].Locked;
        value = own || ParentId(nodeId) is { } parent && IsLocked(parent);
        locked.Add(nodeId, value); return value;
    }
    /// <summary>Resolves the supplied draft's current content; it need not already belong to the entity table.</summary>
    public CanvasElement ToWorld(CanvasElement element)
    {
        var result = element.Clone();
        ApplyPlacement(result, ScopeWorldPlacement(element.GroupId));
        if (!LayerPlacement.Finite(result.Transform) || !double.IsFinite(result.BillboardAnchor.X) || !double.IsFinite(result.BillboardAnchor.Y) || !double.IsFinite(result.BillboardHeight))
            throw new InvalidDataException(L.Get("Scene.InvalidElementTransform"));
        if (element.GroupId is { } parent) { result.Visible &= IsVisible(parent); result.Locked |= IsLocked(parent); }
        result.GroupId = null; return result;
    }
    public CanvasElement ToLocal(CanvasElement worldElement, Guid? parentId)
    {
        var result = worldElement.Clone(); ApplyPlacement(result, ScopeWorldPlacement(parentId).Inverse());
        result.GroupId = parentId; return result;
    }
    public static void ApplyPlacement(CanvasElement element, LayerPlacement placement)
    {
        if (!placement.IsValid) throw new InvalidDataException(L.Get("Scene.InvalidElementTransform"));
        BillboardGeometry.ApplyPlaneTransform(element, placement.PlaneTransform);
        if (element.IsBillboard) element.BillboardHeight *= placement.ElevationScale;
    }
    public CanvasElement ResolveElement(CanvasElement element)
    {
        var result = ScopeContainerPlacement(element.GroupId).Resolve(element);
        result.Visible = IsVisible(element.Id); result.Locked = IsLocked(element.Id); result.GroupId = null;
        return result;
    }
    public Guid? ResolveSelectableNode(Guid nodeId, Guid? scopeId = null)
    {
        if (!elements.ContainsKey(nodeId) && !groups.ContainsKey(nodeId) && !instances.ContainsKey(nodeId)) return null;
        if (nodeId == scopeId) return null;
        var candidate = nodeId;
        while (ParentId(candidate) != scopeId)
        {
            if (ParentId(candidate) is not { } parent) return null;
            candidate = parent;
        }
        return candidate;
    }
    /// <summary>Materializes only the draw ledger; ownership and the exact old leaf order stay unchanged.</summary>
    public void MaterializeOrder()
    {
        RequireEditable();
        if (document.RootOrder is not null) return;
        document.RootOrder = document.Elements.Select(element => element.Id).ToList(); Refresh();
    }
    public Guid? PaintContext(Guid nodeId) => PaintContextForParent(ParentId(nodeId));
    public IReadOnlyList<Guid> PaintOrder(Guid? contextId) => contextId is { } id
        ? groups[id].ChildOrder ?? throw InvalidGraph()
        : (IReadOnlyList<Guid>?)document.RootOrder ?? document.Elements.Select(element => element.Id).ToArray();

    public CanvasElement InsertElement(CanvasElement element, Guid? parentId = null, int index = -1, bool worldCoordinates = true, RootCanvasKind rootCanvas = RootCanvasKind.Main)
    {
        _ = SceneCanvasRoots.PaintRank(rootCanvas);
        RequireParent(parentId); RequireNewId(element.Id); MaterializeOrder();
        var inserted = worldCoordinates ? ToLocal(element, parentId) : element.Clone(); inserted.GroupId = parentId;
        document.Elements.Add(inserted); InsertTokens(MutableOrder(PaintContextForParent(parentId)), [inserted.Id], index); Refresh();
        if (document is SceneDocument scene && parentId is null) SceneCanvasRoots.Assign(scene, inserted.Id, rootCanvas, this);
        return inserted;
    }
    public CanvasGroup InsertGroup(CanvasGroup group, Guid? parentId = null, int index = -1, bool worldCoordinates = false, RootCanvasKind rootCanvas = RootCanvasKind.Main)
    {
        _ = SceneCanvasRoots.PaintRank(rootCanvas);
        RequireParent(parentId); RequireNewId(group.Id); MaterializeOrder();
        var inserted = group.Clone(); inserted.ParentId = parentId;
        if (inserted.OrderMode != GroupOrderMode.AtomicBlock || inserted.ChildOrder is { Count: > 0 }) throw InvalidGraph();
        inserted.ChildOrder = [];
        if (worldCoordinates)
        {
            var local = ScopeWorldPlacement(parentId).Inverse().Compose(new(inserted.Transform, inserted.ElevationScale));
            inserted.Transform = local.PlaneTransform; inserted.ElevationScale = local.ElevationScale;
        }
        document.Groups.Add(inserted); InsertTokens(MutableOrder(PaintContextForParent(parentId)), [inserted.Id], index); Refresh();
        if (document is SceneDocument scene && parentId is null) SceneCanvasRoots.Assign(scene, inserted.Id, rootCanvas, this);
        return inserted;
    }
    public PrefabInstance InsertInstance(PrefabInstance instance, Guid? parentId = null, int index = -1, bool worldCoordinates = true, RootCanvasKind rootCanvas = RootCanvasKind.Main)
    {
        _ = SceneCanvasRoots.PaintRank(rootCanvas);
        RequireParent(parentId); RequireNewId(instance.Id); MaterializeOrder();
        var inserted = instance.Clone(); inserted.ParentId = parentId;
        if (worldCoordinates)
        {
            var local = ScopeWorldPlacement(parentId).Inverse().Compose(new(inserted.Transform, inserted.ElevationScale));
            inserted.Transform = local.PlaneTransform; inserted.ElevationScale = local.ElevationScale;
        }
        document.Instances.Add(inserted); InsertTokens(MutableOrder(PaintContextForParent(parentId)), [inserted.Id], index); Refresh();
        if (document is SceneDocument scene && parentId is null) SceneCanvasRoots.Assign(scene, inserted.Id, rootCanvas, this);
        return inserted;
    }
    /// <summary>Moves complete ownership subtrees. Moving a legacy interleaved group explicitly gathers its paint slots.</summary>
    public void ReparentPreservingWorld(IEnumerable<Guid> nodeIds, Guid? parentId, int index = -1, bool preserveContainerAppearance = true)
    {
        RequireParent(parentId);
        var selected = NormalizeSelection(nodeIds);
        if (selected.Count == 0) return;
        var sourceRoots = document is SceneDocument sourceDocument
            ? selected.ToDictionary(id => id, id => SceneCanvasRoots.Resolve(sourceDocument, id, this)) : null;
        if (parentId is { } parent && selected.Any(id => id == parent || IsAncestor(id, parent))) throw InvalidGraph();
        var prepared = preserveContainerAppearance && document is SceneDocument sceneDocument
            ? ContainerReparent.Prepare(sceneDocument, this, selected, parentId, index) : null;
        var changes = selected.ToDictionary(id => id, id => ScopeWorldPlacement(parentId).Inverse().Compose(ParentWorldPlacement(id)));
        var tokens = SelectedPaintTokens(selected);
        MaterializeOrder(); RemoveTokens(tokens);
        foreach (var id in selected)
        {
            if (groups.TryGetValue(id, out var group))
            {
                var placement = changes[id].Compose(new(group.Transform, group.ElevationScale));
                group.Transform = placement.PlaneTransform; group.ElevationScale = placement.ElevationScale; group.ParentId = parentId;
            }
            else if (instances.TryGetValue(id, out var instance))
            {
                var placement = changes[id].Compose(new(instance.Transform, instance.ElevationScale));
                instance.Transform = placement.PlaneTransform; instance.ElevationScale = placement.ElevationScale; instance.ParentId = parentId;
            }
            else { ApplyPlacement(elements[id], changes[id]); elements[id].GroupId = parentId; }
            if (prepared?.FindElement(id) is { } preparedElement) EditorSession.CopyPlacement(elements[id], preparedElement);
            else if (prepared?.FindGroup(id) is { } preparedGroup) { groups[id].Transform = preparedGroup.Transform; groups[id].ElevationScale = preparedGroup.ElevationScale; groups[id].Billboard = preparedGroup.Billboard; }
            else if (prepared?.FindInstance(id) is { } preparedInstance) { instances[id].Transform = preparedInstance.Transform; instances[id].ElevationScale = preparedInstance.ElevationScale; instances[id].Billboard = preparedInstance.Billboard; }
        }
        InsertTokens(MutableOrder(PaintContextForParent(parentId)), tokens, index); Refresh();
        if (document is SceneDocument scene)
        {
            foreach (var id in selected)
                if (parentId is null) SceneCanvasRoots.Assign(scene, id, sourceRoots![id], this);
                else scene.RootCanvasAssignments.Remove(id);
            SceneCanvasRoots.Prune(scene);
        }
    }
    public void RemoveNodes(IEnumerable<Guid> nodeIds)
    {
        RequireEditable();
        var selected = NormalizeSelection(nodeIds).ToHashSet();
        if (selected.Count == 0) return;
        var removed = elements.Keys.Concat(groups.Keys).Concat(instances.Keys).Where(id => selected.Contains(id) || AncestorIds(id).Any(selected.Contains)).ToHashSet();
        RemoveTokens(removed);
        document.Elements.RemoveAll(element => removed.Contains(element.Id)); document.Groups.RemoveAll(group => removed.Contains(group.Id));
        document.Instances.RemoveAll(instance => removed.Contains(instance.Id)); Refresh();
        if (document is SceneDocument scene) SceneCanvasRoots.Prune(scene);
    }
    public void CollectLegacyGroup(Guid groupId)
    {
        if (!groups.TryGetValue(groupId, out var group)) throw InvalidGraph();
        if (group.OrderMode == GroupOrderMode.AtomicBlock) return;
        var tokens = SelectedPaintTokens([groupId]);
        MaterializeOrder();
        var context = MutableOrder(PaintContextForParent(group.ParentId));
        int highest = tokens.Count == 0 ? context.Count : tokens.Max(token => context.IndexOf(token)) + 1;
        int insertion = context.Take(highest).Count(token => !tokens.Contains(token));
        RemoveTokens(tokens); group.OrderMode = GroupOrderMode.AtomicBlock; group.ChildOrder = tokens.ToList();
        context.Insert(insertion, groupId); Refresh();
    }
    public static void Validate(SceneNodeGraph document) => _ = new SceneGraph(document);

    private void Refresh()
    {
        if (document.Elements is null || document.Groups is null || document.Instances is null || document.Elements.Count > SceneLimits.MaximumElements ||
            document.Groups.Count > SceneLimits.MaximumElements || document.Instances.Count > SceneLimits.MaximumElements) throw InvalidGraph();
        elements = new(document.Elements.Count); groups = new(document.Groups.Count); instances = new(document.Instances.Count);
        world.Clear(); containerContexts.Clear(); visible.Clear(); locked.Clear(); children.Clear(); descendantLeaves.Clear();
        foreach (var element in document.Elements)
            if (element is null || element.Id == Guid.Empty || !elements.TryAdd(element.Id, element)) throw InvalidGraph();
        foreach (var group in document.Groups)
            if (group is null || group.Id == Guid.Empty || elements.ContainsKey(group.Id) || !groups.TryAdd(group.Id, group) ||
                !Enum.IsDefined(group.OrderMode) || !new LayerPlacement(group.Transform, group.ElevationScale).IsValid || group.Billboard is { IsValid: false } ||
                group.Billboard is not null && group.OrderMode != GroupOrderMode.AtomicBlock ||
                group.OrderMode == GroupOrderMode.AtomicBlock && group.ChildOrder is null ||
                group.OrderMode == GroupOrderMode.LegacyPassThrough && group.ChildOrder is { Count: > 0 }) throw InvalidGraph();
        foreach (var instance in document.Instances)
            if (instance is null || instance.Id == Guid.Empty || instance.DefinitionId == Guid.Empty || elements.ContainsKey(instance.Id) || groups.ContainsKey(instance.Id) ||
                !instances.TryAdd(instance.Id, instance) || !new LayerPlacement(instance.Transform, instance.ElevationScale).IsValid || instance.Billboard is { IsValid: false }) throw InvalidGraph();
        // Reuse validation scratch storage; a large scene otherwise allocates a
        // fresh hash set (and buckets) for every leaf on every read index build.
        var visited = new HashSet<Guid>();
        foreach (var id in elements.Keys.Concat(groups.Keys).Concat(instances.Keys))
        {
            Guid? parent = ParentId(id); visited.Clear();
            while (parent is { } value)
            {
                if (value == id || !groups.ContainsKey(value) || !visited.Add(value) || visited.Count > SceneLimits.MaximumGroupDepth) throw InvalidGraph();
                parent = groups[value].ParentId;
            }
        }
        if (document.RootOrder is null && (instances.Count > 0 || groups.Values.Any(group => group.OrderMode == GroupOrderMode.AtomicBlock))) throw InvalidGraph();
        int contentCount = elements.Count + instances.Count;
        var seen = new HashSet<Guid>(contentCount + groups.Count); var leaves = new List<Guid>(elements.Count);
        var content = new List<Guid>(contentCount); var ranks = new Dictionary<Guid, int>(contentCount + groups.Count); int rank = 0;
        void Visit(Guid? contextId)
        {
            foreach (var id in PaintOrder(contextId))
            {
                if (!seen.Add(id) || !elements.ContainsKey(id) && !instances.ContainsKey(id) && (!groups.TryGetValue(id, out var group) || group.OrderMode != GroupOrderMode.AtomicBlock) || PaintContext(id) != contextId) throw InvalidGraph();
                ranks[id] = rank++;
                var ancestor = ParentId(id);
                while (ancestor is { } parent) { ranks.TryAdd(parent, ranks[id]); ancestor = groups[parent].ParentId; }
                if (elements.ContainsKey(id)) { leaves.Add(id); content.Add(id); }
                else if (instances.ContainsKey(id)) content.Add(id); else Visit(id);
            }
        }
        Visit(null);
        if (leaves.Count != elements.Count || seen.Count != elements.Count + instances.Count + groups.Values.Count(group => group.OrderMode == GroupOrderMode.AtomicBlock)) throw InvalidGraph();
        orderedLeaves = leaves.AsReadOnly();
        orderedContent = content.AsReadOnly();
        paintRanks = ranks;
        foreach (var id in groups.Keys.Concat(instances.Keys)) _ = NodeWorldPlacement(id);
        var lists = groups.Keys.ToDictionary(id => id, _ => new List<Guid>()); var roots = new List<Guid>();
        foreach (var id in elements.Keys.Concat(groups.Keys).Concat(instances.Keys))
        {
            if (ParentId(id) is { } parent) lists[parent].Add(id); else roots.Add(id);
        }
        IReadOnlyList<Guid> Ordered(List<Guid> ids) => ids.OrderBy(id => ranks.GetValueOrDefault(id, int.MaxValue)).ToArray();
        rootChildren = Ordered(roots); foreach (var pair in lists) children.Add(pair.Key, Ordered(pair.Value));
    }
    private Guid? PaintContextForParent(Guid? parentId)
    {
        while (parentId is { } id)
        {
            var group = groups[id]; if (group.OrderMode == GroupOrderMode.AtomicBlock) return id; parentId = group.ParentId;
        }
        return null;
    }
    private List<Guid> MutableOrder(Guid? contextId) => contextId is { } id ? groups[id].ChildOrder ?? throw InvalidGraph() : document.RootOrder ?? throw InvalidGraph();
    private List<Guid> SelectedPaintTokens(IReadOnlyList<Guid> selected)
    {
        var selectedSet = selected.SelectMany(DescendantNodeIds).ToHashSet(); var result = new List<Guid>();
        void Visit(Guid? context)
        {
            foreach (var id in PaintOrder(context))
            {
                if (selectedSet.Contains(id)) result.Add(id);
                else if (groups.ContainsKey(id)) Visit(id);
            }
        }
        Visit(null); return result;
    }
    private void RemoveTokens(IEnumerable<Guid> tokens)
    {
        var removed = tokens.ToHashSet(); document.RootOrder?.RemoveAll(removed.Contains);
        foreach (var group in document.Groups) group.ChildOrder?.RemoveAll(removed.Contains);
    }
    private static void InsertTokens(List<Guid> order, IEnumerable<Guid> tokens, int index) => order.InsertRange(index < 0 ? order.Count : Math.Clamp(index, 0, order.Count), tokens);
    private void RequireParent(Guid? id) { if (id is { } value && !groups.ContainsKey(value)) throw InvalidGraph(); }
    private void RequireNewId(Guid id) { if (id == Guid.Empty || elements.ContainsKey(id) || groups.ContainsKey(id) || instances.ContainsKey(id) || (document as SceneDocument)?.Background?.Id == id) throw InvalidGraph(); }
    private void RequireEditable() { if (document.IsRuntimeEvaluated) throw new InvalidDataException("An evaluated render view is not an editable source."); }
    private static InvalidDataException InvalidGraph() => new(L.Get("Scene.InvalidElementId"));
}
