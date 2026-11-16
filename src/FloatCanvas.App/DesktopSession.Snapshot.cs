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
        CancelCanvasRectification(reason);
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
            RequestEditScopeVersion = request.EditScopeVersion, CurrentEditScopeVersion = Active.EditScopeVersion,
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
        var display = CurrentGroupCanvasView();
        var capturedMapping = _editor.CurrentCanvasMapping;
        var viewing = EditingCanvasView; var viewEpoch = _canvasViewEpoch;
        var screenOrigin = _editor.CanvasOrigin;
        var prefab = _prefabEditContext; var mode = _groupCanvasMode;
        var owners = prefab?.Chain().Select(item => (Context: item, Document: item.Owner.Document, Revision: item.Owner.PreviewRevision,
            Scope: item.Owner.EditScopeVersion, Path: item.SourcePath, Instance: item.EnteredInstanceId)).ToArray();
        bool ContextCurrent() => ReferenceEquals(prefab, _prefabEditContext) && mode == _groupCanvasMode &&
            (owners is null || owners.All(item => ReferenceEquals(item.Document, item.Context.Owner.Document) && item.Revision == item.Context.Owner.PreviewRevision &&
                item.Scope == item.Context.Owner.EditScopeVersion && item.Path == item.Context.SourcePath && item.Instance == item.Context.EnteredInstanceId));
        var request = new SnapshotCaptureRequest(Active, _host.EditingSessionVersion, viewEpoch, screenOrigin, ContextCurrent);
        var presentation = FreezePresentationCapture(request.Target);
        using var windows = FreezeWindowSourceCapture(request.Target);
        _snapshotCaptureRequest = request;
        bool ContentCurrent() => !_disposed && !_documentStopped && !_documentExitRequested &&
            ReferenceEquals(_snapshotCaptureRequest, request) && request.MatchesContent(Active, _canvasViewEpoch, _editor.CanvasOrigin);
        bool CanInsert()
        {
            string? rejection = _disposed ? "Disposed" : _documentStopped ? "Stopped" : _documentExitRequested ? "ApplicationExit" :
                !ReferenceEquals(_snapshotCaptureRequest, request) ? "Superseded" : request.ContentRejection(Active, _canvasViewEpoch, _editor.CanvasOrigin) ??
                (!_host.IsEditing ? "NotEditing" :
                _windowCoordinator?.CanContinue(new(request.EditingVersion, true, Active.Document), Active.Document) != true ? "SessionOrForegroundChanged" : null);
            if (rejection is null) return true;
            LogSnapshot(request, "InsertRejected", rejection);
            ReportSnapshotFailure(request, rejection, CaptureRestoreStatus.Failed);
            return false;
        }
        _documentBusy = true;
        bool inserted=false;
        var animationTime = _renderer.AnimationTime;
        LogSnapshot(request, "Begin", selection: selection);
        try
        {
            if (!CanInsert()) return;
            var snapshot = _host.IsVisible ? display.Document.Clone() : HiddenCanvasPreview().Clone();
            // Freeze the displayed graph before yielding; parent drafts can have
            // their own revisions even when the active child's content is unchanged.
            snapshot.LayerOpacity = 1;
            var resources = ResourceSnapshot(snapshot);
            // Bind the request before yielding, but let the original pointer-up handler
            // release its gesture capture before a clean-capture carrier acquires it.
            await Dispatcher.Yield(DispatcherPriority.Input);
            if (!CanInsert()) return;
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
            var result = await Task.Run(() => SnapshotImageCommand.Render(snapshot, resources, selection, desktop, displays, animationTime, screenOrigin, presentation, windows, viewing), _documentLifetime.Token);
            if (!CanInsert()) return;
            if (!result.HasVisiblePixels) { LogSnapshot(request, "Empty"); UpdateStatus(L.Get("Snapshot.Empty")); return; }
            SnapshotImageCommand.Insert(request.Target, result, capturedMapping.PlaneToScreen,
                selection.BillboardDepthLine, selection.BillboardScreenFirst, capturedMapping);
            inserted=true;
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
            CompleteGlobalCapture(inserted);
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
