using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Core;

public sealed record LayerFlagResult(int Changed, int Unchanged, int Skipped);

public sealed partial class EditorSession
{
    /// <summary>Changes local flags in one history entry; inherited locks remain authoritative.</summary>
    public int SetNodesFlag(IEnumerable<Guid> ids, bool visibility, bool value)
        => SetNodesFlagDetailed(ids, visibility, value).Changed;

    public LayerFlagResult SetNodesFlagDetailed(IEnumerable<Guid> ids, bool visibility, bool value)
    {
        EnsureWritableParent(EditScopeId);
        if (IsTransactionActive) throw new InvalidOperationException(L.Get("History.TransactionAlreadyActive"));
        var graph = new SceneGraph(Document);
        var normalized = graph.NormalizeSelection(ids).ToArray();
        int unchanged = 0, skipped = 0;
        var changed = normalized.Where(id =>
        {
            if (graph.ParentId(id) is { } parent && graph.IsLocked(parent)) { skipped++; return false; }
            var group = graph.FindGroup(id); var instance = graph.FindInstance(id); var element = graph.FindElement(id);
            bool? current = visibility ? group?.Visible ?? instance?.Visible ?? element?.Visible : group?.Locked ?? instance?.Locked ?? element?.Locked;
            if (current is null) { skipped++; return false; }
            if (current == value) { unchanged++; return false; }
            return true;
        }).ToArray();
        if (changed.Length == 0) return new(0, unchanged, skipped);
        Execute(L.Get(visibility ? value ? "History.ShowElements" : "History.HideElements" : value ? "History.LockElements" : "History.UnlockElements"),
            _ => { foreach (var id in changed) SetNodeFlag(graph, id, value, locking: !visibility); });
        return new(changed.Length, unchanged, skipped);
    }
}
