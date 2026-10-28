using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Core;

/// <summary>One editable document and atomic user operations. Assets are never copied into history.</summary>
public sealed partial class EditorSession
{
    private sealed record HistoryEntry(string Label, SceneDocument? Before, SceneDocument? After, HashSet<Guid>? SelectionBefore, HashSet<Guid>? SelectionAfter, long Bytes, long Sequence, string[] AssetIds, long BeforeVersion, long AfterVersion, Action? UndoOperation = null, Action? RedoOperation = null);
    private readonly List<HistoryEntry> undo = [], redo = [];
    private SceneDocument? transactionBefore;
    private HashSet<Guid>? transactionSelection;
    private string transactionLabel = "";
    private long sequence, nextContentVersion, transactionContentVersion, transactionPreviewRevision;
    public long ContentVersion { get; private set; }
    public bool HasPendingChanges { get; private set; }

    public EditorSession(SceneDocument? document = null, AssetLibrary? assets = null, int historyLimit = 100, long historyByteLimit = 256L * 1024 * 1024)
    {
        if (historyLimit < 0 || historyByteLimit < 0) throw new ArgumentOutOfRangeException(nameof(historyLimit));
        Document = document ?? new(); Assets = assets ?? new(); HistoryLimit = historyLimit; HistoryByteLimit = historyByteLimit;
    }
    public SceneDocument Document { get; private set; }
    public AssetLibrary Assets { get; }
    public HashSet<Guid> Selection { get; } = [];
    public int HistoryLimit { get; }
    public long HistoryByteLimit { get; }
    public long HistoryBytes => undo.Sum(e => e.Bytes) + redo.Sum(e => e.Bytes) + undo.Concat(redo).SelectMany(e => e.AssetIds).Distinct(StringComparer.Ordinal).Sum(id => Assets.TryGet(id, out var asset) ? (long)asset.Data.Length : 0L);
    public int UndoCount => undo.Count;
    public int RedoCount => redo.Count;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public bool IsTransactionActive => transactionBefore is not null;
    /// <summary>Changes after committed content, rollback or transaction-external content notification; selection alone does not change it.</summary>
    public long SceneRevision { get; private set; }
    /// <summary>Every content notification, including intermediate gesture values. Selection does not advance it.</summary>
    public long PreviewRevision { get; private set; }
    /// <summary>Latest preview revision which can have changed elements rather than only scene settings.</summary>
    public long PreviewElementsRevision { get; private set; }
    /// <summary>Monotonic identity for a transaction, including a transaction later cancelled.</summary>
    public long TransactionId { get; private set; }
    public string? UndoLabel => undo.LastOrDefault()?.Label;
    public string? RedoLabel => redo.LastOrDefault()?.Label;
    public event EventHandler? Changed;

    /// <summary>An isolated last committed state, excluding any in-progress gesture.</summary>
    public SceneDocument CreateCommittedSnapshot() => (transactionBefore ?? Document).Clone();

