using L = FloatCanvas.Localization.AppText;
using System.ComponentModel;
using System.Runtime.InteropServices;
using SkiaSharp;
using static FloatCanvas.Windows.NativeMethods;

namespace FloatCanvas.Windows;

/// <summary>A reusable top-down BGRA premultiplied DIB. Skia renders directly into its native pixels.</summary>
internal sealed class DibBuffer : IDisposable
{
    private nint _dc, _bitmap, _previousBitmap;
    internal SKSurface Surface { get; }
    internal nint Pixels { get; private set; }
    internal SKImageInfo ImageInfo { get; }

    internal DibBuffer(int width, int height)
    {
        ImageInfo = new(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var screenDc = GetDC(0);
        try
        {
            _dc = CreateCompatibleDC(screenDc);
            if (_dc == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var info = new BitmapInformation { Header = new BitmapHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapHeader>(), Width = width, Height = -height,
                Planes = 1, BitCount = 32
            }};
            _bitmap = CreateDIBSection(screenDc, ref info, 0, out var pixels, 0, 0);
            Pixels = pixels;
            if (_bitmap == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _previousBitmap = SelectObject(_dc, _bitmap);
            Surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), pixels, checked(width * 4))
                ?? throw new InvalidOperationException(L.Get("Overlay.RenderBufferFailed"));
        }
        catch { Dispose(); throw; }
        finally { ReleaseDC(0, screenDc); }
    }

    internal unsafe void Present(nint hwnd, PixelBounds bounds)
    {
        var destination = new Point(bounds.Left, bounds.Top);
        var size = new Size(bounds.Width, bounds.Height);
        var source = new Point(0, 0);
        var blend = new BlendFunction { ConstantAlpha = 255, AlphaFormat = 1 };
        var info = new LayeredInformation
        {
            Size = (uint)sizeof(LayeredInformation), Destination = &destination, Dimensions = &size,
            SourceDc = _dc, Source = &source, Blend = &blend, Flags = 2
        };
        if (!UpdateLayeredWindowIndirect(hwnd, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error(), L.Get("Overlay.DisplayUpdateFailed"));
    }

    public void Dispose()
    {
        Surface?.Dispose();
        if (_dc != 0 && _previousBitmap != 0) SelectObject(_dc, _previousBitmap);
        if (_bitmap != 0) { DeleteObject(_bitmap); _bitmap = 0; }
        if (_dc != 0) { DeleteDC(_dc); _dc = 0; }
        Pixels = 0;
    }
}
