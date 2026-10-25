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
            string fingerprint = Fingerprint(loaded.Document);
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

    private bool NeedsNewCanvasConfirmation() => FileState(_live).Path is null &&
        (_capture is null || FileState(_capture).Path is null) && RecoveryHistory.HasNewVersion(new()
        {LiveDocument=_live.Document,CaptureDocument=_capture?.Document,CaptureOrigin=_captureOrigin});

    private async Task<bool> ConfirmLeaveWorkspaceAsync()
    {
        if (!await ConfirmReplaceAsync(_live, L.Get("Project.ReplaceLive"))) return false;
        return _capture is null || await ConfirmReplaceAsync(_capture, L.Get("Project.ReplaceCapture"));
    }
    private async Task ArchiveWorkspaceAsync(bool force = false)
    {
        var snapshot = SnapshotWorkspace();
        byte[]? preview;
        try { preview = WorkspacePreview.Render(snapshot.LiveDocument, _assets, _renderer.AnimationTime); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or System.IO.IOException or OverflowException) { preview = null; }
        await RecoveryHistory.ArchiveAsync(snapshot, _assets, preview, FileState(Active).Path, force, _documentLifetime.Token);
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
            int first = Math.Clamp(_settings.NumberStart,1,999999);
            _live = new(new SceneDocument { Viewport = previous.Viewport, LayerOpacity = previous.LayerOpacity,
                Canvas = previous.Canvas.Clone(),
                DisplayLayout = previous.DisplayLayout.Select(m => m.Clone()).ToList(), NextNumber = first }, _assets);
            _capture = null; _captureOrigin = default; _documentFrozenCaptureWarning = null;
            _documentFiles.Clear();
            _editor.ReplaceSession(_live, new(0,0)); _editor.Tool = tool;
            _documentRecoveryFingerprint = null;
            RecoveryHistory.WorkspaceRestored();
            RecoveryHistory.EnterWorkspace(SnapshotWorkspace());
            await FlushRecoveryAsync();
            UpdateStatus(L.Get("Feedback4.Canvas.NewStarted"));
            RequestRender();
        }
        finally { _documentBusy = false; ScheduleRecovery(); }
    }
}
