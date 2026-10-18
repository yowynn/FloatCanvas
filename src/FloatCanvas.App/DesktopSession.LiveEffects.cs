using System.Diagnostics;
using System.Windows.Threading;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private LiveDesktopCapture? _liveDesktopCapture;
    private DesktopBackdrop? _desktopBackdrop;
    private long _liveCleanBoundary, _liveTargetVersion;
    private sealed class CaptureDispatchGate { public int Posted; }
    private bool _liveCaptureExcluded;
    private string? _liveCaptureError;
    private int _cleanCaptureDepth;
    private Task<bool>? _sessionCaptureProbe;

    private async Task ObserveSessionCaptureProbeAsync(Task<bool> probe)
    {
        try { await probe; }
        catch (Exception exception) { _diagnostics.Write("LiveCapture.CapabilityFailed", exception: exception); }
        // Re-evaluate the current canvas; this cannot reopen a hidden toolbar or restore editing.
        if (!_disposed) RequestRender();
    }

    private void SyncLiveEffectsCapture()
    {
        if (_liveEffectPreparation is { } pending && !IsLivePreparationCurrent(pending)) CancelLivePreparation();
        if (_desktopBackdrop is not null && !IsLiveBackdropCompatible()) ClearLiveBackdrop();
        bool hasEffects = SceneRenderer.HasLiveEffects(Active.Document);
        bool needed = !_disposed && _cleanCaptureDepth == 0 && _host.IsVisible && Active.Document.Kind == DocumentKind.LiveOverlay &&
            (hasEffects || _liveEffectPreparation is not null);
        if (!needed)
        {
            if (!_disposed && !_host.IsVisible && _cleanCaptureDepth == 0 && hasEffects && IsLiveBackdropCompatible())
                ReleaseLiveEffectsCapture(keepBackdrop: true);
            else StopLiveEffectsCapture();
            return;
        }
        if (!_host.Capabilities.SupportsCaptureExclusion)
        {
            // Older Windows accepts 0x11 as WDA_MONITOR, which masks the whole
            // overlay in capture. Never sample that result as a live desktop.
            if (_liveDesktopCapture is not null || _liveCaptureExcluded || _desktopBackdrop is not null)
                StopLiveEffectsCapture();
            string message = L.Get("Effect.LegacyLiveRegion");
            if (_liveCaptureError != message) { _liveCaptureError = message; UpdateStatus(message); }
            return;
        }
        if (_liveDesktopCapture?.Completion.IsCompleted==true)StopLiveEffectsCapture();
        if (_liveDesktopCapture is null)
        {
            if (_sessionCaptureProbe is null)
            {
                _sessionCaptureProbe = LiveDesktopCapture.ProbeSessionExclusionAsync();
                _ = ObserveSessionCaptureProbeAsync(_sessionCaptureProbe);
            }
            if (!_sessionCaptureProbe.IsCompleted) return;
            if (!_sessionCaptureProbe.IsCompletedSuccessfully)
            {
                CancelLivePreparation();
                string failure = _sessionCaptureProbe.Exception?.GetBaseException().Message ?? "Capture capability probe failed.";
                if (_liveCaptureError != failure) { _liveCaptureError = failure; UpdateStatus(L.Get("Feedback4.Effect.CaptureUnavailable") + failure); }
                return;
            }
            try
            {
                bool sessionExclusion = _sessionCaptureProbe.Result;
                // A session-local list leaves the visible canvas available to other capture
                // applications. Global affinity is needed only on the older fallback path.
                if (!sessionExclusion) { _host.SetLiveCaptureExclusion(true); _liveCaptureExcluded = true; }
                _liveCleanBoundary=Stopwatch.GetTimestamp();
                var source=new LiveDesktopCapture(useSessionWindowExclusion: sessionExclusion); _liveDesktopCapture=source;
                _diagnostics.Write("LiveCapture.Started", new { source.UsesSessionWindowExclusion,
                    GlobalExclusion = _liveCaptureExcluded, Boundary = _liveCleanBoundary });
                if (!sessionExclusion) UpdateStatus(L.Get("Effect.GlobalCaptureExclusion"));
                var gate=new CaptureDispatchGate();
                source.FrameReady += () => QueueLiveDesktopFrame(source,gate);
                source.Failed += error => _app.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    if (_disposed || !ReferenceEquals(source,_liveDesktopCapture) || !ReferenceEquals(source.LastError,error)) return;
                    _diagnostics.Write("LiveCapture.Failed", new { source.UsesSessionWindowExclusion }, error);
                    CancelLivePreparation();
                    if (!IsLiveBackdropCompatible()) ClearLiveBackdrop();
                    if (_liveCaptureError!=error.Message)
                    { _liveCaptureError=error.Message; UpdateStatus(L.Get("Feedback4.Effect.CaptureUnavailable")+error.Message); }
                    RequestRender();
                });
            }
            catch(Exception ex)
            {
                StopLiveEffectsCapture();
                _liveCaptureError=ex.Message; UpdateStatus(L.Get("Feedback4.Effect.CaptureUnavailable")+ex.Message);
                return;
            }
        }
        var samplingRegions=Active.Document.Elements.Where(e=>e.Visible && e.Style.Opacity>0 && ElementGeometry.IsLiveEffect(e.Kind))
            .Select(e=>SceneRenderer.LiveEffectSamplingBounds(e).Intersect(Active.Document.Viewport))
            .Concat(_liveEffectPreparation is { } preparation ? [preparation.Bounds] : []);
        var regions=samplingRegions.Where(b=>!b.IsEmpty).Select(b=>new PixelBounds((int)Math.Floor(b.Left),(int)Math.Floor(b.Top),
                checked((int)Math.Ceiling(b.Right)-(int)Math.Floor(b.Left)),
                checked((int)Math.Ceiling(b.Bottom)-(int)Math.Floor(b.Top)))).ToArray();
        long version=_liveDesktopCapture.UpdateTargets(_host.Monitors,regions,_liveCleanBoundary);
        if(version!=_liveTargetVersion)
        {
            _liveTargetVersion=version;
            if (!IsLiveBackdropCompatible()) ClearLiveBackdrop();
        }
    }

    private void QueueLiveDesktopFrame(LiveDesktopCapture source, CaptureDispatchGate gate)
    {
        if(Interlocked.Exchange(ref gate.Posted,1)!=0)return;
        _app.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref gate.Posted,0);
            if(_disposed || !ReferenceEquals(source,_liveDesktopCapture))return;
            if(!source.TryGetLatest(out var frame))return;
            using(frame)
            {
                if(frame.TargetVersion!=_liveTargetVersion)return;
                var tiles=new List<DesktopBackdrop.Tile>();
                try
                {
                    foreach(var output in frame.Outputs)
                    {
                        output.Bitmap.SetImmutable();
                        var b=output.Bounds;
                        tiles.Add(new(new RectD(b.Left,b.Top,b.Width,b.Height),SKImage.FromBitmap(output.Bitmap)));
                    }
                    var next=new DesktopBackdrop(tiles);
                    _renderer.LiveBackdrop=next; _desktopBackdrop?.Dispose(); _desktopBackdrop=next;
                    RememberLiveBackdrop(source, frame.TargetVersion);
                    _liveCaptureError=null;
                    if (_liveEffectPreparation is { } pending && IsLivePreparationCurrent(pending)) pending.Ready.TrySetResult(true);
                    RequestPixelRender();
                }
                catch { foreach(var tile in tiles)tile.Image.Dispose(); throw; }
            }
        });
    }

    private void StopLiveEffectsCapture()
    {
        CancelLivePreparation();
        ReleaseLiveEffectsCapture(keepBackdrop: false);
    }

    private void ReleaseLiveEffectsCapture(bool keepBackdrop)
    {
        var capture=_liveDesktopCapture; _liveDesktopCapture=null;
        capture?.Dispose();
        if(_liveCaptureExcluded)
        {
            try { _host.SetLiveCaptureExclusion(false); _liveCaptureExcluded=false; }
            catch(Exception ex) { if(!_disposed)UpdateStatus(ex.Message); }
        }
        if (!keepBackdrop) ClearLiveBackdrop();
        _liveTargetVersion=0; _liveCaptureError=null;
    }
}
