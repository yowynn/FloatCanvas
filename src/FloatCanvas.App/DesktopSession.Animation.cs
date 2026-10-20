using System.Diagnostics;
using System.Windows.Threading;
using FloatCanvas.Core;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private readonly Stopwatch _animationClock = new();
    private EditorSession? _animationSession;
    private DispatcherTimer? _animationTimer;
    private bool _pixelsOnlyRender;

    private void PrepareAnimationFrame()
    {
        _animationTimer?.Stop();
        // Undo, redo and cancelled gestures replace the document inside the same
        // editor session. Only switching canvases should restart playback.
        if (!ReferenceEquals(_animationSession, Active))
        {
            _animationSession = Active;
            _animationClock.Reset();
        }
        if (_host.IsVisible && _renderer.HasAnimatedImages(Active.Document, Active.Assets)) _animationClock.Start();
        else _animationClock.Stop();
        _renderer.AnimationTime = _animationClock.Elapsed;
        _editor.AnimationTime = _renderer.AnimationTime;
    }

    private void ScheduleAnimationFrame()
    {
        if (_disposed || !_host.IsVisible || _cleanCaptureDepth != 0 ||
            !_renderer.HasAnimatedImages(Active.Document, Active.Assets)) return;
        var drawnTime = _renderer.AnimationTime;
        var delay = _renderer.GetNextAnimationDelay(Active.Document, Active.Assets, drawnTime);
        // No pending frame means finite animations have reached their last frame.
        if (delay is null) return;
        _animationTimer ??= CreateAnimationTimer();
        var remaining = drawnTime + delay.Value - _animationClock.Elapsed;
        _animationTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(remaining.TotalMilliseconds, 16, 1000));
        _animationTimer.Start();
    }

    private DispatcherTimer CreateAnimationTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, _app.Dispatcher);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_host.IsVisible || _cleanCaptureDepth != 0) return;
            RequestPixelRender();
        };
        return timer;
    }
}
