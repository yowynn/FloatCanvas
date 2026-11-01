using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private GroupCanvasMode _groupCanvasMode = GroupCanvasMode.Temporary;
    private bool HasGroupCanvasContext => _prefabEditContext is not null || Active.EditScopeId is not null;

    private void RefreshGroupCanvasContext()
    {
        if (_layers is null) return;
        var owner = Active; var document = owner.Document; var scope = owner.EditScopeId; var context = _prefabEditContext;
        long scopeVersion = owner.EditScopeVersion;
        var graph = new SceneGraph(document);
        Guid? node = scope;
        bool entered = scope is not null || context is not null;
        if (!entered) { _layers.SetGroupContext(null); return; }
        bool Current() => !DocumentOperationBusy && !owner.IsTransactionActive && ReferenceEquals(Active, owner) &&
            ReferenceEquals(owner.Document, document) && owner.EditScopeId == scope && owner.EditScopeVersion == scopeVersion && ReferenceEquals(_prefabEditContext, context) &&
            (entered || node is { } id && owner.Selection.SetEquals([id]));
        Action View(GroupCanvasMode mode) => () =>
        {
            if (Current()) _ = RunCommand(() => OpenGroupCanvasAsync(owner, entered ? null : node, mode));
        };
        if (node is { } groupId && graph.FindGroup(groupId) is { } group)
        {
            string name = string.IsNullOrWhiteSpace(group.Name) ? L.Get("Layers.UntitledGroup") : group.Name;
            _layers.SetGroupContext(new(name, GroupStorageKind.Ordinary, null, _groupCanvasMode, entered,
                graph.IsLocked(groupId) ? null : View(GroupCanvasMode.Temporary), graph.IsLocked(groupId) ? null : View(GroupCanvasMode.RootProject),
                () => { if (Current()) _ = RunCommand(() => ExportGroupAsync(owner, groupId)); }, IsReadOnly: owner.IsReadOnly));
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
            _groupCanvasMode, entered, locked ? null : View(GroupCanvasMode.Temporary), locked ? null : View(GroupCanvasMode.RootProject),
            external ? null : () =>
            {
                if (!Current()) return;
                _ = RunCommand(() => context is not null && node is null ? ExportPrefabDraftAsync(context) : ExportPrefabAsync(definition.Id));
            }, locked ? null : detach, IsReadOnly: owner.IsReadOnly));
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
            if (graph.IsLocked(id)) return;
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
        if (DocumentOperationBusy || Active.IsTransactionActive || !HasGroupCanvasContext) return;
        _inspector?.CompletePendingEdit(); FinishInlineText(); _editor.Cancel();
        EndCanvasCalibration(hidePanel: true); CancelLivePreparation(); ClearProjectionHover();
        _groupCanvasMode = mode;
        _propertiesClicks?.Reset();
        _editor.ClearPresentedScene(); _editor.SuspendPresentedPicking();
        RefreshGroupCanvasContext(); RequestRender();
    }

    private void DetachGroupInstance(EditorSession owner, Guid instanceId)
    {
        if (DocumentOperationBusy || owner.IsReadOnly || owner.IsTransactionActive || !ReferenceEquals(owner, Active) || owner.IsNodeLocked(instanceId)) return;
        owner.DetachPrefab(instanceId); owner.SetEditScope(instanceId);
        _groupCanvasMode = GroupCanvasMode.Temporary;
        RefreshGroupCanvasContext(); ScheduleRecovery(); RequestRender();
    }

    private async Task DetachEditingGroupAsync(PrefabEditContext context)
    {
        if (!ReferenceEquals(_prefabEditContext, context) || context.EnteredInstanceId is not { } instanceId || DocumentOperationBusy || Active.IsTransactionActive) return;
        // Leaving uses the existing save/discard/cancel flow; detaching never silently
        // writes an unfinished source edit or applies it to other instances.
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
