using System.Text.Json.Serialization;

namespace FloatCanvas.Core;

public enum GroupOrderMode { LegacyPassThrough = 0, AtomicBlock = 1 }

/// <summary>Editable ownership and local placement. Legacy groups preserve their interleaved draw slots.</summary>
public sealed class CanvasGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = "";
    public Affine2D Transform { get; set; } = Affine2D.Identity;
    public double ElevationScale { get; set; } = 1;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ContainerBillboard? Billboard { get; set; }
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public GroupOrderMode OrderMode { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SubprojectCanvasSettings? PreviewCanvas { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Guid>? ChildOrder { get; set; }
    public CanvasGroup Clone(IReadOnlyDictionary<string, string>? assetIds = null)
    {
        var result = (CanvasGroup)MemberwiseClone(); result.ChildOrder = ChildOrder?.ToList();
        result.PreviewCanvas = PreviewCanvas?.Clone(assetIds); return result;
    }
}

public sealed partial class SceneDocument
{
    public IEnumerable<CanvasElement> GroupElements(Guid groupId)
    {
        var groups = Groups.ToDictionary(g => g.Id);
        return Elements.Where(element => Ancestors(element.GroupId, groups).Contains(groupId));
    }

    public Guid? RootGroupId(CanvasElement element)
    {
        var ancestors = Ancestors(element.GroupId, Groups.ToDictionary(g => g.Id)).ToArray();
        return ancestors.Length == 0 ? null : ancestors[^1];
    }

    /// <summary>Clones exact entities, retaining their nested group ancestry and referenced asset IDs.</summary>
    public SceneDocument CloneElements(IEnumerable<Guid> ids, bool newIds = false,
        IReadOnlyDictionary<string, string>? assetIdMap = null)
    {
        var graph = new SceneGraph(this);
        var selected = ids.Where(id => graph.FindElement(id) is not null || graph.FindGroup(id) is not null || graph.FindInstance(id) is not null).ToHashSet();
        var selectedGroups = selected.Where(id => graph.FindGroup(id) is not null).ToHashSet();
        var elementIds = Elements.Where(element => selected.Contains(element.Id) || graph.AncestorIds(element.Id).Any(selectedGroups.Contains))
            .Select(element => element.Id).ToHashSet();
        var groupIds = Groups.Where(group => selectedGroups.Contains(group.Id) || graph.AncestorIds(group.Id).Any(selectedGroups.Contains))
            .Select(group => group.Id).ToHashSet();
        var instanceIds = Instances.Where(instance => selected.Contains(instance.Id) || graph.AncestorIds(instance.Id).Any(selectedGroups.Contains)).Select(instance => instance.Id).ToHashSet();
        foreach (var id in elementIds.Concat(groupIds).Concat(instanceIds).ToArray()) groupIds.UnionWith(graph.AncestorIds(id));
        var retained = elementIds.Concat(groupIds).Concat(instanceIds).ToHashSet();
        var snapshot = new SceneDocument
        {
            IsRuntimeEvaluated = IsRuntimeEvaluated, Provenance = Provenance,
            Elements = Elements.Where(e => elementIds.Contains(e.Id)).Select(e => e.Clone()).ToList(),
            Instances = Instances.Where(instance => instanceIds.Contains(instance.Id)).Select(instance => instance.Clone()).ToList(),
            Groups = Groups.Where(group => groupIds.Contains(group.Id)).Select(group =>
            {
                var copy = group.Clone(); copy.ChildOrder?.RemoveAll(id => !retained.Contains(id)); return copy;
            }).ToList(),
            RootOrder = RootOrder?.Where(retained.Contains).ToList(), Viewport = Viewport,
            RootCanvasAssignments = RootCanvasAssignments.Where(pair => retained.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value),
            LayerOpacity = LayerOpacity, NextNumber = NextNumber
        };
        snapshot.resourceIdentity = resourceIdentity;
        snapshot.Definitions = PrefabClosure.ReachableDefinitions(snapshot, Definitions).Select(definition => definition.Clone()).ToList();
        return snapshot.Clone(newIds, assetIdMap);
    }

    /// <summary>Ordinary groups require content; clean complete empty chains and their paint slots.</summary>
    public void PruneGroups()
    {
        SceneGraph.PruneEmptyGroups(this);
        foreach (var definition in Definitions) SceneGraph.PruneEmptyGroups(definition.Graph);
    }

    private static IEnumerable<Guid> Ancestors(Guid? id, IReadOnlyDictionary<Guid, CanvasGroup> groups)
    {
        var visited = new HashSet<Guid>();
        while (id is { } value && visited.Add(value) && groups.TryGetValue(value, out var group))
        {
            yield return value;
            id = group.ParentId;
        }
    }
}
