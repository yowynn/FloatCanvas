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
        if (DocumentOperationBusy || !_host.IsEditing || !_host.IsVisible || !Active.Document.Canvas.Projection.Enabled) return;
        _editor.Cancel(); _canvasCalibration.CancelDrag(); _canvasGridScale.Cancel();
        var target = Active;
        var request = new SnapshotCaptureRequest(target, _host.EditingSessionVersion);
        _canvasRectificationRequest = request;
        var origin = _editor.ScreenOrigin;
        var presentation = FreezePresentationCapture(target);
        using var windows = FreezeWindowSourceCapture(target);
        bool Current() => !_disposed && !_documentStopped && !_documentExitRequested && _host.IsEditing && _host.IsVisible &&
            ReferenceEquals(_canvasRectificationRequest, request) && request.MatchesContent(Active) &&
            _host.EditingSessionVersion == request.EditingVersion && _editor.ScreenOrigin == origin;
        _documentBusy = true;
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Input);
            if (!Current()) return;
            var frozen = target.CreateCommittedSnapshot(); var resources = ResourceSnapshot(frozen);
            var quad = frozen.Canvas.Projection.Quad.ToArray();
            var bounds = new RectD(quad.Min(p => p.X), quad.Min(p => p.Y), quad.Max(p => p.X) - quad.Min(p => p.X), quad.Max(p => p.Y) - quad.Min(p => p.Y));
            if (bounds.Width * bounds.Height > 64_000_000) { UpdateStatus(L.Get("Export.BufferTooLarge")); return; }
            var selection = new CaptureSelection(CaptureShape.PolygonLasso, CaptureSource.BackgroundAndCanvas, bounds, quad, 0, 0);
            var desktop = await CaptureCleanDesktopAsync();
            request.AcceptRestoredVersion(_host.EditingSessionVersion);
            if (!Current()) return;
            var displays = _host.Monitors.Select(m => new RectD(m.Bounds.Left - origin.X, m.Bounds.Top - origin.Y, m.Bounds.Width, m.Bounds.Height)).ToArray();
            var animationTime = _renderer.AnimationTime;
            var raster = await Task.Run(() => SnapshotImageCommand.Render(frozen, resources, selection, desktop, displays, animationTime, origin, presentation, windows), _documentLifetime.Token);
            if (!Current()) return;
            var window = new CanvasRectificationPreviewWindow(_canvasSettingsWindow is { IsVisible: true } ? _canvasSettingsWindow : _toolbar,
                raster, frozen.Canvas.Projection);
            _canvasRectificationWindow = window;
            ConfigureAppWindow(window); SessionWindowCoordinator.ShowModal(window);
            if (!window.Applied || !Current()) return;
            var next = window.Projection.Clone();
            if (window.InsertScreenshot && !target.CanInsertInEditScope) { UpdateStatus(L.Get("Inspector.LockedSuffix")); return; }
            CanvasElement? image = window.InsertScreenshot ? SnapshotImageCommand.CreateElement(target.Assets, raster, next.Matrix) : null;
            target.Execute(L.Get("Canvas.RectificationPreview"), scene =>
            {
                scene.Canvas.Projection = next;
                if (image is not null) new SceneGraph(scene).InsertElement(image, target.EditScopeId);
            });
            if (image is not null) target.SetSelection([image.Id]);
            _canvasSettingsWindow?.Refresh(target.Document.Canvas);
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
        if (DocumentOperationBusy || !_host.IsEditing || !_host.IsVisible || !Active.Document.Canvas.Projection.Enabled) return;
        _editor.Cancel(); _canvasGridScale.Cancel(); _canvasCalibration.End();
        _canvasGridCalibrationSession = Active; _canvasGridCalibrationRevision = Active.SceneRevision;
        _canvasGridCalibrationEditingVersion = _host.EditingSessionVersion;
        _canvasGridCalibrationPoints.Clear(); _canvasGridCalibrationPreview = null;
        _canvasGridCalibrationDialogPending = false; _canvasDraftRevision++;
        if (_canvasPlacementSession is null)
        {
            _canvasPlacementSession = Active; _canvasPlacementEditingVersion = _host.EditingSessionVersion;
            _canvasPlacementWindows = _app.Windows.Cast<Window>().Where(w => w.IsVisible && !ReferenceEquals(w, _toolbar)).ToArray();
            _windowCoordinator?.HideTemporarily(_canvasPlacementWindows);
        }
        UpdateStatus(L.Get("Canvas.GridCalibrationPickOrigin")); RequestRender();
    }
    private void ClearCanvasGridCalibration()
    {
        if (_canvasGridCalibrationSession is null && _canvasGridCalibrationPreview is null && _canvasGridCalibrationPoints.Count == 0) return;
        _canvasGridCalibrationSession = null; _canvasGridCalibrationPoints.Clear(); _canvasGridCalibrationPreview = null;
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
        if (!ReferenceEquals(target, Active) || target.SceneRevision != _canvasGridCalibrationRevision || _host.EditingSessionVersion != _canvasGridCalibrationEditingVersion ||
            input.Kind == OverlayPointerKind.Cancel || input.Kind == OverlayPointerKind.Down && input.Button == OverlayMouseButton.Right)
        { CancelCanvasGridCalibration(); return true; }
        if (input.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick && input.Button == OverlayMouseButton.Left && _canvasGridCalibrationPoints.Count < 3)
        {
            var point = new PointD(input.Position.X - _editor.ScreenOrigin.X, input.Position.Y - _editor.ScreenOrigin.Y);
            if (_canvasGridCalibrationPoints.Any(p => p.DistanceTo(point) < 8)) return true;
            _canvasGridCalibrationPoints.Add(point);
            UpdateStatus(L.Get(_canvasGridCalibrationPoints.Count == 1 ? "Canvas.GridCalibrationPickX" : "Canvas.GridCalibrationPickY"));
            RequestRender();
        }
        if (input.Kind == OverlayPointerKind.Up && input.Button == OverlayMouseButton.Left && _canvasGridCalibrationPoints.Count == 3 && !_canvasGridCalibrationDialogPending)
        {
            _canvasGridCalibrationDialogPending = true;
            var points = _canvasGridCalibrationPoints.ToArray();
            _app.Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => FinishCanvasGridCalibration(target, points));
        }
        return true;
    }
    private void FinishCanvasGridCalibration(EditorSession target, PointD[] points)
    {
        if (!ReferenceEquals(_canvasGridCalibrationSession, target) || !ReferenceEquals(Active, target) ||
            target.SceneRevision != _canvasGridCalibrationRevision || !_host.IsEditing || !_host.IsVisible ||
            _host.EditingSessionVersion != _canvasGridCalibrationEditingVersion) { CancelCanvasGridCalibration(); return; }
        try
        {
            var dialog = new CanvasGridCalibrationWindow(_toolbar, target.Document.Canvas, points, settings =>
            {
                if (!ReferenceEquals(_canvasGridCalibrationSession, target) || _host.EditingSessionVersion != _canvasGridCalibrationEditingVersion) return;
                _canvasGridCalibrationPreview = settings; _canvasDraftRevision++; RequestRender();
            });
            _canvasGridCalibrationWindow = dialog;
            ConfigureAppWindow(dialog); SessionWindowCoordinator.ShowModal(dialog);
            if (dialog.Result is { } fitted && ReferenceEquals(_canvasGridCalibrationSession, target) && ReferenceEquals(Active, target) &&
                target.SceneRevision == _canvasGridCalibrationRevision && _host.IsEditing && _host.IsVisible && _host.EditingSessionVersion == _canvasGridCalibrationEditingVersion)
            {
                ClearCanvasGridCalibration();
                target.ExecuteSettings(L.Get("Canvas.GridCalibration"), scene => scene.Canvas = fitted.Clone());
                ScheduleRecovery();
            }
        }
        finally { CancelCanvasGridCalibration(); _canvasSettingsWindow?.Refresh(Active.Document.Canvas); }
    }
    private void DrawCanvasGridCalibration(SKCanvas canvas)
    {
        using var paint = new SKPaint { Color = new(255, 170, 30), StrokeWidth = 2, IsAntialias = true, Style = SKPaintStyle.Stroke };
        foreach (var point in _canvasGridCalibrationPoints) canvas.DrawCircle((float)point.X, (float)point.Y, 7, paint);
        for (int i = 1; i < _canvasGridCalibrationPoints.Count; i++)
            canvas.DrawLine((float)_canvasGridCalibrationPoints[0].X, (float)_canvasGridCalibrationPoints[0].Y,
                (float)_canvasGridCalibrationPoints[i].X, (float)_canvasGridCalibrationPoints[i].Y, paint);
    }
}