    public void BeginTransaction(string label)
    {
        if (IsTransactionActive) throw new InvalidOperationException(L.Get("History.TransactionAlreadyActive"));
        transactionContentVersion = ContentVersion; transactionPreviewRevision = PreviewRevision; HasPendingChanges = false;
        transactionBefore = Document.Clone(); transactionSelection = [.. Selection]; transactionLabel = label; TransactionId++;
    }
    public void Commit() => CommitCore(settingsOnly: false);
    /// <summary>
    /// Commit a scene-wide settings edit without changing elements, background, groups or display layout.
    /// A violated declaration conservatively invalidates preview nodes; undo semantics stay unchanged.
    /// </summary>
    public void CommitSettings() => CommitCore(settingsOnly: true);
    private void CommitCore(bool settingsOnly, long? restoredContentVersion = null, bool restoringDocument = false)
    {
        if (transactionBefore is null) throw new InvalidOperationException(L.Get("History.NoActiveTransaction"));
        var before = transactionBefore; var selectedBefore = transactionSelection!;
        try { Document.PruneGroups(); SceneLimits.ValidateCapacity(Document); }
        catch { Cancel(); throw; }
        PruneSelection();
        bool knownSettings = settingsOnly && PreviewElementsRevision <= transactionPreviewRevision && SettingsElementsUnchanged(before);
        // Explicit restoration also restores save-point identity. Equal scene
        // content can still belong to a different, unsaved history version.
        if (!restoringDocument && SceneContent.Equal(before, Document))
        {
            transactionBefore = null; transactionSelection = null; HasPendingChanges = false;
            // Even a net-zero gesture may have published an intermediate preview before
            // its final unnotified mutation. Preserve history while invalidating that frame.
            if (PreviewRevision != transactionPreviewRevision) RecordPreviewChange(elementsChanged: !knownSettings);
            OnChanged(); return;
        }
        var after = Document.Clone();
        transactionBefore = null; transactionSelection = null; HasPendingChanges = false;
        ContentVersion = restoredContentVersion ?? ++nextContentVersion;
        redo.Clear();
        undo.Add(new(transactionLabel, before, after, selectedBefore, [.. Selection], EstimateBytes(before) + EstimateBytes(after), ++sequence, before.ReferencedAssets.Concat(after.ReferencedAssets).Distinct(StringComparer.Ordinal).ToArray(), transactionContentVersion, ContentVersion));
        TrimHistory(); OnChanged(contentChanged: true, preserveContentVersion: true, previewElementsChanged: !knownSettings);
    }
    public void Cancel()
    {
        if (transactionBefore is null) return;
        Document = transactionBefore; SetSelectionCore(transactionSelection!); transactionBefore = null; transactionSelection = null; HasPendingChanges = false; ContentVersion = transactionContentVersion; OnChanged(contentChanged: true, preserveContentVersion: true);
    }
    public void Execute(string label, Action<SceneDocument> operation)
    {
        BeginTransaction(label);
        try { operation(Document); Commit(); }
        catch { Cancel(); throw; }
    }
    /// <summary>
    /// Execute an atomic canvas/viewport/opacity setting edit. Elements, background, groups and
    /// display layout must remain unchanged; unexpected object changes invalidate preview reuse.
    /// </summary>
    public void ExecuteSettings(string label, Action<SceneDocument> operation)
    {
        BeginTransaction(label);
        try { operation(Document); CommitSettings(); }
        catch { Cancel(); throw; }
    }
    /// <summary>
    /// Apply an atomic resource edit and retain its inverse in the same chronological history as
    /// scene edits. Callbacks must leave resource state unchanged when they throw. Resource entries
    /// do not snapshot, dirty or restore the document or its selection.
    /// </summary>
    public void ExecuteExternal(string label, Action apply, Action undoOperation, Action redoOperation, long estimatedBytes = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(undoOperation);
        ArgumentNullException.ThrowIfNull(redoOperation);
        if (estimatedBytes < 0) throw new ArgumentOutOfRangeException(nameof(estimatedBytes));
        if (IsTransactionActive) throw new InvalidOperationException(L.Get("History.TransactionAlreadyActive"));
        apply();
        redo.Clear();
        undo.Add(new(label, null, null, null, null, estimatedBytes, ++sequence, [], ContentVersion, ContentVersion, undoOperation, redoOperation));
        TrimHistory(); OnChanged();
    }
    /// <summary>
    /// Notify after direct document mutations. Outside a transaction this also invalidates committed
    /// render caches; inside a transaction only the preview changes until commit or cancellation.
    /// Prefer Execute for user edits so changes also participate in undo and recovery.
    /// </summary>
    public void NotifyChanged() => OnChanged(contentChanged: true);
    /// <summary>For a scene-wide setting edit which leaves every element unchanged.</summary>
    public void NotifySettingsChanged() => OnChanged(contentChanged: true, previewElementsChanged: false);
    /// <summary>Refresh output layout metadata without treating a screen change as a new user edit.</summary>
    public void NotifyDisplayLayoutChanged() => OnChanged(contentChanged: true, preserveContentVersion: true, previewElementsChanged: false);
    public bool Undo()
    {
        Cancel();
        if (!CanUndo) return false;
        var item = undo[^1];
        if (item.UndoOperation is not null) item.UndoOperation();
        else { Document = item.Before!.Clone(); ContentVersion = item.BeforeVersion; SetSelectionCore(item.SelectionBefore!); }
        undo.RemoveAt(undo.Count - 1); redo.Add(item);
        OnChanged(contentChanged: item.UndoOperation is null, preserveContentVersion: true); return true;
    }
    public bool Redo()
    {
        Cancel();
        if (!CanRedo) return false;
        var item = redo[^1];
        if (item.RedoOperation is not null) item.RedoOperation();
        else { Document = item.After!.Clone(); ContentVersion = item.AfterVersion; SetSelectionCore(item.SelectionAfter!); }
        redo.RemoveAt(redo.Count - 1); undo.Add(item);
        OnChanged(contentChanged: item.RedoOperation is null, preserveContentVersion: true); return true;
    }
    public void ReplaceDocument(SceneDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        transactionBefore = null; transactionSelection = null; Document = document; undo.Clear(); redo.Clear(); Selection.Clear(); OnChanged(contentChanged: true);
    }
    /// <summary>Restore a snapshot as one undoable edit. A known version must identify that exact saved content.</summary>
    public void RestoreDocument(string label, SceneDocument document, long? knownContentVersion = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (knownContentVersion is { } version && (version < 0 || version > nextContentVersion))
            throw new ArgumentOutOfRangeException(nameof(knownContentVersion));
        BeginTransaction(label);
        try { Document = document.Clone(); Selection.Clear(); CommitCore(settingsOnly: false, knownContentVersion, restoringDocument: true); }
        catch { Cancel(); throw; }
    }
    public void SetSelection(IEnumerable<Guid> ids) { SetSelectionCore(ids); PruneSelection(); OnChanged(); }
    public void SelectAll() => SetSelection(Document.Elements.Where(e => e.Visible && !e.Locked).Select(e => e.Id));
    public CanvasElement? HitTest(PointD point, double tolerance = 4) => Document.Elements.LastOrDefault(e => !e.Locked && ElementGeometry.HitTest(e, point, tolerance));
    public void SelectAt(PointD point, bool additive = false, double tolerance = 4, bool individual = false)
    {
        var element = HitTest(point, tolerance);
        if (!additive) Selection.Clear();
        if (element is not null)
        {
            var unit = SelectionUnitIds(element.Id, individual).ToArray();
            if (additive && unit.All(Selection.Contains)) Selection.ExceptWith(unit); else Selection.UnionWith(unit);
        }
        OnChanged();
    }
    public void SelectRectangle(RectD bounds, bool additive = false)
    {
        if (!additive) Selection.Clear();
        foreach (var element in Document.Elements.Where(e => e.Visible && !e.Locked && ElementGeometry.Bounds(e).Intersects(bounds)))
            Selection.UnionWith(SelectionUnitIds(element.Id));
        OnChanged();
    }
    public RectD? SelectionBounds()
    {
        var selected = SelectedElements().ToArray();
        return selected.Length == 0 ? null : selected.Select(e => ElementGeometry.Bounds(e)).Aggregate(RectD.Union);
    }
    public IEnumerable<CanvasElement> SelectedElements() => Document.Elements.Where(e => Selection.Contains(e.Id));
    public void Add(CanvasElement element, string? label = null, int numberStep = 1)
    {
        Execute(label ?? L.Get("History.AddElement"), d =>
        {
            if (d.Elements.Any(e => e.Id == element.Id)) throw new ArgumentException(L.Get("History.DuplicateElementId"), nameof(element));
            d.Elements.Add(element.Clone()); Selection.Clear(); Selection.Add(element.Id);
            if (element.Kind == ElementKind.Number) d.NextNumber = NumberSequence.Advance(d.NextNumber, element.Number, numberStep);
        });
    }
    public void DeleteSelection() => Execute(L.Get("History.DeleteElements"), d => { d.Elements.RemoveAll(e => Selection.Contains(e.Id) && !e.Locked); Selection.Clear(); });
    public void TransformSelection(Affine2D transform, string? label = null) => Execute(label ?? L.Get("History.TransformElements"), _ => ApplySelectionTransform(transform));
    /// <summary>For live gesture updates inside an existing transaction; transform is relative to current positions.</summary>
    public void ApplySelectionTransform(Affine2D transform)
    {
        var selected = SelectedElements().Where(e => !e.Locked).ToArray();
        var candidates = selected.Select(e => e.Clone()).ToArray();
        foreach (var candidate in candidates) BillboardGeometry.ApplyPlaneTransform(candidate, transform);
        EnsureSafeInsertion(candidates);
        foreach (var element in selected) BillboardGeometry.ApplyPlaneTransform(element, transform);
        OnChanged(contentChanged: true);
    }
    public void UpdateSelection(Action<CanvasElement> update, string? label = null) => Execute(label ?? L.Get("History.UpdateProperties"), _ => { foreach (var e in SelectedElements().Where(e => !e.Locked)) update(e); });
    public void SetLocked(bool locked) => Execute(locked ? L.Get("History.LockElements") : L.Get("History.UnlockElements"), _ => { foreach (var e in SelectedElements()) e.Locked = locked; });
    public void SetVisible(bool visible) => Execute(visible ? L.Get("History.ShowElements") : L.Get("History.HideElements"), _ => { foreach (var e in SelectedElements()) e.Visible = visible; });
    public void DuplicateSelection(double offsetX = 16, double offsetY = 16)
    {
        var snapshot = Document.CloneElements(SelectedElements().Where(e => !e.Locked).Select(e => e.Id), true);
        foreach (var clone in snapshot.Elements) BillboardGeometry.ApplyPlaneTransform(clone, Affine2D.Translation(offsetX, offsetY));
        EnsureSafeInsertion(snapshot.Elements);
        Execute(L.Get("History.DuplicateElements"), d =>
        {
            var clones = snapshot.Elements;
            d.Groups.AddRange(snapshot.Groups);
            Selection.Clear();
            foreach (var clone in clones) { d.Elements.Add(clone); Selection.Add(clone.Id); }
        });
    }
    private void EnsureSafeInsertion(IEnumerable<CanvasElement> elements)
    {
        if (!Document.Canvas.Projection.Enabled) return;
        var mapping = CanvasMapping.Create(Document);
        if (elements.Any(e => !mapping.IsElementSafe(e))) throw new InvalidOperationException(L.Get("Perspective.InputOutsidePlane"));
    }
    public void ReorderSelection(LayerOrder order)
    {
        Execute(L.Get("History.ReorderElements"), d =>
        {
            bool Selected(CanvasElement e) => Selection.Contains(e.Id) && !e.Locked;
            if (order is LayerOrder.Front or LayerOrder.Back)
            {
                var selected = d.Elements.Where(Selected).ToList(); d.Elements.RemoveAll(e => Selected(e));
                if (order == LayerOrder.Front) d.Elements.AddRange(selected); else d.Elements.InsertRange(0, selected);
            }
            else if (order == LayerOrder.Forward)
            {
                for (int i = d.Elements.Count - 2; i >= 0; i--) if (Selected(d.Elements[i]) && !Selected(d.Elements[i + 1])) (d.Elements[i], d.Elements[i + 1]) = (d.Elements[i + 1], d.Elements[i]);
            }
            else for (int i = 1; i < d.Elements.Count; i++) if (Selected(d.Elements[i]) && !Selected(d.Elements[i - 1])) (d.Elements[i], d.Elements[i - 1]) = (d.Elements[i - 1], d.Elements[i]);
        });
    }
    public IEnumerable<string> ReferencedAssetsIncludingHistory() => Document.ReferencedAssets.Concat(undo.Concat(redo).SelectMany(e => e.AssetIds)).Concat(transactionBefore?.ReferencedAssets ?? []).Distinct(StringComparer.Ordinal);
    private void SetSelectionCore(IEnumerable<Guid> ids) { var copy = ids.ToArray(); Selection.Clear(); Selection.UnionWith(copy); }
    private void PruneSelection() => Selection.IntersectWith(Document.Elements.Select(e => e.Id));
    private void TrimHistory()
    {
        while (undo.Count + redo.Count > HistoryLimit || HistoryBytes > HistoryByteLimit)
        {
            var oldestUndo = undo.MinBy(e => e.Sequence); var oldestRedo = redo.MinBy(e => e.Sequence);
            if (oldestUndo is null && oldestRedo is null) break;
            if (oldestRedo is null || oldestUndo is not null && oldestUndo.Sequence < oldestRedo.Sequence) undo.Remove(oldestUndo!); else redo.Remove(oldestRedo);
        }
    }
    public static long EstimateBytes(SceneDocument document)
    {
        long size = 256 + document.DisplayLayout.Count * 128 + document.Groups.Sum(g => 64L + g.Name.Length * 2L);
        foreach (var e in document.Elements.Append(document.Background).Where(e => e is not null))
            size += 512 + e!.Points.Count * 16L + e.Text.Length * 2L + (e.Name?.Length ?? 0) * 2L + e.Style.FontFamily.Length * 2L + (e.AssetId?.Length ?? 0) * 2L + e.Effects.Count * 96L;
        return size;
    }
    private void OnChanged(bool contentChanged = false, bool preserveContentVersion = false, bool previewElementsChanged = true,
        IReadOnlySet<Guid>? previewChangedElements = null)
    {
        if (contentChanged) RecordPreviewChange(previewElementsChanged, previewChangedElements);
        if (contentChanged && IsTransactionActive) HasPendingChanges = true;
        if (contentChanged && !IsTransactionActive) { SceneRevision++; if (!preserveContentVersion) ContentVersion = ++nextContentVersion; }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
