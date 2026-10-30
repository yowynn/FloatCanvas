using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

/// <summary>Constrains the full physical toolbar frame before native movement or resizing is applied.</summary>
internal static class ToolbarWindowBounds
{
    private const string ResourceKey = "FloatCanvas.ToolbarWindowBounds";

    internal static void Attach(Window window, Func<IReadOnlyList<MonitorInfo>> monitors)
    {
        if (!window.Resources.Contains(ResourceKey)) window.Resources[ResourceKey] = new Placement(window, monitors);
    }

    private sealed class Placement
    {
        private readonly Window window;
        private readonly Func<IReadOnlyList<MonitorInfo>> monitors;
        private HwndSource? source;

        internal Placement(Window window, Func<IReadOnlyList<MonitorInfo>> monitors)
        {
            this.window = window; this.monitors = monitors;
            window.SourceInitialized += Initialized;
            window.Closed += Closed;
            Install();
        }

        private void Initialized(object? sender, EventArgs args) => Install();
        private void Install()
        {
            if (source is not null) return;
            nint handle = new WindowInteropHelper(window).Handle;
            if (handle == 0) return;
            source = HwndSource.FromHwnd(handle);
            source?.AddHook(Hook);
        }
        private void Closed(object? sender, EventArgs args)
        {
            source?.RemoveHook(Hook); source = null;
            window.SourceInitialized -= Initialized; window.Closed -= Closed;
        }

        private nint Hook(nint handle, int message, nint wParam, nint lParam, ref bool handled)
        {
            if (lParam == 0) return 0;
            if (message == 0x0046) // WM_WINDOWPOSCHANGING: shared by DragMove and delegated SetWindowPos drags.
            {
                var position = Marshal.PtrToStructure<WindowPosition>(lParam);
                const uint noSize = 0x0001, noMove = 0x0002;
                if ((position.Flags & (noSize | noMove)) == (noSize | noMove) || !GetWindowRect(handle, out var current)) return 0;
                var requested = new PixelBounds(
                    (position.Flags & noMove) == 0 ? position.X : current.Left,
                    (position.Flags & noMove) == 0 ? position.Y : current.Top,
                    (position.Flags & noSize) == 0 ? position.Width : current.Right - current.Left,
                    (position.Flags & noSize) == 0 ? position.Height : current.Bottom - current.Top);
                var constrained = Constrain(requested);
                if (constrained.Left == requested.Left && constrained.Top == requested.Top) return 0;
                position.X = constrained.Left; position.Y = constrained.Top;
                position.Flags &= ~noMove;
                Marshal.StructureToPtr(position, lParam, false);
            }
            else if (message == 0x0216) // WM_MOVING: also keep the native drag proposal inside the screen union.
            {
                var rectangle = Marshal.PtrToStructure<NativeRect>(lParam);
                var constrained = Constrain(new(rectangle.Left, rectangle.Top, rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top));
                rectangle = new() { Left = constrained.Left, Top = constrained.Top, Right = constrained.Right, Bottom = constrained.Bottom };
                Marshal.StructureToPtr(rectangle, lParam, false);
                handled = true; return 1;
            }
            return 0;
        }

        private PixelBounds Constrain(PixelBounds requested)
        {
            var screens = monitors().Select(monitor => monitor.Bounds).ToArray();
            if (ToolbarScreenBounds.TryConstrain(requested, screens, out var constrained)) return constrained;
            // A toolbar larger than every usable region cannot fit. Retain its
            // dimensions and keep its launcher reachable on the nearest screen.
            if (requested.Width <= 0 || requested.Height <= 0) return requested;
            double nearest = double.PositiveInfinity;
            foreach (var screen in screens)
            {
                if (screen.Width <= 0 || screen.Height <= 0) continue;
                int left = Math.Clamp(requested.Left, screen.Left, Math.Max(screen.Left, screen.Right - requested.Width));
                int top = Math.Clamp(requested.Top, screen.Top, Math.Max(screen.Top, screen.Bottom - requested.Height));
                double dx = (double)left - requested.Left, dy = (double)top - requested.Top, distance = dx * dx + dy * dy;
                if (distance >= nearest) continue;
                nearest = distance; constrained = requested with { Left = left, Top = top };
            }
            return constrained;
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct WindowPosition
    {
        public nint Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRect rectangle);
}
