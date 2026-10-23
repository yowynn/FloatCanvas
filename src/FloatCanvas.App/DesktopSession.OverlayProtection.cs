using System.Windows.Threading;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private readonly OverlayBackendProtection? _backendProtection;
    private readonly DesktopFallbackState? _fallbackState;
    private bool _desktopFallbackPending;
    internal event Action<string>? DesktopFallbackRequested;
    internal bool UsesEnhancedOverlay => _host.UsesSystemInput;

    internal sealed record DesktopFallbackState(AssetLibrary Assets, EditorSession Live, EditorSession? Capture,
        PointD CaptureOrigin, bool CaptureActive, EditorTool Tool, EditorTool LiveTool, bool Visible,
        Dictionary<EditorSession, (string? Path, string? Fingerprint)> Files,
        WorkspaceRecoveryHistory? History, string? RecoveryFingerprint, bool RecoveryWritesBlocked,
        string? FrozenCaptureWarning, string[] PendingProjects);

    internal bool TryRequestDesktopFallback(Exception error)
    {
        if (!_host.UsesSystemInput || _backendProtection is null || !OverlayBackendProtection.IsInteropFailure(error)) return false;
        RequestDesktopFallback(error.Message);
        return true;
    }

    private void RequestDesktopFallback(string reason)
    {
        if (_disposed || _desktopFallbackPending || !_host.UsesSystemInput || _backendProtection is null) return;
        _desktopFallbackPending = true;
        _backendProtection.Disable(reason);
        _diagnostics.Write("Input.DesktopFallbackRequested", new { Reason = reason });
        // Leave the native callback before rebuilding any of its HWNDs.
        _app.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => DesktopFallbackRequested?.Invoke(reason));
    }

    internal async Task<DesktopFallbackState> PrepareDesktopFallbackAsync()
    {
        // A failed release must abort the live switch. The durable marker still makes the next
        // ordinary launch safe; never run a second input backend beside an unconfirmed lease.
        _host.ExitEditing();
        _toolbar.Hide(); _inspector?.Hide(); _layers?.Hide(); CloseToolOptions();
        while (DocumentOperationBusy && !_disposed && !_documentExitRequested)
            await Task.Delay(50, _documentLifetime.Token);
        if (_disposed || _documentExitRequested) throw new OperationCanceledException();
        _inspector?.CompletePendingEdit();
        FinishInlineText(); FlushToolState(commitDraft: true); _editor.Cancel();
        try { await FlushRecoveryAsync(); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            // The live object graph below remains authoritative even if autosave storage is full.
            _diagnostics.Write("Recovery.DesktopFallbackSaveFailed", exception: error);
        }
        return new(_assets, _live, _capture, _captureOrigin, ReferenceEquals(Active, _capture), _editor.Tool,
            _documentLiveTool, _host.IsVisible,
            _documentFiles.ToDictionary(pair => pair.Key, pair => (pair.Value.Path, pair.Value.SavedFingerprint)),
            _workspaceRecoveryHistory, _documentRecoveryFingerprint, _documentRecoveryWritesBlocked,
            _documentFrozenCaptureWarning, _projectOpenRequests.ToArray());
    }

    private void RestoreFallbackWorkspace(DesktopFallbackState state)
    {
        _live = state.Live; _capture = state.Capture; _captureOrigin = state.CaptureOrigin;
        _documentLiveTool = state.LiveTool;
        foreach (var pair in state.Files)
            _documentFiles[pair.Key] = new() { Path = pair.Value.Path, SavedFingerprint = pair.Value.Fingerprint };
        _workspaceRecoveryHistory = state.History;
        _documentRecoveryFingerprint = state.RecoveryFingerprint;
        _documentRecoveryWritesBlocked = state.RecoveryWritesBlocked;
        _documentFrozenCaptureWarning = state.FrozenCaptureWarning;
        var active = state.CaptureActive && _capture is not null ? _capture : _live;
        _editor.ReplaceSession(active, ReferenceEquals(active, _capture) ? _captureOrigin : new(0, 0));
        SwitchToolState(state.Tool);
        if (!state.Visible) _host.SetVisible(false);
        foreach (var project in state.PendingProjects) QueueProjectOpen(project);
    }

    private void NotifyDesktopFallback()
    {
        if (_backendProtection?.Failure is null) return;
        _diagnostics.Write("Input.DesktopFallback", new { Reason = _backendProtection.Failure });
        UpdateStatus(L.Get("OverlaySafety.DesktopNotice"));
        if (!_smokeStartup)
            _tray.ShowBalloonTip(5000, L.Get("OverlaySafety.DesktopTitle"), L.Get("OverlaySafety.DesktopNotice"),
                System.Windows.Forms.ToolTipIcon.Warning);
    }

    private bool _smokeStartup;
}
