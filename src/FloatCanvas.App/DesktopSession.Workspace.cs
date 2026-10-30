using System.Windows;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private bool CanAddCanvasContent()
    {
        UpdateHiddenCanvasState();
        return true;
    }

    private void ToggleCanvasVisibility()
    {
        EndCanvasCalibration(hidePanel: true);
        // Preserve the hide intent even if a second toggle shows the canvas before the
        // dispatcher next synchronizes capture or resumes an asynchronous preparation.
        if (_host.IsVisible) CancelLivePreparation();
        if (_snapshotCaptureRequest is not null) { CancelSnapshotCapture("CanvasHidden"); _host.SetVisible(false); SetEditing(false); return; }
        if (DocumentOperationBusy && !_host.IsEditing) return;
        _editor.Cancel();
        CloseImageDrop();
        _host.SetVisible(!_host.IsVisible);
        UpdateHiddenCanvasState();
        SynchronizeCanvasTool();
        RefreshPassThroughLauncher();
        UpdateStatus(L.Get(_host.IsVisible ? "Feedback4.Canvas.Visible" : "Feedback4.Canvas.Hidden"));
        RequestRender();
    }

    private async Task StartNewCanvasAsync()
        => await ReplaceCanvasAsync();

    private void RefreshBoundFileIndicator()
    {
        if(_boundFileIndicator is null)return;
        string? path=FileState(Active).Path;
        _boundFileIndicator.Refresh(path,path is not null&&IsDirty(Active),_settings.ToolbarExpanded);
    }
    private void DetachActiveFile()
    {
        if(DocumentOperationBusy)return;
        var file=FileState(Active);if(file.Path is null)return;
        file.Path=null;file.SavedFingerprint=null;file.SavedContentVersion=-1;file.DiskVersion=null;
        RecoveryHistory.EnterWorkspace(new());
        _documentRecoveryFingerprint=null;ScheduleRecovery();RequestRender();
    }
    private bool CanRevertActiveFile() => !DocumentOperationBusy && FileState(Active).Path is not null && IsDirty(Active);

    private async Task RevertActiveFileAsync()
    {
        if (!CanRevertActiveFile()) return;
        EndCanvasCalibration(hidePanel: true);
        _inspector?.CompletePendingEdit();
        FinishInlineText();
        _editor.Cancel();
        var target = Active;
        var file = FileState(target);
        _documentBusy = true;
        try
        {
            var loaded = await _projectStore.LoadAsync(file.Path!, _documentLifetime.Token);
            ImageImportBudget.ValidateResources(loaded.Assets.Resources);
            MergeAssets(loaded.Assets);
            string fingerprint = await Task.Run(() => Fingerprint(loaded.Document), _documentLifetime.Token);
            long? savedVersion = fingerprint == file.SavedFingerprint && file.SavedContentVersion >= 0 ? file.SavedContentVersion : null;
            target.RestoreDocument(L.Get("Project.Revert"), loaded.Document, savedVersion);
            file.SavedFingerprint = fingerprint;
            file.SavedContentVersion = target.ContentVersion;
            file.DiskVersion = loaded.FileVersion;
            _documentRecoveryFingerprint = null;
            RefreshBoundFileIndicator();
            UpdateStatus(L.Get("Project.Reverted"));
            RequestRender();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowError(L.Get("Project.RevertFailed") + ex.Message); }
        finally { _documentBusy = false; PruneDocumentResources(); ScheduleRecovery(); }
    }

    private void LocateActiveFile()
    {
        if(FileState(Active).Path is not { } path)return;
        RunUiAction(()=>
        {
            var start=new System.Diagnostics.ProcessStartInfo("explorer.exe") {UseShellExecute=true};
            start.ArgumentList.Add("/select,");start.ArgumentList.Add(path);
            System.Diagnostics.Process.Start(start);
        });
    }

    private async Task<bool> NeedsNewCanvasConfirmationAsync()
    {
        while (!_disposed)
        {
            var live = _live; var capture = _capture;
            if (FileState(live).Path is not null || capture is not null && FileState(capture).Path is not null) return false;
            long liveRevision = live.PreviewRevision; long? captureRevision = capture?.PreviewRevision;
            var origin = _captureOrigin;
            var snapshot = new WorkspaceState { LiveDocument = live.Document.Clone(), CaptureDocument = capture?.Document.Clone(), CaptureOrigin = origin };
            bool changed = await RecoveryHistory.HasNewVersionAsync(snapshot, _documentLifetime.Token);
            if (ReferenceEquals(_live, live) && ReferenceEquals(_capture, capture) && live.PreviewRevision == liveRevision &&
                capture?.PreviewRevision == captureRevision && _captureOrigin == origin) return changed;
        }
        return false;
    }

    private async Task<bool> ConfirmLeaveWorkspaceAsync()
    {
        if (!await ConfirmReplaceAsync(_live, L.Get("Project.ReplaceLive"))) return false;
        return _capture is null || await ConfirmReplaceAsync(_capture, L.Get("Project.ReplaceCapture"));
    }
    private async Task ArchiveWorkspaceAsync(bool force = false)
    {
        await _documentRecoveryGate.WaitAsync(_documentLifetime.Token);
        try
        {
            await _documentRecoveryTask;
            var snapshot = SnapshotWorkspace();
            var assets = ResourceLibrary(RecoveryResourceSnapshot(snapshot));
            var animationTime = _renderer.AnimationTime;
            await RecoveryHistory.ArchiveAsync(snapshot, assets, null, FileState(Active).Path, force, _documentLifetime.Token,
                (state, resources) => WorkspacePreview.TryRender(state.LiveDocument, resources, animationTime));
        }
        finally { _documentRecoveryGate.Release(); }
    }
    private async Task ClearCurrentCanvasAsync()
    {
        if (DocumentOperationBusy) return;
        _inspector?.CompletePendingEdit();
        _editor.Cancel();
        var target = Active;
        if (target.Document.Elements.Count == 0) return;
        _documentBusy = true;
        try
        {
            FinishInlineText();
            _documentRecoveryTimer?.Stop();
            await _documentRecoveryTask;
            await RecoveryHistory.PrepareClearAsync(_documentLifetime.Token);
            target.Execute(L.Get("Feedback4.Toolbar.ClearCanvas"), document => { document.Elements.Clear(); document.Groups.Clear(); target.Selection.Clear(); });
            await FlushRecoveryAsync();
            UpdateStatus(L.Get("History.Cleared"));
            RequestRender();
        }
        finally { _documentBusy = false; ScheduleRecovery(); }
    }

    private CanvasSettings CreateDefaultCanvasSettings(RectD viewport)
    {
        var primary = _host.PrimaryMonitor.Bounds;
        var reference = new RectD(primary.Left - _liveOrigin.X, primary.Top - _liveOrigin.Y, primary.Width, primary.Height);
        return CanvasPresetLibrary.Apply(CanvasPresetLibrary.OfficialOnly().Official.First().Parameters, new(), reference, viewport);
    }

    private async Task ReplaceCanvasAsync()
    {
        if (DocumentOperationBusy) return;
        EndCanvasCalibration(hidePanel: true);
        _inspector?.CompletePendingEdit();
        _documentBusy = true;
        _documentRecoveryTimer?.Stop();
        try
        {
            _editor.Cancel();
            await _documentRecoveryTask;
            if (!await ConfirmLeaveWorkspaceAsync()) return;
            // Complete a self-contained archive before replacing any live document.
            await RecoveryHistory.PreserveBeforeFirstWriteAsync(_documentLifetime.Token);
            await FlushRecoveryAsync();
            await ArchiveWorkspaceAsync();
            FinishInlineText();
            var previous = _live.Document;
            var tool = _editor.Tool;
            int first = Math.Clamp(_settings.NumberStart,-999999,999999);
            var canvas = _settings.PreserveCanvasSettingsOnNewCanvas ? previous.Canvas.Clone() :
                CreateDefaultCanvasSettings(previous.Viewport);
            _live = new(new SceneDocument { Viewport = previous.Viewport, LayerOpacity = previous.LayerOpacity,
                Canvas = canvas,
                DisplayLayout = previous.DisplayLayout.Select(m => m.Clone()).ToList(), NextNumber = first }, _assets);
            _capture = null; _captureOrigin = default; _documentFrozenCaptureWarning = null;
            _documentFiles.Clear();
            _editor.ReplaceSession(_live, _liveOrigin); _editor.Tool = tool;
            _documentRecoveryFingerprint = null;
            RecoveryHistory.WorkspaceRestored();
            await RecoveryHistory.EnterWorkspaceAsync(SnapshotWorkspace(), _documentLifetime.Token);
            await FlushRecoveryAsync();
            UpdateStatus(L.Get("Feedback4.Canvas.NewStarted"));
            RequestRender();
        }
        finally { _documentBusy = false; ScheduleRecovery(); }
    }
}
