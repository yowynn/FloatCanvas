using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatCanvas.Windows;
using SkiaSharp;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private IReadOnlyList<OverlayVisualSnapshot> CaptureScreenshotTools()
    {
        // Capture our current presentation before focus-loss closes tools and cancels
        // drafts. Never capture desktop pixels or re-render the document here.
        var sources = PresentationSource.CurrentSources.OfType<HwndSource>()
            .Where(source => !source.IsDisposed && source.RootVisual is UIElement { IsVisible: true } &&
                IsScreenshotWindowVisible(source.Handle)).ToDictionary(source => source.Handle);
        var ordered = new List<HwndSource>();
        var visited = new HashSet<nint>();
        for (nint window = GetScreenshotTopWindow(0); window != 0 && visited.Add(window); window = GetScreenshotWindow(window, 2))
            if (sources.TryGetValue(window, out var source)) ordered.Add(source);
        var result = new List<OverlayVisualSnapshot>();
        try
        {
            foreach (var source in ordered.AsEnumerable().Reverse())
            {
                if (source.RootVisual is not FrameworkElement visual || !GetScreenshotWindowRect(source.Handle, out var bounds) ||
                    bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top || visual.ActualWidth <= 0 || visual.ActualHeight <= 0) continue;
                int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
                var pixels = new RenderTargetBitmap(width, height, width * 96d / visual.ActualWidth, height * 96d / visual.ActualHeight, PixelFormats.Pbgra32);
                pixels.Render(visual);
                using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
                pixels.CopyPixels(Int32Rect.Empty, bitmap.GetPixels(), checked(bitmap.RowBytes * height), bitmap.RowBytes);
                result.Add(new(source.Handle, new(bounds.Left, bounds.Top, width, height), SKImage.FromPixelCopy(bitmap.PeekPixels()) ?? throw new OutOfMemoryException()));
            }
            return result;
        }
        catch { foreach (var item in result) item.Dispose(); throw; }
    }

    private void RestoreScreenshotTools(IReadOnlyList<nint> handles)
    {
        if (_disposed) return;
        var allowed = handles.ToHashSet();
        foreach (var source in PresentationSource.CurrentSources.OfType<HwndSource>())
            if (!source.IsDisposed && allowed.Contains(source.Handle) && source.RootVisual is UIElement { IsVisible: true })
                _ = SetScreenshotWindowPos(source.Handle, 0, 0, 0, 0, 0, 0x257);
        RefreshPassThroughLauncher();
    }

    [StructLayout(LayoutKind.Sequential)] private struct ScreenshotRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")] private static extern bool GetScreenshotWindowRect(nint window, out ScreenshotRect bounds);
    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")] private static extern bool IsScreenshotWindowVisible(nint window);
    [DllImport("user32.dll", EntryPoint = "GetTopWindow")] private static extern nint GetScreenshotTopWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindow")] private static extern nint GetScreenshotWindow(nint window, uint command);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")] private static extern bool SetScreenshotWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
