using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private sealed class LiveEffectPreparation(EffectRenderRequest request, EditorTool tool, RectD bounds)
    {
        internal EffectRenderRequest Request { get; } = request;
        internal EditorTool Tool { get; } = tool;
        internal RectD Bounds { get; } = bounds;
        internal TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private LiveEffectPreparation? _liveEffectPreparation;
    private EditorSession? _liveBackdropSession;
    private RectD _liveBackdropViewport;
    private PointD _liveBackdropOrigin;
    private MonitorInfo[] _liveBackdropMonitors = [];
    private LiveDesktopCapture? _liveBackdropSource;
    private long _liveBackdropTargetVersion;

    private bool IsLiveBackdropCompatible() => _desktopBackdrop is not null &&
        ReferenceEquals(_liveBackdropSession, Active) && _liveBackdropViewport == Active.Document.Viewport && _liveBackdropOrigin == _editor.ScreenOrigin &&
        _liveBackdropMonitors.SequenceEqual(_host.Monitors);

    private bool IsLivePreparationCurrent(LiveEffectPreparation pending) =>
        pending.Tool == _editor.Tool && CanFinishEffect(pending.Request);

    private void ClearLiveBackdrop()
    {
        _renderer.LiveBackdrop = null;
        _desktopBackdrop?.Dispose(); _desktopBackdrop = null;
        _liveBackdropSession = null; _liveBackdropMonitors = [];
        _liveBackdropSource = null; _liveBackdropTargetVersion = 0;
    }

    private void RememberLiveBackdrop(LiveDesktopCapture source, long targetVersion)
    {
        // Undo/redo replace SceneDocument snapshots inside the same logical canvas.
        // Raw desktop pixels do not depend on those annotation snapshots.
        _liveBackdropSession = Active;
        _liveBackdropViewport = Active.Document.Viewport;
        _liveBackdropOrigin = _editor.ScreenOrigin;
        _liveBackdropMonitors = _host.Monitors.ToArray();
        _liveBackdropSource = source; _liveBackdropTargetVersion = targetVersion;
    }

    private bool CurrentLiveBackdropCovers(RectD bounds)
    {
        if (!IsLiveBackdropCompatible() || _liveDesktopCapture is not { } source || source.Completion.IsCompleted ||
            !ReferenceEquals(source, _liveBackdropSource) || _liveBackdropTargetVersion != _liveTargetVersion ||
            source.TargetVersion != _liveTargetVersion || source.LastError is not null) return false;
        var required = _host.Monitors.Select(m => bounds.Intersect(new(m.Bounds.Left - _editor.ScreenOrigin.X, m.Bounds.Top - _editor.ScreenOrigin.Y, m.Bounds.Width, m.Bounds.Height)))
            .Where(region => !region.IsEmpty).ToArray();
        return required.Length > 0 && required.All(region => _desktopBackdrop!.Tiles.Any(tile =>
            tile.Bounds.Left <= region.Left && tile.Bounds.Top <= region.Top &&
            tile.Bounds.Right >= region.Right && tile.Bounds.Bottom >= region.Bottom));
    }

    private void CancelLivePreparation()
    {
        var pending = _liveEffectPreparation; _liveEffectPreparation = null;
        pending?.Ready.TrySetResult(false);
    }

    private async Task<bool> PrepareLiveEffectAsync(EffectRenderRequest request, CanvasElement effect)
    {
        CancelLivePreparation();
        var pending = new LiveEffectPreparation(request, _editor.Tool,
            SceneRenderer.LiveEffectSamplingBounds(request.Document,effect).Intersect(request.Document.Viewport));
        // An unchanged ROI on a static desktop may produce no new notification. Its
        // already displayed, current-source pixels are sufficient for another filter.
        if (CurrentLiveBackdropCovers(pending.Bounds) && IsLivePreparationCurrent(pending)) return true;
        _liveEffectPreparation = pending;
        UpdateStatus(L.Get("Effect.PreparingLive"));
        RequestRender();
        try
        {
            // A pending region stays outside the document and undo stack until a complete
            // frame for the current target version is available.
            bool ready = await pending.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5), _documentLifetime.Token);
            return ready && ReferenceEquals(pending, _liveEffectPreparation) && IsLivePreparationCurrent(pending);
        }
        catch (TimeoutException exception)
        {
            _diagnostics.Write("LiveCapture.PreparationTimedOut", exception: exception);
            if (IsLivePreparationCurrent(pending)) UpdateStatus(L.Get("Effect.LiveNotReady"));
            return false;
        }
        finally
        {
            if (ReferenceEquals(pending, _liveEffectPreparation)) _liveEffectPreparation = null;
            RequestRender();
        }
    }
}
