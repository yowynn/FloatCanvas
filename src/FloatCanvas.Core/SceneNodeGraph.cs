using System.Text.Json.Serialization;

namespace FloatCanvas.Core;

/// <summary>Authored content only. Definitions share this model without inheriting a document camera.</summary>
public class SceneNodeGraph
{
    public List<CanvasElement> Elements { get; set; } = [];
    public List<CanvasGroup> Groups { get; set; } = [];
    public List<PrefabInstance> Instances { get; set; } = [];
    /// <summary>Null retains a legacy flat ledger. Instances and atomic groups require explicit slots.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Guid>? RootOrder { get; set; }
    [JsonIgnore] public bool IsRuntimeEvaluated { get; internal set; }
    /// <summary>Only this graph's source leaves; definition dependencies are resolved separately.</summary>
    [JsonIgnore] public IEnumerable<string> ReferencedAssetIds => Elements.OfType<CanvasElement>().Where(element => element.AssetId is not null)
        .Select(element => element.AssetId!).Distinct(StringComparer.Ordinal);
}

public enum GraphCloneIdentity { Preserve, NewNodes }
public sealed record GraphCloneResult(SceneNodeGraph Graph, IReadOnlyDictionary<Guid, Guid> NodeIdMap);

public static class SceneGraphClone
{
    /// <summary>Clones one real graph. Definition IDs remain shared unless an explicit complete remapping is supplied.</summary>
    public static GraphCloneResult CloneGraph(SceneNodeGraph source, GraphCloneIdentity identity = GraphCloneIdentity.Preserve,
        IReadOnlyDictionary<Guid, Guid>? definitionIds = null, IReadOnlyDictionary<string, string>? assetIds = null)
    {
        if (!Enum.IsDefined(identity)) throw new ArgumentOutOfRangeException(nameof(identity));
        bool newIds = identity == GraphCloneIdentity.NewNodes;
        var map = source.Elements.Select(element => element.Id).Concat(source.Groups.Select(group => group.Id)).Concat(source.Instances.Select(instance => instance.Id))
            .ToDictionary(id => id, id => newIds ? Guid.NewGuid() : id);
        Guid? Parent(Guid? parent) => parent is { } id ? map[id] : null;
        var result = new SceneNodeGraph
        {
            IsRuntimeEvaluated = source.IsRuntimeEvaluated,
            Elements = source.Elements.Select(element =>
            {
                var copy = element.Clone(newIds); copy.Id = map[element.Id]; copy.GroupId = Parent(element.GroupId);
                if (copy.AssetId is { } asset && assetIds is not null) copy.AssetId = assetIds[asset]; return copy;
            }).ToList(),
            Groups = source.Groups.Select(group =>
            {
                var copy = group.Clone(); copy.Id = map[group.Id]; copy.ParentId = Parent(group.ParentId);
                copy.ChildOrder = group.ChildOrder?.Select(id => map[id]).ToList(); return copy;
            }).ToList(),
            Instances = source.Instances.Select(instance =>
            {
                var copy = instance.Clone(); copy.Id = map[instance.Id]; copy.ParentId = Parent(instance.ParentId);
                if (definitionIds is not null) copy.DefinitionId = definitionIds[instance.DefinitionId]; return copy;
            }).ToList(),
            RootOrder = source.RootOrder?.Select(id => map[id]).ToList()
        };
        return new(result, map);
    }
}
