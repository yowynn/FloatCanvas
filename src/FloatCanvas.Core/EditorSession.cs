using L = FloatCanvas.Localization.AppText;
using System.Text.Json;

namespace FloatCanvas.Core;

/// <summary>One editable document and atomic user operations. Assets are never copied into history.</summary>
public sealed class EditorSession
{
    private sealed record HistoryEntry(string Label, SceneDocument Before, SceneDocument After, HashSet<Guid> SelectionBefore, HashSet<Guid> SelectionAfter, long Bytes, long Sequence, string[] AssetIds);
    private readonly List<HistoryEntry> undo = [], redo = [];
    private SceneDocument? transactionBefore;
    private HashSet<Guid>? transactionSelection;
    private string transactionLabel = "";
    private long sequence;

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
        transactionBefore = Document.Clone(); transactionSelection = [.. Selection]; transactionLabel = label; TransactionId++;
    }
    public void Commit()
    {
        if (transactionBefore is null) throw new InvalidOperationException(L.Get("History.NoActiveTransaction"));
        var before = transactionBefore; var selectedBefore = transactionSelection!;
        PruneSelection();
        if (JsonSerializer.SerializeToUtf8Bytes(before).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(Document)))
        {
            transactionBefore = null; transactionSelection = null; OnChanged(); return;
        }
        var after = Document.Clone();
        transactionBefore = null; transactionSelection = null;
        redo.Clear();
        undo.Add(new(transactionLabel, before, after, selectedBefore, [.. Selection], EstimateBytes(before) + EstimateBytes(after), ++sequence, before.ReferencedAssets.Concat(after.ReferencedAssets).Distinct(StringComparer.Ordinal).ToArray()));
        TrimHistory(); OnChanged(contentChanged: true);
    }
    public void Cancel()
    {
        if (transactionBefore is null) return;
        Document = transactionBefore; SetSelectionCore(transactionSelection!); transactionBefore = null; transactionSelection = null; OnChanged(contentChanged: true);
    }
    public void Execute(string label, Action<SceneDocument> operation)
    {
        BeginTransaction(label);
        try { operation(Document); Commit(); }
        catch { Cancel(); throw; }
    }
    /// <summary>
    /// Notify after direct document mutations. Outside a transaction this also invalidates committed
    /// render caches; inside a transaction only the preview changes until commit or cancellation.
    /// Prefer Execute for user edits so changes also participate in undo and recovery.
    /// </summary>
    public void NotifyChanged() => OnChanged(contentChanged: true);
    public bool Undo()
    {
        Cancel();
        if (!CanUndo) return false;
        var item = undo[^1]; undo.RemoveAt(undo.Count - 1); redo.Add(item);
        Document = item.Before.Clone(); SetSelectionCore(item.SelectionBefore); OnChanged(contentChanged: true); return true;
    }
    public bool Redo()
    {
        Cancel();
        if (!CanRedo) return false;
        var item = redo[^1]; redo.RemoveAt(redo.Count - 1); undo.Add(item);
        Document = item.After.Clone(); SetSelectionCore(item.SelectionAfter); OnChanged(contentChanged: true); return true;
    }
    public void ReplaceDocument(SceneDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        transactionBefore = null; transactionSelection = null; Document = document; undo.Clear(); redo.Clear(); Selection.Clear(); OnChanged(contentChanged: true);
    }
    public void SetSelection(IEnumerable<Guid> ids) { SetSelectionCore(ids); PruneSelection(); OnChanged(); }
    public void SelectAll() => SetSelection(Document.Elements.Where(e => e.Visible && !e.Locked).Select(e => e.Id));
    public CanvasElement? HitTest(PointD point, double tolerance = 4) => Document.Elements.LastOrDefault(e => !e.Locked && ElementGeometry.HitTest(e, point, tolerance));
    public void SelectAt(PointD point, bool additive = false, double tolerance = 4)
    {
        var element = HitTest(point, tolerance);
        if (!additive) Selection.Clear();
        if (element is not null && !Selection.Add(element.Id) && additive) Selection.Remove(element.Id);
        OnChanged();
    }
    public void SelectRectangle(RectD bounds, bool additive = false)
    {
        if (!additive) Selection.Clear();
        foreach (var element in Document.Elements.Where(e => e.Visible && !e.Locked && ElementGeometry.Bounds(e).Intersects(bounds))) Selection.Add(element.Id);
        OnChanged();
    }
    public RectD? SelectionBounds()
    {
        var selected = SelectedElements().ToArray();
        return selected.Length == 0 ? null : selected.Select(e => ElementGeometry.Bounds(e)).Aggregate(RectD.Union);
    }
    public IEnumerable<CanvasElement> SelectedElements() => Document.Elements.Where(e => Selection.Contains(e.Id));
    public void Add(CanvasElement element, string? label = null)
    {
        Execute(label ?? L.Get("History.AddElement"), d =>
        {
            if (d.Elements.Any(e => e.Id == element.Id)) throw new ArgumentException(L.Get("History.DuplicateElementId"), nameof(element));
            d.Elements.Add(element.Clone()); Selection.Clear(); Selection.Add(element.Id);
            if (element.Kind == ElementKind.Number) d.NextNumber = Math.Max(d.NextNumber, element.Number + 1);
        });
    }
    public void DeleteSelection() => Execute(L.Get("History.DeleteElements"), d => { d.Elements.RemoveAll(e => Selection.Contains(e.Id) && !e.Locked); Selection.Clear(); });
    public void TransformSelection(Affine2D transform, string? label = null) => Execute(label ?? L.Get("History.TransformElements"), _ => ApplySelectionTransform(transform));
    /// <summary>For live gesture updates inside an existing transaction; transform is relative to current positions.</summary>
    public void ApplySelectionTransform(Affine2D transform)
    {
        foreach (var element in SelectedElements().Where(e => !e.Locked)) element.Transform = transform.Compose(element.Transform);
        OnChanged(contentChanged: true);
    }
    public void UpdateSelection(Action<CanvasElement> update, string? label = null) => Execute(label ?? L.Get("History.UpdateProperties"), _ => { foreach (var e in SelectedElements().Where(e => !e.Locked)) update(e); });
    public void SetLocked(bool locked) => Execute(locked ? L.Get("History.LockElements") : L.Get("History.UnlockElements"), _ => { foreach (var e in SelectedElements()) e.Locked = locked; });
    public void SetVisible(bool visible) => Execute(visible ? L.Get("History.ShowElements") : L.Get("History.HideElements"), _ => { foreach (var e in SelectedElements()) e.Visible = visible; });
    public void DuplicateSelection(double offsetX = 16, double offsetY = 16)
    {
        Execute(L.Get("History.DuplicateElements"), d =>
        {
            var clones = SelectedElements().Where(e => !e.Locked).Select(e => e.Clone(true)).ToArray();
            Selection.Clear();
            foreach (var clone in clones) { clone.Transform = Affine2D.Translation(offsetX, offsetY).Compose(clone.Transform); d.Elements.Add(clone); Selection.Add(clone.Id); }
        });
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
    public IEnumerable<string> ReferencedAssetsIncludingHistory() => Document.ReferencedAssets.Concat(undo.Concat(redo).SelectMany(e => e.Before.ReferencedAssets.Concat(e.After.ReferencedAssets))).Concat(transactionBefore?.ReferencedAssets ?? []).Distinct(StringComparer.Ordinal);
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
        long size = 256 + document.DisplayLayout.Count * 128;
        foreach (var e in document.Elements.Append(document.Background).Where(e => e is not null))
            size += 512 + e!.Points.Count * 16L + e.Text.Length * 2L + e.Style.FontFamily.Length * 2L + (e.AssetId?.Length ?? 0) * 2L + e.Effects.Count * 96L;
        return size;
    }
    private void OnChanged(bool contentChanged = false)
    {
        if (contentChanged && !IsTransactionActive) SceneRevision++;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
