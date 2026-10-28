using System.Diagnostics;
using System.Windows.Threading;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private readonly Stopwatch _animationClock = new();
    private EditorSession? _animationSession;
    private DispatcherTimer? _animationTimer;
    private SceneAnimationSchedule? _animationSchedule;
    private CanvasPreviewContext? _animationScheduleContext;
    private bool _pixelsOnlyRender;
    private readonly List<PixelBounds> _pixelDirtyRegions = [];

    private void PrepareAnimationFrame()
    {
        _animationTimer?.Stop();
        // Undo, redo and cancelled gestures replace the document inside the same
        // editor session. Only switching canvases should restart playback.
        if (!ReferenceEquals(_animationSession, Active))
        {
            _animationSession = Active;
            _animationClock.Reset();
            _animationSchedule = null;
            _animationScheduleContext = null;
        }
        if (_host.IsVisible && CurrentAnimationSchedule() is { HasAnimations: true }) _animationClock.Start();
        else _animationClock.Stop();
        _renderer.AnimationTime = _animationClock.Elapsed;
        // Input uses the last successfully presented snapshot, not this request clock.
    }

    private void ScheduleAnimationFrame()
    {
        if (_disposed || _presentationUncertain || !_host.IsVisible || _cleanCaptureDepth != 0 ||
            CurrentAnimationSchedule() is not { HasAnimations: true } schedule) return;
        // No pending frame means finite animations have reached their last frame.
        if (schedule.NextDelay is not { } delay) return;
        _animationTimer ??= CreateAnimationTimer();
        // SampleTime precedes the worker render. A slow completed frame catches up
        // against the live playback clock instead of starting a fresh delay now.
        var remaining = schedule.SampleTime + delay - _animationClock.Elapsed;
        _animationTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(remaining.TotalMilliseconds, 16, 1000));
        _animationTimer.Start();
    }

    private SceneAnimationSchedule? CurrentAnimationSchedule() =>
        _animationScheduleContext == CurrentPreviewContext() ? _animationSchedule : null;

    private void ApplyAnimationSchedule(SceneAnimationSchedule? schedule)
    {
        _animationTimer?.Stop();
        _animationSchedule = schedule;
        _animationScheduleContext = CurrentPreviewContext();
        if (_host.IsVisible && schedule is { HasAnimations: true }) _animationClock.Start();
        else _animationClock.Stop();
        ScheduleAnimationFrame();
    }

    private DispatcherTimer CreateAnimationTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, _app.Dispatcher);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_presentationUncertain || !_host.IsVisible || _cleanCaptureDepth != 0 || CurrentAnimationSchedule() is not { HasAnimations: true } schedule) return;
            RequestPixelRender(schedule.DirtyBounds.Select(bounds => bounds.Inflate(1)).Select(bounds =>
                new PixelBounds((int)Math.Floor(bounds.Left + _editor.ScreenOrigin.X), (int)Math.Floor(bounds.Top + _editor.ScreenOrigin.Y),
                    checked((int)Math.Ceiling(bounds.Right) - (int)Math.Floor(bounds.Left)),
                    checked((int)Math.Ceiling(bounds.Bottom) - (int)Math.Floor(bounds.Top)))));
        };
        return timer;
    }
}
