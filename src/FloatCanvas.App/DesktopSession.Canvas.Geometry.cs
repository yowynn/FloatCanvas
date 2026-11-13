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
            var window = new CanvasRectificationPreviewWindow(IsCanvasSettingsOpen ? _toolOptionsWindow : _toolbar,
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

}
