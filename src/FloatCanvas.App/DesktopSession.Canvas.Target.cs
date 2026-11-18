using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

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
        bool independent = mode == GroupCanvasMode.Temporary && (owner.EditScopeId is not null || prefab is not null);
        return new(owner, independent,
            () => ReferenceEquals(Active, owner) && _groupCanvasMode == mode && ReferenceEquals(_prefabEditContext, prefab) &&
                (mode != GroupCanvasMode.Temporary || IndependentEditingViewport == viewport),
            independent ? viewport : null);
    }
    private CanvasSettings CurrentCanvasSettings => !CanEditDisplayedCanvas && _prefabEditContext is { } context
        ? context.Chain().Last().Owner.Document.Canvas : CaptureCanvasSettingsTarget().Settings;
    private RectD NewBillboardImageBounds()
    {
        var target = CaptureCanvasSettingsTarget();
        return BillboardImagePlacement.LogicalBounds(target.Settings, target.Viewport);
    }
    private RectD CurrentCanvasViewport => !CanEditDisplayedCanvas && _prefabEditContext is { } context
        ? context.Chain().Last().Owner.Document.Viewport : CaptureCanvasSettingsTarget().Viewport;
    private double CurrentCanvasLayerOpacity => !CanEditDisplayedCanvas && _prefabEditContext is { } context
        ? context.Chain().Last().Owner.Document.LayerOpacity : CaptureCanvasSettingsTarget().LayerOpacity;
    private bool CanEditDisplayedCanvas => !HasPrefabEditContext || _groupCanvasMode == GroupCanvasMode.Temporary;
    private bool CanChangeCanvasOpacity => CanEditDisplayedCanvas && Active.CanInsertInEditScope && !Active.IsReadOnly && !DocumentOperationBusy;
    private bool IsCanvasWindowTargetCurrent => _canvasWindowTarget?.IsCurrent == true && CanEditDisplayedCanvas && !Active.IsReadOnly && Active.CanInsertInEditScope;

    private void RefreshCanvasSettingsTarget()
    {
        if (_canvasToolOptions is null) return;
        if (_canvasWindowTarget?.IsCurrent == false) _canvasToolOptions.CancelEditing();
        _canvasWindowTarget = CaptureCanvasSettingsTarget();
        _canvasToolOptions.SetFlatGridReference(_canvasWindowTarget.Viewport.Center);
        _canvasToolOptions.Refresh(_canvasWindowTarget.Settings);
        _canvasToolOptions.RefreshPreviewControls(PreviewAppearance.DepthPriorityOcclusion, _canvasGuideOpacity,
            !DocumentOperationBusy && !Active.IsTransactionActive);
        var scope = DescribeEditingScope();
        string target = _canvasWindowTarget.Independent ? L.Format("EditingScope.IndependentSettings", scope.Path) : L.Get("EditingScope.MainSettings");
        _canvasToolOptions.TargetLabel = _canvasWindowTarget.Independent ? scope.Path + " · " + L.Get("GroupCanvas.Temporary") : L.Get("RootCanvas.Main");
        if (_editor.Tool == EditorTool.Canvas) _canvasToolOptions.NotifyHeaderChanged();
        _canvasToolOptions.SetTargetDescription(target + "\n" + L.Get(_canvasWindowTarget.Independent ? "GroupCanvas.TemporaryNotice" : "EditingScope.DocumentSettingsHint"));
        _canvasToolOptions.SetAuthoringAllowed(!Active.IsReadOnly && Active.CanInsertInEditScope);
        RefreshCanvasNavigationButton();
    }
    private void ChangeCurrentCanvasOpacity(double opacity)
    {
        if (!CanChangeCanvasOpacity) return;
        CaptureCanvasSettingsTarget().ChangeOpacity(opacity);
    }
}
