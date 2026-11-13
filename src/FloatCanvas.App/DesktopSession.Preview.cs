using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using System.Windows.Threading;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private event Action? PreviewBackendChanged;
    private string? _previewBackendSignature;
    private CanvasPreviewContext? _presentedCanvasContext;
    private (CanvasPreviewContext From, CanvasPreviewContext To)? _sceneExitPresentation;
    private CanvasPreviewFrame? _lastBaseFrame, _cancelledDraftFrame;
    private bool _canvasControlsWereVisible;
    private readonly CanvasPreviewScene _previewScene = new();
    private readonly CanvasPreviewScene _groupEditingScene = new();
    private readonly CanvasPreviewScene _hiddenLiveEffectScene = new();
    private GroupCanvasScene? _groupCanvasScene;
    private GroupCanvasScene? _rootPresentationScene;
    private bool _hasEditableCanvasOccurrence = true;
    private SceneDocument? _hiddenPreviewSource, _hiddenPreviewOwner;
    private object? _hiddenPreviewMembership;
    private long _hiddenPreviewRevision = -1;
    private CanvasPreviewContext? _currentCanvasContext;
    private long _canvasContextEpoch, _canvasInputSequence, _canvasControlsSequence;
    private long _canvasDraftRevision;
    private object? _screenDraftEpoch;
    private bool _presentationPixelsDirty;
    private void InvalidateScreenDraftPreview()
    {
        bool active = _editor.HasScreenBillboardDraft || _inlineText?.HasScreenDraft == true;
        if (active || _screenDraftEpoch is not null) _canvasDraftRevision++;
        if (active && _screenDraftEpoch is null) _screenDraftEpoch = new object();
        else if (!active) _screenDraftEpoch = null;
    }

    internal static bool CanPresentCanvasFrame(CanvasPreviewFrame frame, CanvasPreviewContext context) =>
        frame.Context.HasSameScene(context) &&
        (frame.Context.ScreenDraftEpoch is null || ReferenceEquals(frame.Context.ScreenDraftEpoch, context.ScreenDraftEpoch)) &&
        (frame.Context.PresentationEpoch is null || ReferenceEquals(frame.Context.PresentationEpoch, context.PresentationEpoch)) &&
        (frame.Context.WindowEpoch is null || ReferenceEquals(frame.Context.WindowEpoch, context.WindowEpoch));
    private readonly CanvasPreviewRequestState _canvasPreviewRequests = new();
    private PreviewRasterFrame? _presentedPixels;
    private DispatcherOperation? _presentOperation;
    private readonly List<PixelBounds> _presentationDirty = [], _previewPixelDamage = [];
    private IReadOnlyList<PixelBounds> _presentedControls = [];
    private SceneDocument? _preparedHiddenPreview;
    private CanvasPreviewFrame? _submittingCanvasFrame;
    private CanvasPreviewContext? _submittingCanvasContext, _failedPresentationContext;
    private int _submissionDirtyCount, _presentationFailures;
    private long _failedPresentationRevision;
    private bool _presentationUncertain;
    private DispatcherOperation? _hiddenPresentationRecovery;

    private CanvasPreviewContext CurrentPreviewContext()
    {
        var view = CurrentGroupCanvasView();
        bool foreignSource = view.IsRootPreview && _prefabEditContext is not null;
        bool flatCanvasTool = _editor.Tool == EditorTool.Canvas && !Active.IsReadOnly && !view.Document.Canvas.Projection.Enabled;
        var context = new CanvasPreviewContext(Active, _host.Monitors, view.Origin,
            !_host.IsVisible, !foreignSource && _host.IsEditing && (_canvasCalibrationRequested || _canvasCalibration.IsActive || flatCanvasTool),
            !foreignSource && _canvasCalibration.IsPlacing, Active.Document, _host.IsVisible ? null : _hiddenCanvasElements,
            GuideOpacity: _canvasGuideOpacity, ScreenDraftEpoch: foreignSource ? null : _screenDraftEpoch,
            PresentationEpoch: _presentationRuntime?.ObservingKeyboard == true ? _presentationRuntime.Lifetime : null,
            WindowEpoch: foreignSource ? null : CurrentWindowRenderEpoch(), GroupEpoch: _host.IsEditing ? _canvasViewGroupEpoch : view.Epoch,
            View: EditingCanvasView == CanvasViewTransform.Identity ? null : EditingCanvasView,
            DepthPriorityOcclusion: PreviewAppearance.DepthPriorityOcclusion);
        if (_currentCanvasContext is { } current && current with { Epoch = 0 } == context) return current;
        long epoch = _currentCanvasContext is { } prior && prior.HasSameScene(context with { Epoch = prior.Epoch })
            ? prior.Epoch : ++_canvasContextEpoch;
        return _currentCanvasContext = context with { Epoch = epoch };
    }

    private GroupCanvasView CurrentGroupCanvasView()
    {
        if (!_host.IsEditing)
            return (_rootPresentationScene ??= new()).Capture(Active, _prefabEditContext,
                GroupCanvasMode.RootProject, _editor.ScreenOrigin, _assets);
        var view = (_groupCanvasScene ??= new()).Capture(Active, _prefabEditContext,
            _groupCanvasMode, _editor.ScreenOrigin, _assets, IndependentEditingViewport);
        SynchronizeCanvasViewing(view);
        view = view with { Epoch = _canvasViewEpoch };
        _hasEditableCanvasOccurrence = !view.IsRootPreview || _prefabEditContext is null || view.EditingPlacement is not null;
        var scopeGraph = SceneGraph.Read(Active.Document);
        var scopeContext = scopeGraph.ScopeContainerPlacement(Active.EditScopeId);
        if (view.EditingContainer is { } container) scopeContext = container.Compose(scopeContext);
        _editor.SetCanvasView(view.IsRootPreview && scopeContext.Surface is not null
                ? CanvasMapping.CreateSurfaceView(view.Document, scopeContext, scopeGraph.ScopeWorldPlacement(Active.EditScopeId), EditingCanvasView, view.EditingRootCanvas, view.EditingContainer)
            : view.EditingPlacement is { } placement
                ? CanvasMapping.CreateView(view.Document, placement, view.EditingRootCanvas, EditingCanvasView)
                : CanvasMapping.CreateViewing(view.Document, EditingCanvasView, view.EditingRootCanvas),
            view.Origin, view.Epoch, view.EditingPlacement, view.Document, view.EditingRootCanvas,
            _groupCanvasMode == GroupCanvasMode.Temporary, EditingCanvasView, _canvasViewDuringMove, view.EditingContainer);
        return view;
    }

    private void RequestCanvasPreview(bool pixelsOnly, IReadOnlyList<PixelBounds>? pixelDamage)
    {
        var context = CurrentPreviewContext();
        if (pixelsOnly) _previewPixelDamage.AddRange(pixelDamage ?? _host.Monitors.Select(monitor => monitor.Bounds));
        if (!_canvasPreviewRequests.TryRequest(new(context, Active.PreviewRevision, _canvasDraftRevision, _settings.PreferGpuAcceleration),
            _canvasPreview.RequestedSequence + 1, force: pixelsOnly)) return;
        _canvasPreview.Request();
    }

    private void CanvasPreviewRequestFailed(CanvasPreviewContext? context, long sequence, Exception error)
    {
        _diagnostics.Write("Rendering.PreviewFailed", new { Sequence = sequence }, exception: error);
        if (_disposed || !_canvasPreviewRequests.Fail(sequence, out bool retry)) return;
        UpdateStatus(error.Message);
        if (retry) RequestRender();
    }

    private bool CanvasControlsVisible() => HasCanvasViewingBadge || _hasEditableCanvasOccurrence && !Active.IsReadOnly && _editor.Tool != EditorTool.PointerPresentation && (_host.IsReceivingDrop || _host.IsEditing &&
        (_inlineText is not null || _canvasCalibration.IsActive || _editor.Tool == EditorTool.Canvas ||
         _unsafeHoverVisible || _eyedropper is not null || _editor.CapturePreview is not null || HasPresentationLayerGuides ||
         _editor.EffectPreviewPolygon is { Count: > 0 } || _editor.Marquee is not null ||
         _editor.VisibleBillboardDepthLine is not null || _editor.BillboardDrawingGuide is not null ||
         _editor.Session.Selection.Count > 0 && _editor.ShouldShowSelection(_host.InteractionModifiers)));

    private void PresentCanvasControls(IReadOnlyList<PixelBounds>? dirtyRegions)
    {
        var controls = CanvasControlRegions();
        if (_presentedCanvasContext != CurrentPreviewContext())
            QueueCanvasPresentation(_host.Monitors.Select(monitor => monitor.Bounds));
        else QueueCanvasPresentation(_presentedControls.Concat(controls));
    }

    private IReadOnlyList<PixelBounds> CanvasControlRegions()
    {
        if (!CanvasControlsVisible()) return [];
        // Screen-spanning guides and unknown control effects use a conservative
        // full-output update. Ordinary selection handles remain a small region.
        if (_host.IsReceivingDrop || _inlineText is not null || _canvasCalibration.IsActive ||
            _editor.Tool == EditorTool.Canvas || _unsafeHoverVisible || _eyedropper is not null || HasPresentationLayerGuides ||
            _editor.CapturePreview is not null || _editor.EffectPreviewPolygon is { Count: > 0 } ||
            _editor.VisibleBillboardDepthLine is not null || _editor.BillboardDrawingGuide is not null ||
            _editor.BillboardSelectionGuide is not null)
            return _host.Monitors.Select(monitor => monitor.Bounds).ToArray();
        var regions = new List<PixelBounds>();
        if (HasCanvasViewingBadge) regions.Add(CanvasViewingBadgeBounds);
        if (_editor.MarqueeScreenPolygon is { } marquee) Polygon(marquee);
        if (_editor.ShouldShowSelection(_host.InteractionModifiers) && _editor.SelectionScreenCorners is { } corners)
        {
            Polygon(corners);
            foreach (var point in _editor.SelectionScreenEndpoints) Region(point, point);
            if (EditorController.RotationAnchor(corners) is { } anchor && EditorController.RotationHandle(corners) is { } handle)
                Region(anchor, handle);
        }
        return regions;

        void Polygon(IReadOnlyList<PointD> points)
        { for (int i = 0; i < points.Count; i++) Region(points[i], points[(i + 1) % points.Count]); }
        void Region(PointD first, PointD last)
        {
            int left = (int)Math.Floor(Math.Min(first.X, last.X) + _editor.CanvasOrigin.X - 10);
            int top = (int)Math.Floor(Math.Min(first.Y, last.Y) + _editor.CanvasOrigin.Y - 10);
            int right = (int)Math.Ceiling(Math.Max(first.X, last.X) + _editor.CanvasOrigin.X + 10);
            int bottom = (int)Math.Ceiling(Math.Max(first.Y, last.Y) + _editor.CanvasOrigin.Y + 10);
            regions.Add(new(left, top, right - left, bottom - top));
        }
    }

    private void QueueCanvasPresentation(IEnumerable<PixelBounds> regions)
    {
        if (_disposed) return;
        AddPresentationDamage(regions);
        if (_presentationDirty.Count == 0 || _presentOperation is { Status: DispatcherOperationStatus.Pending }) return;
        _presentOperation = _app.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(PresentCanvas));
    }

    private void PresentCanvas()
    {
        using var sceneRead = SceneGraph.BeginReadScope();
        _presentOperation = null;
        if (_disposed) return;
        var context = CurrentPreviewContext();
        var frame = _canvasPreview.Frame;
        bool current = frame is not null && CanPresentCanvasFrame(frame, context);
        if (_presentationUncertain || _presentedCanvasContext != context) AddPresentationDamage(_host.Monitors.Select(monitor => monitor.Bounds));
        if (current) AddPresentationDamage(frame!.Pixels.DamageComparedTo(_presentedPixels));
        var damage = _presentationDirty.ToArray();
        if (damage.Length == 0) return;
        _host.SetGpuWorkload(false);
        try { if (!_host.TryRenderRegions(damage)) ResumeHiddenCanvasPicking(); }
        catch (Exception error)
        {
            // CompleteCanvasSubmission already retained full damage and scheduled the
            // bounded retry. A native failure must not open a recursive WPF error dialog.
            _diagnostics.Write("Rendering.SubmissionFailed", exception: error);
        }
    }

    private void ResumeHiddenCanvasPicking()
    {
        if (_host.IsVisible || _host.IsPreviewVisible) return;
        // Cancelling a failed new draft can hide the final preview HWND. No visible
        // content remains to reconcile, and the next pen down must be able to start.
        _editor.ClearPresentedScene(); _editor.ResumePresentedPicking(); _presentationUncertain = false;
        if (_failedPresentationContext is not null) UpdateStatus(FloatCanvas.Localization.AppText.Get("Rendering.PresentationRecovered"));
    }

    private void ReconcileHiddenPresentation()
    {
        if (_host.IsVisible || _hiddenPresentationRecovery is { Status: DispatcherOperationStatus.Pending }) return;
        _hiddenPresentationRecovery = _app.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            _hiddenPresentationRecovery = null;
            if (_disposed || _host.IsVisible) return;
            // A failed first draft can reenter UpdateHiddenCanvasState while its
            // outer call still owns that guard. Recompute after the outer call unwinds.
            UpdateHiddenCanvasState(); ResumeHiddenCanvasPicking();
        }));
    }

    private void AddPresentationDamage(IEnumerable<PixelBounds> regions)
    {
        foreach (var region in regions)
        {
            if (region.Width <= 0 || region.Height <= 0) continue;
            _presentationDirty.Add(region);
            if (_presentationDirty.Count <= 128) continue;
            _presentationDirty.Clear(); _presentationDirty.AddRange(_host.Monitors.Select(monitor => monitor.Bounds));
            return;
        }
    }

    private void BeginCanvasSubmission(OverlayFrameRequest request)
    {
        request.FailureHandler = error => _diagnostics.Write("Rendering.SubmissionFailed", exception: error);
        var context = CurrentPreviewContext();
        var frame = _canvasPreview.Frame;
        // An object drag owns its authored transaction and grab mapping. Keep
        // its last pixels during edge navigation rather than applying an older
        // camera's picking snapshot into that live transaction.
        if (_editor.IsMovingCanvasSelection && frame?.Context.View != context.View) frame = null;
        _submittingCanvasContext = context;
        _submittingCanvasFrame = null;
        // Finishing a text transaction can replace the document identity (Escape)
        // or end a screen draft. Keep the last displayed pixels for this exact
        // transition only, until the replacement scene is ready.
        if ((frame is null || !CanPresentCanvasFrame(frame, context)) && _cancelledDraftFrame is { } restored && restored.Context == context) frame = restored;
        bool textExit = _sceneExitPresentation is { } transition && transition.From == _presentedCanvasContext && transition.To == context;
        if ((frame is null || !CanPresentCanvasFrame(frame, context)) && _presentedPixels is not null &&
            (textExit || _presentedCanvasContext is { } shown && ReferenceEquals(shown.Session, context.Session) &&
            ReferenceEquals(shown.DocumentEpoch, context.DocumentEpoch) && ReferenceEquals(shown.Monitors, context.Monitors) &&
            ReferenceEquals(shown.ScreenDraftEpoch, context.ScreenDraftEpoch) && ReferenceEquals(shown.PresentationEpoch, context.PresentationEpoch) &&
            ReferenceEquals(shown.WindowEpoch, context.WindowEpoch) &&
            shown.Origin == context.Origin && shown.Hidden == context.Hidden))
        {
            // Camera changes invalidate the worker frame, not the displayed artwork or
            // the in-flight drag. Never submit an empty intermediate frame.
            request.Deferred = true; _submittingCanvasContext = null;
            RequestCanvasPreview(false, null); return;
        }
        if (frame is not null && CanPresentCanvasFrame(frame, context))
        {
            _submittingCanvasFrame = frame.Retain();
        }
        _sceneExitPresentation = null;
        _submissionDirtyCount = _presentationDirty.Count;
        if (_presentationUncertain || _presentedCanvasContext != context) request.DirtyRegions = null;
        else if (request.DirtyRegions is { } dirty)
        {
            var pixels = _submittingCanvasFrame?.Pixels.DamageComparedTo(_presentedPixels) ?? [];
            request.DirtyRegions = dirty.Concat(_presentationDirty).Concat(pixels).ToArray();
        }
        _host.SetPresentationStamp(_submittingCanvasFrame?.Sequence ?? 0, _submittingCanvasFrame?.InputSequence ?? 0, ++_canvasControlsSequence);
    }

    private void CompleteCanvasSubmission(OverlayFrameTiming timing)
    {
        using var sceneRead = SceneGraph.BeginReadScope();
        var frame = _submittingCanvasFrame;
        var context = _submittingCanvasContext;
        _submittingCanvasFrame = null; _submittingCanvasContext = null;
        try
        {
            if (_disposed || context is null) return;
            var currentContext = CurrentPreviewContext();
            if (timing.Succeeded && _sceneExitPresentation is { } textExit && textExit.From == context && textExit.To == currentContext)
            {
                // A completed text-draft frame may report success after the text
                // transaction has ended. Retain its displayed baseline, but never
                // publish its obsolete picking scene into the finished edit.
                _editor.SuspendPresentedPicking();
                AddPresentationDamage(_host.Monitors.Select(monitor => monitor.Bounds));
                RequestRender(); return;
            }
            if (timing.Succeeded && context != currentContext &&
                (!ReferenceEquals(context.ScreenDraftEpoch, currentContext.ScreenDraftEpoch) ||
                    !ReferenceEquals(context.PresentationEpoch, currentContext.PresentationEpoch) ||
                    !ReferenceEquals(context.WindowEpoch, currentContext.WindowEpoch)))
            {
                // Completion of a successful older private-draft submission can arrive
                // after cancellation or a new gesture. Discard its pixels and baseline;
                // it is not a display failure belonging to the new transaction.
                _presentedPixels?.Dispose(); _presentedPixels = null; _presentedCanvasContext = null;
                AddPresentationDamage(_host.Monitors.Select(monitor => monitor.Bounds));
                RequestRender(); return;
            }
            if (!timing.Succeeded || context != currentContext)
            {
                _presentationUncertain = true;
                _editor.SuspendPresentedPicking();
                ResumeHiddenCanvasPicking();
                ReconcileHiddenPresentation();
                AddPresentationDamage(_host.Monitors.Select(monitor => monitor.Bounds));
                _animationTimer?.Stop();
                long revision = frame?.ContentRevision ?? Active.PreviewRevision;
                if (_failedPresentationContext != context || _failedPresentationRevision != revision)
                { _failedPresentationContext = context; _failedPresentationRevision = revision; _presentationFailures = 0; }
                bool retry = ++_presentationFailures == 1;
                UpdateStatus(FloatCanvas.Localization.AppText.Get(retry ? "Rendering.PresentationRetrying" : "Rendering.PresentationFailed"));
                if (retry) QueueCanvasPresentation(_host.Monitors.Select(monitor => monitor.Bounds));
                return;
            }
            if (timing.Outputs.Count == 0) return;
            _presentationDirty.RemoveRange(0, Math.Min(_submissionDirtyCount, _presentationDirty.Count));
            bool reuseFrame = frame is not null && !context.Hidden && !_presentationUncertain &&
                _presentedCanvasContext == context && _canvasPreviewRequests.PresentedSequence == frame.Sequence;
            if (frame is { IsTransitionFallback: true })
            {
                _editor.SuspendPresentedPicking(); _presentationUncertain = false;
                RequestCanvasPreview(false, null);
            }
            else if (frame is not null && !context.Hidden && frame.Context.View != context.View)
            {
                // Show progress without making a lagging image pickable through
                // the newer mapping. The newest request remains queued.
                _editor.SuspendPresentedPicking(); _presentationUncertain = false;
                _canvasPreviewRequests.Presented(frame.Sequence);
                RequestCanvasPreview(false, null);
            }
            else if (frame is not null && !context.Hidden && frame.PickingDocument is { } picking)
            {
                if (!reuseFrame)
                {
                    _editor.ApplyPresentedScene(picking, frame.PickingImages, frame.AnimationTime, frame.PickingResources, frame.ContentRevision,
                        frame.PickingWindowFrames, frame.WindowOwnerContext, frame.PickingEvaluation);
                    _canvasPreviewRequests.Presented(frame.Sequence);
                    _presentationUncertain = false;
                    ApplyAnimationSchedule(frame.Animation);
                }
                if (_failedPresentationContext is not null) UpdateStatus(FloatCanvas.Localization.AppText.Get("Rendering.PresentationRecovered"));
                _presentationUncertain = false;
            }
            else if (context.Hidden) { _editor.ClearPresentedScene(); _editor.ResumePresentedPicking(); _presentationUncertain = false; }
            else if (_canvasPreview.Frame is { } pendingDraft && pendingDraft.Context.HasSameContent(context) &&
                (pendingDraft.Context.ScreenDraftEpoch is not null && !ReferenceEquals(pendingDraft.Context.ScreenDraftEpoch, context.ScreenDraftEpoch) ||
                    pendingDraft.Context.PresentationEpoch is not null && !ReferenceEquals(pendingDraft.Context.PresentationEpoch, context.PresentationEpoch) ||
                    pendingDraft.Context.WindowEpoch is not null && !ReferenceEquals(pendingDraft.Context.WindowEpoch, context.WindowEpoch)))
            {
                // A cancelled private draft is awaiting a base-only frame. Its picking view
                // was always the real base; this expected transition is not a failed submission.
                RequestCanvasPreview(false, null);
            }
            else { _editor.SuspendPresentedPicking(); _presentationUncertain = true; }
            _presentationFailures = 0; _failedPresentationContext = null;
            if (!reuseFrame || _presentedPixels is null)
            { _presentedPixels?.Dispose(); _presentedPixels = frame?.Pixels.Retain(); }
            _presentedCanvasContext = frame?.Context ?? context; _presentedControls = CanvasControlRegions();
            if (frame is { IsTransitionFallback: false } && !context.Hidden && context.ScreenDraftEpoch is null)
            {
                _lastBaseFrame?.Dispose(); _lastBaseFrame = frame.Retain();
                _cancelledDraftFrame?.Dispose(); _cancelledDraftFrame = null;
            }
            if (context.Hidden) { _lastBaseFrame?.Dispose(); _lastBaseFrame = null; _cancelledDraftFrame?.Dispose(); _cancelledDraftFrame = null; }
            RecordPresentedPresentation(frame, context);
            if (frame is not null) RecordPresentedWindowFrames(frame);
            _canvasControlsWereVisible = _presentedControls.Count > 0;
            _host.NotifyInputProgress();
        }
        finally { frame?.Dispose(); }
    }

    private async ValueTask<CanvasPreviewSnapshot> CaptureCanvasPreviewAsync()
    {
        while (!_disposed)
        {
            var context = CurrentPreviewContext(); var session = Active;
            var view = CurrentGroupCanvasView();
            long revision = session.PreviewRevision;
            var source = PreviewSource(context.Hidden);
            var range = _editor.RasterPreview;
            IReadOnlySet<Guid>? containers = null;
            IReadOnlySet<Guid>? changed = null;
            if (!view.IsProjected && session.TryGetPreviewChanges(_previewScene.CapturedRevision, out var known, out var groups)) { changed = known; containers = groups; }
            bool current() => !_disposed && ReferenceEquals(Active, session) && session.PreviewRevision == revision && CurrentPreviewContext() == context;
            if (!await _previewScene.PrepareAsync(source, revision, session.PreviewElementsRevision,
                session.TransactionId, view.IsProjected ? -1 : range.First, view.IsProjected ? -1 : range.Last,
                !view.IsProjected && range.BypassCache, changed, session.TransactionPreviewRevision, current,
                ReferenceEquals(source, session.Document) ? session.PreviewDefinitionsRevision : null, containers)) continue;
            if (context.Hidden) _preparedHiddenPreview = source;
            return CaptureCanvasPreview();
        }
        throw new OperationCanceledException();
    }

    private CanvasPreviewSnapshot CaptureCanvasPreview()
    {
        var context = CurrentPreviewContext();
        var view = CurrentGroupCanvasView();
        bool foreignSource = view.IsRootPreview && _prefabEditContext is not null;
        var source = context.Hidden ? _preparedHiddenPreview ?? PreviewSource(true) : view.Document;
        _preparedHiddenPreview = null;
        var preview = _editor.RasterPreview;
        int first = view.IsProjected ? -1 : preview.First, last = view.IsProjected ? -1 : preview.Last;
        bool bypass = !view.IsProjected && preview.BypassCache;
        IReadOnlySet<Guid>? containers = null;
        IReadOnlySet<Guid>? changedIds = null;
        if (!view.IsProjected && Active.TryGetPreviewChanges(_previewScene.CapturedRevision, out var known, out var groups)) { changedIds = known; containers = groups; }
        var frozen = _previewScene.Capture(source, Active.PreviewRevision, Active.PreviewElementsRevision,
            Active.TransactionId, first, last, bypass, changedIds, Active.TransactionPreviewRevision,
            ReferenceEquals(source, Active.Document) ? Active.PreviewDefinitionsRevision : null, containers);
        var canvasOverride = !foreignSource && ReferenceEquals(_canvasGridCalibrationSession, Active) ? _canvasGridCalibrationPreview : null;
        var evaluation = _previewScene.EvaluationFor(frozen, PreviewAppearance.Resolve(canvasOverride ?? frozen.Canvas));
        var document = frozen;
        FrozenSceneEvaluation? editingEvaluation = null;
        if (view.EditingPlacement is not null)
        {
            var editing = _groupEditingScene.Capture(Active.Document, Active.PreviewRevision, Active.PreviewElementsRevision,
                Active.TransactionId, -1, -1, true, null, Active.TransactionPreviewRevision, Active.PreviewDefinitionsRevision);
            editingEvaluation = _groupEditingScene.EvaluationFor(editing);
        }
        var pixelDamage = _previewPixelDamage.ToArray(); _previewPixelDamage.Clear();
        SceneDocument? placement = null; IReadOnlyList<PointD>? clip = null;
        var points = _canvasCalibration.PlacementPreview;
        if (!foreignSource && points.Count == 4 && CanvasCalibration.TryOrderCorners(points, out var quad) &&
            CanvasCalibration.TryCalibrate(document.Canvas.Projection, quad, document.Viewport, out var projection, out _))
        {
            placement = new() { Viewport = document.Viewport, Canvas = new CanvasSettings { Projection = projection } };
            clip = quad;
        }
        return new()
        {
            Context = context, SceneIdentity = source, Document = document,
            SourceDocument = view.IsProjected && !view.IsRootPreview ? Active.Document : frozen, FrozenEvaluation = evaluation,
            EditingEvaluation = editingEvaluation, EditingPlacement = view.EditingPlacement, EditingRootCanvas = view.EditingRootCanvas, EditingContainer = view.EditingContainer,
            AuthorRootCanvas = Active.ActiveRootCanvas, EditingScopeId = Active.EditScopeId,
            IndependentCanvas = _groupCanvasMode == GroupCanvasMode.Temporary,
            Resources = _assets.Resources.ToArray(),
            Outputs = _host.Monitors.Select(monitor => monitor.Bounds).ToArray(),
            Revision = Active.SceneRevision, Transaction = Active.TransactionId,
            First = first, Last = last,
            BypassCache = bypass || !foreignSource && _canvasCalibration.IsDragging, GraphChanged = _previewScene.GraphChanged,
            PreferGpu = _settings.PreferGpuAcceleration, AnimationTime = _animationClock.Elapsed,
            Backdrop = _desktopBackdrop?.Retain(), PlacementScene = placement, PlacementClip = clip,
            ContentRevision = Active.PreviewRevision, InputSequence = _canvasInputSequence, PixelDamage = pixelDamage,
            ScreenDraft = foreignSource ? null : CaptureScreenBillboardDraft(view),
            ScreenDraftScopeId = view.IsProjected && !view.IsRootPreview ? null : Active.EditScopeId,
            PresentationFrame = context.PresentationEpoch is not null ? _presentationRuntime?.Frame : null,
            PresentationPlanIdentity = context.PresentationEpoch is not null ? _presentationContext : null,
            PresentationOrigin = view.Origin,
            WindowFrames = context.WindowEpoch is not null ? _windowRuntime?.CaptureFrames() : null,
            WindowOwnerContext = context.WindowEpoch
        };
    }

    private TransientScreenElement? CaptureScreenBillboardDraft(GroupCanvasView view)
    {
        // The editor cancels its private drafts on mode, scope, visibility and session
        // changes. Capturing that state does not acquire or inspect an input lease.
        var raw = _editor.ScreenBillboardDraft ?? _inlineText?.ScreenDraft;
        if (raw is null) return null;
        // Private screen-first ink is already expressed in the viewed output.
        // The worker applies the camera to the whole scene, so cancel it only
        // on this pending screen draft. A resolved world candidate stays authored.
        if (EditingCanvasView != CanvasViewTransform.Identity)
            SceneGraph.ApplyPlacement(raw, new(EditingCanvasView.Matrix.Inverse(), 1 / EditingCanvasView.Zoom));
        var world = _editor.HasScreenBillboardDraft ? _editor.ScreenBillboardCandidate : _inlineText?.ResolvedScreenDraft;
        if (_editor.CurrentCanvasMapping.IsSurfaceView && world is not null)
        {
            var graph = SceneGraph.Read(Active.Document);
            world = graph.ScopeContainerPlacement(Active.EditScopeId).Resolve(graph.ToLocal(world, Active.EditScopeId));
        }
        if (view.EditingPlacement is { } placement)
        {
            world = world?.Clone();
            if (world is not null)
            {
                if (view.EditingContainer is { } container) world = container.ResolveWorld(world);
                else SceneGraph.ApplyPlacement(world, placement);
            }
            return new(raw, 0, world, RootCanvasKind.Main);
        }
        return new(raw, 0, world, Active.ActiveRootCanvas);
    }

    private SceneDocument PreviewSource(bool hidden)
    {
        if (!hidden) return CurrentGroupCanvasView().Document;
        if (!ReferenceEquals(_hiddenPreviewOwner, Active.Document) || _hiddenPreviewRevision != Active.PreviewRevision ||
            !ReferenceEquals(_hiddenPreviewMembership, _hiddenCanvasElements))
        {
            _hiddenPreviewOwner = Active.Document; _hiddenPreviewRevision = Active.PreviewRevision;
            _hiddenPreviewMembership = _hiddenCanvasElements; _hiddenPreviewSource = HiddenCanvasPreview();
        }
        return _hiddenPreviewSource!;
    }

    private SceneDocument CurrentWorldEffectScene()
    {
        var view = CurrentGroupCanvasView(); var source = view.Document;
        if (source.IsRuntimeEvaluated || source.Groups.Count == 0 && source.Instances.Count == 0 && source.RootOrder is null) return source;
        var freezer = _host.IsVisible ? _previewScene : _hiddenLiveEffectScene;
        var range = _editor.RasterPreview;
        IReadOnlySet<Guid>? containers = null;
        IReadOnlySet<Guid>? changed = null;
        if (!view.IsProjected && Active.TryGetPreviewChanges(freezer.CapturedRevision, out var known, out var groups)) { changed = known; containers = groups; }
        var snapshot = freezer.Capture(source, Active.PreviewRevision, Active.PreviewElementsRevision, Active.TransactionId,
            view.IsProjected ? -1 : range.First, view.IsProjected ? -1 : range.Last, !view.IsProjected && range.BypassCache, changed, Active.TransactionPreviewRevision,
            ReferenceEquals(source, Active.Document) ? Active.PreviewDefinitionsRevision : null, containers);
        return freezer.EvaluateFrozen(snapshot).RenderDocument;
    }

    private void PresentCanvasPreview()
    {
        if (_disposed) return;
        var frame = _canvasPreview.Frame!;
        string signature = $"{frame.Backend}|{frame.GpuDetails}|{frame.GpuFailure}";
        if (_previewBackendSignature != signature)
        {
            _previewBackendSignature = signature;
            _diagnostics.Write("Rendering.PreviewBackend", new
                { Backend = frame.Backend.ToString(), frame.GpuDetails, frame.GpuFailure, frame.RenderMilliseconds });
        }
        if (_canvasPreview.Frame is { } currentFrame && CanPresentCanvasFrame(currentFrame, CurrentPreviewContext()))
        {
            ApplyAnimationSchedule(frame.Animation);
            // An already-running animation may complete after a failed publication.
            // It is not a new recovery intent and must not restart the retry budget.
            if (!_presentationUncertain || _failedPresentationContext != frame.Context || _failedPresentationRevision != frame.ContentRevision)
                QueueCanvasPresentation(_presentedCanvasContext == frame.Context
                    ? frame.Pixels.DamageComparedTo(_presentedPixels)
                    : _host.Monitors.Select(monitor => monitor.Bounds));
        }
        else
        {
            // Focus privacy or another view epoch can change while its first
            // frame is rendering. Pixel-only requests are paused until native
            // presentation is certain, so rejecting that old frame must also
            // reserve a frame for the current context. The request key dedupes
            // an already queued replacement.
            RequestCanvasPreview(false, null);
        }
        PreviewBackendChanged?.Invoke();
    }
}
