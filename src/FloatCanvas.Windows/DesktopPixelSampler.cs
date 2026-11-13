using System.Runtime.InteropServices;

namespace FloatCanvas.Windows;

/// <summary>Read one currently displayed pixel for a transient color preview.</summary>
public static class DesktopPixelSampler
{
    /// <summary>Small displayed-pixel patch for a transient magnifier. No screenshot is persisted.</summary>
    public static SkiaSharp.SKBitmap? SamplePatch(OverlayPoint point, int size)
    {
        if (size is < 1 or > 128 || !double.IsFinite(point.X + point.Y) || Math.Abs(point.X) > int.MaxValue / 2 || Math.Abs(point.Y) > int.MaxValue / 2) return null;
        nint dc = GetDC(0); if (dc == 0) return null;
        nint memory = 0, bitmap = 0, old = 0;
        try
        {
            memory = CreateCompatibleDC(dc); if (memory == 0) return null;
            var info = new BitmapInfo { Size = 40, Width = size, Height = -size, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(dc, ref info, 0, out nint pixels, 0, 0);
            if (bitmap == 0 || pixels == 0) return null;
            old = SelectObject(memory, bitmap);
            if (!BitBlt(memory, 0, 0, size, size, dc, (int)point.X - size / 2, (int)point.Y - size / 2, 0x00CC0020)) return null;
            var bytes = new byte[size * size * 4]; Marshal.Copy(pixels, bytes, 0, bytes.Length);
            for (int i = 3; i < bytes.Length; i += 4) bytes[i] = 255;
            var result = new SkiaSharp.SKBitmap(size, size, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Opaque);
            Marshal.Copy(bytes, 0, result.GetPixels(), bytes.Length); return result;
        }
        finally
        {
            if (old != 0) SelectObject(memory, old);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            ReleaseDC(0, dc);
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, ImageSize; public int XPels, YPels; public uint ColorsUsed, ImportantColors;
    }
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int sx, int sy, uint operation);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
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
