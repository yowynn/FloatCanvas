using System.Diagnostics;
using System.Windows.Threading;
using FloatCanvas.Core;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private DispatcherTimer? _canvasEdgePanTimer;
    private OverlayPointerEvent? _canvasEdgePointer;
    private long _canvasEdgeEntered, _canvasEdgeLastTick;
    private object? _canvasEdgeContext;
    private string? _canvasEdgeKey;
    private void StopCanvasEdgePan()
    {
        _canvasEdgePanTimer?.Stop(); _canvasEdgePointer = null; _canvasEdgeEntered = 0;
    }
    private void ObserveCanvasEdgePan(OverlayPointerEvent input)
    {
        if (input.Kind is OverlayPointerKind.Up or OverlayPointerKind.Cancel || !_editor.IsMovingCanvasSelection ||
            !_host.IsEditing || !_host.IsVisible || _canvasViewPanLast is not null)
        { StopCanvasEdgePan(); return; }
        if (input.Kind != OverlayPointerKind.Move) return;
        var screens = _host.Monitors.Select(monitor => new RectD(monitor.Bounds.Left, monitor.Bounds.Top, monitor.Bounds.Width, monitor.Bounds.Height)).ToArray();
        if (CanvasEdgePan.Velocity(new(input.Position.X, input.Position.Y), screens) == default)
        { StopCanvasEdgePan(); return; }
        _canvasEdgePointer = input;
        if (_canvasEdgeEntered != 0) return;
        _canvasEdgeContext = _canvasViewGroupEpoch; _canvasEdgeKey = _canvasViewKey;
        _canvasEdgeEntered = _canvasEdgeLastTick = Stopwatch.GetTimestamp();
        _canvasEdgePanTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input,
            (_, _) => TickCanvasEdgePan(), _toolbar.Dispatcher);
        _canvasEdgePanTimer.Start();
    }
    private void TickCanvasEdgePan()
    {
        if (_canvasEdgePointer is not { } input || _disposed || !_host.IsEditing || !_host.IsVisible ||
            !_editor.IsMovingCanvasSelection || !ReferenceEquals(_canvasEdgeContext, _canvasViewGroupEpoch) || _canvasEdgeKey != _canvasViewKey)
        { StopCanvasEdgePan(); return; }
        long now = Stopwatch.GetTimestamp();
        input = input with { Modifiers = _host.InteractionModifiers };
        double elapsed = Math.Min(.05, Stopwatch.GetElapsedTime(_canvasEdgeLastTick, now).TotalSeconds);
        _canvasEdgeLastTick = now;
        if (Stopwatch.GetElapsedTime(_canvasEdgeEntered, now).TotalMilliseconds < 200) return;
        var screens = _host.Monitors.Select(monitor => new RectD(monitor.Bounds.Left, monitor.Bounds.Top, monitor.Bounds.Width, monitor.Bounds.Height)).ToArray();
        var velocity = CanvasEdgePan.Velocity(new(input.Position.X, input.Position.Y), screens);
        if (velocity == default) { StopCanvasEdgePan(); return; }
        ChangeCanvasViewing(_canvasView.Pan(velocity * -elapsed), duringMove: true);
        // Retain the initial logical grab point; only the inverse-mapped current
        // pointer changes. The existing pointer transaction remains the sole undo.
        _editor.PointerForCanvas(input, propertiesDoubleClick: false);
        _canvasInputSequence++;
    }
}
