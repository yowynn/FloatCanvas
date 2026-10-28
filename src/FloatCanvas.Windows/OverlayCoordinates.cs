namespace FloatCanvas.Windows;

/// <summary>Converts monitor-local WPF DIPs to global desktop physical pixels without scaling the desktop origin.</summary>
public static class OverlayCoordinates
{
    /// <summary>Raises an application-owned tool window without moving, resizing or activating it.</summary>
    public static void RaiseToolWindow(nint window)
    {
        if (!NativeMethods.SetWindowPos(window, NativeMethods.HwndTopmost, 0, 0, 0, 0, NativeMethods.ToolWindowRaiseFlags))
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
    }

    /// <summary>Places an application-owned tool window using physical coordinates, preserving size, activation and relative order.</summary>
    public static void PositionToolWindow(nint window, OverlayPoint position)
    {
        using var dpi = new ThreadDpiAwarenessScope(WindowsCapabilities.Current);
        if (!NativeMethods.SetWindowPos(window, 0, checked((int)Math.Round(position.X)),
                checked((int)Math.Round(position.Y)), 0, 0, NativeMethods.ToolWindowPositionFlags))
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
    }

    public static OverlayPoint DipToPixel(OverlayPoint dip, MonitorInfo monitor) =>
        new(monitor.Bounds.Left + dip.X * monitor.DpiX / 96d, monitor.Bounds.Top + dip.Y * monitor.DpiY / 96d);

    public static OverlayPoint PixelToDip(OverlayPoint pixel, MonitorInfo monitor) =>
        new((pixel.X - monitor.Bounds.Left) * 96d / monitor.DpiX, (pixel.Y - monitor.Bounds.Top) * 96d / monitor.DpiY);
}
