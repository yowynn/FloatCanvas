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
    CanvasElement RenderElement, LayerPlacement ParentWorld, Guid HostNodeId)
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
    private readonly IReadOnlyDictionary<Guid, bool> visible, locked;
    private readonly IReadOnlyDictionary<Guid, ResolvedLeaf> runtime;
    private readonly IReadOnlyDictionary<OccurrencePath, ResolvedLeaf> occurrences;
    private readonly IReadOnlyDictionary<SourceNodeAddress, IReadOnlyList<ResolvedLeaf>> sourceOccurrences;
    private readonly IReadOnlyDictionary<Guid, IReadOnlyList<ResolvedLeaf>> hostOccurrences;
    internal EvaluatedScene(SceneDocument renderDocument, IReadOnlyList<ResolvedLeaf> leaves,
        Dictionary<Guid, Guid?> parents, Dictionary<Guid, LayerPlacement> placements,
        Dictionary<Guid, bool> visible, Dictionary<Guid, bool> locked)
    {
        RenderDocument = renderDocument; Leaves = leaves;
        this.parents = new ReadOnlyDictionary<Guid, Guid?>(parents);
        this.placements = new ReadOnlyDictionary<Guid, LayerPlacement>(placements);
        this.visible = new ReadOnlyDictionary<Guid, bool>(visible); this.locked = new ReadOnlyDictionary<Guid, bool>(locked);
        runtime = new ReadOnlyDictionary<Guid, ResolvedLeaf>(leaves.ToDictionary(leaf => leaf.RuntimeElementId));
        occurrences = new ReadOnlyDictionary<OccurrencePath, ResolvedLeaf>(leaves.ToDictionary(leaf => leaf.OccurrencePath));
        var bySource = new Dictionary<SourceNodeAddress, List<ResolvedLeaf>>();
        var byHost = new Dictionary<Guid, List<ResolvedLeaf>>();
        foreach (var leaf in leaves)
        {
            if (!bySource.TryGetValue(leaf.Source, out var sameSource)) bySource.Add(leaf.Source, sameSource = []);
            sameSource.Add(leaf);
            Guid? node = leaf.HostNodeId;
            while (node is { } id)
            {
                if (!byHost.TryGetValue(id, out var sameHost)) byHost.Add(id, sameHost = []);
                sameHost.Add(leaf); node = parents.GetValueOrDefault(id);
            }
        }
        sourceOccurrences = new ReadOnlyDictionary<SourceNodeAddress, IReadOnlyList<ResolvedLeaf>>(
            bySource.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<ResolvedLeaf>)pair.Value.AsReadOnly()));
        hostOccurrences = new ReadOnlyDictionary<Guid, IReadOnlyList<ResolvedLeaf>>(
            byHost.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<ResolvedLeaf>)pair.Value.AsReadOnly()));
    }
    internal EvaluatedScene(SceneDocument renderDocument, EvaluatedScene baseScene)
    {
        RenderDocument = renderDocument; Leaves = baseScene.Leaves;
        parents = baseScene.parents; placements = baseScene.placements; visible = baseScene.visible; locked = baseScene.locked;
        runtime = baseScene.runtime; occurrences = baseScene.occurrences;
        sourceOccurrences = baseScene.sourceOccurrences; hostOccurrences = baseScene.hostOccurrences;
    }
    public SceneDocument RenderDocument { get; }
    public IReadOnlyList<ResolvedLeaf> Leaves { get; }
    /// <summary>Runtime identity, or a real host leaf's unchanged identity; never the first match of a definition source ID.</summary>
    public CanvasElement? FindElement(Guid runtimeElementId) => FindOccurrence(runtimeElementId)?.RenderElement;
    public ResolvedLeaf? FindOccurrence(Guid runtimeElementId) => runtime.GetValueOrDefault(runtimeElementId);
    public ResolvedLeaf? FindOccurrence(OccurrencePath path) => occurrences.GetValueOrDefault(path);
    public IReadOnlyList<ResolvedLeaf> OccurrencesOf(SourceNodeAddress source) => sourceOccurrences.GetValueOrDefault(source) ?? [];
    public IReadOnlyList<ResolvedLeaf> OccurrencesOfHostNode(Guid nodeId) => hostOccurrences.GetValueOrDefault(nodeId) ?? [];
    public SourceNodeAddress? SourceAddress(Guid runtimeElementId) => FindOccurrence(runtimeElementId)?.Source;
    public Guid? HostNodeOfOccurrence(Guid runtimeElementId) => FindOccurrence(runtimeElementId)?.HostNodeId;
    /// <summary>Instances are selected as a whole; member locks apply when editing the definition's own graph.</summary>
    public bool IsPickableOccurrence(Guid runtimeElementId) => FindOccurrence(runtimeElementId) is { } leaf && leaf.RenderElement.Visible &&
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
            SceneGraph.ApplyPlacement(result, placements[parent]); result.Visible &= visible[parent]; result.Locked |= locked[parent];
        }
        result.GroupId = null; return result;
    }
}

