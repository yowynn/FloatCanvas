using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FloatCanvas.App;

/// <summary>Samples the already formatted label drawing, including trimming and font fallback.</summary>
internal static class LayerNameInkHit
{
    internal static bool Contains(TextBlock label, Point point)
    {
        if (label.Visibility != Visibility.Visible || !new Rect(label.RenderSize).Contains(point) ||
            VisualTreeHelper.GetDrawing(label) is not { } drawing) return false;
        var dpi = VisualTreeHelper.GetDpi(label);
        double x = Math.Floor(point.X * dpi.DpiScaleX) / dpi.DpiScaleX;
        double y = Math.Floor(point.Y * dpi.DpiScaleY) / dpi.DpiScaleY;
        var sample = new DrawingVisual();
        using (var context = sample.RenderOpen())
        {
            context.PushTransform(new TranslateTransform(-x, -y));
            context.DrawDrawing(drawing);
            context.Pop();
        }
        var pixel = new RenderTargetBitmap(1, 1, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        pixel.Render(sample);
        var rgba = new byte[4]; pixel.CopyPixels(rgba, 4, 0);
        return rgba[3] >= 16;
    }
}
