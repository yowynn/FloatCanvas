namespace FloatCanvas.Core;

public sealed partial class SceneGraph
{
    /// <summary>Removes empty ownership chains after a complete edit, never during group construction.</summary>
    internal static bool PruneEmptyGroups(SceneNodeGraph document)
    {
        if (document.Groups.Count == 0) return false;
        var groups = document.Groups.ToDictionary(group => group.Id);
        var visited = new HashSet<Guid>();
        foreach (var group in document.Groups)
        {
            visited.Clear(); Guid? parent = group.ParentId;
            while (parent is { } id)
            {
                if (id == group.Id || !visited.Add(id) || visited.Count > SceneLimits.MaximumGroupDepth || !groups.TryGetValue(id, out var ancestor)) throw InvalidGraph();
                parent = ancestor.ParentId;
            }
        }
        var occupied = new HashSet<Guid>();
        void Retain(Guid? parent)
        {
            while (parent is { } id)
            {
                if (!groups.TryGetValue(id, out var group)) throw InvalidGraph();
                if (!occupied.Add(id)) return;
                parent = group.ParentId;
            }
        }
        foreach (var element in document.Elements) Retain(element.GroupId);
        foreach (var instance in document.Instances) Retain(instance.ParentId);
        var removed = groups.Keys.Where(id => !occupied.Contains(id)).ToHashSet();
        if (removed.Count == 0) return false;
        document.Groups.RemoveAll(group => removed.Contains(group.Id));
        document.RootOrder?.RemoveAll(removed.Contains);
        foreach (var group in document.Groups) group.ChildOrder?.RemoveAll(removed.Contains);
        if (document is SceneDocument scene)
            foreach (var id in removed) scene.RootCanvasAssignments.Remove(id);
        InvalidateReads(document);
        return true;
    }
}
