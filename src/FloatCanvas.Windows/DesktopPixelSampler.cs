using System.Runtime.InteropServices;

namespace FloatCanvas.Windows;

/// <summary>Read one currently displayed pixel for a transient color preview.</summary>
public static class DesktopPixelSampler
{
    public static uint? Sample(OverlayPoint point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < int.MinValue || point.X > int.MaxValue || point.Y < int.MinValue || point.Y > int.MaxValue) return null;
        nint dc = GetDC(0);
        if (dc == 0) return null;
        try
        {
            uint color = GetPixel(dc,(int)Math.Floor(point.X),(int)Math.Floor(point.Y));
            return color == uint.MaxValue ? null : 0xFF000000 | ((color & 255) << 16) | (color & 0xFF00) | ((color >> 16) & 255);
        }
        finally { ReleaseDC(0,dc); }
    }
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window,nint dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint dc,int x,int y);
}
