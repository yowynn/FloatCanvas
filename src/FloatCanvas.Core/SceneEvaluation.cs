using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.CompilerServices;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Core;

/// <summary>An authored node's namespace. Null identifies the document's real owning graph.</summary>
public readonly record struct SourceNodeAddress(Guid? DefinitionId, Guid NodeId);
/// <summary>Stable instance/definition path. Revisions and ordinary group ancestry do not change identity.</summary>
public readonly record struct OccurrencePath(string Value);
public sealed record ResolvedLeaf(OccurrencePath OccurrencePath, SourceNodeAddress Source,
    CanvasElement RenderElement, LayerPlacement ParentWorld, Guid HostNodeId, RootCanvasKind RootCanvas = RootCanvasKind.Main)
{
    /// <summary>Compatibility only: a definition leaf may occur many times.</summary>
    public Guid SourceElementId => Source.NodeId;
    public Guid RuntimeElementId => RenderElement.Id;
}

/// <summary>A detached world view and its source/occurrence maps. Consumers must not mutate a published view.</summary>
public sealed class EvaluatedScene
{
    private readonly IReadOnlyDictionary<Guid, Guid?> parents;
    private readonly IReadOnlyDictionary<Guid, LayerPlacement> placements;
    private readonly IReadOnlyDictionary<Guid, ContainerPlacementContext> containers;
    private readonly IReadOnlyDictionary<Guid, bool> visible, locked;
    private readonly IReadOnlyDictionary<Guid, double> inheritedOpacity;
    private readonly IReadOnlyDictionary<Guid, int> runtime;
    private readonly IReadOnlyDictionary<OccurrencePath, int> occurrences;
    private readonly IReadOnlyDictionary<SourceNodeAddress, IReadOnlyList<int>> sourceOccurrences;
    private readonly IReadOnlyDictionary<Guid, IReadOnlyList<int>> hostOccurrences;
    internal EvaluatedScene(SceneDocument renderDocument, IReadOnlyList<ResolvedLeaf> leaves,
        Dictionary<Guid, Guid?> parents, Dictionary<Guid, LayerPlacement> placements,
        Dictionary<Guid, bool> visible, Dictionary<Guid, bool> locked, Dictionary<Guid, ContainerPlacementContext> containers, SceneGraph graph)
    {
        RenderDocument = renderDocument; Leaves = leaves;
        this.parents = new ReadOnlyDictionary<Guid, Guid?>(parents);
        this.placements = new ReadOnlyDictionary<Guid, LayerPlacement>(placements);
        this.containers = new ReadOnlyDictionary<Guid, ContainerPlacementContext>(containers);
        this.visible = new ReadOnlyDictionary<Guid, bool>(visible); this.locked = new ReadOnlyDictionary<Guid, bool>(locked);
        inheritedOpacity = parents.Keys.ToDictionary(id => id, graph.InheritedOpacity);
        runtime = leaves.Select((leaf, i) => (leaf, i)).ToDictionary(p => p.leaf.RuntimeElementId, p => p.i);
        occurrences = leaves.Select((leaf, i) => (leaf, i)).ToDictionary(p => p.leaf.OccurrencePath, p => p.i);
        var bySource = new Dictionary<SourceNodeAddress, List<int>>();
        var byHost = new Dictionary<Guid, List<int>>();
        for (int i = 0; i < leaves.Count; i++)
        {
            var leaf = leaves[i];
            if (!bySource.TryGetValue(leaf.Source, out var sameSource)) bySource.Add(leaf.Source, sameSource = []);
            sameSource.Add(i);
            Guid? node = leaf.HostNodeId;
            while (node is { } id)
            {
                if (!byHost.TryGetValue(id, out var sameHost)) byHost.Add(id, sameHost = []);
                sameHost.Add(i); node = parents.GetValueOrDefault(id);
            }
        }
        sourceOccurrences = bySource.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<int>)pair.Value.AsReadOnly());
        hostOccurrences = byHost.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<int>)pair.Value.AsReadOnly());
    }
    internal EvaluatedScene(SceneDocument renderDocument, EvaluatedScene baseScene)
    {
        RenderDocument = renderDocument; Leaves = baseScene.Leaves;
        parents = baseScene.parents; placements = baseScene.placements; containers = baseScene.containers; visible = baseScene.visible; locked = baseScene.locked;
        inheritedOpacity = baseScene.inheritedOpacity;
        runtime = baseScene.runtime; occurrences = baseScene.occurrences;
        sourceOccurrences = baseScene.sourceOccurrences; hostOccurrences = baseScene.hostOccurrences;
    }
    private sealed class IndexedLeaves(IReadOnlyList<ResolvedLeaf> leaves, IReadOnlyList<int> indices) : IReadOnlyList<ResolvedLeaf>
    {
        public int Count => indices.Count;
        public ResolvedLeaf this[int index] => leaves[indices[index]];
        public IEnumerator<ResolvedLeaf> GetEnumerator() { foreach (var i in indices) yield return leaves[i]; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    internal EvaluatedScene(SceneDocument renderDocument, EvaluatedScene previous, IReadOnlyList<ResolvedLeaf> leaves,
        SceneGraph graph, IEnumerable<Guid> changedNodes) : this(renderDocument, previous)
    {
        Leaves = leaves;
        var nextPlacements = previous.placements.ToDictionary();
        var nextContainers = previous.containers.ToDictionary();
        var nextOpacity = previous.inheritedOpacity.ToDictionary();
        foreach (var id in changedNodes)
        {
            nextPlacements[id] = graph.NodeWorldPlacement(id);
            nextOpacity[id] = graph.InheritedOpacity(id);
            if (graph.FindGroup(id) is not null) nextContainers[id] = graph.ScopeContainerPlacement(id);
        }
        placements = nextPlacements; containers = nextContainers;
        inheritedOpacity = nextOpacity;
    }
    internal int IndexOf(Guid runtimeId) => runtime.GetValueOrDefault(runtimeId, -1);
    public SceneDocument RenderDocument { get; }
    public IReadOnlyList<ResolvedLeaf> Leaves { get; }
    /// <summary>Runtime identity, or a real host leaf's unchanged identity; never the first match of a definition source ID.</summary>
    public CanvasElement? FindElement(Guid runtimeElementId) => FindOccurrence(runtimeElementId)?.RenderElement;
    public ResolvedLeaf? FindOccurrence(Guid runtimeElementId) => runtime.TryGetValue(runtimeElementId, out var index) ? Leaves[index] : null;
    public ResolvedLeaf? FindOccurrence(OccurrencePath path) => occurrences.TryGetValue(path, out var index) ? Leaves[index] : null;
    public IReadOnlyList<ResolvedLeaf> OccurrencesOf(SourceNodeAddress source) => sourceOccurrences.TryGetValue(source, out var indices) ? new IndexedLeaves(Leaves, indices) : [];
    public IReadOnlyList<ResolvedLeaf> OccurrencesOfHostNode(Guid nodeId) => hostOccurrences.TryGetValue(nodeId, out var indices) ? new IndexedLeaves(Leaves, indices) : [];
    public SourceNodeAddress? SourceAddress(Guid runtimeElementId) => FindOccurrence(runtimeElementId)?.Source;
    public Guid? HostNodeOfOccurrence(Guid runtimeElementId) => FindOccurrence(runtimeElementId)?.HostNodeId;
    public RootCanvasKind RootCanvasOf(Guid runtimeElementId) => RenderDocument.RootCanvasAssignments.GetValueOrDefault(runtimeElementId);
    /// <summary>Instances are selected as a whole; member locks apply when editing the definition's own graph.</summary>
    public bool IsPickableOccurrence(Guid runtimeElementId, bool? visibleOverride = null) => FindOccurrence(runtimeElementId) is { } leaf && (visibleOverride ?? leaf.RenderElement.Visible) &&
        (leaf.Source.DefinitionId is not null ? !locked[leaf.HostNodeId] : !leaf.RenderElement.Locked);
    public LayerPlacement GetWorldPlacement(Guid nodeId) => placements[nodeId];
    public Guid? ResolveSelectableNode(Guid runtimeElementId, Guid? editScopeId = null)
    {
        if (FindOccurrence(runtimeElementId) is not { } leaf) return null;
        var candidate = leaf.HostNodeId;
        if (!parents.ContainsKey(candidate) || candidate == editScopeId) return null;
        while (parents[candidate] != editScopeId)
        {
            if (parents[candidate] is not { } parent) return null;
            candidate = parent;
        }
        return candidate;
    }
    public Guid? ResolveSelectableNode(OccurrencePath path, Guid? editScopeId = null) =>
        FindOccurrence(path) is { } leaf ? ResolveSelectableNode(leaf.RuntimeElementId, editScopeId) : null;
    /// <summary>Resolves a host draft's current geometry/style. Definition editing uses its own real source graph.</summary>
    public CanvasElement ResolveElement(CanvasElement draft)
    {
        var result = draft.Clone();
        if (draft.GroupId is { } parent)
        {
            result = containers[parent].Resolve(draft); result.Visible &= visible[parent]; result.Locked |= locked[parent];
        }
        result.Style.Opacity *= inheritedOpacity.GetValueOrDefault(draft.Id, 1);
        result.GroupId = null; return result;
    }
}

public static partial class SceneEvaluation
{
    // Only detached, published runtime views are memoized. Mutable source documents never enter this table.
    private static readonly ConditionalWeakTable<SceneDocument, EvaluatedScene> runtimeViews = new();
    public static EvaluatedScene? RuntimeView(SceneDocument document) => runtimeViews.TryGetValue(document, out var view) ? view : null;
    /// <summary>
    /// A render-only insertion into an immutable world view. Existing leaves and metadata are
    /// borrowed, and only the inserted leaf is cloned. No source/occurrence map includes this draft.
    /// The runtime guard prevents publishing this view as an authored document.
    /// </summary>
    public static SceneDocument WithTransientElement(SceneDocument runtimeWorld, CanvasElement worldElement, int insertionIndex, RootCanvasKind rootCanvas = RootCanvasKind.Main)
    {
        if (!runtimeWorld.IsRuntimeEvaluated || RuntimeView(runtimeWorld) is not { } baseScene || runtimeWorld.Groups.Count != 0 || runtimeWorld.Instances.Count != 0 ||
            runtimeWorld.Definitions.Count != 0 || runtimeWorld.RootOrder is not null)
            throw new ArgumentException("A detached evaluated world view is required.", nameof(runtimeWorld));
        if (insertionIndex < 0 || insertionIndex > runtimeWorld.Elements.Count) throw new ArgumentOutOfRangeException(nameof(insertionIndex));
        if (worldElement.Id == Guid.Empty || runtimeWorld.Elements.Any(element => element.Id == worldElement.Id))
            throw new ArgumentException("A transient element needs a distinct render identity.", nameof(worldElement));
        var elements = new List<CanvasElement>(runtimeWorld.Elements.Count + 1);
        elements.AddRange(runtimeWorld.Elements.Take(insertionIndex));
        var draft = worldElement.Clone(); draft.GroupId = null; elements.Add(draft);
        elements.AddRange(runtimeWorld.Elements.Skip(insertionIndex));
        var document = new SceneDocument
        {
            Version = runtimeWorld.Version, Provenance = runtimeWorld.Provenance, IsRuntimeEvaluated = true,
            ResourceId = runtimeWorld.ResourceId, Kind = runtimeWorld.Kind, Elements = elements,
            RootCanvasAssignments = new(runtimeWorld.RootCanvasAssignments),
            Background = runtimeWorld.Background, Canvas = runtimeWorld.Canvas, Viewport = runtimeWorld.Viewport, AuthoringReference = runtimeWorld.AuthoringReference,
            LayerOpacity = runtimeWorld.LayerOpacity, NextNumber = runtimeWorld.NextNumber, DisplayLayout = runtimeWorld.DisplayLayout
        };
        SceneCanvasRoots.Assign(document, draft.Id, rootCanvas);
        document.Elements = document.Elements.OrderBy(element => SceneCanvasRoots.PaintRank(document.RootCanvasAssignments.GetValueOrDefault(element.Id))).ToList();
        runtimeViews.Add(document, new(document, baseScene)); return document;
    }
    /// <summary>Render-only header for another visible region. Borrows immutable leaves and their occurrence metadata.</summary>
    public static SceneDocument WithViewport(SceneDocument runtimeWorld, RectD viewport, CanvasSettings canvas)
    {
        if (!runtimeWorld.IsRuntimeEvaluated || RuntimeView(runtimeWorld) is not { } baseScene)
            throw new ArgumentException("A detached evaluated world view is required.", nameof(runtimeWorld));
        var document = new SceneDocument
        {
            Version = runtimeWorld.Version, Provenance = runtimeWorld.Provenance, IsRuntimeEvaluated = true,
            ResourceId = runtimeWorld.ResourceId, Kind = runtimeWorld.Kind, Elements = runtimeWorld.Elements,
            RootCanvasAssignments = runtimeWorld.RootCanvasAssignments,
            Background = runtimeWorld.Background, Canvas = canvas, Viewport = viewport, AuthoringReference = runtimeWorld.AuthoringReference,
            LayerOpacity = runtimeWorld.LayerOpacity, NextNumber = runtimeWorld.NextNumber, DisplayLayout = runtimeWorld.DisplayLayout
        };
        runtimeViews.Add(document, new(document, baseScene)); return document;
    }
    /// <summary>Builds once from a frozen source, or afresh for a mutable public render/export. Never reads external files.</summary>
    public static EvaluatedScene Build(SceneDocument source, SceneEvaluationCache? immutableDefinitions = null)
    {
        if (source.IsRuntimeEvaluated && RuntimeView(source) is { } existing) return existing;
        if (!source.IsRuntimeEvaluated && (source.Instances.Count > 0 || source.Definitions.Count > 0)) PrefabClosure.Validate(source);
        var graph = new SceneGraph(source);
        SceneCanvasRoots.Validate(source);
        var parents = new Dictionary<Guid, Guid?>(); var placements = new Dictionary<Guid, LayerPlacement>();
        var containers = source.Groups.ToDictionary(group => group.Id, group => graph.ScopeContainerPlacement(group.Id));
        var visible = new Dictionary<Guid, bool>(); var locked = new Dictionary<Guid, bool>();
        foreach (var id in source.Elements.Select(element => element.Id).Concat(source.Groups.Select(group => group.Id)).Concat(source.Instances.Select(instance => instance.Id)))
        {
            parents.Add(id, graph.ParentId(id)); placements.Add(id, graph.NodeWorldPlacement(id));
            visible.Add(id, graph.IsVisible(id)); locked.Add(id, graph.IsLocked(id));
        }
        var definitions = source.Definitions.ToDictionary(definition => definition.Id);
        var localDefinitions = immutableDefinitions ?? new SceneEvaluationCache();
        var leaves = new List<ResolvedLeaf>(source.Elements.Count);
        var usedRuntimeIds = new HashSet<Guid>();
        Expand(graph, null, ContainerPlacementContext.Identity, true, false, "", null, RootCanvasKind.Main);
        leaves = leaves.OrderBy(leaf => SceneCanvasRoots.PaintRank(leaf.RootCanvas)).ToList();
        var renderDocument = new SceneDocument
        {
            Version = source.Version, Provenance = source.Provenance, IsRuntimeEvaluated = true, ResourceId = source.ResourceId,
            Kind = source.Kind, Elements = leaves.Select(leaf => leaf.RenderElement).ToList(),
            RootCanvasAssignments = leaves.Where(leaf => leaf.RootCanvas != RootCanvasKind.Main).ToDictionary(leaf => leaf.RuntimeElementId, leaf => leaf.RootCanvas),
            Background = source.Background?.Clone(), Canvas = source.Canvas.Clone(), Viewport = source.Viewport, AuthoringReference = source.AuthoringReference,
            LayerOpacity = source.LayerOpacity, NextNumber = source.NextNumber,
            DisplayLayout = source.DisplayLayout.Select(display => display.Clone()).ToList()
        };
        var result = new EvaluatedScene(renderDocument, leaves.AsReadOnly(), parents, placements, visible, locked, containers, graph);
        runtimeViews.Add(renderDocument, result); return result;

        void Expand(SceneGraph current, Guid? definitionId, ContainerPlacementContext incoming, bool inheritedVisible,
            bool inheritedLocked, string path, Guid? hostInstance, RootCanvasKind inheritedRoot)
        {
            var contexts = new Dictionary<Guid, ContainerPlacementContext>();
            ContainerPlacementContext Scope(Guid? parent)
            {
                if (parent is not { } groupId) return incoming;
                if (contexts.TryGetValue(groupId, out var cached)) return cached;
                var group = current.FindGroup(groupId)!;
                var resolved = Scope(group.ParentId).Enter(group.Transform, group.ElevationScale, group.Billboard);
                contexts.Add(groupId, resolved); return resolved;
            }
            foreach (var id in current.OrderedContentNodeIds)
            {
                var rootCanvas = definitionId is null ? SceneCanvasRoots.Resolve(source, id, graph) : inheritedRoot;
                if (current.FindElement(id) is { } element)
                {
                    var occurrence = new OccurrencePath(path.Length == 0 ? id.ToString("N") : path + "/e:" + id.ToString("N"));
                    Guid runtimeId = definitionId is null ? id : RuntimeId(occurrence);
                    if ((definitionId is not null && parents.ContainsKey(runtimeId)) || !usedRuntimeIds.Add(runtimeId))
                        throw new InvalidDataException(L.Get("Scene.InvalidElementId"));
                    var placement = Scope(element.GroupId);
                    var parent = placement.Placement;
                    var world = placement.Resolve(element);
                    if (!LayerPlacement.Finite(world.Transform) || !double.IsFinite(world.BillboardAnchor.X) ||
                        !double.IsFinite(world.BillboardAnchor.Y) || !double.IsFinite(world.BillboardHeight))
                        throw new InvalidDataException(L.Get("Scene.InvalidElementTransform"));
                    world.Id = runtimeId; world.GroupId = null;
                    world.Style.Opacity *= current.InheritedOpacity(id);
                    world.Visible = inheritedVisible && current.IsVisible(id); world.Locked = inheritedLocked || current.IsLocked(id);
                    leaves.Add(new(occurrence, new(definitionId, id), world, parent, hostInstance ?? id, rootCanvas));
                }
                else if (current.FindInstance(id) is { } instance)
                {
                    var definition = definitions[instance.DefinitionId];
                    string prefix = path.Length == 0 ? "i:" : path + "/i:";
                    prefix += id.ToString("N") + "/d:" + definition.Id.ToString("N");
                    var container = Scope(instance.ParentId).Enter(instance.Transform, instance.ElevationScale, instance.Billboard);
                    bool shown = inheritedVisible && current.IsVisible(id), blocked = inheritedLocked || current.IsLocked(id);
                    var local = localDefinitions.Definition(definition, source.Definitions);
                    foreach (var leaf in local.Leaves)
                    {
                        string relative = leaf.OccurrencePath.Value;
                        var occurrence = new OccurrencePath(prefix + "/" + (leaf.Source.DefinitionId is null ? "e:" : "") + relative);
                        var runtimeId = RuntimeId(occurrence);
                        if (parents.ContainsKey(runtimeId) || !usedRuntimeIds.Add(runtimeId)) throw new InvalidDataException(L.Get("Scene.InvalidElementId"));
                        var world = container.ResolveWorld(leaf.RenderElement);
                        if (!LayerPlacement.Finite(world.Transform) || !double.IsFinite(world.BillboardAnchor.X) ||
                            !double.IsFinite(world.BillboardAnchor.Y) || !double.IsFinite(world.BillboardHeight) ||
                            world.BillboardSurface is { ContentPlacement.IsFinite: false })
                            throw new InvalidDataException(L.Get("Scene.InvalidElementTransform"));
                        world.Id = runtimeId; world.GroupId = null; world.Visible &= shown; world.Locked |= blocked;
                        world.Style.Opacity *= current.InheritedOpacity(id) * instance.Opacity;
                        leaves.Add(new(occurrence, leaf.Source with { DefinitionId = leaf.Source.DefinitionId ?? definition.Id }, world,
                            container.Placement.Compose(leaf.ParentWorld), hostInstance ?? id, rootCanvas));
                    }
                }
            }
        }
    }
    private static Guid RuntimeId(OccurrencePath path)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("FloatCanvas.Occurrence.v1/" + path.Value));
        // This is a deterministic render key. The complete path remains authoritative.
        digest[6] = (byte)((digest[6] & 15) | 128); digest[8] = (byte)((digest[8] & 63) | 128);
        return new Guid(digest.AsSpan(0, 16));
    }
    public static CanvasElement ResolveElement(SceneDocument source, CanvasElement element) =>
        source.IsRuntimeEvaluated ? element.Clone() : SceneGraph.Read(source).ResolveElement(element);
}
