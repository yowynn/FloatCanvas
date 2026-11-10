using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Core;

/// <summary>One editable document and atomic user operations. Assets are never copied into history.</summary>
public sealed partial class EditorSession
{
    private sealed record HistoryEntry(string Label, SceneDocument? Before, SceneDocument? After, HashSet<Guid>? SelectionBefore, HashSet<Guid>? SelectionAfter, long Bytes, long Sequence, string[] AssetIds, long BeforeVersion, long AfterVersion, Action? UndoOperation = null, Action? RedoOperation = null, CanvasViewHistoryChange? Viewing = null);
    private readonly List<HistoryEntry> undo = [], redo = [];
    private SceneDocument? transactionBefore;
    private HashSet<Guid>? transactionSelection;
    private string transactionLabel = "";
    private long sequence, nextContentVersion, transactionContentVersion, transactionPreviewRevision;
    public long ContentVersion { get; private set; }
    /// <summary>Runtime document replacement identity, unchanged by edits and undo.</summary>
    public long DocumentGeneration { get; private set; }
    public long HistoryNavigationVersion { get; private set; }
    public bool HasPendingChanges { get; private set; }

    public EditorSession(SceneDocument? document = null, AssetLibrary? assets = null, int historyLimit = 100, long historyByteLimit = 256L * 1024 * 1024, bool readOnly = false,
        PointD independentCanvasOrigin = default)
    {
        if (historyLimit < 0 || historyByteLimit < 0) throw new ArgumentOutOfRangeException(nameof(historyLimit));
        Document = document ?? new(); Assets = assets ?? new(); HistoryLimit = historyLimit; HistoryByteLimit = historyByteLimit;
        Document.PruneGroups();
        Document.IndependentCanvasOrigin = independentCanvasOrigin;
        IsReadOnly = readOnly;
    }
    public SceneDocument Document { get; private set; }
    public AssetLibrary Assets { get; }
    public bool IsReadOnly { get; }
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
        SceneGraph.InvalidateReads(Document);
        if (IsReadOnly) throw new InvalidOperationException(L.Get("GroupCanvas.ReadOnly"));
        EnsureWritableParent(EditScopeId);
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
    private void CommitCore(bool settingsOnly, long? restoredContentVersion = null, bool restoringDocument = false, CanvasViewHistoryChange? viewing = null)
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
            if (viewing is not null) CanvasViewRestored?.Invoke(viewing.After);
            OnChanged(); return;
        }
        var after = Document.Clone();
        transactionBefore = null; transactionSelection = null; HasPendingChanges = false;
        ContentVersion = restoredContentVersion ?? ++nextContentVersion;
        redo.Clear();
        undo.Add(new(transactionLabel, before, after, selectedBefore, [.. Selection], EstimateBytes(before) + EstimateBytes(after), ++sequence, before.ReferencedAssets.Concat(after.ReferencedAssets).Distinct(StringComparer.Ordinal).ToArray(), transactionContentVersion, ContentVersion, Viewing: viewing));
        if (viewing is not null) CanvasViewRestored?.Invoke(viewing.After);
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
        if (IsReadOnly) throw new InvalidOperationException(L.Get("GroupCanvas.ReadOnly"));
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
        undo.RemoveAt(undo.Count - 1); redo.Add(item); HistoryNavigationVersion++;
        PruneSelection();
        if (item.Viewing is { } viewing) CanvasViewRestored?.Invoke(viewing.Before);
        OnChanged(contentChanged: item.UndoOperation is null, preserveContentVersion: true); return true;
    }
    public bool Redo()
    {
        Cancel();
        if (!CanRedo) return false;
        var item = redo[^1];
        if (item.RedoOperation is not null) item.RedoOperation();
        else { Document = item.After!.Clone(); ContentVersion = item.AfterVersion; SetSelectionCore(item.SelectionAfter!); }
        redo.RemoveAt(redo.Count - 1); undo.Add(item); HistoryNavigationVersion++;
        PruneSelection();
        if (item.Viewing is { } viewing) CanvasViewRestored?.Invoke(viewing.After);
        OnChanged(contentChanged: item.RedoOperation is null, preserveContentVersion: true); return true;
    }
    public void ReplaceDocument(SceneDocument document)
    {
        if (IsReadOnly) throw new InvalidOperationException(L.Get("GroupCanvas.ReadOnly"));
        ArgumentNullException.ThrowIfNull(document);
        document.PruneGroups();
        transactionBefore = null; transactionSelection = null; Document = document; DocumentGeneration++; undo.Clear(); redo.Clear(); Selection.Clear();
        ActiveRootCanvas = RootCanvasKind.Main; editScopeId = null; editScopeAncestors = []; EditScopeVersion++; OnChanged(contentChanged: true);
    }
    /// <summary>Restore a snapshot as one undoable edit. A known version must identify that exact saved content.</summary>
    public void RestoreDocument(string label, SceneDocument document, long? knownContentVersion = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        // A restore edits this session; changing the slot's kind requires opening a new session.
        if (document.Kind != Document.Kind) throw new ArgumentException(L.Get("Document.RestoreKindMismatch"), nameof(document));
        if (knownContentVersion is { } version && (version < 0 || version > nextContentVersion))
            throw new ArgumentOutOfRangeException(nameof(knownContentVersion));
        BeginTransaction(label);
        try { Document = document.Clone(); Selection.Clear(); CommitCore(settingsOnly: false, knownContentVersion, restoringDocument: true); }
        catch { Cancel(); throw; }
    }
    public void SetSelection(IEnumerable<Guid> ids) { SetSelectionCore(ids); PruneSelection(); OnChanged(); }
    public void SelectAll()
    {
        var graph = new SceneGraph(Document);
        SetSelection(graph.EnumerateChildren(EditScopeId).Where(id => InActiveRoot(graph, id) && graph.IsVisible(id) && !graph.IsLocked(id)));
    }
    public CanvasElement? HitTest(PointD point, double tolerance = 4)
    {
        var graph = new SceneGraph(Document);
        if (Document.Instances.Count > 0)
        {
            var evaluated = SceneEvaluation.Build(Document);
            var hit = evaluated.Leaves.Reverse().FirstOrDefault(leaf => leaf.RootCanvas == ActiveRootCanvas && leaf.RenderElement.Visible && !graph.IsLocked(leaf.HostNodeId) &&
                evaluated.ResolveSelectableNode(leaf.RuntimeElementId, EditScopeId) is not null && ElementGeometry.HitTest(leaf.RenderElement, point, tolerance));
            return hit is null ? null : graph.FindElement(hit.RuntimeElementId) ?? hit.RenderElement.Clone();
        }
        var id = graph.OrderedLeafIds.Reverse().FirstOrDefault(id => InActiveRoot(graph, id) && graph.IsVisible(id) && !graph.IsLocked(id) &&
            graph.ResolveSelectableNode(id, EditScopeId) is not null && ElementGeometry.HitTest(graph.ToWorld(graph.FindElement(id)!), point, tolerance));
        return graph.FindElement(id);
    }
    public void SelectAt(PointD point, bool additive = false, double tolerance = 4, bool individual = false)
    {
        var element = HitTest(point, tolerance);
        if (!additive) Selection.Clear();
        if (element is not null)
        {
            var graph = new SceneGraph(Document);
            var unit = graph.FindElement(element.Id) is not null ? SelectionUnitIds(element.Id, individual).ToArray() :
                SceneEvaluation.Build(Document).ResolveSelectableNode(element.Id, EditScopeId) is { } node ? [node] : Array.Empty<Guid>();
            if (additive && unit.All(Selection.Contains)) Selection.ExceptWith(unit); else Selection.UnionWith(unit);
        }
        SetSelectionCore(new SceneGraph(Document).NormalizeSelection(Selection));
        OnChanged();
    }
    public void SelectRectangle(RectD bounds, bool additive = false)
    {
        if (!additive) Selection.Clear();
        var graph = new SceneGraph(Document);
        if (Document.Instances.Count > 0)
        {
            var evaluated = SceneEvaluation.Build(Document);
            foreach (var leaf in evaluated.Leaves)
                if (leaf.RootCanvas == ActiveRootCanvas && leaf.RenderElement.Visible && !graph.IsLocked(leaf.HostNodeId) && WorldBounds(leaf.RenderElement).Intersects(bounds) &&
                    evaluated.ResolveSelectableNode(leaf.RuntimeElementId, EditScopeId) is { } node) Selection.Add(node);
        }
        else foreach (var id in graph.OrderedLeafIds)
                if (InActiveRoot(graph, id) && graph.IsVisible(id) && !graph.IsLocked(id) && WorldBounds(graph.ToWorld(graph.FindElement(id)!)).Intersects(bounds) && graph.ResolveSelectableNode(id, EditScopeId) is { } node) Selection.Add(node);
        SetSelectionCore(graph.NormalizeSelection(Selection));
        OnChanged();
    }
    public RectD? SelectionBounds(bool editableOnly = false)
    {
        var selected = SelectedWorldElements(editableOnly).ToArray();
        return selected.Length == 0 ? null : selected.Select(WorldBounds).Aggregate(RectD.Union);
    }
    public IEnumerable<CanvasElement> SelectedElements()
    {
        if (Selection.Count == 0) return [];
        var graph = SceneGraph.Read(Document); var ids = ExpandedSelectionIds(graph);
        return graph.OrderedLeafIds.Where(ids.Contains).Select(id => graph.FindElement(id)!);
    }
    public void Add(CanvasElement element, string? label = null, int numberStep = 1)
    {
        Execute(label ?? L.Get("History.AddElement"), d =>
        {
            EnsureWritableParent(EditScopeId);
            if (d.Elements.Any(e => e.Id == element.Id)) throw new ArgumentException(L.Get("History.DuplicateElementId"), nameof(element));
            new SceneGraph(d).InsertElement(element, EditScopeId, rootCanvas: ActiveRootCanvas); Selection.Clear(); Selection.Add(element.Id);
            if (element.Kind == ElementKind.Number) d.NextNumber = NumberSequence.Advance(d.NextNumber, element.Number, numberStep);
        });
    }
    public void DeleteSelection() => Execute(L.Get("History.DeleteElements"), d =>
    {
        var graph = new SceneGraph(d); graph.RemoveNodes(graph.NormalizeSelection(Selection).Where(id => !graph.IsLocked(id)).ToArray()); Selection.Clear();
    });
    public void TransformSelection(Affine2D transform, string? label = null) => Execute(label ?? L.Get("History.TransformElements"), _ => ApplySelectionTransform(transform));
    /// <summary>For live gesture updates inside an existing transaction; transform is relative to current positions.</summary>
    public void ApplySelectionTransform(Affine2D transform)
    {
        if (IsReadOnly) throw new InvalidOperationException(L.Get("GroupCanvas.ReadOnly"));
        if (!TryApplySelectionTransform(CaptureSelectionTransform(), transform)) throw new InvalidOperationException(L.Get("Perspective.InputOutsidePlane"));
    }
    public void UpdateSelection(Action<CanvasElement> update, string? label = null) => Execute(label ?? L.Get("History.UpdateProperties"), _ =>
    {
        var graph = new SceneGraph(Document);
        foreach (var element in SelectedElements().Where(element => !graph.IsLocked(element.Id))) update(element);
    });
    public void SetLocked(bool locked) => SetNodesFlag(Selection, false, locked);
    public void SetVisible(bool visible) => SetNodesFlag(Selection, true, visible);
    public void DuplicateSelection(double offsetX = 16, double offsetY = 16)
    {
        var snapshot = CreateSelectionSnapshot(normalizeOrigin: false);
        InsertDocument(snapshot, Assets, Affine2D.Translation(offsetX, offsetY), label: L.Get("History.DuplicateElements"), preserveDefinitions: true);
    }
    private void EnsureSafeInsertion(IEnumerable<CanvasElement> elements)
    {
        if (ActiveRootCanvas != RootCanvasKind.Main || !Document.Canvas.Projection.Enabled) return;
        var mapping = CanvasMapping.Create(Document, ActiveRootCanvas);
        if (elements.Any(e => !mapping.IsElementSafe(e))) throw new InvalidOperationException(L.Get("Perspective.InputOutsidePlane"));
    }
    public void ReorderSelection(LayerOrder order)
    {
        if (!WouldReorder(order)) return;
        Execute(L.Get("History.ReorderElements"), d =>
        {
            ReorderNodes(d, order);
        });
    }
    public IEnumerable<string> ReferencedAssetsIncludingHistory() => Document.ReferencedAssets.Concat(undo.Concat(redo).SelectMany(e => e.AssetIds)).Concat(transactionBefore?.ReferencedAssets ?? []).Distinct(StringComparer.Ordinal);
    private void SetSelectionCore(IEnumerable<Guid> ids) { var copy = ids.ToArray(); Selection.Clear(); Selection.UnionWith(copy); }
    private void PruneSelection()
    {
        ValidateEditScope();
        var graph = new SceneGraph(Document);
        SetSelectionCore(graph.NormalizeSelection(Selection).Where(id => InActiveRoot(graph, id)));
    }
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
        long size = 256 + document.DisplayLayout.Count * 128 + document.RootCanvasAssignments.Count * 64L;
        var previews = document.RootPreviewCanvases.Values.Concat(document.Definitions.Select(definition => definition.PreviewCanvas)
            .OfType<SubprojectCanvasSettings>()).Concat(document.Definitions.Select(definition => definition.Graph).Prepend(document)
                .SelectMany(graph => graph.Groups).Select(group => group.PreviewCanvas).OfType<SubprojectCanvasSettings>());
        foreach (var preview in previews)
            size += 2048L + preview.Canvas.Projection.Quad.Count * 16L + (preview.Background is { } image ?
                512L + (image.Points.Count + (long)(image.ImageFrame?.Count ?? 0)) * 16L + (image.AssetId?.Length ?? 0) * 2L : 0);
        foreach (var graph in document.Definitions.Select(definition => definition.Graph).Prepend(document))
        {
            size += graph.Groups.Sum(group => 128L + group.Name.Length * 2L + (group.ChildOrder?.Count ?? 0) * 16L) +
                graph.Instances.Sum(instance => 160L + instance.Name.Length * 2L) + (graph.RootOrder?.Count ?? 0) * 16L;
            foreach (var e in graph.Elements)
                size += 512 + (e.Points.Count + (long)(e.ImageFrame?.Count ?? 0)) * 16L + e.Text.Length * 2L + (e.Name?.Length ?? 0) * 2L + e.Style.FontFamily.Length * 2L + (e.AssetId?.Length ?? 0) * 2L + e.Effects.Count * 96L +
                    e.Style.AdditionalColors.Sum(pair => 64L + pair.Key.Length * 2L) + (e.Presentation is null ? 0 : 512L) + WindowSourceLimits.EstimateBytes(e.WindowSource);
        }
        if (document.Background is { } background) size += 512 + (background.Points.Count + (long)(background.ImageFrame?.Count ?? 0)) * 16L + (background.AssetId?.Length ?? 0) * 2L +
            background.Style.AdditionalColors.Sum(pair => 64L + pair.Key.Length * 2L);
        size += document.Definitions.Sum(definition => 256L + definition.Name.Length * 2L + (definition.Source?.Locator?.Length ?? 0) * 2L +
            (definition.Source?.ImportedDefinitionIds.Count ?? 0) * 64L + (definition.Source?.OriginClosure.Count ?? 0) * 48L);
        return size;
    }
    private void OnChanged(bool contentChanged = false, bool preserveContentVersion = false, bool previewElementsChanged = true,
        IReadOnlySet<Guid>? previewChangedElements = null)
    {
        SceneGraph.InvalidateReads(Document);
        if (contentChanged && !IsTransactionActive) Document.PruneGroups();
        ValidateEditScope();
        if (contentChanged) RecordPreviewChange(previewElementsChanged, previewChangedElements);
        if (contentChanged && IsTransactionActive) HasPendingChanges = true;
        if (contentChanged && !IsTransactionActive) { SceneRevision++; if (!preserveContentVersion) ContentVersion = ++nextContentVersion; }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
