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
    private bool _canvasControlsWereVisible;
    private readonly CanvasPreviewScene _previewScene = new();
    private CanvasPreviewContext? _currentCanvasContext;
    private long _canvasContextEpoch, _canvasInputSequence, _canvasControlsSequence;
    private long _canvasDraftRevision;
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
        var context = new CanvasPreviewContext(Active, _host.Monitors, _editor.ScreenOrigin,
            !_host.IsVisible, _host.IsEditing && (_editor.Tool == EditorTool.Canvas || _canvasCalibration.IsActive),
            _canvasCalibration.IsPlacing, Active.Document, _host.IsVisible ? null : _hiddenCanvasElements, GuideOpacity: _canvasGuideOpacity);
        if (_currentCanvasContext is { } current && current with { Epoch = 0 } == context) return current;
        long epoch = _currentCanvasContext is { } prior && prior.HasSameContent(context with { Epoch = prior.Epoch })
            ? prior.Epoch : ++_canvasContextEpoch;
        return _currentCanvasContext = context with { Epoch = epoch };
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

    private bool CanvasControlsVisible() => _host.IsReceivingDrop || _host.IsEditing &&
        (_inlineText is not null || _canvasCalibration.IsActive || _editor.Tool == EditorTool.Canvas ||
         _unsafeHoverVisible || _eyedropper is not null || _editor.CapturePreview is not null ||
         _editor.EffectPreviewPolygon is { Count: > 0 } || _editor.Marquee is not null ||
         _editor.VisibleBillboardDepthLine is not null || _editor.BillboardDrawingGuide is not null ||
         _editor.Session.Selection.Count > 0 && _editor.ShouldShowSelection(_host.InteractionModifiers));

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
            _editor.Tool == EditorTool.Canvas || _unsafeHoverVisible || _eyedropper is not null ||
            _editor.CapturePreview is not null || _editor.EffectPreviewPolygon is { Count: > 0 } ||
            _editor.VisibleBillboardDepthLine is not null || _editor.BillboardDrawingGuide is not null ||
            _editor.BillboardSelectionGuide is not null)
            return _host.Monitors.Select(monitor => monitor.Bounds).ToArray();
        var regions = new List<PixelBounds>();
        if (_editor.MarqueeScreenPolygon is { } marquee) Polygon(marquee);
        if (_editor.ShouldShowSelection(_host.InteractionModifiers) && _editor.SelectionScreenCorners is { } corners)
        {
            Polygon(corners);
            foreach (var point in _editor.SelectionScreenEndpoints) Region(point, point);
            if (_editor.SelectionScreenRotationAnchor is { } anchor && _editor.SelectionScreenRotationHandle is { } handle)
                Region(anchor, handle);
        }
        return regions;

        void Polygon(IReadOnlyList<PointD> points)
        { for (int i = 0; i < points.Count; i++) Region(points[i], points[(i + 1) % points.Count]); }
        void Region(PointD first, PointD last)
        {
            int left = (int)Math.Floor(Math.Min(first.X, last.X) + _editor.ScreenOrigin.X - 10);
            int top = (int)Math.Floor(Math.Min(first.Y, last.Y) + _editor.ScreenOrigin.Y - 10);
            int right = (int)Math.Ceiling(Math.Max(first.X, last.X) + _editor.ScreenOrigin.X + 10);
            int bottom = (int)Math.Ceiling(Math.Max(first.Y, last.Y) + _editor.ScreenOrigin.Y + 10);
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
        _presentOperation = null;
        if (_disposed) return;
        var context = CurrentPreviewContext();
        var frame = _canvasPreview.Frame;
        bool current = frame is not null && frame.Context.HasSameContent(context);
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
        _submittingCanvasContext = context;
        _submittingCanvasFrame = null;
        if (frame is not null && frame.Context.HasSameContent(context))
        {
            var pixels = frame.Pixels.Retain();
            try { _submittingCanvasFrame = frame with { Pixels = pixels, PickingImages = frame.PickingImages?.Retain() }; }
            catch { pixels.Dispose(); throw; }
        }
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
        var frame = _submittingCanvasFrame;
        var context = _submittingCanvasContext;
        _submittingCanvasFrame = null; _submittingCanvasContext = null;
        try
        {
            if (_disposed || context is null) return;
            if (!timing.Succeeded || context != CurrentPreviewContext())
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
            if (frame is not null && !context.Hidden && frame.PickingDocument is { } picking)
            {
                if (!reuseFrame)
                {
                    _editor.ApplyPresentedScene(picking, frame.PickingImages, frame.AnimationTime, frame.PickingResources);
                    _canvasPreviewRequests.Presented(frame.Sequence);
                    _presentationUncertain = false;
                    ApplyAnimationSchedule(frame.Animation);
                }
                if (_failedPresentationContext is not null) UpdateStatus(FloatCanvas.Localization.AppText.Get("Rendering.PresentationRecovered"));
                _presentationUncertain = false;
            }
            else if (context.Hidden) { _editor.ClearPresentedScene(); _editor.ResumePresentedPicking(); _presentationUncertain = false; }
            else { _editor.SuspendPresentedPicking(); _presentationUncertain = true; }
            _presentationFailures = 0; _failedPresentationContext = null;
            if (!reuseFrame || _presentedPixels is null)
            { _presentedPixels?.Dispose(); _presentedPixels = frame?.Pixels.Retain(); }
            _presentedCanvasContext = context; _presentedControls = CanvasControlRegions();
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
            long revision = session.PreviewRevision;
            var source = context.Hidden ? HiddenCanvasPreview() : session.Document;
            var range = _editor.RasterPreview;
            IReadOnlySet<Guid>? changed = session.TryGetPreviewChangedElements(_previewScene.CapturedRevision, out var known) ? known : null;
            bool current() => !_disposed && ReferenceEquals(Active, session) && session.PreviewRevision == revision && CurrentPreviewContext() == context;
            if (!await _previewScene.PrepareAsync(source, revision, session.PreviewElementsRevision,
                session.TransactionId, range.First, range.Last, range.BypassCache, changed, session.TransactionPreviewRevision, current)) continue;
            if (context.Hidden) _preparedHiddenPreview = source;
            return CaptureCanvasPreview();
        }
        throw new OperationCanceledException();
    }

    private CanvasPreviewSnapshot CaptureCanvasPreview()
    {
        var context = CurrentPreviewContext();
        var source = context.Hidden ? _preparedHiddenPreview ?? HiddenCanvasPreview() : Active.Document;
        _preparedHiddenPreview = null;
        var preview = _editor.RasterPreview;
        IReadOnlySet<Guid>? changedIds = Active.TryGetPreviewChangedElements(_previewScene.CapturedRevision, out var known) ? known : null;
        var document = _previewScene.Capture(source, Active.PreviewRevision, Active.PreviewElementsRevision,
            Active.TransactionId, preview.First, preview.Last, preview.BypassCache, changedIds, Active.TransactionPreviewRevision);
        var pixelDamage = _previewPixelDamage.ToArray(); _previewPixelDamage.Clear();
        SceneDocument? placement = null; IReadOnlyList<PointD>? clip = null;
        var points = _canvasCalibration.PlacementPreview;
        if (points.Count == 4 && CanvasCalibration.TryOrderCorners(points, out var quad) &&
            CanvasCalibration.TryCalibrate(document.Canvas.Projection, quad, document.Viewport, out var projection, out _))
        {
            placement = new() { Viewport = document.Viewport, Canvas = new CanvasSettings { Projection = projection } };
            clip = quad;
        }
        return new()
        {
            Context = context, SceneIdentity = source, Document = document,
            Resources = _assets.Resources.ToArray(),
            Outputs = _host.Monitors.Select(monitor => monitor.Bounds).ToArray(),
            Revision = Active.SceneRevision, Transaction = Active.TransactionId,
            First = preview.First, Last = preview.Last,
            BypassCache = preview.BypassCache || _canvasCalibration.IsDragging,
            PreferGpu = _settings.PreferGpuAcceleration, AnimationTime = _animationClock.Elapsed,
            Backdrop = _desktopBackdrop?.Retain(), PlacementScene = placement, PlacementClip = clip,
            ContentRevision = Active.PreviewRevision, InputSequence = _canvasInputSequence, PixelDamage = pixelDamage
        };
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
        if (_canvasPreview.Frame?.Context == CurrentPreviewContext())
        {
            ApplyAnimationSchedule(frame.Animation);
            // An already-running animation may complete after a failed publication.
            // It is not a new recovery intent and must not restart the retry budget.
            if (!_presentationUncertain || _failedPresentationContext != frame.Context || _failedPresentationRevision != frame.ContentRevision)
                QueueCanvasPresentation(_presentedCanvasContext == frame.Context
                    ? frame.Pixels.DamageComparedTo(_presentedPixels)
                    : _host.Monitors.Select(monitor => monitor.Bounds));
        }
        PreviewBackendChanged?.Invoke();
    }
}
