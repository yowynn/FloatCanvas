using FloatCanvas.Core;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private CanvasSettingsTarget? _canvasWindowTarget, _calibrationTarget, _canvasGridCalibrationTarget, _canvasPlacementTarget;
    private RectD IndependentEditingViewport => _host.Monitors.Select(monitor => new RectD(
        monitor.Bounds.Left - _editor.ScreenOrigin.X, monitor.Bounds.Top - _editor.ScreenOrigin.Y,
        monitor.Bounds.Width, monitor.Bounds.Height)).Aggregate((left, right) => RectD.Union(left, right));
    private CanvasSettingsTarget CaptureCanvasSettingsTarget()
    {
        var owner = Active; var mode = _groupCanvasMode; var prefab = _prefabEditContext;
        var viewport = IndependentEditingViewport;
        return new(owner, mode == GroupCanvasMode.Temporary,
            () => ReferenceEquals(Active, owner) && _groupCanvasMode == mode && ReferenceEquals(_prefabEditContext, prefab) &&
                (mode != GroupCanvasMode.Temporary || IndependentEditingViewport == viewport),
            mode == GroupCanvasMode.Temporary ? viewport : null);
    }
    private CanvasSettings CurrentCanvasSettings => !CanEditDisplayedCanvas && _prefabEditContext is { } context
        ? context.Chain().Last().Owner.Document.Canvas : CaptureCanvasSettingsTarget().Settings;
    private RectD CurrentCanvasViewport => !CanEditDisplayedCanvas && _prefabEditContext is { } context
        ? context.Chain().Last().Owner.Document.Viewport : CaptureCanvasSettingsTarget().Viewport;
    private double CurrentCanvasLayerOpacity => !CanEditDisplayedCanvas && _prefabEditContext is { } context
        ? context.Chain().Last().Owner.Document.LayerOpacity : CaptureCanvasSettingsTarget().LayerOpacity;
    private bool CanEditDisplayedCanvas => !HasPrefabEditContext || _groupCanvasMode == GroupCanvasMode.Temporary;
    private bool IsCanvasWindowTargetCurrent => _canvasWindowTarget?.IsCurrent == true && CanEditDisplayedCanvas;

    private void RefreshCanvasSettingsTarget()
    {
        if (_canvasSettingsWindow is null) return;
        _canvasWindowTarget = CaptureCanvasSettingsTarget();
        _canvasSettingsWindow.Refresh(_canvasWindowTarget.Settings);
    }
    private void ChangeCurrentCanvasOpacity(double opacity)
    {
        if (!CanEditDisplayedCanvas) return;
        CaptureCanvasSettingsTarget().ChangeOpacity(opacity);
    }
}
