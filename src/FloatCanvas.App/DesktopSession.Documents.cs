using L = FloatCanvas.Localization.AppText;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private const string ObjectClipboardFormat = "FloatCanvas.Objects.v1";
    private readonly ProjectStore _projectStore = new();
    private readonly RecoveryStore _recoveryStore = new(Path.Combine(ApplicationPaths.ProfileDirectory, "recovery"));
    private WorkspaceRecoveryHistory? _workspaceRecoveryHistory;
    private WorkspaceRecoveryHistory RecoveryHistory { get { var history = _workspaceRecoveryHistory ??= new(_recoveryStore); history.MaximumVersions = _settings.HistoryVersionLimit; return history; } }
    private readonly CaptureService _documentCaptureService = new();
    private readonly CancellationTokenSource _documentLifetime = new();
    private readonly Dictionary<EditorSession, DocumentFileState> _documentFiles = [];
    private DispatcherTimer? _documentRecoveryTimer;
    private Task _documentRecoveryTask = Task.CompletedTask;
    private readonly SemaphoreSlim _documentRecoveryGate = new(1, 1);
    private string? _documentRecoveryFingerprint;
    private DateTime _documentRecoveryLastWriteUtc;
    private bool _documentRecoveryFlushing;
    private string? _documentFrozenCaptureWarning;
    private bool _documentBusyValue, _documentExitRequestedValue, _documentStopped, _documentRecoveryWritesBlocked;
    private bool _documentBusy
    {
        get => _documentBusyValue;
        set { if (_documentBusyValue == value) return; _documentBusyValue = value; UpdateInteractionAvailability(); }
    }
    private bool _documentExitRequested
    {
        get => _documentExitRequestedValue;
        set { if (_documentExitRequestedValue == value) return; _documentExitRequestedValue = value; UpdateInteractionAvailability(); }
    }
    private EditorTool _documentLiveTool = EditorTool.Freehand;
    private bool DocumentOperationBusy => _documentBusy || _documentExitRequested || _canvasCalibration?.IsAwaitingResolution == true;
    private sealed class DocumentFileState { public string? Path; public string? SavedFingerprint; public long SavedContentVersion = -1; public ProjectFileVersion? DiskVersion; }
    private sealed class ClipboardObjects
    {
        public int Version { get; set; } = 1;
        public SceneDocument Document { get; set; } = new();
        public List<ClipboardAsset> Assets { get; set; } = [];
    }
    private sealed class ClipboardAsset
    {
        public string Id { get; set; } = "";
        public string MediaType { get; set; } = "image/png";
        public byte[] Bytes { get; set; } = [];
    }

    private async Task InitializeDocumentsAsync(bool recover = false)
    {
        _documentBusy = true;
        try
        {
            bool restoredWorkspace = false;
            if (recover)
            {
                RecoveredWorkspace? restored = null;
                try
                {
                    restored = await _recoveryStore.LoadAsync(_documentLifetime.Token);
                    if (restored is not null) ImageImportBudget.ValidateResources(restored.Assets.Resources);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    restored = null;
                    try
                    {
                        string location = await PreserveRecoveryGenerationsAsync();
                        ShowError(L.Get("Recovery.LoadFailedArchived") + location + "\n\n" + ex.Message);
                    }
                    catch (Exception archiveError) when (archiveError is not OperationCanceledException)
                    {
                        ShowError(L.Get("Recovery.LoadFailedPreserved") + ex.Message + "\n" + archiveError.Message);
                    }
                }
                if (restored is not null)
                {
                    MergeAssets(restored.Assets);
                    _live = new(restored.State.LiveDocument, _assets);
                    _capture = restored.State.CaptureDocument is { } draft ? new(draft, _assets) : null;
                    _captureOrigin = restored.State.CaptureOrigin;
                    _documentRecoveryFingerprint = WorkspaceFingerprint(restored.State);
                    restoredWorkspace = true;
                    if (restored.FromBackup)
                    {
                        try
                        {
                            string location = await PreserveRecoveryGenerationsAsync();
                            UpdateStatus(L.Get("Recovery.PreviousLoaded") + location);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        { ShowError(L.Get("Recovery.PreviousArchiveFailed") + ex.Message); }
                    }
                }
            }
            SynchronizeLiveDisplayLayout();
            _editor.ReplaceSession(_live, _liveOrigin);
            if (EnsureCaptureVisible()) ScheduleRecovery();
            if (!restoredWorkspace)
            {
                FileState(_live).SavedFingerprint = Fingerprint(_live.Document); FileState(_live).SavedContentVersion = _live.ContentVersion;
                var baseline = SnapshotWorkspace();
                RecoveryHistory.BeginFreshWorkspace(baseline);
                _documentRecoveryFingerprint = WorkspaceFingerprint(baseline);
            }
            else { RecoveryHistory.WorkspaceRestored(); RecoveryHistory.EnterWorkspace(SnapshotWorkspace()); }
            _host.SetEditing(false);
            _toolbar.Hide(); _inspector?.Hide(); RequestRender();
        }
        finally { _documentBusy = false; }
    }
    private async Task<string> PreserveRecoveryGenerationsAsync()
    {
        _documentRecoveryWritesBlocked = true;
        var archived = await _recoveryStore.ArchiveExistingAsync(_documentLifetime.Token);
        _documentRecoveryWritesBlocked = false;
        return archived.Count == 0 ? _recoveryStore.DirectoryPath : Path.GetDirectoryName(archived[0])!;
    }

    private void ScheduleRecovery()
    {
        if (_documentStopped || _documentExitRequested || _documentRecoveryWritesBlocked) return;
        _documentRecoveryTimer ??= CreateRecoveryTimer();
        // Keep the first pending commit's deadline even while further small edits arrive.
        if (!_documentRecoveryTimer.IsEnabled) _documentRecoveryTimer.Start();
    }
    private DispatcherTimer CreateRecoveryTimer()
    {
        // One bounded UI snapshot per deadline must not starve behind continuous render work.
        // Encoding/writing still runs asynchronously in RecoveryStore.
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            await FlushRecoveryFromTimerAsync();
        };
        return timer;
    }
    private async Task FlushRecoveryFromTimerAsync()
    {
        if (_documentRecoveryFlushing) { ScheduleRecovery(); return; }
        // Bound sustained full-package IO, including previous-generation preservation.
        double minimumSeconds = Math.Max(1, _assets.TotalBytes * 4.0 / (32 * 1024 * 1024));
        if ((DateTime.UtcNow - _documentRecoveryLastWriteUtc).TotalSeconds < minimumSeconds) { ScheduleRecovery(); return; }
        _documentRecoveryFlushing = true;
        try { await FlushRecoveryAsync(); }
        // Disposing a session intentionally cancels in-flight recovery writes. The
        // async timer callback must observe that cancellation before returning to WPF.
        catch (OperationCanceledException) when (_documentLifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OperationCanceledException) { UpdateStatus(L.Get("Recovery.SaveFailed") + ex.Message); }
        finally { _documentRecoveryFlushing = false; }
    }
    private async Task FlushRecoveryAsync()
    {
        _documentRecoveryTimer?.Stop();
        await _documentRecoveryGate.WaitAsync(_documentLifetime.Token);
        try
        {
            await _documentRecoveryTask;
            if (_documentStopped) return;
            if (_documentRecoveryWritesBlocked) await PreserveRecoveryGenerationsAsync();
            PruneDocumentResources();
            // Claim the complete check/write sequence before its first await.
            _documentRecoveryTask = CheckAndWriteRecoveryAsync();
            try { await _documentRecoveryTask; }
            finally { if (_documentRecoveryTask.IsCompleted) _documentRecoveryTask = Task.CompletedTask; }
        }
        finally { _documentRecoveryGate.Release(); }
    }
    private async Task CheckAndWriteRecoveryAsync()
    {
        var snapshot = SnapshotWorkspace();
        string fingerprint = await Task.Run(() => WorkspaceFingerprint(snapshot), _documentLifetime.Token);
        if (fingerprint == _documentRecoveryFingerprint || !await RecoveryHistory.ShouldWriteAsync(snapshot, _documentLifetime.Token)) return;
        await WriteRecoveryAsync();
    }
    private async Task WriteRecoveryAsync()
    {
        await RecoveryHistory.PreserveBeforeFirstWriteAsync(_documentLifetime.Token);
        // The archive can take time. Capture current committed content and resources after it completes.
        var snapshot = SnapshotWorkspace();
        var writeInfo = RecoveryHistory.CreateCurrentInfoWriter(snapshot, FileState(Active).Path);
        var resources = RecoveryResourceSnapshot(snapshot);
        var animationTime = _renderer.AnimationTime;
        if (!await RecoveryHistory.ShouldWriteAsync(snapshot, _documentLifetime.Token)) return;
        string fingerprint = await Task.Run(() => WorkspaceFingerprint(snapshot), _documentLifetime.Token);
        await SaveRecoverySnapshotAsync(snapshot, resources, animationTime);
        _documentRecoveryLastWriteUtc = DateTime.UtcNow;
        await Task.Run(writeInfo, _documentLifetime.Token);
        _documentRecoveryFingerprint = fingerprint;
        RecoveryHistory.WorkspaceSaved();
    }

    private AssetResource[] RecoveryResourceSnapshot(WorkspaceState snapshot) =>
        snapshot.LiveDocument.ReferencedAssets.Concat(snapshot.CaptureDocument?.ReferencedAssets ?? [])
            .Distinct().Select(_assets.Get).ToArray();

    private Task SaveRecoverySnapshotAsync(WorkspaceState snapshot, AssetResource[]? capturedResources = null, TimeSpan? capturedTime = null)
    {
        var animationTime = capturedTime ?? _renderer.AnimationTime;
        var resources = capturedResources ?? RecoveryResourceSnapshot(snapshot);
        var token = _documentLifetime.Token;
        return Task.Run(async () =>
        {
            var assets = ResourceLibrary(resources);
            byte[]? preview;
            try { preview = WorkspacePreview.Render(snapshot.LiveDocument, assets, animationTime); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or IOException or InvalidDataException or OverflowException)
            { preview = null; }
            await _recoveryStore.SaveWithPreviewAsync(snapshot, assets, preview, token).ConfigureAwait(false);
        }, token);
    }

    private async Task RestoreWorkspaceHistoryAsync()
    {
        if (DocumentOperationBusy || _windowCoordinator is not { } coordinator) return;
        _welcomeManual = false;
        _welcomeWindow?.Close();
        _inspector?.CompletePendingEdit();
        var request = new WindowSessionRequest(_host.EditingSessionVersion, _host.IsEditing, _live.Document);
        CloseImageDrop();
        _documentBusy = true;
        _documentRecoveryTimer?.Stop();
        try
        {
            _editor.Cancel();
            _documentRecoveryTimer?.Stop();
            await _documentRecoveryTask;
            if (!coordinator.CanContinue(request, _live.Document)) return;
            var selected = ChooseRecoveryWorkspace();
            if (selected is null) return;
            // Read fully before replacing or writing either workspace.
            var restored = await RecoveryHistory.LoadAsync(selected, _documentLifetime.Token);
            if (!coordinator.CanContinue(request, _live.Document)) return;
            if (restored is null) { UpdateStatus(L.Get("Recovery.NoHistory")); return; }
            if (!await ConfirmReplaceAsync(_live, L.Get("Project.ReplaceLive"))) return;
            if (!coordinator.CanContinue(request, _live.Document)) return;
            if (_capture is not null && !await ConfirmReplaceAsync(_capture, L.Get("Project.ReplaceCapture"))) return;
            if (!coordinator.CanContinue(request, _live.Document)) return;
            await FlushRecoveryAsync();
            await ArchiveWorkspaceAsync();
            if (!coordinator.CanContinue(request, _live.Document)) return;
            FinishInlineText();
            ImageImportBudget.ValidateResources(restored.Assets.Resources);
            MergeAssets(restored.Assets);
            _documentFiles.Clear();
            _live = new(restored.State.LiveDocument, _assets);
            _capture = restored.State.CaptureDocument is { } capture ? new(capture, _assets) : null;
            _captureOrigin = restored.State.CaptureOrigin;
            _documentFrozenCaptureWarning = null;
            SynchronizeLiveDisplayLayout();
            _editor.ReplaceSession(_live, _liveOrigin);
            SwitchToolState(_documentLiveTool);
            RecoveryHistory.WorkspaceRestored();
            RecoveryHistory.EnterWorkspace(SnapshotWorkspace());
            _documentRecoveryFingerprint = null;
            OnMonitorsChanged();
            // The chosen history entry becomes the current editable workspace.
            // Enter before awaiting recovery IO; later callbacks must not reacquire input.
            _host.SetVisible(true);
            SetEditing(true);
            await FlushRecoveryAsync();
            RequestRender();
            UpdateStatus(L.Get(_host.IsEditing ? "Recovery.RestoredEditing" : "Recovery.RestoredPassThrough"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            string message = L.Get("Recovery.RestoreFailed") + ex.Message;
            if (coordinator.CanContinue(request, _live.Document)) ShowError(message);
            else UpdateStatus(message);
        }
        finally { _documentBusy = false; PruneDocumentResources(); ScheduleRecovery(); }
    }

    private WorkspaceRecoveryEntry? ChooseRecoveryWorkspace()
    {
        bool regular = !_host.IsEditing;
        var dialog = new WorkspaceHistoryWindow(RecoveryHistory, regular: regular) { Owner = regular ? _welcomeWindow : _toolbar };
        ConfigureAppWindow(dialog, true, regular);
        return SessionWindowCoordinator.ShowModal(dialog) == true ? dialog.SelectedEntry : null;
    }
    private async Task RequestExitAsync()
    {
        if (_documentExitRequested || _documentBusy) return;
        CloseImageDrop();
        _inspector?.CompletePendingEdit();
        _documentExitRequested = true; _editor.Cancel();
        try { FlushToolState(commitDraft: true); if (!await ConfirmLeaveWorkspaceAsync()) { _documentExitRequested = false; ScheduleRecovery(); return; } await FlushRecoveryAsync(); await ArchiveWorkspaceAsync(); _app.Shutdown(); }
        catch (Exception ex)
        {
            _documentExitRequested = false;
            ShowError(L.Get("Recovery.ExitSaveFailed") + ex.Message);
        }
    }
    private void StopDocumentTimers()
    {
        CancelSnapshotCapture("Shutdown");
        _documentStopped = true; _documentRecoveryTimer?.Stop();
        _documentLifetime.Cancel(); _documentCaptureService.Dispose();
    }
    private WorkspaceState SnapshotWorkspace() => new()
    {
        LiveDocument = _live.CreateCommittedSnapshot(), CaptureDocument = _capture?.CreateCommittedSnapshot(), CaptureOrigin = _captureOrigin
    };
    private static string Fingerprint(SceneDocument document) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(document)));
    private static string WorkspaceFingerprint(WorkspaceState workspace) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(workspace)));
    private DocumentFileState FileState(EditorSession session)
    {
        if (!_documentFiles.TryGetValue(session, out var state)) _documentFiles.Add(session, state = new());
        return state;
    }
    private bool IsDirty(EditorSession session) => FileState(session).SavedContentVersion != session.ContentVersion || session.HasPendingChanges;
    private void PruneDocumentResources()
    {
        if (_documentBusy || _live.IsTransactionActive || _capture?.IsTransactionActive == true) return;
        // Save/export workers capture immutable AssetResource references before dispatching.
        // Removing a library entry cannot invalidate a snapshot already owned by those workers.
        _assets.Prune(_live.ReferencedAssetsIncludingHistory().Concat(_capture?.ReferencedAssetsIncludingHistory() ?? []));
        foreach (var session in _documentFiles.Keys.Where(s => !ReferenceEquals(s, _live) && !ReferenceEquals(s, _capture)).ToArray())
            _documentFiles.Remove(session);
    }
    private void MergeAssets(AssetLibrary source)
    {
        foreach (var resource in source.Resources)
            if (!_assets.TryGet(resource.Id, out _)) _assets.Add(resource);
    }
    private AssetResource[] ResourceSnapshot(SceneDocument document) => document.ReferencedAssets.Select(_assets.Get).ToArray();
    private static AssetLibrary ResourceLibrary(IEnumerable<AssetResource> resources)
    {
        var assets = new AssetLibrary();
        foreach (var resource in resources) assets.Add(resource);
        return assets;
    }

    private Task OpenProjectAsync() => OpenProjectPathAsync(null);

    private async Task OpenProjectPathAsync(string? projectPath)
    {
        if (DocumentOperationBusy) return;
        _inspector?.CompletePendingEdit();
        CloseImageDrop();
        _documentBusy = true;
        try
        {
            if (projectPath is null)
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Title = WindowTitles.Format(L.Get("Project.OpenTitle"), !_host.IsEditing), Filter = L.Get("Project.Filter"), CheckFileExists = true };
                if (SessionWindowCoordinator.ShowNativeModal(_toolbar, () => dialog.ShowDialog(_toolbar)) != true) return;
                projectPath = dialog.FileName;
            }
            projectPath = ProjectLaunchRequest.NormalizePath(projectPath);
            _editor.Cancel(); var previous = Active;
            if (!await ConfirmReplaceAsync(previous, L.Get("Project.ReplaceCurrent"))) return;
            string previousContent = Fingerprint(previous.Document);
            long focusVersion = _host.ForegroundLossVersion;
            var loaded = await _projectStore.LoadAsync(projectPath, _documentLifetime.Token);
            if (!ReferenceEquals(Active, previous) || Fingerprint(previous.Document) != previousContent)
            { UpdateStatus(L.Get("Project.OpenChanged")); return; }
            ImageImportBudget.ValidateResources(loaded.Assets.Resources);
            MergeAssets(loaded.Assets);
            var session = new EditorSession(loaded.Document, _assets);
            if (loaded.Document.Kind == DocumentKind.FrozenCapture)
            {
                if (_capture is not null && !ReferenceEquals(previous, _capture) && !await ConfirmReplaceAsync(_capture, L.Get("Project.ReplaceCapture"))) return;
                await FlushRecoveryAsync(); await ArchiveWorkspaceAsync();
                if (ReferenceEquals(previous, _live)) _documentLiveTool = _editor.Tool;
                if (_capture is not null) _documentFiles.Remove(_capture);
                _capture = session; _captureOrigin = CenteredOrigin(loaded.Document.Viewport);
                _documentFrozenCaptureWarning = null;
                _editor.ReplaceSession(session, _captureOrigin);
            }
            else
            {
                if (!ReferenceEquals(previous, _live) && !await ConfirmReplaceAsync(_live, L.Get("Project.ReplaceLive"))) return;
                await FlushRecoveryAsync(); await ArchiveWorkspaceAsync();
                _documentFiles.Remove(_live);
                _live = session; SynchronizeLiveDisplayLayout();
                _editor.ReplaceSession(session, _liveOrigin); SwitchToolState(_documentLiveTool);
                OnMonitorsChanged();
            }
            _documentFiles[session] = new() { Path = projectPath, SavedFingerprint = Fingerprint(session.Document), SavedContentVersion = session.ContentVersion, DiskVersion = loaded.FileVersion };
            RecoveryHistory.WorkspaceRestored();
            RecoveryHistory.EnterWorkspace(SnapshotWorkspace());
            _documentRecoveryFingerprint = null;
            bool editing;
            if (focusVersion != _host.ForegroundLossVersion)
            { SetEditing(false); editing = false; UpdateStatus(L.Get("Project.OpenedPassThrough")); }
            else editing = SetEditing(true);
            ScheduleRecovery();
            if (editing) UpdateStatus(L.Get("Project.OpenedPrefix") + Path.GetFileName(projectPath));
            RequestRender();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowError(L.Get("Project.OpenFailed") + ex.Message); }
        finally { _documentBusy = false; PruneDocumentResources(); ScheduleRecovery(); }
    }
    private async Task SaveProjectAsync(bool saveAs = false)
    {
        if (DocumentOperationBusy) return;
        _inspector?.CompletePendingEdit();
        _editor.Cancel(); _documentBusy = true;
        try { await SaveSessionAsync(Active, saveAs); }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowError(L.Get("Project.SaveFailed") + ex.Message); }
        finally { _documentBusy = false; PruneDocumentResources(); ScheduleRecovery(); }
    }
    private async Task<bool> SaveSessionAsync(EditorSession session, bool saveAs)
    {
        var state = FileState(session); string? path = state.Path;
        if (saveAs || path is null)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = WindowTitles.Format(L.Get("Project.SaveTitle"), !_host.IsEditing), Filter = L.Get("Project.Filter"), DefaultExt = ".floatcanvas", AddExtension = true,
                FileName = path is null ? L.Get("Project.FilePrefix") + DateTime.Now.ToString("yyyyMMdd-HHmmss") : Path.GetFileName(path)
            };
            if (SessionWindowCoordinator.ShowNativeModal(_toolbar, () => dialog.ShowDialog(_toolbar)) != true) return false;
            path = dialog.FileName;
        }
        long savedContentVersion = session.ContentVersion;
        var snapshot = session.Document.Clone(); string fingerprint = Fingerprint(snapshot);
        var diskVersion = !saveAs && string.Equals(path, state.Path, StringComparison.OrdinalIgnoreCase) ? state.DiskVersion : await ProjectFileVersion.ReadAsync(path, _documentLifetime.Token);
        ProjectFileVersion writtenVersion;
        try { writtenVersion = await _projectStore.SaveVersionedAsync(path, snapshot, _assets, _documentLifetime.Token, diskVersion); }
        catch (ProjectFileConflictException ex)
        {
            var answer = AppDialog.Show(_toolbar, ex.Message + "\n" + L.Get("Project.SaveConflictChoice"), L.Get("App.Name"), MessageBoxButton.YesNo, MessageBoxImage.Warning, "Project.SaveCopy", "Project.KeepCurrent");
            return answer == MessageBoxResult.Yes && await SaveSessionAsync(session, true);
        }
        state.DiskVersion = writtenVersion;
        state.Path = path; state.SavedFingerprint = fingerprint; state.SavedContentVersion = savedContentVersion;
        RefreshBoundFileIndicator();
        UpdateStatus(L.Get("Project.SavedPrefix") + Path.GetFileName(path)); return true;
    }
    private async Task<bool> ConfirmReplaceAsync(EditorSession session, string description)
    {
        if (FileState(session).Path is null || !IsDirty(session)) return true;
        var result = AppDialog.Show(_toolbar, description + L.Get("Project.ReplaceQuestion"), L.Get("App.Name"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question, "History.SaveLeave", "History.KeepLeave");
        return result == MessageBoxResult.No || result == MessageBoxResult.Yes && await SaveSessionAsync(session, false);
    }

    private async Task ExportAsync(bool transparent = false)
    {
        if (DocumentOperationBusy) return;
        _inspector?.CompletePendingEdit();
        _editor.Cancel(); var session = Active;
        _documentBusy = true;
        try
        {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = WindowTitles.Format(L.Get(transparent ? "Export.TransparentTitle" : "Export.CompositeTitle"), !_host.IsEditing),
            Filter = transparent ? L.Get("Export.PngFilter") : L.Get("Export.ImageFilter"),
            DefaultExt = ".png", AddExtension = true, FileName = L.Get("Project.FilePrefix") + DateTime.Now.ToString("yyyyMMdd-HHmmss")
        };
        if (SessionWindowCoordinator.ShowNativeModal(_toolbar, () => dialog.ShowDialog(_toolbar)) != true) return;
        var snapshot = session.Document.Clone(); var resources = ResourceSnapshot(snapshot).ToList();
        var animationTime = _renderer.AnimationTime;
        var screenOrigin = _editor.ScreenOrigin;
            string? captureWarning = snapshot.Kind == DocumentKind.FrozenCapture ? _documentFrozenCaptureWarning : null;
            CapturedDesktop? screenCapture = null;
            if ((!transparent || SceneRenderer.HasLiveEffects(snapshot)) && snapshot.Kind == DocumentKind.LiveOverlay)
            {
                var capture = await CaptureCleanDesktopAsync();
                captureWarning = CaptureWarning(capture);
                screenCapture = capture;
            }
            bool jpeg = !transparent && Path.GetExtension(dialog.FileName).ToLowerInvariant() is ".jpg" or ".jpeg";
            byte[] result = await Task.Run(() =>
            {
                using var backdrop = CreateScreenBackdrop(screenCapture, screenOrigin);
                var assets = ResourceLibrary(resources); using var renderer = new SceneRenderer { AnimationTime = animationTime, ScreenBackdrop = backdrop };
                return jpeg ? renderer.ExportJpeg(snapshot, assets) : renderer.ExportPng(snapshot, assets, transparent);
            }, _documentLifetime.Token);
            await WriteExportAsync(dialog.FileName, result, _documentLifetime.Token);
            UpdateStatus(WithCaptureWarning(L.Get("Export.SavedPrefix") + Path.GetFileName(dialog.FileName), transparent ? null : captureWarning));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowError(L.Get("Export.Failed") + ex.Message); }
        finally { _documentBusy = false; PruneDocumentResources(); RequestRender(); }
    }
    private static async Task WriteExportAsync(string path, byte[] bytes, CancellationToken token)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temporary, bytes, token); token.ThrowIfCancellationRequested(); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void CopySelection() => _ = CopySelectionAsync();
    private async Task CopySelectionAsync()
    {
        if (DocumentOperationBusy) return;
        _editor.Cancel(); var session = Active;
        var selected = ObjectClipboardCommand.CreateSnapshot(session);
        bool objects = selected.Elements.Count > 0;
        var snapshot = objects ? ObjectClipboardCommand.CreateRasterSnapshot(session, selected) : session.Document.Clone();
        var resources = ResourceSnapshot(snapshot).ToList();
        var effectContext = objects && SceneRenderer.HasLiveEffects(snapshot) ? session.Document.Clone() : null;
        var rasterResources = effectContext is null ? resources.ToList() : ResourceSnapshot(effectContext).ToList();
        var animationTime = _renderer.AnimationTime;
        var screenOrigin = _editor.ScreenOrigin;
        _documentBusy = true;
        try
        {
            string? captureWarning = !objects && snapshot.Kind == DocumentKind.FrozenCapture ? _documentFrozenCaptureWarning : null;
            CapturedDesktop? screenCapture = null;
            if ((!objects || SceneRenderer.HasLiveEffects(snapshot)) &&
                (effectContext?.Kind ?? snapshot.Kind) == DocumentKind.LiveOverlay)
            {
                var capture = await CaptureCleanDesktopAsync();
                captureWarning = CaptureWarning(capture);
                screenCapture = capture;
            }
            var encoded = await Task.Run(() =>
            {
                using var backdrop = CreateScreenBackdrop(screenCapture, screenOrigin);
                var assets = ResourceLibrary(rasterResources); using var renderer = new SceneRenderer { LiveEffectContext = effectContext, AnimationTime = animationTime, ScreenBackdrop = backdrop };
                byte[] png = renderer.ExportPng(snapshot, assets, objects);
                string? internalObjects = objects ? JsonSerializer.Serialize(new ClipboardObjects
                {
                    Document = selected, Assets = resources.Select(r => new ClipboardAsset { Id = r.Id, MediaType = r.MediaType, Bytes = r.Data.ToArray() }).ToList()
                }) : null;
                return (png, internalObjects);
            }, _documentLifetime.Token);
            var data = new Wpf.DataObject();
            using var pngStream = new MemoryStream(encoded.png, writable: false);
            var decoder = new PngBitmapDecoder(pngStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var bitmap = decoder.Frames[0]; bitmap.Freeze();
            data.SetData("PNG", new MemoryStream(encoded.png, writable: false));
            data.SetData(Wpf.DataFormats.Bitmap, bitmap);
            if (encoded.internalObjects is not null) data.SetData(ObjectClipboardFormat, encoded.internalObjects);
            Wpf.Clipboard.SetDataObject(data, copy: true);
            UpdateStatus(WithCaptureWarning(objects ? L.Get("Clipboard.CopiedObjects") : L.Get("Clipboard.CopiedImage"), captureWarning));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowError(L.Get("Clipboard.CopyFailed") + ex.Message); }
        finally { _documentBusy = false; PruneDocumentResources(); ScheduleRecovery(); }
    }

    private async Task PasteAsync()
    {
        if (!CanAddCanvasContent()) return;
        if (DocumentOperationBusy) return;
        _editor.Cancel(); var target = Active;
        var projection = CanvasMapping.Create(target.Document).PlaneToScreen;
        _documentBusy = true;
        try
        {
            var data = Wpf.Clipboard.GetDataObject();
            if (data is null) { UpdateStatus(L.Get("Clipboard.Empty")); return; }
            if (data.GetDataPresent(ObjectClipboardFormat))
            {
                string? json = data.GetData(ObjectClipboardFormat) as string;
                if (json is null || json.Length > ImageImportBudget.MaximumBatchBytes) throw new InvalidDataException(L.Get("Clipboard.InvalidObjects"));
                var payload = await Task.Run(() =>
                {
                    var objects = JsonSerializer.Deserialize<ClipboardObjects>(json) ?? throw new InvalidDataException(L.Get("Clipboard.NullObjects"));
                    if (objects.Version != 1 || objects.Assets is null) throw new InvalidDataException(L.Get("Clipboard.InvalidVersion"));
                    SceneValidation.Validate(objects.Document);
                    if (objects.Assets.Count > ImageImportBudget.MaximumFiles) throw new InvalidDataException(L.Get("Images.ImportCapacityExceeded"));
                    ImageImportBudget.RequireBytes(objects.Assets.Sum(a => a.Bytes?.LongLength ?? 0), ImageImportBudget.MaximumBatchBytes);
                    var assets = new AssetLibrary();
                    foreach (var asset in objects.Assets)
                        if (asset.Bytes is null || assets.Add(asset.Bytes, asset.MediaType) != asset.Id) throw new InvalidDataException(L.Get("Clipboard.InvalidAsset"));
                    ImageImportBudget.ValidateImportBatch(assets.Resources);
                    foreach (string id in objects.Document.ReferencedAssets) _ = assets.Get(id);
                    return (objects.Document, assets);
                }, _documentLifetime.Token);
                if (!ReferenceEquals(Active, target) || CanvasMapping.Create(target.Document).PlaneToScreen != projection || !CanAddCanvasContent()) return;
                ObjectClipboardCommand.Insert(target, payload.Document, payload.assets, VisibleDocumentArea());
                SwitchToolState(EditorTool.Select); return;
            }
            // Prefer original copied files over a static bitmap preview when both exist.
            if (data.GetDataPresent(Wpf.DataFormats.FileDrop) && data.GetData(Wpf.DataFormats.FileDrop) is string[] paths)
            {
                _documentBusy = false; await ImportImagesAsync(paths); return;
            }
            byte[]? bytes = await ReadClipboardImageAsync(data, _documentLifetime.Token);
            if (bytes is not null)
            {
                var normalized = await Task.Run(() => AssetImport.Normalize(bytes), _documentLifetime.Token);
                if (ReferenceEquals(Active, target) && CanvasMapping.Create(target.Document).PlaneToScreen == projection && CanAddCanvasContent())
                { if (!InsertImages(target, [normalized], false)) SwitchToolState(EditorTool.Select); }
            }
            else UpdateStatus(L.Get("Clipboard.Empty"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowError(L.Get("Clipboard.PasteFailed") + ex.Message); }
        finally { _documentBusy = false; PruneDocumentResources(); ScheduleRecovery(); }
    }
    internal static byte[]? ReadClipboardImage(Wpf.IDataObject data)
    {
        foreach (string format in new[] { "GIF", "image/gif", "WebP", "WEBP", "image/webp" })
        {
            if (!data.GetDataPresent(format, autoConvert: false)) continue;
            object? value = data.GetData(format, autoConvert: false);
            byte[]? encoded = value as byte[];
            if (value is Stream stream)
            {
                encoded = ImageImportBudget.Read(stream);
            }
            if (encoded is null) continue;
            ImageImportBudget.RequireBytes(encoded.LongLength);
            bool gif = format is "GIF" or "image/gif";
            bool valid = gif ? encoded.AsSpan().StartsWith("GIF87a"u8) || encoded.AsSpan().StartsWith("GIF89a"u8) :
                encoded.Length >= 12 && encoded.AsSpan(0, 4).SequenceEqual("RIFF"u8) && encoded.AsSpan(8, 4).SequenceEqual("WEBP"u8);
            if (!valid) throw new InvalidDataException(L.Get("Images.UnsupportedOrCorrupt"));
            return encoded;
        }
        return ReadClipboardPng(data);
    }
    private static byte[]? ReadClipboardPng(Wpf.IDataObject data)
    {
        if (data.GetDataPresent("PNG"))
        {
            object? value = data.GetData("PNG");
            if (value is byte[] bytes) { ImageImportBudget.RequireBytes(bytes.LongLength); return bytes; }
            if (value is Stream stream) return ImageImportBudget.Read(stream);
        }
        BitmapSource? bitmap = data.GetData(Wpf.DataFormats.Bitmap, autoConvert: true) as BitmapSource;
        if (bitmap is null && Wpf.Clipboard.ContainsImage()) bitmap = Wpf.Clipboard.GetImage();
        if (bitmap is null) return null;
        ImageDecodeBudget.ValidateDimensions(bitmap.PixelWidth, bitmap.PixelHeight);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream(); encoder.Save(output); ImageImportBudget.RequireBytes(output.Length); return output.ToArray();
    }

    private async Task ImportImagesAsync(string[]? paths = null, bool replaceSelected = false)
    {
        if (!CanAddCanvasContent()) return;
        if (DocumentOperationBusy) return;
        _editor.Cancel(); var target = Active;
        var projection = CanvasMapping.Create(target.Document).PlaneToScreen;
        _documentBusy = true;
        try
        {
        if (paths is null)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = WindowTitles.Format(L.Get(replaceSelected ? "Image.ReplaceTitle" : "Image.ImportTitle"), !_host.IsEditing), Filter = L.Get("Image.Filter"), Multiselect = !replaceSelected };
            if (SessionWindowCoordinator.ShowNativeModal(_toolbar, () => dialog.ShowDialog(_toolbar)) != true) return; paths = dialog.FileNames;
        }
        if (paths.Length == 0) return;
        if (replaceSelected && target.SelectedElements().Count(e => e.Kind == ElementKind.Image && !e.Locked) != 1)
        { UpdateStatus(L.Get("Image.SelectFirst")); return; }
        var selectedIds = target.Selection.ToHashSet();
            var normalized = await ImageImportCommand.NormalizeFilesAsync(paths, _documentLifetime.Token);
            if (!CanAddCanvasContent()) return;
            if (!ReferenceEquals(Active, target) || CanvasMapping.Create(target.Document).PlaneToScreen != projection || replaceSelected && !target.Selection.SetEquals(selectedIds))
            { UpdateStatus(L.Get("Image.ImportChanged")); return; }
            if (!InsertImages(target, normalized, replaceSelected)) SwitchToolState(EditorTool.Select);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowError(L.Get("Image.ImportFailed") + ex.Message); }
        finally { _documentBusy = false; PruneDocumentResources(); ScheduleRecovery(); }
    }
    private bool InsertImages(EditorSession target, IReadOnlyList<NormalizedImage> images, bool replace)
    {
        var document = target.Document;
        long revision = target.SceneRevision;
        var projection = CanvasMapping.Create(document).PlaneToScreen;
        if (!replace && _editor.BeginBillboardInsertion((depth, screenCenter) =>
        {
            if (_disposed || _documentStopped || !CanAddCanvasContent() || !ReferenceEquals(Active, target) ||
                !ReferenceEquals(target.Document, document) || target.SceneRevision != revision ||
                CanvasMapping.Create(document).PlaneToScreen != projection) return;
            ImageImportCommand.InsertBillboard(target, images, document.Viewport, depth, screenCenter);
            SwitchToolState(EditorTool.Select);
            ScheduleRecovery(); RequestRender(); UpdateStatus(L.Format("Image.Imported", images.Count));
        }))
        {
            RequestRender();
            return true;
        }
        ImageImportCommand.Insert(target, images, VisibleDocumentArea(), replace);
        UpdateStatus(replace ? L.Get("Image.Replaced") : L.Format("Image.Imported", images.Count));
        return false;
    }

    private void ReturnToLive()
    {
        if (ReferenceEquals(Active, _live)) return;
        _editor.Cancel(); SynchronizeLiveDisplayLayout(); _editor.ReplaceSession(_live, _liveOrigin); SwitchToolState(_documentLiveTool);
        ScheduleRecovery(); RequestRender(); UpdateStatus(L.Get("Capture.ReturnedLive"));
    }
    private async Task<CapturedDesktop> CaptureCleanDesktopAsync()
    {
        var target = Active;
        var document = target.Document;
        long revision = target.SceneRevision, editingVersion = _host.EditingSessionVersion;
        bool ContentCurrent() => !_disposed && !_documentStopped && !_documentExitRequested &&
            ReferenceEquals(target, Active) && ReferenceEquals(document, target.Document) && target.SceneRevision == revision;
        _cleanCaptureDepth++;
        StopLiveEffectsCapture();
        try
        {
            // WPF Popup/ContextMenu/ToolTip HWNDs are not members of Application.Windows.
            // Include their presentation sources so transient editor chrome cannot enter a capture.
            var ownWindows = PresentationSource.CurrentSources.OfType<HwndSource>()
                .Where(source => source.RootVisual is UIElement { IsVisible: true }).Select(source => source.Handle)
                .Concat(_app.Windows.Cast<Window>().Where(w => w.IsVisible).Select(w => new WindowInteropHelper(w).Handle))
                .Where(h => h != 0).Distinct().ToArray();
            var scope = _host.BeginCleanCapture(ownWindows, ContentCurrent);
            CapturedDesktop captured;
            try { captured = await _documentCaptureService.CaptureAsync(_host.Monitors, scope.FrameBoundary, _documentLifetime.Token); }
            finally { scope.Dispose(); }
            if (!ContentCurrent() || scope.Continuation is not { } receipt ||
                !_host.TryConsumeCaptureContinuation(receipt, editingVersion, out _))
            {
                _diagnostics.Write("Capture.ResultRejected", new { scope.Status, scope.Strategy, ContentCurrent = ContentCurrent() });
                throw new OperationCanceledException(L.Get("Desktop.Cancelled"));
            }
            if (CaptureWarning(captured) is { } warning) UpdateStatus(warning);
            return captured;
        }
        finally { _cleanCaptureDepth--; RequestRender(); }
    }
    private static string? CaptureWarning(CapturedDesktop capture) => capture.Outputs.Any(output => output.ProtectedContentMaskedOut)
        ? L.Get("Capture.ProtectedRegions") : null;
    private static string WithCaptureWarning(string message, string? warning) => warning is null ? message : message + " " + warning;
    private MonitorInfo CurrentMonitor()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        return _host.Monitors.FirstOrDefault(m => m.Bounds.Contains(new(cursor.X, cursor.Y))) ?? _host.PrimaryMonitor;
    }
    private RectD VisibleDocumentArea()
    {
        var b = CurrentMonitor().WorkArea;
        var visible = new RectD(b.Left - _editor.ScreenOrigin.X, b.Top - _editor.ScreenOrigin.Y, b.Width, b.Height).Intersect(Active.Document.Viewport);
        if (visible.IsEmpty) visible = Active.Document.Viewport;
        var mapping = CanvasMapping.Create(Active.Document);
        if (mapping.ScreenToPlane.TryTransformBounds(visible, out var plane)) return plane;
        return mapping.TryToPlane(visible.Center, out var center)
            ? new(center.X - 320, center.Y - 240, 640, 480) : Active.Document.Viewport;
    }
    private static DesktopBackdrop? CreateScreenBackdrop(CapturedDesktop? capture, PointD origin = default) => capture is null ? null :
        DesktopBackdrop.FromEncoded(new(capture.Bounds.Left - origin.X, capture.Bounds.Top - origin.Y, capture.Bounds.Width, capture.Bounds.Height), capture.PngBytes);
    private PointD CenteredOrigin(RectD viewport)
    {
        return CaptureViewportPlacement.CenterOrigin(viewport, CurrentMonitor().Bounds);
    }
    private bool EnsureCaptureVisible()
    {
        if (_capture is null) return false;
        var origin = CaptureViewportPlacement.ResolveOrigin(_capture.Document.Viewport, _captureOrigin, _host.Monitors);
        if (origin == _captureOrigin) return false;
        _captureOrigin = origin;
        if (ReferenceEquals(Active, _capture)) _editor.ScreenOrigin = origin;
        return true;
    }
}
