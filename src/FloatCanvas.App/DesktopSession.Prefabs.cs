using System.IO;
using System.Windows;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private PrefabEditContext? _prefabEditContext;
    private PrefabResourcesWindow? _prefabResources;
    private readonly PrefabLinkResolver _prefabLinkResolver = new();
    private readonly PrefabStore _prefabStore = new();
    private bool _changingPrefabContext;
    private long _prefabRequest;
    private PrefabEditContext? _shownPrefabContext;
    private LayersWindow? _shownPrefabLayers;
    private long _shownPrefabOwnerRevision = -1;
    private readonly HashSet<string> _prefabPendingSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(EditorSession Owner, Guid Definition), (PrefabLinkStatusBinding Binding, string Message)> _prefabLinkStates = [];
    private readonly Dictionary<EditorSession, long> _prefabBackgroundChecks = [];
    private bool _drainingPrefabSources;
    private bool HasPrefabEditContext => _prefabEditContext is not null;
    private bool IsPrefabRetainedSession(EditorSession session) => _prefabEditContext?.Chain().Any(context =>
        ReferenceEquals(context.Owner, session) || ReferenceEquals(context.Session, session)) == true;
    private IEnumerable<string> PrefabRetainedAssetIds() => _prefabEditContext?.RetainedAssets() ?? [];

    private void ConnectPrefabLayers(LayersWindow window)
    {
        window.PrefabResourcesRequested += ShowPrefabResources;
        window.CreatePrefabRequested += () => _ = RunCommand(CreatePrefabFromSelectionAsync);
        window.EditPrefabRequested += id => _ = RunCommand(() => EditPrefabInstanceAsync(id));
        window.DetachPrefabRequested += id => RunUiAction(() => { Active.DetachPrefab(id); RequestRender(); ScheduleRecovery(); });
        window.ExportPrefabRequested += id => _ = RunCommand(() => ExportPrefabAsync(id));
        window.ContextEscapeRequested += () => TryHandlePrefabEscape();
        RefreshPrefabWindows();
    }
    private void ShowPrefabResources()
    {
        _inspector?.CompletePendingEdit(); _editor.Cancel();
        if (_prefabResources is null)
        {
            _prefabResources = new(() => Active, () => !DocumentOperationBusy && _host.IsVisible && !Active.IsReadOnly,
                (owner, definition) => _prefabLinkStates.TryGetValue((owner, definition.Id), out var state) &&
                    state.Binding.Matches(definition.Source) ? state.Message : null)
                { Execute = ExecutePrefabResourceAsync };
            ConfigureAppWindow(_prefabResources); _windowCoordinator?.Register(_prefabResources, () => true);
            _prefabResources.CommandKey += OnKey;
        }
        _prefabResources.Refresh(); ShowAppWindow(_prefabResources, true);
    }
    private async Task ExecutePrefabResourceAsync(PrefabResourceRequest request)
    {
        if (DocumentOperationBusy || Active.IsReadOnly || !ReferenceEquals(request.Owner, Active) || request.Owner.SceneRevision != request.OwnerRevision) return;
        _inspector?.CompletePendingEdit(); FinishInlineText(); _editor.Cancel();
        switch (request.Action)
        {
            case PrefabResourceAction.Create: await CreatePrefabFromSelectionAsync(); break;
            case PrefabResourceAction.Import: await ImportPrefabFileAsync(linked: false); break;
            case PrefabResourceAction.Link: await ImportPrefabFileAsync(linked: true); break;
            case PrefabResourceAction.RemoveUnused: Active.RemoveUnusedPrefabDefinitions(); break;
            case PrefabResourceAction.Place when request.DefinitionId is { } id:
                Active.PlacePrefab(id, new(Affine2D.Translation(PrefabInsertionPoint().X, PrefabInsertionPoint().Y), 1)); break;
            case PrefabResourceAction.Edit when request.DefinitionId is { } id: await EditPrefabDefinitionAsync(id); break;
            case PrefabResourceAction.Rename when request.DefinitionId is { } id:
                var name = NamePrompt.Show(_prefabResources ?? (DependencyObject)_toolbar, L.Get("Prefab.RenameDefinition"), Active.Document.Definitions.Single(definition => definition.Id == id).Name, EditorSession.MaximumLayerNameLength, _toolbar);
                if (name is not null) Active.RenamePrefabDefinition(id, name); break;
            case PrefabResourceAction.Export when request.DefinitionId is { } id: await ExportPrefabAsync(id); break;
            case PrefabResourceAction.Check when request.DefinitionId is { } id: await CheckPrefabLinkAsync(id); break;
            case PrefabResourceAction.Refresh when request.DefinitionId is { } id: await RefreshPrefabLinkAsync(id); break;
            case PrefabResourceAction.Relink when request.DefinitionId is { } id: await RelinkPrefabAsync(id); break;
            case PrefabResourceAction.Embed when request.DefinitionId is { } id: Active.ConvertPrefabToEmbedded(id); break;
            case PrefabResourceAction.EditSource when request.DefinitionId is { } id: await EditPrefabSourceAsync(id); break;
        }
        ScheduleRecovery(); RequestRender();
    }
    private PointD PrefabInsertionPoint()
    {
        var bounds = CurrentMonitor().Bounds; var screen = new PointD(bounds.Left + bounds.Width / 2d, bounds.Top + bounds.Height / 2d) - _editor.CanvasOrigin;
        return _editor.CurrentCanvasMapping.TryToPlane(screen, out var point) ? point : Active.Document.Viewport.Center;
    }
    private Task CreatePrefabFromSelectionAsync()
    {
        if (DocumentOperationBusy || Active.IsTransactionActive) return Task.CompletedTask;
        if (Active.Selection.Count == 0) { UpdateStatus(L.Get("Prefab.NoSelection")); return Task.CompletedTask; }
        var name = NamePrompt.Show(_toolbar, L.Get("Prefab.CreateName"), L.Get("Prefab.DefaultName"), EditorSession.MaximumLayerNameLength, _toolbar);
        if (name is null) return Task.CompletedTask;
        var created = Active.CreatePrefabFromSelection(name); _prefabResources?.Refresh(); _prefabResources?.Select(created.DefinitionId);
        ScheduleRecovery(); RequestRender(); return Task.CompletedTask;
    }
    private Task EditPrefabInstanceAsync(Guid instanceId)
    {
        var instance = new SceneGraph(Active.Document).FindInstance(instanceId);
        return instance is null ? Task.CompletedTask : EditPrefabDefinitionCoreAsync(instance.DefinitionId, instanceId);
    }
    private Task EditPrefabDefinitionAsync(Guid definitionId) => EditPrefabDefinitionCoreAsync(definitionId, null);
    private Task EditPrefabDefinitionCoreAsync(Guid definitionId, Guid? instanceId)
    {
        if (DocumentOperationBusy || Active.IsTransactionActive) return Task.CompletedTask;
        var owner = Active; var definition = owner.Document.Definitions.Single(definition => definition.Id == definitionId);
        if (definition.Source?.IsLinked == true && !owner.IsReadOnly) return EditPrefabSourceCoreAsync(definitionId, instanceId);
        if (_prefabEditContext?.Contains(owner, definitionId) == true) return Task.CompletedTask;
        _inspector?.CompletePendingEdit(); FinishInlineText(); _editor.Cancel();
        var draft = owner.BeginPrefabEdit(definitionId, readOnly: owner.IsReadOnly || definition.Source?.IsLinked == true);
        _prefabEditContext = new(owner, draft, _editor.ScreenOrigin, _editor.Tool, _prefabEditContext)
        { EnteredInstanceId = instanceId, SourcePath = definition.Source?.IsLinked == true ? GroupSourcePath(definition.Source, owner) : null };
        SwitchPrefabContext(draft.Session); return Task.CompletedTask;
    }
    private void SwitchPrefabContext(EditorSession session)
    {
        ++_prefabRequest;
        _changingPrefabContext = true;
        try { EndCanvasCalibration(hidePanel: true); _canvasOriginDocument = session.Document; _editor.ReplaceSession(session, _editor.ScreenOrigin); SwitchToolState(EditorTool.Select); }
        finally { _changingPrefabContext = false; }
        EnsurePrefabLayersVisible(); OnEditorChanged(); RefreshPrefabWindows();
    }
    private void EnsurePrefabLayersVisible()
    {
        if (_layers is null || !_layers.IsVisible) { _layersWanted = false; ToggleLayers(); }
    }
    private void RefreshPrefabWindows()
    {
        if (_prefabPendingSources.Count > 0 && !_drainingPrefabSources && !DocumentOperationBusy && !HasPrefabEditContext)
            _ = DrainPrefabSourcesAsync();
        _prefabResources?.Refresh();
        RefreshGroupCanvasContext();
        var context = _prefabEditContext;
        long revision = context?.Owner.PreviewRevision ?? -1;
        if (ReferenceEquals(_shownPrefabContext, context) && ReferenceEquals(_shownPrefabLayers, _layers) && _shownPrefabOwnerRevision == revision) return;
        _shownPrefabContext = context; _shownPrefabLayers = _layers; _shownPrefabOwnerRevision = revision;
        _layers?.SetEditContext(context?.DisplayName,
            context is null ? null : () => _ = RunCommand(() => CompleteCurrentPrefabAsync()),
            context is null ? null : () => _ = RunCommand(() => LeaveCurrentPrefabAsync()), context?.IsSource == true || context?.IsOfflineSnapshot == true);
        var path = new List<(string Name, Action Navigate)>();
        if (context is not null)
        {
            foreach (var item in context.Chain().Reverse())
            {
                var owner = item.Owner;
                path.Add((item.Parent?.DisplayName ?? L.Get("RootCanvas." + owner.ActiveRootCanvas),
                    () => _ = RunCommand(() => NavigatePrefabAncestorAsync(owner, null))));
                foreach (var group in owner.EditScopePath)
                {
                    var id = group.Id;
                    path.Add((group.Name, () => _ = RunCommand(() => NavigatePrefabAncestorAsync(owner, id))));
                }
            }
        }
        _layers?.SetAncestorPath(path);
    }

    private async Task NavigatePrefabAncestorAsync(EditorSession owner, Guid? scope)
    {
        if (DocumentOperationBusy || Active.IsTransactionActive) return;
        if (_prefabEditContext?.Chain().Any(item => ReferenceEquals(item.Owner, owner)) != true) return;
        while (!ReferenceEquals(Active, owner))
            if (!await CompleteCurrentPrefabAsync()) return;
        owner.SetEditScope(scope); OnEditorChanged(); RefreshPrefabWindows();
    }
    private void OnPrefabEditorChanged()
    {
        if (_changingPrefabContext) return;
        // The normal editor event invalidates the active graph; owner publishing stays explicit.
        _prefabResources?.Refresh();
        RefreshGroupCanvasContext();
    }
    private bool TryHandlePrefabEscape()
    {
        if (_prefabEditContext is null) return false;
        _ = RunCommand(() => LeaveCurrentPrefabAsync()); return true;
    }
    private async Task<bool> LeaveCurrentPrefabAsync()
    {
        if (_prefabEditContext is not { } context) return true;
        if (!context.IsSource) return await CompleteCurrentPrefabAsync();
        _inspector?.CompletePendingEdit(); FinishInlineText(); _editor.Cancel();
        if (!(context.IsSource ? IsDirty(context.Session) : context.HasChanges))
        {
            if (context.IsSource) return await CompleteCurrentPrefabAsync();
            ReturnFromPrefabContext(context); return true;
        }
        var answer = AppDialog.Show(_toolbar, L.Get(context.IsSource ? "Prefab.SourceLeaveQuestion" : "Prefab.LeaveQuestion"), L.Get("Prefab.Resources"),
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question, "Prefab.Apply", "Prefab.Discard");
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) return await CompleteCurrentPrefabAsync();
        ReturnFromPrefabContext(context); return true;
    }
    private Task<bool> CompleteCurrentPrefabAsync() => CompleteCurrentPrefabWithCopyPathAsync(null);
    private async Task<bool> CompleteCurrentPrefabWithCopyPathAsync(Func<string?>? chooseCopyPath)
    {
        if (_prefabEditContext is not { } context) return true;
        _inspector?.CompletePendingEdit(); FinishInlineText(); _editor.Cancel();
        if (context.IsOfflineSnapshot) { ReturnFromPrefabContext(context); return true; }
        if (!context.IsSource && !context.HasChanges) { ReturnFromPrefabContext(context); return true; }
        if (context.IsSource)
        {
            bool previousBusy = _documentBusy; _documentBusy = true;
            try
            {
                if (!await SavePrefabSourceWithCopyPathAsync(context, saveAs: false, chooseCopyPath)) return false;
                QueueWindowSourceHandoff(CaptureWindowSourceHandoff(context));
                ReturnFromPrefabContext(context);
                try { await RefreshPrefabLinkAsync(context.DefinitionId, context.SourcePath); }
                catch { UpdateStatus(L.Get("Prefab.SourceSavedRefreshPending")); throw; }
                return true;
            }
            finally { _documentBusy = previousBusy; RefreshPrefabWindows(); }
        }
        var plan = context.Owner.PreparePrefabEdit(context.Draft!);
        if (!ApplyPrefabPlan(context.Owner, plan)) return false;
        QueueWindowSourceHandoff(CaptureWindowSourceHandoff(context));
        CompleteWindowSourceHandoffs(context.Owner);
        ReturnFromPrefabContext(context); return true;
    }
    private void ReturnFromPrefabContext(PrefabEditContext context)
    {
        if (!ReferenceEquals(_prefabEditContext, context)) throw new InvalidOperationException(L.Get("Prefab.NotCurrent"));
        ++_prefabRequest;
        _changingPrefabContext = true;
        try
        {
            _canvasOriginDocument = context.Owner.Document;
            _editor.ReplaceSession(context.Owner, context.ReturnOrigin); SwitchToolState(context.ReturnTool);
            _prefabEditContext = context.Parent;
            context.Owner.SetEditScope(context.ReturnScopeId); context.Owner.SetSelection(context.ReturnSelection);
        }
        finally { _changingPrefabContext = false; }
        ForgetPrefabWindowSources(context);
        OnEditorChanged();
        RefreshPrefabWindows(); PruneDocumentResources(); ScheduleRecovery(); RequestRender();
    }
    private bool ApplyPrefabPlan(EditorSession owner, PrefabEditPlan plan)
    {
        bool allowUnsafe = false;
        if (plan.UnsafeRootIds.Count > 0)
        {
            var relocated = plan.Candidate.Clone(); bool canRelocate = CanvasSafetyResolution.TryRelocate(relocated, plan.UnsafeRootIds);
            var dialog = CanvasSafetyDialog.Create(_toolbar, plan.UnsafeRootIds.Count, canRelocate);
            // A shared-definition edit cannot silently remove selected occurrences or source leaves.
            if (((System.Windows.Controls.DockPanel)dialog.Window.Content).Children[0] is System.Windows.Controls.StackPanel buttons)
                foreach (var button in buttons.Children.OfType<System.Windows.Controls.Button>().Where(button => System.Windows.Automation.AutomationProperties.GetAutomationId(button) == "DeleteUnsafeLayers")) button.IsEnabled = false;
            SessionWindowCoordinator.ShowModal(dialog.Window);
            switch (dialog.Result())
            {
                case CanvasSafetyAction.Relocate: if (!CanvasSafetyResolution.TryRelocate(plan.Candidate, plan.UnsafeRootIds)) return false; break;
                case CanvasSafetyAction.Keep: allowUnsafe = true; break;
                default: return false;
            }
        }
        owner.ApplyPrefabEdit(plan, allowUnsafe); CompleteWindowSourceHandoffs(owner); return true;
    }
    private async Task<bool> CompletePrefabEditingBeforeDocumentChangeAsync()
    {
        while (_prefabEditContext is not null) if (!await LeaveCurrentPrefabAsync()) return false;
        return true;
    }
    private async Task<bool> TryHandlePrefabSaveAsync(bool saveAs)
    {
        if (_prefabEditContext is not { } context) return false;
        _inspector?.CompletePendingEdit(); FinishInlineText(); _editor.Cancel();
        if (context.IsOfflineSnapshot)
        {
            if (saveAs) await ExportPrefabDraftAsync(context);
            else UpdateStatus(L.Get("GroupCanvas.ReadOnly"));
            return true;
        }
        if (context.IsSource) await SavePrefabSourceAsync(context, saveAs);
        else if (saveAs) await ExportPrefabDraftAsync(context);
        else await CompleteCurrentPrefabAsync();
        return true;
    }
    private async Task ExportPrefabDraftAsync(PrefabEditContext context, string? selectedPath = null)
    {
        string? path = selectedPath;
        if (path is null)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = L.Get("Prefab.ExportDraft"), Filter = L.Get("Prefab.Filter"), DefaultExt = ".floatprefab", AddExtension = true, FileName = context.Name };
            if (SessionWindowCoordinator.ShowNativeModal(_toolbar, () => dialog.ShowDialog(_toolbar)) != true) return;
            path = dialog.FileName;
        }
        var package = context.Owner.ExportPrefabDraft(context.Draft!);
        var prepared = await PrepareWindowPrefabPackageAsync(context.Session, package, package.EntryDefinitionId);
        await _prefabStore.SaveAsync(path, prepared.Package, prepared.Assets, _documentLifetime.Token);
    }

    private async Task ExportPrefabAsync(Guid definitionId)
    {
        var owner = Active; var definition = owner.Document.Definitions.Single(definition => definition.Id == definitionId);
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = L.Get("Prefab.Export"), Filter = L.Get("Prefab.Filter"), DefaultExt = ".floatprefab", AddExtension = true, FileName = definition.Name };
        if (SessionWindowCoordinator.ShowNativeModal(_toolbar, () => dialog.ShowDialog(_toolbar)) != true) return;
        var package = owner.ExportPrefab(definitionId);
        foreach (var item in package.Definitions) if (item.Source is { } source) { source.Locator = null; source.IsRelative = false; }
        var prepared = await PrepareWindowPrefabPackageAsync(owner, package);
        await _prefabStore.SaveAsync(dialog.FileName, prepared.Package, prepared.Assets, _documentLifetime.Token);
    }
    private string? ChoosePrefabFile(bool linked)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = L.Get(linked ? "Prefab.LinkFile" : "Prefab.Import"), Filter = L.Get(linked ? "Prefab.LinkFilter" : "Prefab.Filter"), CheckFileExists = true };
        return SessionWindowCoordinator.ShowNativeModal(_toolbar, () => dialog.ShowDialog(_toolbar)) == true ? dialog.FileName : null;
    }
    private async Task ImportPrefabFileAsync(bool linked, string? selectedPath = null)
    {
        if (Active.IsReadOnly) return;
        string? path = selectedPath ?? ChoosePrefabFile(linked); if (path is null) return;
        var owner = Active; long request = ++_prefabRequest;
        if (linked)
        {
            var prepared = await CanvasContentImport.PrepareAsync(owner, [path], [], owner.Document.Viewport,
                PrefabInsertionPoint(), 1, FileState(owner).Path, _documentLifetime.Token);
            if (request != _prefabRequest || !ReferenceEquals(owner, Active) || !prepared.TryCommit(owner)) return;
        }
        else
        {
            var candidate = await _prefabLinkResolver.PrepareAsync(owner, path, FileState(owner).Path, linked: false, ct: _documentLifetime.Token);
            if (request != _prefabRequest || !ReferenceEquals(owner, Active) || !PrefabLinkResolver.IsCurrent(candidate, owner)) return;
            var point = PrefabInsertionPoint();
            owner.ImportPrefab(candidate.Package, candidate.Assets, new(Affine2D.Translation(point.X, point.Y), 1), candidate.Source);
        }
        RequestRender(); ScheduleRecovery();
    }
    private async Task CheckPrefabLinkAsync(Guid definitionId)
    {
        var owner = Active; var source = owner.Document.Definitions.Single(definition => definition.Id == definitionId).Source!;
        var binding = PrefabLinkStatusBinding.Capture(source);
        var state = await _prefabLinkResolver.CheckAsync(source, FileState(owner).Path, _documentLifetime.Token);
        if (!ReferenceEquals(owner, Active) || !HasCurrentPrefabLink(owner, definitionId, binding)) return;
        string key = state == PrefabLinkStatus.Missing ? "Prefab.SourceMissing" : state == PrefabLinkStatus.Changed ? "Prefab.SourceChanged" : "Prefab.SourceUpToDate";
        SetPrefabLinkStatus(owner, definitionId, key);
        var message = L.Get(key); _prefabResources?.SetStatus(message, owner, definitionId); UpdateStatus(message);
    }
    private async Task RefreshPrefabLinkAsync(Guid definitionId, string? newPath = null)
    {
        var owner = Active; var definition = owner.Document.Definitions.Single(definition => definition.Id == definitionId);
        string path = newPath ?? PrefabLinkResolver.ResolvePath(definition.Source!, FileState(owner).Path); long request = ++_prefabRequest;
        var candidate = await _prefabLinkResolver.PrepareAsync(owner, path, FileState(owner).Path, definitionId, ct: _documentLifetime.Token);
        if (request != _prefabRequest || !ReferenceEquals(owner, Active) || !PrefabLinkResolver.IsCurrent(candidate, owner)) return;
        if (newPath is null && string.Equals(candidate.Source.LastAcceptedFileSha256, definition.Source?.LastAcceptedFileSha256, StringComparison.OrdinalIgnoreCase)) return;
        candidate.Source.Locator = candidate.Path; candidate.Source.IsRelative = false;
        var plan = owner.PreparePrefabRefresh(definitionId, candidate.Package, candidate.Assets, candidate.Source);
        if (ApplyPrefabPlan(owner, plan)) { ScheduleRecovery(); RequestRender(); }
    }
    private async Task RelinkPrefabAsync(Guid definitionId)
    {
        var path = ChoosePrefabFile(linked: true); if (path is null) return;
        var owner = Active; var original = owner.Document.Definitions.Single(definition => definition.Id == definitionId).Source!;
        var candidate = await _prefabLinkResolver.PrepareAsync(owner, path, FileState(owner).Path, definitionId, replaceIdentity: true, ct: _documentLifetime.Token);
        if (!ReferenceEquals(owner, Active) || !PrefabLinkResolver.IsCurrent(candidate, owner)) return;
        bool sameIdentity = original.SourceResourceId == candidate.Source.SourceResourceId && original.EntryKind == candidate.Source.EntryKind && original.EntryDefinitionId == candidate.Source.EntryDefinitionId;
        if (!sameIdentity && AppDialog.Show(_toolbar, L.Get("Prefab.ReplaceSourceQuestion"), L.Get("Prefab.Relink"),
            MessageBoxButton.YesNo, MessageBoxImage.Question, "Prefab.ReplaceSource", "Prefab.Cancel") != MessageBoxResult.Yes) return;
        candidate.Source.Locator = candidate.Path; candidate.Source.IsRelative = false;
        var plan = owner.PreparePrefabRefresh(definitionId, candidate.Package, candidate.Assets, candidate.Source);
        if (ApplyPrefabPlan(owner, plan)) { ScheduleRecovery(); RequestRender(); }
    }
    private Task EditPrefabSourceAsync(Guid definitionId) => EditPrefabSourceCoreAsync(definitionId, null);
    private async Task EditPrefabSourceCoreAsync(Guid definitionId, Guid? instanceId)
    {
        if (DocumentOperationBusy || Active.IsTransactionActive) return;
        var owner = Active; var definition = owner.Document.Definitions.Single(definition => definition.Id == definitionId);
        var expectedDocument = owner.Document; var parentContext = _prefabEditContext;
        long expectedRevision = owner.SceneRevision, expectedScope = owner.EditScopeVersion, request = ++_prefabRequest;
        long expectedEditingSession = _host.EditingSessionVersion;
        bool expectedVisibility = _host.IsVisible;
        Guid expectedDefinitionRevision = definition.Revision;
        bool Current() => request == _prefabRequest && !DocumentOperationBusy && !owner.IsTransactionActive &&
            ReferenceEquals(owner, Active) && ReferenceEquals(owner.Document, expectedDocument) &&
            owner.SceneRevision == expectedRevision && owner.EditScopeVersion == expectedScope &&
            _host.EditingSessionVersion == expectedEditingSession && _host.IsVisible == expectedVisibility &&
            ReferenceEquals(_prefabEditContext, parentContext) &&
            owner.Document.Definitions.Any(item => item.Id == definitionId && item.Revision == expectedDefinitionRevision);
        var source = definition.Source!; string path = PrefabLinkResolver.ResolvePath(source, FileState(owner).Path);
        PrefabLinkCandidate candidate;
        try { candidate = await _prefabLinkResolver.PrepareAsync(owner, path, FileState(owner).Path, definitionId, ct: _documentLifetime.Token); }
        catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            if (!Current()) return;
            var draft = owner.BeginPrefabEdit(definitionId, readOnly: true);
            _documentFiles[draft.Session] = new() { Path = path, SavedContentVersion = draft.Session.ContentVersion };
            _prefabEditContext = new(owner, draft, _editor.ScreenOrigin, _editor.Tool, _prefabEditContext)
            { EnteredInstanceId = instanceId, SourcePath = path };
            SetPrefabLinkStatus(owner, definitionId, "Prefab.SourceMissing");
            SwitchPrefabContext(draft.Session); UpdateStatus(L.Get("GroupCanvas.ReadOnly"));
            return;
        }
        if (!Current() || !PrefabLinkResolver.IsCurrent(candidate, owner)) return;
        MergeAssets(candidate.Assets);
        SceneDocument document; Guid? sourceEntry = null; PointD previewOrigin = default;
        if (candidate.SourceProject is { } project) document = project.Document.Clone();
        else
        {
            var entry = candidate.Package.Definitions.Single(definition => definition.Id == candidate.Package.EntryDefinitionId);
            var graph = SceneGraphClone.CloneGraph(entry.Graph).Graph; sourceEntry = entry.Id;
            document = new() { ResourceId = candidate.Package.ResourceId, Elements = graph.Elements, Groups = graph.Groups, Instances = graph.Instances, RootOrder = graph.RootOrder,
                Definitions = candidate.Package.Definitions.Select(definition => definition.Clone()).ToList(), Provenance = candidate.Package.Provenance };
            entry.PreviewCanvas?.ApplyTo(document);
            previewOrigin = entry.PreviewCanvas?.Origin ?? entry.Pivot;
        }
        NormalizePrefabLinkPaths(document, candidate.Path);
        var session = new EditorSession(document, _assets, readOnly: true, independentCanvasOrigin: previewOrigin);
        _documentFiles[session] = new() { Path = candidate.Path, DiskVersion = candidate.FileVersion, SavedContentVersion = session.ContentVersion };
        _prefabEditContext = new(owner, session, definitionId, definition.Revision, _editor.ScreenOrigin, _editor.Tool, _prefabEditContext, candidate.Path, sourceEntry) { EnteredInstanceId = instanceId };
        SwitchPrefabContext(session);
    }
    private Task<bool> SavePrefabSourceAsync(PrefabEditContext context, bool saveAs)
        => SavePrefabSourceWithCopyPathAsync(context, saveAs, null);
    private async Task<bool> SavePrefabSourceWithCopyPathAsync(PrefabEditContext context, bool saveAs, Func<string?>? chooseCopyPath)
    {
        if (!saveAs && !IsDirty(context.Session) && !HasWindowMediaPending(context.Session)) return true;
        bool previousBusy = _documentBusy; _documentBusy = true;
        try { return await SavePrefabSourceCoreAsync(context, saveAs, chooseCopyPath); }
        finally { _documentBusy = previousBusy; }
    }
    private async Task<bool> SavePrefabSourceCoreAsync(PrefabEditContext context, bool saveAs, Func<string?>? chooseCopyPath)
    {
        if (!context.IsPrefabSource)
        {
            var previousPath = context.SourcePath!;
            bool saved = await SaveSessionAsync(context.Session, saveAs);
            if (saved) { context.SourcePath = FileState(context.Session).Path; CopySourceCanvasViews(context, previousPath, context.SourcePath!); context.Restored = false; await OnPrefabSourceSavedAsync(context.SourcePath!); }
            return saved;
        }
        var file = FileState(context.Session); string path = context.SourcePath!;
        if (saveAs)
        {
            if (chooseCopyPath is not null)
            {
                var selectedPath = chooseCopyPath(); if (selectedPath is null) return false;
                path = selectedPath;
            }
            else
            {
                var dialog = new Microsoft.Win32.SaveFileDialog { Title = L.Get("Prefab.Export"), Filter = L.Get("Prefab.Filter"), DefaultExt = ".floatprefab", AddExtension = true, FileName = Path.GetFileName(path) };
                if (SessionWindowCoordinator.ShowNativeModal(_toolbar, () => dialog.ShowDialog(_toolbar)) != true) return false;
                path = dialog.FileName;
            }
        }
        long savedContentVersion = context.Session.ContentVersion, savedMediaVersion = _windowMediaCheckpoint.ForOwner(context.Session);
        bool authoredChanged = IsDirty(context.Session) || context.Restored;
        var snapshot = context.Session.Document.Clone(); string fingerprint = Fingerprint(snapshot);
        var package = WindowSourcePersistence.CreateSourcePrefabPackage(snapshot, context.SourceEntryDefinitionId!.Value, authoredChanged, file.SavedPrefabRevision);
        var entry = package.Definitions.Single(definition => definition.Id == package.EntryDefinitionId);
        entry.PreviewCanvas = context.Session.GetRootSubprojectCanvas();
        var prepared = await PrepareWindowPrefabPackageAsync(context.Session, package, package.EntryDefinitionId, snapshot);
        package = prepared.Package;
        RebasePrefabSources(package.Definitions, context.SourcePath, path);
        var assets = prepared.Assets;
        try
        {
            file.DiskVersion = await _prefabStore.SaveVersionedAsync(path, package, assets, _documentLifetime.Token,
                saveAs ? await ProjectFileVersion.ReadAsync(path, _documentLifetime.Token) : file.DiskVersion);
        }
        catch (ProjectFileConflictException ex)
        {
            var answer = AppDialog.Show(_toolbar, ex.Message + "\n" + L.Get("Project.SaveConflictChoice"), L.Get("App.Name"), MessageBoxButton.YesNo, MessageBoxImage.Warning, "Project.SaveCopy", "Project.KeepCurrent");
            return answer == MessageBoxResult.Yes && await SavePrefabSourceWithCopyPathAsync(context, saveAs: true, chooseCopyPath);
        }
        CopySourceCanvasViews(context, context.SourcePath!, path);
        file.Path = path; file.SavedContentVersion = savedContentVersion; file.SavedMediaVersion = savedMediaVersion;
        file.SavedFingerprint = fingerprint; file.SavedPrefabRevision = entry.Revision; context.SourcePath = path; context.Restored = false;
        await OnPrefabSourceSavedAsync(path); return true;
    }

    private void AddPrefabRecoveryState(WorkspaceState snapshot)
    {
        PrefabDraftState? Capture(PrefabEditContext? context) => context is null ? null : context.IsOfflineSnapshot ? Capture(context.Parent) : new()
        {
            OwnerResourceId = context.Owner.Document.ResourceId, DefinitionId = context.DefinitionId, ExpectedRevision = context.ExpectedRevision,
            DraftDocument = context.Session.CreateCommittedSnapshot(), ReturnScopeId = context.ReturnScopeId, ReturnSelection = [.. context.ReturnSelection],
            PreviewOrigin = context.Session.GetRootSubprojectCanvas().Origin,
            DraftScopeId = context.Session.EditScopeId, DraftSelection = [.. context.Session.Selection], ParentEdit = Capture(context.Parent),
            IsSourceProject = context.IsSource, SourcePath = context.SourcePath, SourceFileSha256 = context.IsSource ? FileState(context.Session).DiskVersion?.Sha256 : null
        };
        snapshot.PendingPrefabEdit = Capture(_prefabEditContext);
    }
    private void RestorePrefabRecoveryState(PrefabDraftState? state)
    {
        if (state is null) return;
        void Restore(PrefabDraftState item)
        {
            if (item.ParentEdit is not null) Restore(item.ParentEdit);
            var owner = new[] { _live, _capture, _prefabEditContext?.Session }.OfType<EditorSession>().LastOrDefault(session => session.Document.ResourceId == item.OwnerResourceId);
            if (owner is null || owner.Document.Definitions.All(definition => definition.Id != item.DefinitionId || definition.Revision != item.ExpectedRevision)) return;
            owner.SetEditScope(item.ReturnScopeId); owner.SetSelection(item.ReturnSelection);
            PrefabEditContext context;
            if (item.IsSourceProject)
            {
                var session = new EditorSession(item.DraftDocument.Clone(), _assets, independentCanvasOrigin: item.PreviewOrigin ?? default); var entry = owner.Document.Definitions.Single(definition => definition.Id == item.DefinitionId).Source?.EntryDefinitionId;
                context = new(owner, session, item.DefinitionId, item.ExpectedRevision, _editor.ScreenOrigin, _editor.Tool, _prefabEditContext, item.SourcePath, entry) { Restored = true };
                _documentFiles[session] = new() { Path = item.SourcePath, DiskVersion = new(item.SourceFileSha256), SavedContentVersion = -1 };
            }
            else
            {
                var draft = owner.BeginPrefabEdit(item.DefinitionId); draft.Session.RestoreDocument(L.Get("Prefab.DraftRestored"), item.DraftDocument.Clone());
                if (item.PreviewOrigin is { } origin)
                    draft.Session.ChangeEditingSubprojectCanvas(L.Get("Prefab.DraftRestored"), settings => settings.Origin = origin);
                context = new(owner, draft, _editor.ScreenOrigin, _editor.Tool, _prefabEditContext) { Restored = true };
            }
            context.Session.SetEditScope(item.DraftScopeId); context.Session.SetSelection(item.DraftSelection);
            if (context.ReturnSelection.Length == 1 && new SceneGraph(owner.Document).FindInstance(context.ReturnSelection[0]) is { } instance && instance.DefinitionId == context.DefinitionId)
                context.EnteredInstanceId = instance.Id;
            _prefabEditContext = context; _editor.ReplaceSession(context.Session, context.ReturnOrigin);
        }
        Restore(state); if (_prefabEditContext is not null) { RefreshPrefabWindows(); UpdateStatus(L.Get("Prefab.DraftRestored")); }
    }
    private void DisposePrefabWindows()
    {
        ++_prefabRequest; _prefabEditContext = null;
        _prefabPendingSources.Clear(); _prefabLinkStates.Clear(); _prefabBackgroundChecks.Clear();
        if (_prefabResources is not null) { SessionWindowCoordinator.Close(_prefabResources); _prefabResources = null; }
    }
    private static void NormalizePrefabLinkPaths(SceneDocument document, string? projectPath)
    {
        foreach (var definition in document.Definitions)
            if (definition.Source is { IsLinked: true } source)
            {
                source.Locator = PrefabLinkResolver.ResolvePath(source, projectPath); source.IsRelative = false;
            }
    }
    private static void RebasePrefabLinksForSave(SceneDocument snapshot, string? previousPath, string destinationPath)
        => RebasePrefabSources(snapshot.Definitions, previousPath, destinationPath);
    private static void RebasePrefabSources(IEnumerable<PrefabDefinition> definitions, string? previousPath, string destinationPath)
    {
        foreach (var definition in definitions)
            if (definition.Source is { IsLinked: true } source)
            {
                string absolute = PrefabLinkResolver.ResolvePath(source, previousPath);
                definition.Source = PrefabLinkResolver.Locate(source, absolute, destinationPath);
            }
    }
    private async Task OnPrefabSourceSavedAsync(string path)
    {
        if (_disposed || _documentStopped) return;
        path = Path.GetFullPath(path); _prefabPendingSources.Add(path);
        foreach (var owner in OpenPrefabOwners())
            foreach (var definition in owner.Document.Definitions.Where(definition => definition.Source?.IsLinked == true))
                if (string.Equals(PrefabLinkResolver.ResolvePath(definition.Source!, FileState(owner).Path), path, StringComparison.OrdinalIgnoreCase))
                    SetPrefabLinkStatus(owner, definition.Id, "Prefab.SourceChanged");
        if (!DocumentOperationBusy && !HasPrefabEditContext) await DrainPrefabSourcesAsync();
    }
    private IEnumerable<EditorSession> OpenPrefabOwners() => new[] { _live, _capture }.OfType<EditorSession>()
        .Concat(_prefabEditContext?.Chain().Select(context => context.Owner) ?? []).Distinct();
    private bool IsOpenPrefabOwner(EditorSession owner) => OpenPrefabOwners().Any(current => ReferenceEquals(current, owner));
    private void SetPrefabLinkStatus(EditorSession owner, Guid definitionId, string key)
    {
        var source = owner.Document.Definitions.FirstOrDefault(definition => definition.Id == definitionId)?.Source;
        if (source is not { IsLinked: true }) return;
        var state = (PrefabLinkStatusBinding.Capture(source), L.Get(key));
        if (_prefabLinkStates.TryGetValue((owner, definitionId), out var previous) && previous == state) return;
        _prefabLinkStates[(owner, definitionId)] = state;
        if (ReferenceEquals(owner, Active)) _prefabResources?.InvalidateResourceStatus();
    }
    private static bool HasCurrentPrefabLink(EditorSession owner, Guid definitionId, PrefabLinkStatusBinding binding) =>
        binding.Matches(owner.Document.Definitions.FirstOrDefault(definition => definition.Id == definitionId)?.Source);
    private async Task DrainPrefabSourcesAsync()
    {
        if (_drainingPrefabSources || DocumentOperationBusy || HasPrefabEditContext || _disposed || _documentStopped) return;
        _drainingPrefabSources = true;
        try
        {
            var pending = _prefabPendingSources.ToArray();
            foreach (var owner in OpenPrefabOwners().ToArray())
            {
                if (owner.IsTransactionActive) continue;
                await CheckOpenedPrefabLinksAsync(owner);
            }
            if (!DocumentOperationBusy && !HasPrefabEditContext && OpenPrefabOwners().All(owner => !owner.IsTransactionActive))
                foreach (string path in pending) _prefabPendingSources.Remove(path);
        }
        catch (OperationCanceledException) when (_documentLifetime.IsCancellationRequested) { }
        catch (Exception ex) { UpdateStatus(ex.Message); }
        finally { _drainingPrefabSources = false; }
    }
    private async Task CheckOpenedPrefabLinksAsync(EditorSession owner)
    {
        if (_disposed || _documentStopped || !IsOpenPrefabOwner(owner)) return;
        long request = _prefabBackgroundChecks.GetValueOrDefault(owner) + 1; _prefabBackgroundChecks[owner] = request;
        var ids = owner.Document.Definitions.Where(definition => definition.Source?.IsLinked == true).Select(definition => definition.Id).ToArray();
        foreach (var id in ids)
        {
            if (_disposed || _documentStopped || !IsOpenPrefabOwner(owner)) return;
            var definition = owner.Document.Definitions.FirstOrDefault(definition => definition.Id == id);
            if (definition?.Source?.IsLinked != true) continue;
            var binding = PrefabLinkStatusBinding.Capture(definition.Source);
            long scopeVersion = owner.EditScopeVersion;
            if (DocumentOperationBusy || owner.IsTransactionActive || IsPrefabRetainedSession(owner)) { SetPrefabLinkStatus(owner, id, "Prefab.SourceChanged"); continue; }
            try
            {
                string path = PrefabLinkResolver.ResolvePath(definition.Source, FileState(owner).Path);
                var state = await _prefabLinkResolver.CheckAsync(definition.Source, FileState(owner).Path, _documentLifetime.Token);
                if (_prefabBackgroundChecks.GetValueOrDefault(owner) != request || !IsOpenPrefabOwner(owner) || owner.EditScopeVersion != scopeVersion) return;
                if (!HasCurrentPrefabLink(owner, id, binding)) continue;
                if (state != PrefabLinkStatus.Changed) { SetPrefabLinkStatus(owner, id, state == PrefabLinkStatus.Missing ? "Prefab.SourceMissing" : "Prefab.SourceUpToDate"); continue; }
                var candidate = await _prefabLinkResolver.PrepareAsync(owner, path, FileState(owner).Path, id, ct: _documentLifetime.Token);
                if (!HasCurrentPrefabLink(owner, id, binding)) continue;
                if (_prefabBackgroundChecks.GetValueOrDefault(owner) != request || !IsOpenPrefabOwner(owner) || owner.EditScopeVersion != scopeVersion ||
                    DocumentOperationBusy || IsPrefabRetainedSession(owner) || !PrefabLinkResolver.IsCurrent(candidate, owner)) { SetPrefabLinkStatus(owner, id, "Prefab.SourceChanged"); continue; }
                candidate.Source.Locator = candidate.Path; candidate.Source.IsRelative = false;
                var plan = owner.PreparePrefabRefresh(id, candidate.Package, candidate.Assets, candidate.Source);
                if (plan.UnsafeRootIds.Count > 0) { SetPrefabLinkStatus(owner, id, "Prefab.SourceChanged"); continue; }
                owner.ApplyPrefabEdit(plan); CompleteWindowSourceHandoffs(owner); SetPrefabLinkStatus(owner, id, "Prefab.SourceUpToDate");
                ScheduleRecovery(); RequestRender();
            }
            catch (OperationCanceledException) when (_documentLifetime.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                if (!HasCurrentPrefabLink(owner, id, binding)) continue;
                SetPrefabLinkStatus(owner, id, "Prefab.SourceChanged");
                if (ReferenceEquals(owner, Active)) { _prefabResources?.SetStatus(ex.Message, owner, id); UpdateStatus(ex.Message); }
            }
        }
    }
}
