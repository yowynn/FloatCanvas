using FloatCanvas.Core;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using System.Windows.Threading;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private CanvasViewStateStore? _canvasViews;
    private string? _canvasViewKey;
    private RectD _canvasViewViewport;
    private CanvasViewTransform _canvasView = CanvasViewTransform.Identity;
    private object _canvasViewEpoch = new();
    private object? _canvasViewGroupEpoch;
    private PointD? _canvasViewPanLast;
    private DispatcherTimer? _canvasViewSaveTimer;
    private bool _canvasViewDuringMove;
    private OverlayMouseButton _canvasNavigationMouseButton;
    private bool _canvasNavigationZoom;
    private PointD _canvasNavigationAnchor;
    private bool _canvasNavigationSpaceHeld;
    private EditorSession? _canvasViewHistorySession;
    private CanvasViewTransform EditingCanvasView => _host.IsEditing && _host.IsVisible ? _canvasView : CanvasViewTransform.Identity;
    private void CancelCanvasNavigationGesture()
    { _canvasViewPanLast = null; StopCanvasEdgePan(); }

    private void SynchronizeCanvasViewing(GroupCanvasView display)
    {
        if (!ReferenceEquals(_canvasViewHistorySession, Active))
        {
            if (_canvasViewHistorySession is not null) _canvasViewHistorySession.CanvasViewRestored -= OnCanvasViewHistoryRestored;
            _canvasViewHistorySession = Active;
            Active.CanvasViewRestored += OnCanvasViewHistoryRestored;
        }
        // Pass-through presentation does not need to read or write local editor
        // preferences. Initialize the camera only when editing actually begins.
        if (!_host.IsEditing && _canvasViews is null)
        { _canvasViewEpoch = display.Epoch; _canvasViewGroupEpoch = display.Epoch; return; }
        _canvasViews ??= new(ApplicationPaths.ProfileDirectory);
        string key = CanvasViewingKey();
        var primary = _host.PrimaryMonitor.Bounds;
        var viewport = new RectD(primary.Left - display.Origin.X, primary.Top - display.Origin.Y, primary.Width, primary.Height);
        if (_canvasViewKey != key)
        {
            StopCanvasEdgePan();
            RememberCanvasViewing();
            _canvasViewKey = key; _canvasViewViewport = viewport; _canvasViewPanLast = null;
            if (!_canvasViews.TryRestore(key, viewport, out _canvasView))
                _canvasView = CanvasViewTransform.Identity;
            _canvasViewEpoch = new();
            RememberCanvasViewing();
        }
        else if (_canvasViewViewport != viewport)
        {
            // Moving the desktop/window origin is not a navigation gesture. In
            // particular a newly replaced root must retain its formal identity.
            if ((_canvasViewViewport.Width != viewport.Width || _canvasViewViewport.Height != viewport.Height) &&
                (_canvasView != CanvasViewTransform.Identity || _groupCanvasMode == GroupCanvasMode.Temporary))
                _canvasView = CanvasViewTransform.Restore(_canvasView.ToCanvas(_canvasViewViewport.Center), _canvasView.Zoom, viewport) with { PlaneOffset = _canvasView.PlaneOffset };
            _canvasViewViewport = viewport; _canvasViewPanLast = null; _canvasViewEpoch = new();
            RememberCanvasViewing();
        }
        if (!ReferenceEquals(_canvasViewGroupEpoch, display.Epoch))
        { StopCanvasEdgePan(); _canvasViewGroupEpoch = display.Epoch; _canvasViewEpoch = new(); _canvasViewPanLast = null; }
        RefreshCanvasNavigationButton();
    }

    private string CanvasViewingKey()
    {
        if (_groupCanvasMode == GroupCanvasMode.RootProject)
        {
            // Project preview has one camera owned by the displayed root project,
            // including while editing a nested group or shared draft.
            var owner = _prefabEditContext?.Chain().Last().Owner ?? Active;
            return $"{CanvasIdentityFor(owner).Namespace}/{owner.ActiveRootCanvas}//{GroupCanvasMode.RootProject}";
        }
        return $"{CanvasEditingNamespace()}/{Active.ActiveRootCanvas}/{Active.EditScopeId}/{(Active.EditScopeId is null && _prefabEditContext is null ? GroupCanvasMode.RootProject : _groupCanvasMode)}";
    }

    private RectD CanvasViewingReference(GroupCanvasView display) => _groupCanvasMode == GroupCanvasMode.RootProject
        ? display.Document.AuthoringReference?.Frame ?? display.Document.Viewport : Active.EditScopeId is null
            ? Active.Document.AuthoringReference?.Frame ?? Active.Document.Viewport : Active.GetEditingSubprojectCanvas().Viewport;

    private static RectD CanvasViewingBounds(SceneDocument document, RectD? emptyReference = null)
    {
        var scene = SceneEvaluation.Build(document).RenderDocument;
        var elements = scene.LayerOpacity > 0 ? scene.Elements : [];
        var ink = elements.Where(element => element.Visible && element.Style.Opacity > 0)
            .Select(element => CanvasMapping.Create(scene, SceneCanvasRoots.Resolve(scene, element.Id)).ProjectedBounds(element));
        if (scene.Background is { Visible: true, Style.Opacity: > 0 } background)
            ink = ink.Append(CanvasMapping.Create(scene).ProjectedBounds(background));
        var bounds = ink
            .Where(bounds => !bounds.IsEmpty && double.IsFinite(bounds.X + bounds.Y + bounds.Width + bounds.Height)).ToArray();
        return bounds.Length > 0 ? bounds.Aggregate(RectD.Union) : emptyReference ?? document.Viewport;
    }

    private void RememberCanvasViewing()
    {
        if (_canvasViews is null || _canvasViewKey is null) return;
        _canvasViews.Remember(_canvasViewKey, _canvasView, _canvasViewViewport);
        _canvasViewSaveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
            (_, _) => { _canvasViewSaveTimer!.Stop(); _ = SaveCanvasViewingAsync(); }, Dispatcher.CurrentDispatcher);
        _canvasViewSaveTimer.Stop(); _canvasViewSaveTimer.Start();
    }

    private async Task SaveCanvasViewingAsync()
    {
        if (_canvasViews is null) return;
        try { await _canvasViews.SaveAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        { _diagnostics.Write("Canvas.ViewSaveFailed", exception: error); }
    }

    private void ChangeCanvasViewing(CanvasViewTransform view, bool duringMove = false)
    {
        if (_canvasView == view) return;
        _canvasView = view; _canvasViewEpoch = new();
        RefreshCanvasNavigationButton();
        RememberCanvasViewing();
        _canvasViewDuringMove = duringMove;
        try { _ = CurrentGroupCanvasView(); }
        finally { _canvasViewDuringMove = false; }
        RequestRender();
    }

    private void ExecuteCanvasViewing(CanvasViewingCommand command)
    {
        if (!_host.IsEditing || DocumentOperationBusy || _editor.HasPointerGesture || Active.IsTransactionActive ||
            _canvasCalibration.IsDragging || _canvasCalibration.IsPlacing || _canvasGridCalibrationSession is not null) return;
        var display = CurrentGroupCanvasView();
        if (command == CanvasViewingCommand.FitContent && CanvasViewingBounds(display.Document,default(RectD)).IsEmpty) return;
        var view = _canvasView;
        ChangeCanvasViewing(command switch
        {
            CanvasViewingCommand.OriginalSize => view.ZoomAt(1, _canvasViewViewport.Center),
            CanvasViewingCommand.CenterContent => (view with { PlaneOffset = default }).Center(CanvasViewingBounds(display.Document, CanvasViewingReference(display)).Center, _canvasViewViewport),
            CanvasViewingCommand.FitContent => CanvasViewTransform.Fit(CanvasViewingBounds(display.Document, CanvasViewingReference(display)), _canvasViewViewport, allowEnlarge: true),
            CanvasViewingCommand.FullCanvas => CanvasViewTransform.Fit(CanvasViewingReference(display), _canvasViewViewport, allowEnlarge: true),
            CanvasViewingCommand.Reset => CanvasViewTransform.Identity,
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        });
    }

    private bool HandleCanvasViewingPointer(OverlayPointerEvent input)
    {
        if (_editor.InteractionTool == EditorTool.PointerPresentation && !_canvasNavigationSpaceHeld)
        { _canvasViewPanLast = null; return false; }
        if (input.Kind == OverlayPointerKind.Cancel)
        { bool panning = _canvasViewPanLast is not null; _canvasViewPanLast = null; return panning; }
        if (!_host.IsEditing || !_host.IsVisible || DocumentOperationBusy)
        { _canvasViewPanLast = null; return false; }
        var point = new PointD(input.Position.X - _editor.CanvasOrigin.X, input.Position.Y - _editor.CanvasOrigin.Y);
        if (_canvasViewPanLast is { } previous)
        {
            if (input.Kind == OverlayPointerKind.Move)
            {
                _canvasViewPanLast = point;
                ChangeCanvasViewing(_canvasNavigationZoom
                    ? _canvasView.ZoomAt(_canvasView.Zoom * Math.Exp((previous.Y - point.Y) * .01), _canvasNavigationAnchor)
                    : _editor.CurrentCanvasMapping.IsPerspective ? _canvasView.PanPlane(point - previous) : _canvasView.Pan(point - previous));
            }
            else if (input.Kind == OverlayPointerKind.Up && input.Button == _canvasNavigationMouseButton) _canvasViewPanLast = null;
            return true;
        }
        bool wheel = input.Kind == OverlayPointerKind.Wheel;
        bool down = (input.Button == OverlayMouseButton.Middle || _canvasNavigationSpaceHeld && input.Button == OverlayMouseButton.Left) &&
            input.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick;
        if ((input.Modifiers & (OverlayModifiers.Alt | OverlayModifiers.Windows)) != 0) return false;
        if (!wheel && !down) return false;
        if (_editor.HasPointerGesture || Active.IsTransactionActive || _inlineText is not null ||
            _canvasCalibration.IsDragging || _canvasCalibration.IsPlacing || _canvasGridCalibrationSession is not null) return true;
        if (wheel)
        {
            bool zoom = input.Modifiers == OverlayModifiers.Control;
            if (zoom) ChangeCanvasViewing(_canvasView.ZoomAt(_canvasView.Zoom * Math.Pow(1.1, input.WheelDelta / 120d), point));

        }
        else
        {
            if (input.Modifiers != OverlayModifiers.None && !(input.Button == OverlayMouseButton.Middle && input.Modifiers == OverlayModifiers.Control)) return true;
            using var destination = _windowCoordinator?.DirectTransfer(WindowDismissReason.CanvasPress);
            DismissPanelsForCanvasPress();
            _canvasViewPanLast = point; _canvasNavigationAnchor = point;
            _canvasNavigationMouseButton = input.Button;
            _canvasNavigationZoom = input.Button == OverlayMouseButton.Middle && input.Modifiers == OverlayModifiers.Control;
        }
        _propertiesClicks?.Reset(); return true;
    }
}
