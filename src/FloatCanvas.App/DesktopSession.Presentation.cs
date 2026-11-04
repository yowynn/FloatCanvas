using FloatCanvas.Core;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using System.IO;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private PresentationRuntimeBroker? _presentationRuntime;
    private object _presentationContext = new();
    private EditorSession? _presentationOwner;
    private SceneDocument? _presentationDocument;
    private SceneDocument? _presentationDisplayDocument;
    private object? _presentationViewEpoch;
    private CanvasViewTransform _presentationViewing;
    private long _presentationVersion = -1;
    private IReadOnlyList<ResolvedLeaf> _presentationLeaves = [];
    private EvaluatedScene? _presentationEvaluation;
    private bool _presentationPlanBuilt;
    private PointD _presentationPlanOrigin;
    private object? _presentationPlanMonitors;
    private long _presentationPlanScope;
    private Guid _presentationResource;
    private PresentationFrame? _presentedPresentationFrame;
    private object? _presentedPresentationLifetime;
    private PresentationRuntimeUiState? _presentationUiState;
    private object? _presentationInputContext;
    private Func<bool>? _presentationDesktopAvailable = PresentationDesktopAvailability.IsCurrentInputDesktop;
    private long _presentedPresentationRevision = -1;
    private object? _presentedPresentationPlan;
    private PresentationPauseSnapshot? _presentationPause;
    private bool _presentationInputSubscribed;
    private sealed record PresentationPauseSnapshot(EditorSession Owner, SceneDocument Document, long ContentVersion,
        long ScopeVersion, PointD Origin, object Monitors, object Lifetime, PresentationFrame Frame,
        EvaluatedScene Evaluation, IReadOnlyList<ResolvedLeaf> Leaves);

    private PresentationRuntimeBroker PresentationRuntime
    {
        get
        {
            if (_presentationRuntime is not null) return _presentationRuntime;
            _presentationRuntime = new(_app.Dispatcher, OnPresentationRuntimeChanged, isInternalInput: IsPresentationInputInternal);
            _presentationRuntime.BeforeInput += batch => { UpdatePresentationRuntime(); ObservePresentationKeyboardPrivacy(batch); };
            return _presentationRuntime;
        }
    }
    partial void ObservePresentationKeyboardPrivacy(PresentationObservedBatch batch);

    private PresentationRuntimeUiState GetPresentationRuntimeUiState(Guid? hostId = null) => new(
        _presentationRuntime?.Armed == true, _presentationRuntime?.Observing == true, false,
        _presentationRuntime?.StatusKey ?? "Presentation.Status.NoLayers");

    private void InitializePresentation()
    {
        if (!_presentationInputSubscribed)
        {
            _host.PresentationInputObserved += OnPresentationEditingInput;
            _presentationInputSubscribed = true;
        }
        PresentationRuntime.SetArmed(true);
        UpdatePresentationRuntime();
    }

    private void TogglePresentationRuntime()
    {
        UpdatePresentationRuntime(); var runtime = PresentationRuntime;
        if (runtime.Armed)
        {
            PausePresentationRuntime(retainTrail: true); var retained = _presentationPause;
            runtime.Toggle();
            if (retained is not null) _presentationPause = retained with { Lifetime = runtime.Lifetime };
        }
        else runtime.Toggle();
        UpdatePresentationRuntime(); RefreshPresentationRuntimeUi();
    }
    private void BakePresentationTrail(Guid? hostId = null)
    {
        if (!TryGetPresentationBakeSource(hostId, out var paused, out var source) ||
            !PresentationTrailBake.TryPrepare(source!.RenderElement, CanvasMapping.Create(Active.Document, source.RootCanvas), Active.Document.Viewport,
                paused!.Frame, paused.Origin, out var candidate, out _) || candidate is null)
        { UpdateStatus(L.Get("Presentation.Bake.Unavailable")); return; }
        var strokes = candidate.CloneWorldStrokes(); var preview = Active.Document.Clone(); var graph = new SceneGraph(preview);
        try
        {
            foreach (var stroke in strokes) graph.InsertElement(stroke, Active.EditScopeId, rootCanvas: Active.ActiveRootCanvas);
            SceneValidation.Validate(preview);
            if (!TryGetPresentationBakeSource(hostId, out var current, out _) || !ReferenceEquals(current, paused)) return;
            Active.Execute(L.Get("History.AddElement"), document =>
            {
                var target = new SceneGraph(document); Active.Selection.Clear();
                foreach (var stroke in strokes) { target.InsertElement(stroke, Active.EditScopeId, rootCanvas: Active.ActiveRootCanvas); Active.Selection.Add(stroke.Id); }
            });
            _presentationPause = null; _presentationRuntime?.ClearPausedTrail();
            ScheduleRecovery(); RequestRender(); RefreshPresentationRuntimeUi();
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or ArgumentException)
        { UpdateStatus(L.Get("Presentation.Bake.Unavailable")); }
    }
    private void OnPresentationRuntimeChanged()
    {
        if (_disposed) return;
        UpdatePresentationInputRoute();
        RefreshPresentationPrivacy();
        if (_presentationPause is { } paused && !ReferenceEquals(paused.Lifetime, _presentationRuntime?.Lifetime)) _presentationPause = null;
        UpdatePresentationPixels();
        RefreshPresentationRuntimeUi();
    }
    private void RefreshPresentationRuntimeUi()
    {
        var state = GetPresentationRuntimeUiState(); if (state == _presentationUiState) return;
        _presentationUiState = state;
        _toolOptionsPanel?.RefreshPresentationRuntimeUi(); _inspector?.RefreshPresentationRuntimeUi();
    }
    private void ConfigurePresentationUi(ToolOptionsPanel panel)
    {
        panel.SetPresentationRuntimeUi(GetPresentationRuntimeUiState);
        panel.SetPresentationGlobalUi(() => _settings.PresentationGlobal.Clone());
    }
    private void ConfigurePresentationUi(InspectorWindow panel)
    {
        panel.SetPresentationRuntimeUi(GetPresentationRuntimeUiState);
    }
    private void PausePresentationRuntime(bool retainTrail)
    {
        if (_presentationRuntime is not { } runtime) return;
        var capture = CapturePresentationBeforePause(retainTrail, runtime);
        PresentationPauseSnapshot? captured = null;
        if (retainTrail && _presentedPresentationFrame is { Trail.Count: > 1 } frame &&
            ReferenceEquals(_presentedPresentationLifetime, runtime.Lifetime) && _presentationEvaluation is { } evaluation &&
            ReferenceEquals(_presentationOwner, Active) && ReferenceEquals(_presentationDocument, Active.Document))
            captured = new(Active, Active.Document, Active.ContentVersion, Active.EditScopeVersion, _editor.ScreenOrigin,
                _host.Monitors, runtime.Lifetime, frame, evaluation, _presentationLeaves);
        var previous = _presentationPause;
        runtime.Pause(retainTrail);
        if (captured is not null) _presentationPause = captured with { Lifetime = runtime.Lifetime };
        else if (retainTrail && previous is not null && PauseSnapshotIsCurrent(previous, checkLifetime: false))
            _presentationPause = previous with { Lifetime = runtime.Lifetime };
        CompletePresentationCapturePause(capture, runtime);
        RefreshPresentationRuntimeUi();
    }
    private void RecordPresentedPresentation(CanvasPreviewFrame? frame, CanvasPreviewContext context)
    {
        if (frame is null) return;
        _presentedPresentationFrame = frame.PresentationFrameSnapshot;
        _presentedPresentationLifetime = context.PresentationEpoch;
        _presentedPresentationRevision = frame.ContentRevision;
        _presentedPresentationPlan = frame.PresentationPlanIdentity;
        if (frame.PresentationBudgetExceeded) _presentationRuntime?.BlockForBudget();
    }
    private bool PauseSnapshotIsCurrent(PresentationPauseSnapshot paused, bool checkLifetime = true) =>
        ReferenceEquals(paused.Owner, Active) && ReferenceEquals(paused.Document, Active.Document) &&
        paused.ContentVersion == Active.ContentVersion && paused.ScopeVersion == Active.EditScopeVersion &&
        paused.Origin == _editor.ScreenOrigin && ReferenceEquals(paused.Monitors, _host.Monitors) &&
        (!checkLifetime || ReferenceEquals(paused.Lifetime, _presentationRuntime?.Lifetime));

    private bool TryGetPresentationBakeSource(Guid? hostId, out PresentationPauseSnapshot? snapshot, out ResolvedLeaf? source)
    {
        snapshot = _presentationPause; source = null;
        if (snapshot is null || !PauseSnapshotIsCurrent(snapshot) || !_host.IsEditing || DocumentOperationBusy ||
            Active.IsTransactionActive || !Active.CanInsertInEditScope || HasPrefabEditContext) return false;
        var eligible = snapshot.Leaves.Where(leaf => !leaf.RenderElement.Locked && leaf.RenderElement.Presentation is { } settings &&
            (settings.Modules & PresentationModules.Trail) != 0).ToArray();
        var selected = hostId is { } explicitNode ? new[] { explicitNode } : Active.Selection.ToArray();
        if (selected.Length > 0)
        {
            var evaluation = snapshot.Evaluation;
            var ids = selected.SelectMany(id => evaluation.OccurrencesOfHostNode(id)).Select(leaf => leaf.RuntimeElementId).ToHashSet();
            eligible = eligible.Where(leaf => ids.Contains(leaf.RuntimeElementId)).ToArray();
        }
        if (eligible is not [var one]) return false;
        source = one; return true;
    }
    private void UpdatePresentationRuntime()
    {
        if (_disposed || _presentationRuntime is null || _changingPrefabContext) return;
        var view = CurrentGroupCanvasView();
        var display = view.Document;
        bool rebuilt = false;
        if (!ReferenceEquals(_presentationOwner, Active) || !ReferenceEquals(_presentationDocument, Active.Document) || _presentationVersion != Active.ContentVersion)
        {
            _presentationOwner = Active; _presentationDocument = Active.Document; _presentationVersion = Active.ContentVersion;
            _presentationContext = new(); _presentationPlanBuilt = false; _presentationLeaves = []; _presentationEvaluation = null;
        }
        if (_presentationPlanScope != Active.EditScopeVersion || _presentationPlanOrigin != _editor.ScreenOrigin ||
            !ReferenceEquals(_presentationPlanMonitors, _host.Monitors))
        {
            _presentationPlanScope = Active.EditScopeVersion; _presentationPlanOrigin = _editor.ScreenOrigin;
            _presentationPlanMonitors = _host.Monitors; _presentationContext = new(); _presentationPlanBuilt = false;
        }
        if (_presentationResource != Active.Document.ResourceId)
        {
            _presentationResource = Active.Document.ResourceId;
        }
        var camera = EditingCanvasView;
        if (!ReferenceEquals(_presentationDisplayDocument, display) || !ReferenceEquals(_presentationViewEpoch, view.Epoch) || _presentationViewing != camera)
        {
            if (!ReferenceEquals(_presentationDisplayDocument, display)) _presentationEvaluation = null;
            _presentationDisplayDocument = display; _presentationViewEpoch = view.Epoch;
            _presentationViewing = camera;
            _presentationContext = new(); _presentationPlanBuilt = false;
        }
        if (!_presentationPlanBuilt)
        {
            _presentationEvaluation ??= SceneEvaluation.Build(display);
            var viewports = _host.Monitors.Select(monitor => new RectD(monitor.Bounds.Left - view.Origin.X,
                monitor.Bounds.Top - view.Origin.Y, monitor.Bounds.Width, monitor.Bounds.Height).Intersect(display.Viewport)).ToArray();
            _presentationLeaves = _presentationEvaluation.Leaves.Where(leaf =>
                leaf.RenderElement is { Kind: ElementKind.Presentation, Visible: true, Style.Opacity: > 0, Presentation: not null } &&
                viewports.Any(viewport => PresentationGeometry.TryScreenRegion(leaf.RenderElement, CanvasMapping.CreateViewing(display, camera, leaf.RootCanvas), viewport, out _))).ToArray();
            _presentationPlanBuilt = true;
            rebuilt = true;
        }
        bool keyboard = false, all = false, scoped = false;
        double hold = 100;
        if (_host.IsVisible && display.LayerOpacity > 0)
            foreach (var leaf in _presentationLeaves)
            {
                var settings = leaf.RenderElement.Presentation!;
                // A visible keyboard layer records for its lifetime. Obsolete
                // module/enable wire values remain readable but cannot pause it.
                keyboard = true;
                scoped |= settings.KeyboardScope != PresentationKeyboardScope.AllApplications;
                all |= settings.DisplayStyle == PresentationDisplayStyle.Keyboard || settings.KeyDisplay == PresentationKeyDisplayMode.AllPhysicalKeys;
                hold = Math.Max(hold, settings.KeyHoldMilliseconds);
            }
        var global = _settings.PresentationGlobal;
        var pointer = global.Pointer;
        bool clicks = pointer.AnyEnabled && pointer.ClicksEnabled;
        bool trail = pointer.AnyEnabled && pointer.TrailEnabled;
        double ripple = new[] { pointer.LeftClick, pointer.RightClick, pointer.MiddleClick }
            .Where(style => style.Enabled).Select(style => style.DurationMilliseconds).DefaultIfEmpty(100).Max();
        bool allowed = (_host.IsVisible || pointer.AnyEnabled) && !DocumentOperationBusy && _cleanCaptureDepth == 0 &&
            (_presentationDesktopAvailable?.Invoke() ?? PresentationDesktopAvailability.IsCurrentInputDesktop());
        string paused = !_host.IsVisible ? "Presentation.Status.PausedHidden" : "Presentation.Status.Unavailable";
        _presentationRuntime.Configure(new(keyboard, pointer.AnyEnabled, clicks, trail, all, hold, ripple, pointer.TrailMilliseconds) { ScopedKeyboard = scoped },
            allowed, _presentationInputContext ??= new(), paused);
        if (rebuilt) _presentationRuntime.RetryIfFaulted();
        UpdatePresentationInputRoute();
    }
    private void UpdatePresentationInputRoute()
    {
        if (_presentationRuntime is not { } runtime || _disposed) return;
        bool editing = runtime.Observing && _host.HasEditingInput && _host.IsKeyboardIsolationActive;
        runtime.ConfigureEditingInput(editing);
        _host.ConfigurePresentationInputTap(editing && runtime.ObservingKeyboard,
            editing && _settings.PresentationGlobal.Pointer.AnyEnabled, runtime.EditingInputEpoch);
    }
    private void OnPresentationEditingInput(PresentationObservedBatch batch)
    {
        if (!_disposed) _presentationRuntime?.ObserveEditingInput(batch);
    }
}
