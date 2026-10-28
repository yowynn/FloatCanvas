using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private readonly ApplicationDiagnostics _diagnostics = new(ApplicationPaths.ProfileDirectory);
    private SnapshotCaptureRequest? _snapshotCaptureRequest;
    private Task _snapshotCaptureTask = Task.CompletedTask;
    private long _exitDiagnosticStartedAt;
    private string _exitDiagnosticSource = "Unknown";
    private Guid? _exitDiagnosticId;

    private void LogExitStage(string stage, string? reason = null, Exception? exception = null)
    {
        try
        {
            _diagnostics.Write("App.Exit.Stage", new
            {
                Stage = stage, Reason = reason, Source = _exitDiagnosticSource,
                ElapsedMilliseconds = _exitDiagnosticStartedAt == 0 ? (long?)null : Environment.TickCount64 - _exitDiagnosticStartedAt,
                ManagedThreadId = Environment.CurrentManagedThreadId, _quietStartup, _disposed,
                _documentBusy, _documentExitRequested, _documentStopped,
                SnapshotPending = _snapshotCaptureRequest is not null, SnapshotTask = _snapshotCaptureTask.Status,
                RecoveryTask = _documentRecoveryTask.Status, RecoveryGateAvailable = _documentRecoveryGate.CurrentCount,
                Editing = _host.IsEditing, CanvasVisible = _host.IsVisible, Enhanced = _host.UsesSystemInput,
                DispatcherShutdownStarted = _app.Dispatcher.HasShutdownStarted
            }, exception, _exitDiagnosticId);
        }
        catch { } // Diagnostic state capture must not change the shutdown path.
    }

    private void BeginSnapshotCapture(CaptureSelection selection) =>
        _snapshotCaptureTask = RunCommand(() => InsertSnapshotAsync(selection));

    private void CancelSnapshotCapture(string reason = "ModeExit", bool userInitiated = true)
    {
        if (_snapshotCaptureRequest is { } request)
        {
            request.Cancel(reason, userInitiated);
            LogSnapshot(request, "CancelRequested", reason);
        }
        _host.CancelPendingCapture();
    }

    private void OnCaptureDiagnostic(CaptureDiagnosticEvent diagnostic)
    {
        var request = _snapshotCaptureRequest;
        if (request is not null && (request.NativeOperationId is null || request.NativeOperationId == diagnostic.OperationId))
        {
            request.NativeOperationId ??= diagnostic.OperationId;
            request.LatestNativeDiagnostic = diagnostic;
            if (diagnostic.Phase == "Outcome") request.NativeOutcome = diagnostic;
        }
        _diagnostics.Write("Capture." + diagnostic.Phase, diagnostic, requestId: request?.Id);
    }

    private bool LogSnapshot(SnapshotCaptureRequest request, string phase, string? reason = null,
        CaptureSelection? selection = null, CaptureRestoreStatus? status = null, Exception? exception = null) =>
        _diagnostics.Write("Snapshot." + phase, new
        {
            Reason = reason, Status = status, request.NativeOperationId, RequestEditingVersion = request.EditingVersion,
            CurrentEditingVersion = _host.EditingSessionVersion, request.ContentRevision, CurrentRevision = Active.SceneRevision,
            TargetIsCurrent = ReferenceEquals(request.Target, Active), _host.IsEditing, _host.IsVisible,
            KeyboardLeaseActive = _host.IsKeyboardIsolationActive, _host.ForegroundLossVersion,
            request.Cancelled, request.UserCancellation, request.CancellationReason,
            Shape = selection?.Shape, Source = selection?.Source, Bounds = selection?.Bounds,
            PointCount = selection?.Points.Count, UndoCount = Active.UndoCount, ElementCount = Active.Document.Elements.Count
        }, exception, request.Id);

    private void ReportSnapshotFailure(SnapshotCaptureRequest request, string reason, CaptureRestoreStatus status,
        Exception? exception = null)
    {
        bool recorded = LogSnapshot(request, "Failed", reason, status: status, exception: exception);
        if (_disposed || _documentStopped || _documentExitRequested || !request.MatchesDocument(Active) ||
            !SnapshotFailureFeedback.ShouldNotify(status, request.NativeOutcome, request.LatestNativeDiagnostic,
                request.UserCancellation, request.CancellationReason)) return;
        string message = L.Format(recorded ? "Snapshot.FailureLog" : "Snapshot.FailureLogUnavailable", _diagnostics.LogPath);
        UpdateStatus(message);
        // A failed restoration must not activate another window or interrupt an external app.
        try { _tray.ShowBalloonTip(6000, L.Get("Snapshot.FailureTitle"), message, System.Windows.Forms.ToolTipIcon.Warning); }
        catch (Exception notificationError) { _diagnostics.Write("Snapshot.NotificationFailed", exception: notificationError, requestId: request.Id); }
    }

    private async Task ExitAfterSnapshotAsync(string source)
    {
        if (!_documentExitRequested)
        {
            _exitDiagnosticStartedAt = Environment.TickCount64;
            _exitDiagnosticSource = source;
            _exitDiagnosticId = Guid.NewGuid();
        }
        LogExitStage("Requested");
        if (_snapshotCaptureRequest is not null)
        {
            LogExitStage("Snapshot.Cancel.Begin");
            CancelSnapshotCapture("ApplicationExit"); SetEditing(false);
            LogExitStage("Snapshot.Wait.Begin");
            await _snapshotCaptureTask;
            LogExitStage("Snapshot.Wait.Complete");
        }
        LogExitStage("Command.Dispatch", DocumentOperationBusy ? "Busy" : null);
        await RunCommand(RequestExitAsync);
        LogExitStage("Command.Returned");
    }

    private async Task InsertSnapshotAsync(CaptureSelection selection)
    {
        if (_snapshotCaptureRequest is not null || !_host.IsEditing || Active.Document.Kind != DocumentKind.LiveOverlay)
        {
            _diagnostics.Write("Snapshot.BeginRejected", new { _host.IsEditing, _host.IsVisible, Kind = Active.Document.Kind });
            return;
        }
        var request = new SnapshotCaptureRequest(Active, _host.EditingSessionVersion);
        var screenOrigin = _editor.ScreenOrigin;
        _snapshotCaptureRequest = request;
        bool ContentCurrent() => !_disposed && !_documentStopped && !_documentExitRequested &&
            ReferenceEquals(_snapshotCaptureRequest, request) && request.MatchesContent(Active) && screenOrigin == _editor.ScreenOrigin;
        bool CanInsert()
        {
            string? rejection = _disposed ? "Disposed" : _documentStopped ? "Stopped" : _documentExitRequested ? "ApplicationExit" :
                !ReferenceEquals(_snapshotCaptureRequest, request) ? "Superseded" : screenOrigin != _editor.ScreenOrigin ? "ScreenOriginChanged" : request.ContentRejection(Active) ??
                (!_host.IsEditing ? "NotEditing" :
                _windowCoordinator?.CanContinue(new(request.EditingVersion, true, Active.Document), Active.Document) != true ? "SessionOrForegroundChanged" : null);
            if (rejection is null) return true;
            LogSnapshot(request, "InsertRejected", rejection);
            ReportSnapshotFailure(request, rejection, CaptureRestoreStatus.Failed);
            return false;
        }
        _documentBusy = true;
        var animationTime = _renderer.AnimationTime;
        LogSnapshot(request, "Begin", selection: selection);
        try
        {
            // Bind the request before yielding, but let the original pointer-up handler
            // release its gesture capture before a clean-capture carrier acquires it.
            await Dispatcher.Yield(DispatcherPriority.Input);
            if (!CanInsert()) return;
            var snapshot = _host.IsVisible ? Active.CreateCommittedSnapshot() : HiddenCanvasPreview().Clone();
            // Capture and rendering use the same raw opacity, including invisible live effects.
            snapshot.LayerOpacity = 1;
            var resources = ResourceSnapshot(snapshot);
            var displays = _host.Monitors.Select(m => new RectD(m.Bounds.Left - screenOrigin.X, m.Bounds.Top - screenOrigin.Y, m.Bounds.Width, m.Bounds.Height)).ToArray();
            CapturedDesktop? desktop = null;
            if (selection.Source != CaptureSource.CanvasOnly || SceneRenderer.HasLiveEffects(snapshot))
            {
                _cleanCaptureDepth++; StopLiveEffectsCapture();
                try
                {
                    var ownWindows = PresentationSource.CurrentSources.OfType<HwndSource>()
                        .Where(source => source.RootVisual is UIElement { IsVisible: true }).Select(source => source.Handle)
                        .Concat(_app.Windows.Cast<Window>().Where(window => window.IsVisible).Select(window => new WindowInteropHelper(window).Handle))
                        .Where(handle => handle != 0).Distinct().ToArray();
                    var scope = _host.BeginCleanCapture(ownWindows, ContentCurrent);
                    _diagnostics.Write("Snapshot.FrameBoundary", new { scope.Strategy, scope.FrameBoundary }, requestId: request.Id);
                    try
                    {
                        desktop = await _documentCaptureService.CaptureAsync(_host.Monitors, scope.FrameBoundary, _documentLifetime.Token);
                        _diagnostics.Write("Snapshot.FrameAcquired", new { desktop.CapturedAtUtc, desktop.Outputs }, requestId: request.Id);
                    }
                    finally { scope.Dispose(); LogSnapshot(request, "RestoreOutcome", status: scope.Status); }
                    if (scope.Continuation is not { } receipt)
                    { ReportSnapshotFailure(request, "NoContinuation", scope.Status); return; }
                    if (!_host.TryConsumeCaptureContinuation(receipt, request.EditingVersion, out long restoredVersion))
                    { ReportSnapshotFailure(request, "ContinuationRejected", scope.Status); return; }
                    request.AcceptRestoredVersion(restoredVersion);
                    LogSnapshot(request, "ContinuationAccepted", status: scope.Status);
                }
                finally { _cleanCaptureDepth--; RequestRender(); }
            }
            if (!CanInsert()) return;
            LogSnapshot(request, "RenderBegin");
            var result = await Task.Run(() => SnapshotImageCommand.Render(snapshot, resources, selection, desktop, displays, animationTime, screenOrigin), _documentLifetime.Token);
            if (!CanInsert()) return;
            if (!result.HasVisiblePixels) { LogSnapshot(request, "Empty"); UpdateStatus(L.Get("Snapshot.Empty")); return; }
            SnapshotImageCommand.Insert(request.Target, result, CanvasMapping.Create(snapshot).PlaneToScreen,
                selection.BillboardDepthLine);
            LogSnapshot(request, "Inserted");
            UpdateStatus(WithCaptureWarning(L.Get("Snapshot.Inserted"), desktop is null ? null : CaptureWarning(desktop)));
            ScheduleRecovery();
        }
        catch (OperationCanceledException) when (_documentLifetime.IsCancellationRequested || request.Cancelled)
        { LogSnapshot(request, "Cancelled", request.CancellationReason ?? "LifetimeCancelled"); }
        catch (Exception ex) { ReportSnapshotFailure(request, "Exception", CaptureRestoreStatus.Failed, ex); }
        finally
        {
            LogSnapshot(request, "End");
            if (ReferenceEquals(_snapshotCaptureRequest, request)) _snapshotCaptureRequest = null;
            _documentBusy = false; PruneDocumentResources(); RequestRender();
        }
    }

    private static void DrawCapturePreview(SKCanvas canvas, CaptureSelection selection)
    {
        using var builder = new SKPathBuilder { FillType = SKPathFillType.EvenOdd };
        var b = selection.Bounds;
        var bounds = new SKRect((float)b.Left, (float)b.Top, (float)b.Right, (float)b.Bottom);
        if (selection.Shape == CaptureShape.Ellipse && selection.Points.Count == 0) builder.AddOval(bounds);
        else if (selection.Shape == CaptureShape.Rectangle && selection.Points.Count == 0) builder.AddRect(bounds);
        else if (selection.Points.Count > 0)
        {
            builder.MoveTo((float)selection.Points[0].X, (float)selection.Points[0].Y);
            foreach (var p in selection.Points.Skip(1)) builder.LineTo((float)p.X, (float)p.Y);
            builder.Close();
        }
        using var path = builder.Detach();
        using var outer = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 3, IsAntialias = true, StrokeJoin = SKStrokeJoin.Round };
        using var inner = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.White, StrokeWidth = 1.3f, IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash([5, 4], 0) };
        canvas.DrawPath(path, outer); canvas.DrawPath(path, inner);
        if (selection.Shape == CaptureShape.PolygonLasso && selection.Points.Count > 0)
        {
            var start = selection.Points[0];
            canvas.DrawCircle((float)start.X, (float)start.Y, 4, outer);
            canvas.DrawCircle((float)start.X, (float)start.Y, 4, inner);
        }
    }
}
