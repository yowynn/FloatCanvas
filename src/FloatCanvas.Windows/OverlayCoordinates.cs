namespace FloatCanvas.Windows;

/// <summary>Converts monitor-local WPF DIPs to global desktop physical pixels without scaling the desktop origin.</summary>
public static class OverlayCoordinates
{
    /// <summary>Places an application-owned tool window using physical coordinates, preserving its size.</summary>
    public static void PositionToolWindow(nint window, OverlayPoint position)
    {
        var previous = NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DpiPerMonitorV2);
        try
        {
            if (!NativeMethods.SetWindowPos(window, NativeMethods.HwndTopmost, checked((int)Math.Round(position.X)),
                    checked((int)Math.Round(position.Y)), 0, 0, 0x11))
                throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        }
        finally { if (previous != 0) NativeMethods.SetThreadDpiAwarenessContext(previous); }
    }

    public static OverlayPoint DipToPixel(OverlayPoint dip, MonitorInfo monitor) =>
        new(monitor.Bounds.Left + dip.X * monitor.DpiX / 96d, monitor.Bounds.Top + dip.Y * monitor.DpiY / 96d);

    public static OverlayPoint PixelToDip(OverlayPoint pixel, MonitorInfo monitor) =>
        new((pixel.X - monitor.Bounds.Left) * 96d / monitor.DpiX, (pixel.Y - monitor.Bounds.Top) * 96d / monitor.DpiY);
}
