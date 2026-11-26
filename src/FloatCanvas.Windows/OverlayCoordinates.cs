namespace FloatCanvas.Windows;

/// <summary>Converts monitor-local WPF DIPs to global desktop physical pixels without scaling the desktop origin.</summary>
public static class OverlayCoordinates
{
    private static readonly Lazy<SetWindowBandProcedure?> SetWindowBand = new(() =>
    {
        nint module = NativeMethods.GetModuleHandleW("user32.dll");
        nint address = module == 0 ? 0 : NativeMethods.GetProcAddress(module, "SetWindowBand");
        return address == 0 ? null : System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<SetWindowBandProcedure>(address);
    });
    private static readonly Lazy<GetWindowBandProcedure?> GetWindowBand = new(() =>
    {
        nint module = NativeMethods.GetModuleHandleW("user32.dll");
        nint address = module == 0 ? 0 : NativeMethods.GetProcAddress(module, "GetWindowBand");
        return address == 0 ? null : System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<GetWindowBandProcedure>(address);
    });

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

    /// <summary>Places an interactive WPF tool in the same private desktop band as its enhanced-input canvas.</summary>
    public static bool TrySetWindowBand(nint window, uint band) =>
        window != 0 && SetWindowBand.Value is { } set && set(window, 0, band);
    public static bool TryGetWindowBand(nint window, out uint band)
    {
        band = 0;
        return window != 0 && GetWindowBand.Value is { } get && get(window, out band);
    }
    public static bool EnsureWindowBand(nint window, uint band) =>
        TrySetWindowBand(window, band) || TryGetWindowBand(window, out uint actual) && actual == band;

    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Winapi)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private delegate bool SetWindowBandProcedure(nint window, nint insertAfter, uint band);
    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Winapi)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private delegate bool GetWindowBandProcedure(nint window, out uint band);
}
