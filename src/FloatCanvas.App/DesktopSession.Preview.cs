using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private event Action? PreviewBackendChanged;
    private string? _previewBackendSignature;
    private CanvasPreviewContext? _presentedCanvasContext;
    private bool _canvasControlsWereVisible;
    private CanvasPreviewContext CurrentPreviewContext() => new(Active, _host.Monitors, _editor.ScreenOrigin,
        !_host.IsVisible, _host.IsEditing && (_editor.Tool == EditorTool.Canvas || _canvasCalibration.IsActive),
        _canvasCalibration.IsPlacing, Active.Document, _host.IsVisible ? null : _hiddenCanvasElements);

    private bool CanvasControlsVisible() => _host.IsReceivingDrop || _host.IsEditing &&
        (_inlineText is not null || _canvasCalibration.IsActive || _editor.Tool == EditorTool.Canvas ||
         _unsafeHoverVisible || _eyedropper is not null || _editor.CapturePreview is not null ||
         _editor.EffectPreviewPolygon is { Count: > 0 } || _editor.Marquee is not null ||
         _editor.VisibleBillboardDepthLine is not null || _editor.BillboardDrawingGuide is not null ||
         _editor.Session.Selection.Count > 0 && _editor.ShouldShowSelection(_host.InteractionModifiers));

    private void PresentCanvasControls(IReadOnlyList<PixelBounds>? dirtyRegions)
    {
        // Guides stay responsive while raster work is busy. Ordinary strokes and
        // animation have no UI overlay to update: present only when new pixels arrive.
        // Clear removed guides or an obsolete canvas context once, without waiting.
        if (CanvasControlsVisible() || _canvasControlsWereVisible || _presentedCanvasContext != CurrentPreviewContext())
            _host.Render(dirtyRegions);
    }

    private CanvasPreviewSnapshot CaptureCanvasPreview()
    {
        var context = CurrentPreviewContext();
        var source = context.Hidden ? HiddenCanvasPreview() : Active.Document;
        var document = source.Clone();
        var preview = _editor.RasterPreview;
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
            Resources = document.ReferencedAssets.Select(_assets.Get).ToArray(),
            Outputs = _host.Monitors.Select(monitor => monitor.Bounds).ToArray(),
            Revision = Active.SceneRevision, Transaction = Active.TransactionId,
            First = preview.First, Last = preview.Last,
            BypassCache = preview.BypassCache || _canvasCalibration.IsDragging,
            PreferGpu = _settings.PreferGpuAcceleration, AnimationTime = _animationClock.Elapsed,
            Backdrop = _desktopBackdrop?.Retain(), PlacementScene = placement, PlacementClip = clip
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
            _host.SetGpuWorkload(false);
            _host.Render();
            _host.NotifyInputProgress();
        }
        PreviewBackendChanged?.Invoke();
    }
}
