using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private GroupCanvasMode _groupCanvasMode = GroupCanvasMode.RootProject;
    private string MissingOccurrenceNotice => L.Get(_prefabEditContext is { } current && !current.Owner.Document.Instances.Any(i => i.DefinitionId == current.DefinitionId)
        ? "LayerExperience.Unplaced" : "LayerExperience.NoOccurrence");
    private bool HasGroupCanvasContext => _prefabEditContext is not null || Active.EditScopeId is not null || _groupCanvasMode == GroupCanvasMode.Temporary;

    private void RefreshGroupCanvasContext()
    {
        if (_layers is null) return;
        var owner = Active; var document = owner.Document; var scope = owner.EditScopeId; var context = _prefabEditContext;
        long scopeVersion = owner.EditScopeVersion;
        var graph = SceneGraph.Read(document);
        Guid? node = scope;
        bool entered = scope is not null || context is not null;
        bool Current() => !DocumentOperationBusy && !owner.IsTransactionActive && ReferenceEquals(Active, owner) &&
            ReferenceEquals(owner.Document, document) && owner.EditScopeId == scope && owner.EditScopeVersion == scopeVersion && ReferenceEquals(_prefabEditContext, context) &&
            (entered || node is null || owner.Selection.SetEquals([node.Value]));
        Action View(GroupCanvasMode mode) => () =>
        {
            if (Current()) _ = RunCommand(() => OpenGroupCanvasAsync(owner, entered ? null : node, mode));
        };
        if (!entered)
        {
            _layers.SetGroupContext(new(L.Get("RootCanvas." + owner.ActiveRootCanvas), GroupStorageKind.Ordinary, null,
                _groupCanvasMode, false, View(GroupCanvasMode.Temporary), View(GroupCanvasMode.RootProject),
                IsReadOnly: owner.IsReadOnly, IsRootContext: true));
            return;
        }
        if (node is { } groupId && graph.FindGroup(groupId) is { } group)
        {
            string name = string.IsNullOrWhiteSpace(group.Name) ? L.Get("Layers.UntitledGroup") : group.Name;
            _layers.SetGroupContext(new(name, GroupStorageKind.Ordinary, null, _groupCanvasMode, entered,
                View(GroupCanvasMode.Temporary), View(GroupCanvasMode.RootProject),
                () => { if (Current()) _ = RunCommand(() => ExportGroupAsync(owner, groupId)); }, IsReadOnly: owner.IsReadOnly || graph.IsLocked(groupId), Notice: graph.IsLocked(groupId) ? L.Get(group.Locked ? "LayerExperience.LockedBrowse" : "LayerExperience.AncestorLockedBrowse") : null));
            return;
        }
        PrefabInstance? instance = node is { } instanceId ? graph.FindInstance(instanceId) : null;
        var definition = instance is not null ? document.Definitions.FirstOrDefault(item => item.Id == instance.DefinitionId) :
            entered && context is not null ? context.Owner.Document.Definitions.FirstOrDefault(item => item.Id == context.DefinitionId) : null;
        if (definition is null) { _layers.SetGroupContext(null); return; }
        bool external = definition.Source?.IsLinked == true;
        string? sourcePath = external ? context is not null && node is null ? context.SourcePath :
            GroupSourcePath(definition.Source!, owner) : null;
        string displayName = instance is not null && !string.IsNullOrWhiteSpace(instance.Name) ? instance.Name : context?.DisplayName ?? definition.Name;
        bool locked = instance is not null && graph.IsLocked(instance.Id);
        Action? detach = instance is not null ? () =>
        {
            if (Current()) RunUiAction(() => DetachGroupInstance(owner, instance.Id));
        } : context?.EnteredInstanceId is not null && !context.Owner.IsReadOnly ? () =>
        {
            if (Current()) _ = RunCommand(() => DetachEditingGroupAsync(context));
        } : null;
        _layers.SetGroupContext(new(displayName, external ? GroupStorageKind.ExternalFile : GroupStorageKind.Embedded, sourcePath,
            _groupCanvasMode, entered, View(GroupCanvasMode.Temporary), View(GroupCanvasMode.RootProject),
            external ? null : () =>
            {
                if (!Current()) return;
                _ = RunCommand(() => context is not null && node is null ? ExportPrefabDraftAsync(context) : ExportPrefabAsync(definition.Id));
            }, locked ? null : detach, IsReadOnly: owner.IsReadOnly,
            Embed: external && context is not null && !context.Owner.IsReadOnly ? () =>
            {
                if (Current()) _ = RunCommand(() => EmbedEditingGroupAsync(context));
            } : null, IsOfflineSnapshot: context?.IsOfflineSnapshot == true,
            Notice: context?.LockedBrowsing == true ? L.Get(context.InheritedLock ? "LayerExperience.AncestorLockedBrowse" : "LayerExperience.LockedBrowse") : context?.SnapshotReason is { } reason ? L.Get(reason) :
                context is not null && _groupCanvasMode == GroupCanvasMode.RootProject && CurrentGroupCanvasView().EditingPlacement is null ? MissingOccurrenceNotice : null,
            Retry: context?.IsOfflineSnapshot == true ? () => { if (Current()) _ = RunCommand(() => EditPrefabSourceCoreAsync(context.DefinitionId, context.EnteredInstanceId, context)); } : null, LockedBrowsing: context?.LockedBrowsing == true));
    }

    private async Task EmbedEditingGroupAsync(PrefabEditContext context)
    {
        if (!ReferenceEquals(_prefabEditContext, context) || context.Owner.IsReadOnly || DocumentOperationBusy || Active.IsTransactionActive) return;
        if (context.Draft is null)
        {
            // Convert exactly the source currently being viewed, not an older
            // accepted offline snapshot, without writing the external file.
            var plan = PrefabPreviewComposer.PrepareSource(context, context.Session.Document, _assets);
            var source = plan.Candidate.Definitions.Single(item => item.Id == context.DefinitionId).Source!;
            source.Locator = null; source.IsRelative = false;
            if (!ApplyPrefabPlan(context.Owner, plan)) return;
            ReturnFromPrefabContext(context);
        }
        else
        {
            if (!await CompleteCurrentPrefabAsync()) return;
            context.Owner.ConvertPrefabToEmbedded(context.DefinitionId);
        }
        await EditPrefabDefinitionCoreAsync(context.DefinitionId, context.EnteredInstanceId);
        ScheduleRecovery(); RequestRender();
    }

    private string GroupSourcePath(ExternalPrefabSource source, EditorSession owner)
    {
        try { return PrefabLinkResolver.ResolvePath(source, FileState(owner).Path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or System.IO.PathTooLongException or InvalidOperationException)
        {
            // A recovered relative locator can precede restoration of its owner's
            // save location. Keep its origin visible without breaking the panel.
            return L.Get("Prefab.SourceMissing") + "\n" + source.Locator;
        }
    }

    private async Task OpenGroupCanvasAsync(EditorSession owner, Guid? enterNode, GroupCanvasMode mode)
    {
        if (DocumentOperationBusy || owner.IsTransactionActive || !ReferenceEquals(owner, Active)) return;
        _inspector?.CompletePendingEdit(); FinishInlineText(); _editor.Cancel();
        if (enterNode is { } id)
        {
            var graph = new SceneGraph(owner.Document);
            if (graph.FindGroup(id) is not null) owner.SetEditScope(id);
            else if (graph.FindInstance(id) is not null)
            {
                await EditPrefabInstanceAsync(id);
                if (_prefabEditContext is not { } context || !ReferenceEquals(context.Owner, owner) || context.EnteredInstanceId != id) return;
            }
            else return;
        }
        SetGroupCanvasMode(mode);
    }

    private void SetGroupCanvasMode(GroupCanvasMode mode)
    {
        if (DocumentOperationBusy || Active.IsTransactionActive || mode == _groupCanvasMode) return;
        _inspector?.CompletePendingEdit(); FinishInlineText(); _editor.Cancel();
        EndCanvasCalibration(hidePanel: true); CancelLivePreparation(); ClearProjectionHover();
        _groupCanvasMode = mode;
        _propertiesClicks?.Reset();
        _editor.ClearPresentedScene(); _editor.SuspendPresentedPicking();
        // A mode change replaces the displayed graph without changing the
        // authored session. Refresh its runtime plans before capturing pixels.
        RefreshGroupCanvasContext(); OnEditorChanged();
    }

    private void DetachGroupInstance(EditorSession owner, Guid instanceId)
    {
        if (DocumentOperationBusy || owner.IsReadOnly || owner.IsTransactionActive || !ReferenceEquals(owner, Active) || owner.IsNodeLocked(instanceId)) return;
        owner.DetachPrefab(instanceId); owner.SetEditScope(instanceId);
        RefreshGroupCanvasContext(); ScheduleRecovery(); RequestRender();
    }

    private async Task DetachEditingGroupAsync(PrefabEditContext context)
    {
        if (!ReferenceEquals(_prefabEditContext, context) || context.EnteredInstanceId is not { } instanceId || DocumentOperationBusy || Active.IsTransactionActive) return;
        if (context.IsReadOnlyView && context.Draft is null)
        {
            var viewedSource = PrefabPreviewComposer.PrepareSource(context, context.Session.Document, _assets);
            var detached = context.Owner.PreparePrefabDetach(instanceId, viewedSource);
            if (!ApplyPrefabPlan(context.Owner, detached)) return;
            ReturnFromPrefabContext(context);
            context.Owner.SetEditScope(instanceId);
            RefreshGroupCanvasContext(); ScheduleRecovery(); RequestRender();
            return;
        }
        // Internal drafts publish on return; offline snapshots use their accepted
        // generation. Neither path writes an external source file.
        if (!await LeaveCurrentPrefabAsync()) return;
        if (!ReferenceEquals(Active, context.Owner) || new SceneGraph(Active.Document).FindInstance(instanceId) is not { } instance || instance.DefinitionId != context.DefinitionId) return;
        DetachGroupInstance(context.Owner, instanceId);
    }

    private async Task ExportGroupAsync(EditorSession owner, Guid groupId, string? selectedPath = null)
    {
        if (DocumentOperationBusy || owner.IsTransactionActive || !ReferenceEquals(owner, Active)) return;
        var group = new SceneGraph(owner.Document).FindGroup(groupId);
        if (group is null) return;
        string? path = selectedPath;
        if (path is null)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = L.Get("Prefab.Export"), Filter = L.Get("Prefab.Filter"), DefaultExt = ".floatprefab", AddExtension = true,
                FileName = string.IsNullOrWhiteSpace(group.Name) ? L.Get("Layers.UntitledGroup") : group.Name };
            if (SessionWindowCoordinator.ShowNativeModal(_toolbar, () => dialog.ShowDialog(_toolbar)) != true) return;
            path = dialog.FileName;
        }
        var package = owner.ExportGroupPrefab(groupId);
        var prepared = await PrepareWindowPrefabPackageAsync(owner, package);
        await _prefabStore.SaveAsync(path, prepared.Package, prepared.Assets, _documentLifetime.Token);
    }
}
