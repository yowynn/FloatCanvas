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
        var analysis = AnalyzeLayerOperation(visibility ? value ? LayerOperation.Show : LayerOperation.Hide : value ? LayerOperation.Lock : LayerOperation.Unlock, ids, graph);
        if (analysis.BlockReason is { } reason) throw new InvalidOperationException(L.Get(reason));
        var changed = analysis.Eligible; int unchanged = analysis.Unchanged.Count, skipped = analysis.Skipped.Count;
        if (changed.Count == 0) return new(0, unchanged, skipped);
        Execute(L.Get(visibility ? value ? "History.ShowElements" : "History.HideElements" : value ? "History.LockElements" : "History.UnlockElements"),
            _ => { foreach (var id in changed) SetNodeFlag(graph, id, value, locking: !visibility); });
        return new(changed.Count, unchanged, skipped);
    }
}
