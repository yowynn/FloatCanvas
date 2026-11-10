using FloatCanvas.Core;
using FloatCanvas.Storage;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private bool CanApplyCanvasViewing => _host.IsEditing && _host.IsVisible && !DocumentOperationBusy &&
        !Active.IsReadOnly && Active.CanInsertInEditScope && !Active.IsTransactionActive && !_editor.HasPointerGesture &&
        _inlineText is null && !_canvasCalibration.IsDragging && !_canvasCalibration.IsPlacing && _canvasGridCalibrationSession is null &&
        (_groupCanvasMode == GroupCanvasMode.Temporary || _prefabEditContext is null) &&
        EditingCanvasView != CanvasViewTransform.Identity;

    private void ApplyCanvasViewing()
    {
        _ = CurrentGroupCanvasView();
        if (!CanApplyCanvasViewing) return;
        var session = Active; var source = session.Document;
        long revision = session.PreviewRevision, scopeVersion = session.EditScopeVersion;
        var mode = _groupCanvasMode; var context = _prefabEditContext; var view = _canvasView;
        string key = _canvasViewKey!;
        try
        {
            bool subproject = mode == GroupCanvasMode.Temporary && (session.EditScopeId is not null || context is not null);
            var candidate = CanvasViewApplication.Prepare(source, view,
                subproject ? session.ActiveRootCanvas : null,
                subproject ? session.EditScopeId : null);
            if (context is not null && mode == GroupCanvasMode.Temporary && session.EditScopeId is null && session.ActiveRootCanvas == RootCanvasKind.Main)
                SceneValidation.ValidateIndependentCanvas(candidate);
            else SceneValidation.Validate(candidate);
            // The candidate is complete before any data or local camera is changed.
            if (!ReferenceEquals(Active, session) || !ReferenceEquals(session.Document, source) || session.PreviewRevision != revision ||
                session.EditScopeVersion != scopeVersion || _groupCanvasMode != mode || !ReferenceEquals(_prefabEditContext, context) ||
                _canvasView != view || CanvasViewingKey() != key || !CanApplyCanvasViewing) return;
            var history = new CanvasViewHistoryChange(new(key, view.ToCanvas(_canvasViewViewport.Center), view.Zoom, view.PlaneOffset),
                new(key, _canvasViewViewport.Center, 1));
            session.ApplyCanvasView(L.Get("Canvas.ApplyView"), candidate, history);
            RefreshCanvasSettingsTarget(); RequestRender(); ScheduleRecovery();
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.IO.InvalidDataException or OverflowException)
        { UpdateStatus(L.Format("Canvas.ApplyViewFailed", error.Message)); }
    }

    private void OnCanvasViewHistoryRestored(CanvasViewHistoryState state)
    {
        if (_disposed || _canvasViews is null) return;
        string key = CanvasHistoryKey(state.CanvasKey);
        // Restoring another scope's data must not replace the current scope's camera.
        var restored = CanvasViewTransform.Restore(state.ObservationCenter, state.Zoom, _canvasViewViewport) with { PlaneOffset = state.PlaneOffset };
        _canvasViews.Remember(key, restored, _canvasViewViewport);
        if (key == CanvasViewingKey())
        {
            CancelCanvasNavigationGesture();
            _canvasView = restored; _canvasViewEpoch = new();
            RefreshCanvasNavigationButton();
            RememberCanvasViewing();
        }
        else _ = SaveCanvasViewingAsync();
    }
}
