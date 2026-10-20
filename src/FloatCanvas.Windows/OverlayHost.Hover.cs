using System.Runtime.InteropServices;

namespace FloatCanvas.Windows;

public sealed partial class OverlayHost
{
    private nint _hoverInputWindow;
    /// <summary>The pointer left the last canvas input window. Does not cancel a captured gesture.</summary>
    public event Action? PointerLeft;

    private void TrackPointerWindow(WindowState state)
    {
        if (state.Freed) return;
        using var dpi = new ThreadDpiAwarenessScope(Capabilities);
        TrackPointerWindowAt(NativeMethods.GetCursorPos(out var pointer) ? WindowFromPoint(pointer) : 0);
    }

    private void TrackPointerWindowAt(nint hitWindow)
    {
        // Captured moves still arrive at the original HWND, even over another
        // monitor or an auxiliary window. Track the actual window under the pointer.
        var state = _windows.FirstOrDefault(pair => pair.Input.Handle == hitWindow)?.Input;
        if (state is null)
        {
            _hoverInputWindow = 0;
            // The latest captured move may have just updated the app's hover point.
            PointerLeft?.Invoke();
            return;
        }
        _hoverInputWindow = state.Handle;
        if (state.TrackingPointerLeave) return;
        var tracking = new PointerTracking { Size = (uint)Marshal.SizeOf<PointerTracking>(), Flags = 2, Window = state.Handle };
        state.TrackingPointerLeave = TrackMouseEvent(ref tracking);
    }

    private void OnPointerLeft(WindowState state)
    {
        state.TrackingPointerLeave = false;
        // The next monitor may already have delivered its first move before this
        // queued leave arrives. Do not clear that newer window's hover state.
        if (_hoverInputWindow != state.Handle) return;
        _hoverInputWindow = 0;
        PointerLeft?.Invoke();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerTracking { public uint Size, Flags; public nint Window; public uint HoverTime; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref PointerTracking tracking);
    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativeMethods.Point point);
}
