namespace FloatCanvas.Core;

/// <summary>Logical nesting over flat vector elements. Transforms remain on the editable elements.</summary>
public sealed class CanvasGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = "";
    public CanvasGroup Clone() => (CanvasGroup)MemberwiseClone();
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
        var selected = ids.ToHashSet();
        var snapshot = new SceneDocument
        {
            Elements = Elements.Where(e => selected.Contains(e.Id)).Select(e => e.Clone()).ToList(),
            Groups = Groups.Select(g => g.Clone()).ToList(), Viewport = Viewport,
            LayerOpacity = LayerOpacity, NextNumber = NextNumber
        };
        snapshot.PruneGroups();
        return snapshot.Clone(newIds, assetIdMap);
    }

    /// <summary>Removes group records with no remaining descendants after deletion or a partial snapshot.</summary>
    public void PruneGroups()
    {
        var groups = Groups.ToDictionary(g => g.Id);
        var used = Elements.SelectMany(e => Ancestors(e.GroupId, groups)).ToHashSet();
        Groups.RemoveAll(g => !used.Contains(g.Id));
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
