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
    private CanvasPreviewContext? _currentCanvasContext, _requestedCanvasContext;
    private long _canvasContextEpoch, _requestedContentRevision = -1, _canvasInputSequence, _canvasControlsSequence;
    private long _canvasDraftRevision, _requestedDraftRevision = -1;
    private bool? _requestedPreferGpu;
    private PreviewRasterFrame? _presentedPixels;
    private DispatcherOperation? _presentOperation;
    private readonly List<PixelBounds> _presentationDirty = [], _previewPixelDamage = [];
    private IReadOnlyList<PixelBounds> _presentedControls = [];
    private SceneDocument? _preparedHiddenPreview;

    private CanvasPreviewContext CurrentPreviewContext()
    {
        var context = new CanvasPreviewContext(Active, _host.Monitors, _editor.ScreenOrigin,
            !_host.IsVisible, _host.IsEditing && (_editor.Tool == EditorTool.Canvas || _canvasCalibration.IsActive),
            _canvasCalibration.IsPlacing, Active.Document, _host.IsVisible ? null : _hiddenCanvasElements);
        if (_currentCanvasContext is { } current && current with { Epoch = 0 } == context) return current;
        return _currentCanvasContext = context with { Epoch = ++_canvasContextEpoch };
    }

    private void RequestCanvasPreview(bool pixelsOnly, IReadOnlyList<PixelBounds>? pixelDamage)
    {
        var context = CurrentPreviewContext();
        if (pixelsOnly) _previewPixelDamage.AddRange(pixelDamage ?? _host.Monitors.Select(monitor => monitor.Bounds));
        if (!pixelsOnly && _requestedCanvasContext == context && _requestedContentRevision == Active.PreviewRevision &&
            _requestedDraftRevision == _canvasDraftRevision && _requestedPreferGpu == _settings.PreferGpuAcceleration) return;
        _requestedCanvasContext = context; _requestedContentRevision = Active.PreviewRevision;
        _requestedDraftRevision = _canvasDraftRevision; _requestedPreferGpu = _settings.PreferGpuAcceleration;
        _canvasPreview.Request();
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
        _presentationDirty.AddRange(regions.Where(region => region.Width > 0 && region.Height > 0));
        if (_presentationDirty.Count == 0 || _presentOperation is { Status: DispatcherOperationStatus.Pending }) return;
        _presentOperation = _app.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(PresentCanvas));
    }

    private void PresentCanvas()
    {
        _presentOperation = null;
        if (_disposed) return;
        var context = CurrentPreviewContext();
        var frame = _canvasPreview.Frame;
        bool current = frame is not null && frame.Context == context;
        if (_presentedCanvasContext != context) _presentationDirty.AddRange(_host.Monitors.Select(monitor => monitor.Bounds));
        if (current) _presentationDirty.AddRange(frame!.Pixels.DamageComparedTo(_presentedPixels));
        var damage = _presentationDirty.ToArray(); _presentationDirty.Clear();
        if (damage.Length == 0) return;
        _host.SetGpuWorkload(false);
        _host.SetPresentationStamp(current ? frame!.Sequence : 0, current ? frame!.InputSequence : 0, ++_canvasControlsSequence);
        _host.RenderRegions(damage);
        _presentedPixels?.Dispose(); _presentedPixels = current ? frame!.Pixels.Retain() : null;
        _presentedCanvasContext = context; _presentedControls = CanvasControlRegions();
        _canvasControlsWereVisible = _presentedControls.Count > 0;
        _host.NotifyInputProgress();
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
            QueueCanvasPresentation(_presentedCanvasContext == frame.Context
                ? frame.Pixels.DamageComparedTo(_presentedPixels)
                : _host.Monitors.Select(monitor => monitor.Bounds));
        }
        PreviewBackendChanged?.Invoke();
    }
}
