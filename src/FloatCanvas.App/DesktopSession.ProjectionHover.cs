using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private OverlayPoint? _projectionHoverPoint;
    private long _projectionHoverSession;
    private bool _unsafeHoverVisible;

    private void ObserveProjectionHover(OverlayPointerEvent pointer)
    {
        if (pointer.Kind == OverlayPointerKind.Cancel) { ClearProjectionHover(); return; }
        if (!_host.IsEditing) { ClearProjectionHover(); return; }
        _projectionHoverPoint = pointer.Position;
        _projectionHoverSession = _host.EditingSessionVersion;
        bool visible = ShouldShowProjectionUnsafeHover();
        if (_unsafeHoverVisible == visible) return;
        _unsafeHoverVisible = visible;
        RequestPixelRender();
    }

    private void ClearProjectionHover()
    {
        _projectionHoverPoint = null;
        if (!_unsafeHoverVisible) return;
        _unsafeHoverVisible = false;
        RequestPixelRender();
    }

    private bool ShouldShowProjectionUnsafeHover()
    {
        if (!_host.IsEditing || !_host.IsVisible || _canvasCalibration.IsActive || DocumentOperationBusy ||
            _cleanCaptureDepth != 0 || _host.IsReceivingDrop || _eyedropper is not null || _projectionHoverPoint is not { } pointer ||
            _projectionHoverSession != _host.EditingSessionVersion) return false;
        var display = CurrentGroupCanvasView();
        var point = new PointD(pointer.X - _editor.CanvasOrigin.X, pointer.Y - _editor.CanvasOrigin.Y);
        return _editor.CurrentCanvasMapping.IsPerspective && display.Document.Viewport.Contains(point) && !_editor.CurrentCanvasMapping.IsScreenSafe(point);
    }

    private void DrawProjectionHover(SKCanvas canvas)
    {
        _unsafeHoverVisible = ShouldShowProjectionUnsafeHover();
        if (_unsafeHoverVisible) SceneRenderer.DrawProjectionUnsafeRegion(canvas, _editor.CurrentCanvasMapping, CurrentGroupCanvasView().Document.Viewport);
    }
}
