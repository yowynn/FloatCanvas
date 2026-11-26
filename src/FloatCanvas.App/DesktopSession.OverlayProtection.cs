using System.IO;
using System.Windows.Threading;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private readonly OverlayBackendProtection? _backendProtection;
    private readonly DesktopFallbackState? _fallbackState;
    private bool _overlaySwitchPending;
    internal event Action<OverlayBackendMode, string?>? OverlaySwitchRequested;
    internal bool UsesEnhancedOverlay => _host.UsesSystemInput;
    internal OverlayBackendProtection? BackendProtection => _backendProtection;

    internal sealed record DesktopFallbackState(AssetLibrary Assets, EditorSession Live, EditorSession? Capture,
        PointD CaptureOrigin, bool CaptureActive, EditorTool Tool, EditorTool LiveTool, bool Visible,
        Dictionary<EditorSession, (string? Path, string? Fingerprint, long ContentVersion, FloatCanvas.Storage.ProjectFileVersion? DiskVersion)> Files,
        WorkspaceRecoveryHistory? History, string? RecoveryFingerprint, bool RecoveryWritesBlocked,
        string? FrozenCaptureWarning, ProjectOpenRequest[] PendingProjects, PointD LiveOrigin = default,
        PrefabEditContext? PrefabContext = null, PointD ActiveOrigin = default, GroupCanvasMode GroupMode = GroupCanvasMode.RootProject);

    internal bool TryRequestDesktopFallback(Exception error)
    {
        if (!_host.UsesSystemInput || _backendProtection?.AllowsAutomaticFallback != true || !OverlayBackendProtection.IsInteropFailure(error)) return false;
        RequestDesktopFallback(error.Message);
        return true;
    }

    private void RequestDesktopFallback(string reason)
        => RequestOverlaySwitch(OverlayBackendMode.Desktop, reason);

    private void RequestOverlaySwitch(OverlayBackendMode target, string? failure = null)
    {
        if (_disposed || _overlaySwitchPending || _documentExitRequested ||
            (target == OverlayBackendMode.Enhanced) == _host.UsesSystemInput) return;
        if (failure is not null)
        {
            if (_backendProtection?.AllowsAutomaticFallback != true) return;
            _backendProtection.Disable(failure);
            _diagnostics.Write("Input.DesktopFallbackRequested", new { Reason = failure });
        }
        else _diagnostics.Write(target == OverlayBackendMode.Enhanced ? "Input.EnhancedModeRequested" : "Input.DesktopModeRequested");
        _overlaySwitchPending = true;
        // Leave the native callback before rebuilding any of its HWNDs.
        _app.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => OverlaySwitchRequested?.Invoke(target, failure));
    }

    internal async Task<DesktopSession> SwitchOverlayAsync(OverlayBackendMode target, string? failure, Action retire,
        Func<OverlayBackendProtection.ProbeResult>? probe = null, Action<OverlayHost>? prepareInput = null)
    {
        bool enhanced = target == OverlayBackendMode.Enhanced;
        var protection = failure is not null ? _backendProtection : new OverlayBackendProtection(ApplicationPaths.ProfileDirectory,
            enhanced ? OverlayBackendMode.Auto : OverlayBackendMode.Desktop, probe);
        OverlayHost? candidate = null;
        try
        {
            if (enhanced)
            {
                var cancellation = _documentLifetime.Token;
                var availability = await protection!.ProbeAsync(cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (!protection.TryEnable(() =>
                {
                    // Stage the native windows and passive hooks before relinquishing
                    // the ordinary session. Neither preparation starts an input lease.
                    candidate = new OverlayHost(_host.Scope, _host.SelectedMonitorDevice, initiallyVisible: false);
                    candidate.EnableSystemInput(Path.Combine(ApplicationPaths.ProfileDirectory, "overlay-input-safety.log"));
                    if (prepareInput is null) candidate.PrepareKeyboardIsolation();
                    else prepareInput(candidate);
                }, availability))
                {
                    _diagnostics.Write("Input.EnhancedModeSwitchFailed", new { Reason = protection.Failure });
                    var message = L.Get("OverlaySafety.EnhancedSwitchFailed");
                    UpdateStatus(message);
                    if (!_smokeStartup)
                        _tray.ShowBalloonTip(5000, L.Get("OverlaySafety.ModeEnhanced"), message, System.Windows.Forms.ToolTipIcon.Warning);
                    return this;
                }
            }
            else candidate = new OverlayHost(_host.Scope, _host.SelectedMonitorDevice, initiallyVisible: false);
            var state = await PrepareDesktopFallbackAsync();
            retire();
            Dispose();
            if (failure is null) _backendProtection?.Complete();
            var replacement = new DesktopSession(_app, candidate, protection, state);
            candidate = null;
            return replacement;
        }
        finally
        {
            if (candidate is not null)
            {
                candidate.Dispose();
                protection?.Complete();
            }
            if (!_disposed) EndOverlaySwitchRequest();
        }
    }

    private void EndOverlaySwitchRequest()
    {
        _overlaySwitchPending = false;
        if (_projectOpenRequests.Count != 0) ScheduleProjectOpenDrain(DrainProjectOpenRequestsAsync);
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
            _documentFiles.ToDictionary(pair => pair.Key, pair => (pair.Value.Path, pair.Value.SavedFingerprint, pair.Value.SavedContentVersion, pair.Value.DiskVersion)),
            _workspaceRecoveryHistory, _documentRecoveryFingerprint, _documentRecoveryWritesBlocked,
            _documentFrozenCaptureWarning, _projectOpenRequests.ToArray(), _liveOrigin, _prefabEditContext, _editor.ScreenOrigin, _groupCanvasMode);
    }

    private void RestoreFallbackWorkspace(DesktopFallbackState state)
    {
        _live = state.Live; _capture = state.Capture; _captureOrigin = state.CaptureOrigin; _liveOrigin = state.LiveOrigin;
        _documentLiveTool = state.LiveTool;
        foreach (var pair in state.Files)
            _documentFiles[pair.Key] = new() { Path = pair.Value.Path, SavedFingerprint = pair.Value.Fingerprint, SavedContentVersion = pair.Value.ContentVersion, DiskVersion = pair.Value.DiskVersion };
        _workspaceRecoveryHistory = state.History;
        _documentRecoveryFingerprint = state.RecoveryFingerprint;
        _documentRecoveryWritesBlocked = state.RecoveryWritesBlocked;
        _documentFrozenCaptureWarning = state.FrozenCaptureWarning;
        _prefabEditContext = state.PrefabContext;
        _groupCanvasMode = state.GroupMode;
        var active = _prefabEditContext?.Session ?? (state.CaptureActive && _capture is not null ? _capture : _live);
        var origin = _prefabEditContext is not null ? state.ActiveOrigin : ReferenceEquals(active, _capture) ? _captureOrigin : _liveOrigin;
        _editor.ReplaceSession(active, origin);
        SwitchToolState(state.Tool);
        _host.SetVisible(state.Visible);
        foreach (var project in state.PendingProjects) QueueProjectOpen(project.Path, project.EnterEditing);
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