public static class SceneEvaluation
{
    // Only detached, published runtime views are memoized. Mutable source documents never enter this table.
    private static readonly ConditionalWeakTable<SceneDocument, EvaluatedScene> runtimeViews = new();
    public static EvaluatedScene? RuntimeView(SceneDocument document) => runtimeViews.TryGetValue(document, out var view) ? view : null;
    /// <summary>
    /// A render-only insertion into an immutable world view. Existing leaves and metadata are
    /// borrowed, and only the inserted leaf is cloned. No source/occurrence map includes this draft.
    /// The runtime guard prevents publishing this view as an authored document.
    /// </summary>
    public static SceneDocument WithTransientElement(SceneDocument runtimeWorld, CanvasElement worldElement, int insertionIndex)
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
            Background = runtimeWorld.Background, Canvas = runtimeWorld.Canvas, Viewport = runtimeWorld.Viewport,
            LayerOpacity = runtimeWorld.LayerOpacity, NextNumber = runtimeWorld.NextNumber, DisplayLayout = runtimeWorld.DisplayLayout
        };
        runtimeViews.Add(document, new(document, baseScene)); return document;
    }
    /// <summary>Builds once from a frozen source, or afresh for a mutable public render/export. Never reads external files.</summary>
    public static EvaluatedScene Build(SceneDocument source)
    {
        if (source.IsRuntimeEvaluated && RuntimeView(source) is { } existing) return existing;
        if (!source.IsRuntimeEvaluated && (source.Instances.Count > 0 || source.Definitions.Count > 0)) PrefabClosure.Validate(source);
        var graph = new SceneGraph(source);
        var parents = new Dictionary<Guid, Guid?>(); var placements = new Dictionary<Guid, LayerPlacement>();
        var visible = new Dictionary<Guid, bool>(); var locked = new Dictionary<Guid, bool>();
        foreach (var id in source.Elements.Select(element => element.Id).Concat(source.Groups.Select(group => group.Id)).Concat(source.Instances.Select(instance => instance.Id)))
        {
            parents.Add(id, graph.ParentId(id)); placements.Add(id, graph.NodeWorldPlacement(id));
            visible.Add(id, graph.IsVisible(id)); locked.Add(id, graph.IsLocked(id));
        }
        var definitions = source.Definitions.ToDictionary(definition => definition.Id);
        var definitionGraphs = new Dictionary<Guid, SceneGraph>();
        var leaves = new List<ResolvedLeaf>(source.Elements.Count);
        var usedRuntimeIds = new HashSet<Guid>();
        Expand(graph, null, LayerPlacement.Identity, true, false, "", null);
        var renderDocument = new SceneDocument
        {
            Version = source.Version, Provenance = source.Provenance, IsRuntimeEvaluated = true, ResourceId = source.ResourceId,
            Kind = source.Kind, Elements = leaves.Select(leaf => leaf.RenderElement).ToList(),
            Background = source.Background?.Clone(), Canvas = source.Canvas.Clone(), Viewport = source.Viewport,
            LayerOpacity = source.LayerOpacity, NextNumber = source.NextNumber,
            DisplayLayout = source.DisplayLayout.Select(display => display.Clone()).ToList()
        };
        var result = new EvaluatedScene(renderDocument, leaves.AsReadOnly(), parents, placements, visible, locked);
        runtimeViews.Add(renderDocument, result); return result;

        void Expand(SceneGraph current, Guid? definitionId, LayerPlacement incoming, bool inheritedVisible,
            bool inheritedLocked, string path, Guid? hostInstance)
        {
            foreach (var id in current.OrderedContentNodeIds)
            {
                if (current.FindElement(id) is { } element)
                {
                    var occurrence = new OccurrencePath(path.Length == 0 ? id.ToString("N") : path + "/e:" + id.ToString("N"));
                    Guid runtimeId = definitionId is null ? id : RuntimeId(occurrence);
                    if ((definitionId is not null && parents.ContainsKey(runtimeId)) || !usedRuntimeIds.Add(runtimeId))
                        throw new InvalidDataException(L.Get("Scene.InvalidElementId"));
                    var parent = incoming.Compose(current.ParentWorldPlacement(id));
                    var world = element.Clone(); SceneGraph.ApplyPlacement(world, parent);
                    if (!LayerPlacement.Finite(world.Transform) || !double.IsFinite(world.BillboardAnchor.X) ||
                        !double.IsFinite(world.BillboardAnchor.Y) || !double.IsFinite(world.BillboardHeight))
                        throw new InvalidDataException(L.Get("Scene.InvalidElementTransform"));
                    world.Id = runtimeId; world.GroupId = null;
                    world.Visible = inheritedVisible && current.IsVisible(id); world.Locked = inheritedLocked || current.IsLocked(id);
                    leaves.Add(new(occurrence, new(definitionId, id), world, parent, hostInstance ?? id));
                }
                else if (current.FindInstance(id) is { } instance)
                {
                    var definition = definitions[instance.DefinitionId];
                    if (!definitionGraphs.TryGetValue(definition.Id, out var nested))
                        definitionGraphs.Add(definition.Id, nested = new SceneGraph(definition.Graph));
                    string prefix = path.Length == 0 ? "i:" : path + "/i:";
                    prefix += id.ToString("N") + "/d:" + definition.Id.ToString("N");
                    Expand(nested, definition.Id, incoming.Compose(current.NodeWorldPlacement(id)),
                        inheritedVisible && current.IsVisible(id), inheritedLocked || current.IsLocked(id), prefix, hostInstance ?? id);
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
        source.IsRuntimeEvaluated ? element.Clone() : new SceneGraph(source).ToWorld(element);
}
