using System.Windows;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private bool CanAddCanvasContent()
    {
        if (_host.IsVisible) return true;
        UpdateStatus(L.Get("Feedback4.Canvas.Hidden"));
        return false;
    }

    private void ToggleCanvasVisibility()
    {
        if (DocumentOperationBusy && !_host.IsEditing) return;
        _editor.Cancel();
        CloseImageDrop();
        _host.SetVisible(!_host.IsVisible);
        UpdateStatus(L.Get(_host.IsVisible ? "Feedback4.Canvas.Visible" : "Feedback4.Canvas.Hidden"));
        RequestRender();
    }

    private async Task StartNewCanvasAsync()
    {
        _documentBusy = true;
        _documentRecoveryTimer?.Stop();
        try
        {
            _editor.Cancel();
            await _documentRecoveryTask;
            // Complete a self-contained archive before replacing any live document.
            await RecoveryHistory.PreserveBeforeFirstWriteAsync(_documentLifetime.Token);
            await _recoveryStore.SaveAsync(SnapshotWorkspace(), _assets, _documentLifetime.Token);
            await RecoveryHistory.PreserveCurrentAsync(_documentLifetime.Token);
            _textEditor?.Close();
            var previous = _live.Document;
            var tool = _editor.Tool;
            int first = Math.Clamp(_settings.NumberStart,1,999999);
            _live = new(new SceneDocument { Viewport = previous.Viewport, LayerOpacity = previous.LayerOpacity,
                DisplayLayout = previous.DisplayLayout.Select(m => m.Clone()).ToList(), NextNumber = first }, _assets);
            _capture = null; _captureOrigin = default; _documentFrozenCaptureWarning = null;
            _documentFiles.Clear();
            _editor.ReplaceSession(_live, new(0,0)); _editor.Tool = tool;
            _documentRecoveryFingerprint = null;
            RecoveryHistory.WorkspaceRestored();
            await FlushRecoveryAsync();
            UpdateStatus(L.Get("Feedback4.Canvas.NewStarted"));
            RequestRender();
        }
        finally { _documentBusy = false; }
    }
}
