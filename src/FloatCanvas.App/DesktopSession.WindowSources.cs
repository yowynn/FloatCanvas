using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private sealed record WindowOwnerLife(Guid ResourceId, object Token);
    private readonly Dictionary<EditorSession, WindowOwnerLife> _windowOwners = [];
    private WindowSourceRuntime? _windowRuntime;
    private EditorSession? _windowPlanSession;
    private SceneDocument? _windowPlanDocument;
    private long _windowPlanVersion = -1, _windowPlanScope;
    private PointD _windowPlanOrigin;
    private object? _windowPlanMonitors;
    private object _windowPlanIdentity = new();
    private EvaluatedScene? _windowEvaluation;
    private IReadOnlyList<ResolvedLeaf> _windowSourceLeaves = [];
    private WindowRuntimeDemand[] _windowDemands = [];
    private bool _updatingWindowSources, _windowPixelsDirty;
    private WindowFrameSet? _presentedWindowFrames;
    private object? _presentedWindowOwnerContext;

    partial void InitializeWindowSourceCommands();
    partial void ScheduleWindowMediaCheckpoint();
    partial void DisposeWindowMediaCheckpoint();

    private void InitializeWindowSources()
    {
        _windowRuntime = new(_app.Dispatcher, OnWindowRuntimeChanged);
        _windowRuntime.MediaFrameAccepted += () => ScheduleWindowMediaCheckpoint();
        _windowSourcesUiProvider = GetWindowSourcesUiState;
        _setWindowSourcesPausedCommand = SetSelectedWindowSourcesPaused;
        InitializeWindowSourceCommands();
        _editor.WindowSourceCropRequested += id => { if (_cropWindowSourceCommand is { } command) _ = RunCommand(() => command(id)); };
        UpdateWindowSourceRuntime();
    }
    private object WindowOwnerToken(EditorSession owner)
    {
        if (_windowOwners.TryGetValue(owner, out var current) && current.ResourceId == owner.Document.ResourceId) return current.Token;
        var life = new WindowOwnerLife(owner.Document.ResourceId, new());
        _windowOwners[owner] = life;
        if (current is not null) _windowRuntime?.Forget(current.Token);
        return life.Token;
    }
    private void ForgetWindowSourceBindings(EditorSession owner)
    {
        if (_windowRuntime is null) return;
        bool previous = _updatingWindowSources; _updatingWindowSources = true;
        try
        {
            if (_windowOwners.Remove(owner, out var life)) _windowRuntime?.Forget(life.Token);
            if (ReferenceEquals(_windowPlanSession, owner)) { _windowPlanVersion = -1; _windowPlanDocument = null; }
        }
        finally { _updatingWindowSources = previous; }
    }
    private void ForgetAllWindowSourceBindings()
    {
        if (_windowRuntime is null) return;
        _windowSourceHandoffs.Clear();
        foreach (var owner in _windowOwners.Keys.ToArray()) ForgetWindowSourceBindings(owner);
    }
    private bool TryRetainWindowSourceFrame(EditorSession owner, SourceNodeAddress address, Guid intent, out WindowFrameLease? frame)
    {
        frame = null;
        return _windowRuntime?.TryRetainFrame(WindowOwnerToken(owner), owner.Document.ResourceId, address, intent, out frame) == true;
    }
    private void OnWindowRuntimeChanged(bool pixels, bool status)
    {
        if (_disposed) return;
        if (pixels) { _windowPixelsDirty = true; RequestPixelRender(Array.Empty<PixelBounds>()); }
        if (status && !_updatingWindowSources) RefreshWindowSourcesUi();
    }
    private void UpdateWindowSourceRuntime()
    {
        if (_disposed || _windowRuntime is null || _updatingWindowSources) return;
        _updatingWindowSources = true;
        try
        {
            var owner = Active; var document = owner.Document;
            bool newPlan = !ReferenceEquals(_windowPlanSession, owner) || !ReferenceEquals(_windowPlanDocument, document) ||
                _windowPlanVersion != owner.ContentVersion || _windowPlanScope != owner.EditScopeVersion ||
                _windowPlanOrigin != _editor.ScreenOrigin || !ReferenceEquals(_windowPlanMonitors, _host.Monitors);
            if (newPlan)
            {
                _windowPlanSession = owner; _windowPlanDocument = document; _windowPlanVersion = owner.ContentVersion;
                _windowPlanScope = owner.EditScopeVersion; _windowPlanOrigin = _editor.ScreenOrigin; _windowPlanMonitors = _host.Monitors;
                _windowPlanIdentity = new(); _windowEvaluation = null; _windowSourceLeaves = []; _windowDemands = [];
                if (document.Elements.Any(element => element.WindowSource is not null) || document.Definitions.Any(definition => definition.Graph.Elements.Any(element => element.WindowSource is not null)))
                {
                    _windowEvaluation = SceneEvaluation.Build(document);
                    _windowSourceLeaves = _windowEvaluation.Leaves.Where(leaf => leaf.RenderElement.WindowSource is not null).ToArray();
                    var mapping = CanvasMapping.Create(document);
                    var viewports = _host.Monitors.Select(monitor => new RectD(monitor.Bounds.Left - _editor.ScreenOrigin.X,
                        monitor.Bounds.Top - _editor.ScreenOrigin.Y, monitor.Bounds.Width, monitor.Bounds.Height).Intersect(document.Viewport)).ToArray();
                    if (document.LayerOpacity > 0)
                        _windowDemands = _windowSourceLeaves.Where(leaf => leaf.RenderElement.Visible && leaf.RenderElement.Style.Opacity > 0 &&
                                viewports.Any(viewport => WindowRegionVisible(leaf.RenderElement, mapping, viewport)))
                            .Select(leaf => new WindowRuntimeDemand(leaf.Source, leaf.RenderElement.WindowSource!.BindingIntent)).Distinct().ToArray();
                }
            }
            bool allowed = _host.IsVisible && !DocumentOperationBusy && _cleanCaptureDepth == 0 && !HasPrefabEditContext &&
                PresentationDesktopAvailability.IsCurrentInputDesktop();
            _windowRuntime.Configure(WindowOwnerToken(owner), document.ResourceId, _windowDemands, allowed, _windowPlanIdentity);
        }
        finally { _updatingWindowSources = false; }
    }
    private static bool WindowRegionVisible(CanvasElement source, CanvasMapping mapping, RectD viewport)
    {
        // This transient rectangle reuses Core's finite-plane/billboard clipping;
        // it is never inserted in a document, serialized or given a source identity.
        var rectangle = new CanvasElement
        {
            Kind = ElementKind.Presentation, Width = source.Width, Height = source.Height, Transform = source.Transform,
            ProjectivePlacement = source.ProjectivePlacement, IsBillboard = source.IsBillboard,
            BillboardAnchor = source.BillboardAnchor, BillboardHeight = source.BillboardHeight
        };
        return PresentationGeometry.TryScreenRegion(rectangle, mapping, viewport, out _);
    }
    private object? CurrentWindowRenderEpoch() => _windowSourceLeaves.Count > 0 ? _windowRuntime?.RenderEpoch : null;
    private IReadOnlyList<ResolvedLeaf> SelectedWindowSources(IReadOnlyList<Guid> ids) => _windowEvaluation is null ? [] :
        ids.SelectMany(id => _windowEvaluation.OccurrencesOfHostNode(id)).Where(leaf => leaf.RenderElement.WindowSource is not null)
            .DistinctBy(leaf => leaf.Source).ToArray();

    private WindowSourcesUiState GetWindowSourcesUiState(IReadOnlyList<Guid> ids)
    {
        UpdateWindowSourceRuntime(); var leaves = SelectedWindowSources(ids);
        bool writable = !_disposed && !DocumentOperationBusy && !Active.IsTransactionActive && Active.CanInsertInEditScope;
        var graph = new SceneGraph(Active.Document);
        var real = ids.Select(graph.FindElement).Where(element => element?.WindowSource is not null).Cast<CanvasElement>().ToArray();
        bool editable = writable && ids.Count == 1 && real is [var single] && !graph.IsLocked(single.Id);
        var states = leaves.Select(leaf =>
        {
            var state = _windowRuntime?.State(WindowOwnerToken(Active), leaf.RenderElement.WindowSource!.BindingIntent);
            return state is null ? null : state with { Demanded = state.Demanded && _windowRuntime!.IsSourceDemanded(leaf.Source, leaf.RenderElement.WindowSource!.BindingIntent) };
        }).OfType<WindowRuntimeState>().ToArray();
        bool sourceLimit = leaves.Count == 1 && states is [var limited] && limited.Connected && !limited.Demanded &&
            _windowDemands.Contains(new(leaves[0].Source, leaves[0].RenderElement.WindowSource!.BindingIntent));
        string status = "WindowSource.Status.Disconnected";
        if (states is [var one])
            status = !one.Connected ? status : one.Status == WindowCaptureStatus.Closed ? "WindowSource.Status.SourceClosed" :
                one.Status == WindowCaptureStatus.Unavailable || one.BudgetLimited ? "WindowSource.Status.Unavailable" :
                sourceLimit ? "WindowSource.Status.SourceLimit" : one.UserPaused ? "WindowSource.Status.PausedByUser" :
                !one.Demanded || !one.Allowed ? "WindowSource.Status.SuspendedHidden" : one.Status switch
                {
                    WindowCaptureStatus.Live => "WindowSource.Status.Running", WindowCaptureStatus.Starting => "WindowSource.Status.Starting",
                    WindowCaptureStatus.Minimized => "WindowSource.Status.SuspendedMinimized", WindowCaptureStatus.Resizing => "WindowSource.Status.Resizing",
                    WindowCaptureStatus.Hidden => "WindowSource.Status.SourceHidden",
                    WindowCaptureStatus.Closed => "WindowSource.Status.SourceClosed", WindowCaptureStatus.Unavailable => "WindowSource.Status.Unavailable",
                    _ => "WindowSource.Status.SuspendedHidden"
                };
        bool canFreeze = writable && real.Length > 0 && real.All(element => !graph.IsLocked(element.Id) && HasWindowPixels(element));
        return new(leaves.Count, leaves.Count == 1 ? leaves[0].RenderElement.WindowSource!.TitleHint : null, status,
            CanAdd: writable, CanPause: !DocumentOperationBusy && states.Any(state => state.Connected && !state.UserPaused && state.Status is not (WindowCaptureStatus.Closed or WindowCaptureStatus.Stopped)),
            CanResume: !DocumentOperationBusy && states.Any(state => state.Connected && (state.UserPaused || state.Status == WindowCaptureStatus.Unavailable) && state.Status is not (WindowCaptureStatus.Closed or WindowCaptureStatus.Stopped)),
            CanRebind: editable, CanCrop: editable && HasWindowPixels(real[0]), CanFreeze: canFreeze,
            EditableImageId: editable ? real[0].Id : null,
            OccurrenceCount: leaves.Sum(leaf => _windowEvaluation!.OccurrencesOf(leaf.Source).Count), StatusRevision: _windowRuntime?.Revision ?? 0);
    }
    private bool HasWindowPixels(CanvasElement element)
    {
        if (element.AssetId is not null) return true;
        if (element.WindowSource is not { } source || !TryRetainWindowSourceFrame(Active, new(null, element.Id), source.BindingIntent, out var frame)) return false;
        frame!.Dispose(); return true;
    }
    private void SetSelectedWindowSourcesPaused(IReadOnlyList<Guid> ids, bool paused)
    {
        UpdateWindowSourceRuntime();
        _windowRuntime?.SetPaused(WindowOwnerToken(Active), SelectedWindowSources(ids).Select(leaf => leaf.RenderElement.WindowSource!.BindingIntent), paused);
        RefreshWindowSourcesUi();
    }
    private void RecordPresentedWindowFrames(CanvasPreviewFrame frame)
    {
        var next = frame.PickingWindowFrames?.Retain();
        var old = _presentedWindowFrames; _presentedWindowFrames = next; _presentedWindowOwnerContext = frame.WindowOwnerContext; old?.Dispose();
    }
    private void DisposeWindowSources()
    {
        _pendingWindowInsertion?.Source.Dispose(); _pendingWindowInsertion = null;
        DisposeWindowMediaCheckpoint(); _windowRuntime?.Dispose(); _windowRuntime = null;
        _presentedWindowFrames?.Dispose(); _presentedWindowFrames = null; _windowOwners.Clear();
        _windowSourceHandoffs.Clear();
    }
}
