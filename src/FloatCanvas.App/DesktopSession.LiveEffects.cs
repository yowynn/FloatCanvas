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

    private void SyncLiveEffectsCapture()
    {
        bool needed = !_disposed && _cleanCaptureDepth == 0 && _host.IsVisible && Active.Document.Kind == DocumentKind.LiveOverlay &&
            SceneRenderer.HasLiveEffects(Active.Document);
        if (!needed) { StopLiveEffectsCapture(); return; }
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
            try
            {
                _host.SetLiveCaptureExclusion(true);
                _liveCaptureExcluded=true;
                _liveCleanBoundary=Stopwatch.GetTimestamp();
                var source=new LiveDesktopCapture(); _liveDesktopCapture=source;
                var gate=new CaptureDispatchGate();
                source.FrameReady += () => QueueLiveDesktopFrame(source,gate);
                source.Failed += error => _app.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    if (_disposed || !ReferenceEquals(source,_liveDesktopCapture) || !ReferenceEquals(source.LastError,error)) return;
                    _renderer.LiveBackdrop=null; _desktopBackdrop?.Dispose(); _desktopBackdrop=null;
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
        var regions=Active.Document.Elements.Where(e=>e.Visible && e.Style.Opacity>0 && e.Kind==ElementKind.LivePixelate)
            .Select(e=>ElementGeometry.Bounds(e,false).Intersect(Active.Document.Viewport))
            .Where(b=>!b.IsEmpty).Select(b=>new PixelBounds((int)Math.Floor(b.Left),(int)Math.Floor(b.Top),
                checked((int)Math.Ceiling(b.Right)-(int)Math.Floor(b.Left)),
                checked((int)Math.Ceiling(b.Bottom)-(int)Math.Floor(b.Top)))).ToArray();
        long version=_liveDesktopCapture.UpdateTargets(_host.Monitors,regions,_liveCleanBoundary);
        if(version!=_liveTargetVersion)
        {
            _liveTargetVersion=version;
            _renderer.LiveBackdrop=null; _desktopBackdrop?.Dispose(); _desktopBackdrop=null;
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
                    _liveCaptureError=null;
                    RequestRender();
                }
                catch { foreach(var tile in tiles)tile.Image.Dispose(); throw; }
            }
        });
    }

    private void StopLiveEffectsCapture()
    {
        var capture=_liveDesktopCapture; _liveDesktopCapture=null;
        capture?.Dispose();
        if(_liveCaptureExcluded)
        {
            try { _host.SetLiveCaptureExclusion(false); _liveCaptureExcluded=false; }
            catch(Exception ex) { if(!_disposed)UpdateStatus(ex.Message); }
        }
        _renderer.LiveBackdrop=null; _desktopBackdrop?.Dispose(); _desktopBackdrop=null;
        _liveTargetVersion=0; _liveCaptureError=null;
    }
}
