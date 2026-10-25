using System.Collections.Frozen;

namespace FloatCanvas.Core;

public sealed partial class EditorSession
{
    private const int PreviewChangeHistoryLimit = 128;
    private sealed record PreviewChange(long Revision, IReadOnlySet<Guid>? ElementIds);
    private readonly Queue<PreviewChange> previewChanges = new();

    /// <summary>The preview version at the beginning of the most recent transaction.</summary>
    public long TransactionPreviewRevision => transactionPreviewRevision;

    /// <summary>
    /// Notify after changing the supplied elements. IDs must include every affected element,
    /// including removed IDs. The sequence is frozen before observers are notified.
    /// Use NotifyChanged for structural or otherwise unknown changes.
    /// </summary>
    public void NotifyElementsChanged(IEnumerable<Guid> changedIds)
    {
        ArgumentNullException.ThrowIfNull(changedIds);
        OnChanged(contentChanged: true, previewChangedElements: changedIds.ToFrozenSet());
    }

    /// <summary>
    /// Union the known element changes after a captured preview version. False means the caller
    /// must rebuild: an unknown change, replacement/history operation or expired journal is present.
    /// The returned set is immutable and does not retain a caller-owned collection.
    /// </summary>
    public bool TryGetPreviewChangedElements(long sinceRevision, out IReadOnlySet<Guid> changedIds)
    {
        changedIds = FrozenSet<Guid>.Empty;
        if (sinceRevision < 0 || sinceRevision > PreviewRevision) return false;
        if (sinceRevision == PreviewRevision) return true;
        if (previewChanges.Count == 0 || sinceRevision < previewChanges.Peek().Revision - 1) return false;
        HashSet<Guid>? union = null;
        foreach (var change in previewChanges)
        {
            if (change.Revision <= sinceRevision) continue;
            if (change.ElementIds is null) return false;
            if (change.ElementIds.Count == 0) continue;
            union ??= [];
            union.UnionWith(change.ElementIds);
        }
        if (union is not null) changedIds = union.ToFrozenSet();
        return true;
    }

    private void RecordPreviewChange(bool elementsChanged, IReadOnlySet<Guid>? changedIds = null)
    {
        PreviewRevision++;
        if (elementsChanged) PreviewElementsRevision = PreviewRevision;
        if (previewChanges.Count == PreviewChangeHistoryLimit) previewChanges.Dequeue();
        previewChanges.Enqueue(new(PreviewRevision, elementsChanged ? changedIds : FrozenSet<Guid>.Empty));
    }

    private bool SettingsElementsUnchanged(SceneDocument before) =>
        SceneContent.Equal(before.Elements, Document.Elements) && SceneContent.Equal(before.Background, Document.Background) &&
        SceneContent.Equal(before.Groups, Document.Groups) && SceneContent.Equal(before.DisplayLayout, Document.DisplayLayout);
}
