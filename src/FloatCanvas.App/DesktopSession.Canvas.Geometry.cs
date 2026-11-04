using System.Windows;
using System.Windows.Threading;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private EditorSession? _canvasGridCalibrationSession;
    private long _canvasGridCalibrationRevision;
    private long _canvasGridCalibrationEditingVersion;
    private readonly List<PointD> _canvasGridCalibrationPoints = [];
    private CanvasSettings? _canvasGridCalibrationPreview;
    private bool _canvasGridCalibrationDialogPending;
    private SnapshotCaptureRequest? _canvasRectificationRequest;
    private CanvasRectificationPreviewWindow? _canvasRectificationWindow;
    private CanvasGridCalibrationWindow? _canvasGridCalibrationWindow;

    private void CancelCanvasRectification(string reason)
    {
        _canvasRectificationRequest?.Cancel(reason, userInitiated: true);
        if (_canvasRectificationWindow is { IsVisible: true } window) SessionWindowCoordinator.Close(window);
    }

    private void ResizeCanvasPlane(double width, double height) => ChangeCanvas(canvas =>
    {
        EnsureCanvasReference(canvas);
        if (CanvasProjectionGeometry.TryResize(canvas.Projection, width, height, out var next, out var error)) canvas.Projection = next;
        else UpdateStatus(L.Get(error!));
    });
    private void SetCanvasPlaneAspect(double aspect) => ChangeCanvas(canvas =>
    {
        EnsureCanvasReference(canvas);
        if (CanvasProjectionGeometry.TrySetAspect(canvas.Projection, aspect, out var next, out var error)) canvas.Projection = next;
        else UpdateStatus(L.Get(error!));
    });

    private async Task ShowCanvasRectificationPreviewAsync()
    {
        if (DocumentOperationBusy || !_host.IsEditing || !_host.IsVisible || !CanEditDisplayedCanvas || !CurrentCanvasSettings.Projection.Enabled) return;
        _editor.Cancel(); _canvasCalibration.CancelDrag(); _canvasGridScale.Cancel();
        var target = Active;
        var canvasTarget = CaptureCanvasSettingsTarget();
        var viewing = EditingCanvasView; var viewEpoch = _canvasViewEpoch;
        var origin = _editor.CanvasOrigin;
        var request = new SnapshotCaptureRequest(target, _host.EditingSessionVersion, viewEpoch, origin);
        _canvasRectificationRequest = request;
        var frozen = canvasTarget.CreateDisplayDocument(); var resources = ResourceSnapshot(frozen);
        var presentation = FreezePresentationCapture(target);
        using var windows = FreezeWindowSourceCapture(target);
        bool Current() => !_disposed && !_documentStopped && !_documentExitRequested && _host.IsEditing && _host.IsVisible &&
            ReferenceEquals(_canvasRectificationRequest, request) && request.MatchesContent(Active, _canvasViewEpoch, _editor.CanvasOrigin) &&
            _host.EditingSessionVersion == request.EditingVersion && canvasTarget.IsCurrent;
        _documentBusy = true;
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Input);
            if (!Current()) return;
            var quad = frozen.Canvas.Projection.Quad.Select(viewing.ToView).ToArray();
            var bounds = new RectD(quad.Min(p => p.X), quad.Min(p => p.Y), quad.Max(p => p.X) - quad.Min(p => p.X), quad.Max(p => p.Y) - quad.Min(p => p.Y));
            if (bounds.Width * bounds.Height > 64_000_000) { UpdateStatus(L.Get("Export.BufferTooLarge")); return; }
            var selection = new CaptureSelection(CaptureShape.PolygonLasso, CaptureSource.BackgroundAndCanvas, bounds, quad, 0, 0);
            var desktop = await CaptureCleanDesktopAsync();
            request.AcceptRestoredVersion(_host.EditingSessionVersion);
            if (!Current()) return;
            var displays = _host.Monitors.Select(m => new RectD(m.Bounds.Left - origin.X, m.Bounds.Top - origin.Y, m.Bounds.Width, m.Bounds.Height)).ToArray();
            var animationTime = _renderer.AnimationTime;
            var raster = await Task.Run(() => SnapshotImageCommand.Render(frozen, resources, selection, desktop, displays, animationTime, origin, presentation, windows, viewing), _documentLifetime.Token);
            if (!Current()) return;
            var window = new CanvasRectificationPreviewWindow(_canvasSettingsWindow is { IsVisible: true } ? _canvasSettingsWindow : _toolbar,
                raster, frozen.Canvas.Projection, viewing);
            _canvasRectificationWindow = window;
            ConfigureAppWindow(window); SessionWindowCoordinator.ShowModal(window);
            if (!window.Applied || !Current()) return;
            var next = window.Projection.Clone();
            if (window.InsertScreenshot && !target.CanInsertInEditScope) { UpdateStatus(L.Get("Inspector.LockedSuffix")); return; }
            CanvasElement? image = window.InsertScreenshot ? SnapshotImageCommand.CreateElement(target.Assets, raster,
                CanvasRectificationPreviewWindow.ViewingProjection(next, viewing).Matrix) : null;
            if (image is not null && canvasTarget.Independent) SceneGraph.ApplyPlacement(image, target.GetEditingSubprojectPlacement().Inverse());
            target.Execute(L.Get("Canvas.RectificationPreview"), scene =>
            {
                canvasTarget.Change(settings => settings.Projection = next);
                if (image is not null) new SceneGraph(scene).InsertElement(image, target.EditScopeId, rootCanvas: target.ActiveRootCanvas);
            });
            if (image is not null) target.SetSelection([image.Id]);
            RefreshCanvasSettingsTarget();
            ScheduleRecovery(); RequestRender();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_canvasRectificationRequest, request)) { _canvasRectificationRequest = null; _canvasRectificationWindow = null; }
            _documentBusy = false; PruneDocumentResources(); RequestRender();
        }
    }

    private void BeginCanvasGridCalibration()
    {
        if (DocumentOperationBusy || !_host.IsEditing || !_host.IsVisible || !CanEditDisplayedCanvas || !CurrentCanvasSettings.Projection.Enabled) return;
        _editor.Cancel(); _canvasGridScale.Cancel(); _canvasCalibration.End();
        _canvasGridCalibrationSession = Active; _canvasGridCalibrationRevision = Active.SceneRevision;
        _canvasGridCalibrationTarget = CaptureCanvasSettingsTarget();
        _canvasGridCalibrationEditingVersion = _host.EditingSessionVersion;
        _canvasGridCalibrationPoints.Clear(); _canvasGridCalibrationPreview = null;
        _canvasGridCalibrationDialogPending = false; _canvasDraftRevision++;
        if (_canvasPlacementSession is null)
        {
            _canvasPlacementSession = Active; _canvasPlacementEditingVersion = _host.EditingSessionVersion;
            _canvasPlacementTarget = CaptureCanvasSettingsTarget();
            _canvasPlacementWindows = _app.Windows.Cast<Window>().Where(w => w.IsVisible && !ReferenceEquals(w, _toolbar)).ToArray();
            _windowCoordinator?.HideTemporarily(_canvasPlacementWindows);
        }
        UpdateStatus(L.Get("Canvas.GridCalibrationPickOrigin")); RequestRender();
    }
    private void ClearCanvasGridCalibration()
    {
        if (_canvasGridCalibrationSession is null && _canvasGridCalibrationPreview is null && _canvasGridCalibrationPoints.Count == 0) return;
        _canvasGridCalibrationSession = null; _canvasGridCalibrationTarget = null; _canvasGridCalibrationPoints.Clear(); _canvasGridCalibrationPreview = null;
        _canvasGridCalibrationDialogPending = false; _canvasDraftRevision++;
        var dialog = _canvasGridCalibrationWindow; _canvasGridCalibrationWindow = null;
        if (dialog is { IsVisible: true }) SessionWindowCoordinator.Close(dialog);
    }
    private void CancelCanvasGridCalibration()
    {
        ClearCanvasGridCalibration(); RestoreCanvasPlacementWindows(); SynchronizeCanvasTool(); RequestRender();
    }
    private bool HandleCanvasGridCalibrationPointer(OverlayPointerEvent input)
    {
        if (_canvasGridCalibrationSession is not { } target) return false;
        if (!ReferenceEquals(target, Active) || _canvasGridCalibrationTarget?.IsCurrent != true || target.SceneRevision != _canvasGridCalibrationRevision || _host.EditingSessionVersion != _canvasGridCalibrationEditingVersion ||
            input.Kind == OverlayPointerKind.Cancel || input.Kind == OverlayPointerKind.Down && input.Button == OverlayMouseButton.Right)
        { CancelCanvasGridCalibration(); return true; }
        if (input.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick && input.Button == OverlayMouseButton.Left && _canvasGridCalibrationPoints.Count < 3)
        {
            var point = EditingCanvasView.ToCanvas(new(input.Position.X - _editor.CanvasOrigin.X, input.Position.Y - _editor.CanvasOrigin.Y));
            if (_canvasGridCalibrationPoints.Any(p => p.DistanceTo(point) * EditingCanvasView.Zoom < 8)) return true;
            _canvasGridCalibrationPoints.Add(point);
            UpdateStatus(L.Get(_canvasGridCalibrationPoints.Count == 1 ? "Canvas.GridCalibrationPickX" : "Canvas.GridCalibrationPickY"));
            RequestRender();
        }
        if (input.Kind == OverlayPointerKind.Up && input.Button == OverlayMouseButton.Left && _canvasGridCalibrationPoints.Count == 3 && !_canvasGridCalibrationDialogPending)
        {
            _canvasGridCalibrationDialogPending = true;
            var points = _canvasGridCalibrationPoints.ToArray();
            var canvasTarget = _canvasGridCalibrationTarget;
            _app.Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => FinishCanvasGridCalibration(target, points, canvasTarget));
        }
        return true;
    }
    private void FinishCanvasGridCalibration(EditorSession target, PointD[] points, CanvasSettingsTarget? canvasTarget)
    {
        // A cancelled A callback must not borrow a newer B request or cancel B's active capture.
        if (canvasTarget is null || !ReferenceEquals(_canvasGridCalibrationTarget, canvasTarget)) return;
        if (!ReferenceEquals(_canvasGridCalibrationSession, target) || !ReferenceEquals(Active, target) ||
            canvasTarget?.IsCurrent != true ||
            target.SceneRevision != _canvasGridCalibrationRevision || !_host.IsEditing || !_host.IsVisible ||
            _host.EditingSessionVersion != _canvasGridCalibrationEditingVersion) { CancelCanvasGridCalibration(); return; }
        try
        {
            var dialog = new CanvasGridCalibrationWindow(_toolbar, canvasTarget.Settings, points, settings =>
            {
                if (!ReferenceEquals(_canvasGridCalibrationSession, target) || !ReferenceEquals(_canvasGridCalibrationTarget, canvasTarget) || !canvasTarget.IsCurrent || _host.EditingSessionVersion != _canvasGridCalibrationEditingVersion) return;
                _canvasGridCalibrationPreview = settings; _canvasDraftRevision++; RequestRender();
            });
            _canvasGridCalibrationWindow = dialog;
            ConfigureAppWindow(dialog); SessionWindowCoordinator.ShowModal(dialog);
            if (dialog.Result is { } fitted && ReferenceEquals(_canvasGridCalibrationSession, target) && ReferenceEquals(Active, target) &&
                ReferenceEquals(_canvasGridCalibrationTarget, canvasTarget) && canvasTarget.IsCurrent && target.SceneRevision == _canvasGridCalibrationRevision && _host.IsEditing && _host.IsVisible && _host.EditingSessionVersion == _canvasGridCalibrationEditingVersion)
            {
                ClearCanvasGridCalibration();
                canvasTarget.Replace(fitted, L.Get("Canvas.GridCalibration"));
                ScheduleRecovery();
            }
        }
        finally
        {
            if (_canvasGridCalibrationTarget is null || ReferenceEquals(_canvasGridCalibrationTarget, canvasTarget)) CancelCanvasGridCalibration();
            RefreshCanvasSettingsTarget();
        }
    }
    private void DrawCanvasGridCalibration(SKCanvas canvas)
    {
        using var paint = new SKPaint { Color = new(255, 170, 30), StrokeWidth = 2, IsAntialias = true, Style = SKPaintStyle.Stroke };
        var points = _canvasGridCalibrationPoints.Select(EditingCanvasView.ToView).ToArray();
        foreach (var point in points) canvas.DrawCircle((float)point.X, (float)point.Y, 7, paint);
        for (int i = 1; i < points.Length; i++)
            canvas.DrawLine((float)points[0].X, (float)points[0].Y, (float)points[i].X, (float)points[i].Y, paint);
    }
}
