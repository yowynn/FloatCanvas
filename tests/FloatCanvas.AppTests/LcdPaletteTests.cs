using System.IO;
using FloatCanvas.App;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;
using Input = System.Windows.Input;

internal static class LcdPaletteTests
{
    internal static void Run(Action<string, bool> check)
    {
        if (Wpf.Application.Current is not null) throw new InvalidOperationException("Run --lcd-only in a dedicated process.");
        var app = new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        string output = Environment.GetEnvironmentVariable("FLOATCANVAS_LCD_PREVIEW_DIR") ?? Path.Combine(Path.GetTempPath(), "FloatCanvas-lcd");
        Directory.CreateDirectory(output);
        var text = new Controls.TextBlock { Text = "矩形 · 160 × 100  |  Rectangle" };
        var display = new ToolbarStatusDisplay(text) { Width = 356 };
        var blank = new ToolbarStatusDisplay(new Controls.TextBlock()) { Width = 356 };
        var stack = new Controls.StackPanel(); stack.Children.Add(display); stack.Children.Add(new Controls.Border { Height = 10 }); stack.Children.Add(blank);
        var host = new Controls.Border { Child = stack, Width = 392, Padding = new Wpf.Thickness(16), CornerRadius = new Wpf.CornerRadius(10) };
        host.SetResourceReference(Controls.Border.BackgroundProperty, "CanvasSurface");
        var owner = new Wpf.Window();
        try
        {
            foreach (string theme in new[] { "light", "dark", "light" })
            {
                // Resource updates traverse Window owners. Keep the same ownership as
                // production, without showing the window or creating its native handle.
                owner.Content = host; AppTheme.Apply(theme); Pump(); owner.Content = null; Measure(host);
                var background = ((Media.SolidColorBrush)display.Background).Color;
                var foreground = ((Media.SolidColorBrush)text.Foreground).Color;
                var expected = (Media.Color)Media.ColorConverter.ConvertFromString(theme == "dark" ? "#332E24" : "#C4CCAE");
                check(theme + " LCD uses its calculator surface after live theme changes", background == expected && ((Media.SolidColorBrush)blank.Background).Color == expected);
                check(theme + " LCD text has strong contrast", Contrast(background, foreground) >= 7);
                check(theme + " LCD palette does not recolor ordinary inputs or the toolbar", background != ((Media.SolidColorBrush)app.Resources["CanvasInput"]).Color && background != ((Media.SolidColorBrush)host.Background).Color);
                check(theme + " LCD has a clean inset edge without a texture", display.BorderBrush is Media.LinearGradientBrush edge && edge.GradientStops.Count == 2 && Luminance(edge.GradientStops[0].Color) < Luminance(edge.GradientStops[1].Color) && display.BorderThickness == new Wpf.Thickness(1) && display.Effect is null);
                check(theme + " LCD retains compact layout and readable monospaced text", display.Height == 24 && display.Margin == new Wpf.Thickness(2, 1, 2, 1) && text.FontSize == 11 && display.ActualWidth == 356);
                var bitmap = new Imaging.RenderTargetBitmap(784, (int)Math.Ceiling(host.ActualHeight * 2), 192, 192, Media.PixelFormats.Pbgra32); bitmap.Render(host);
                var blankPoint = blank.TranslatePoint(new Wpf.Point(40, 12), host);
                var blankPoint2 = blank.TranslatePoint(new Wpf.Point(200, 12), host);
                check(theme + " actual LCD pixels paint a flat noise-free surface", Pixel(bitmap, (int)(blankPoint.X * 2), (int)(blankPoint.Y * 2)) == expected && Pixel(bitmap, (int)(blankPoint2.X * 2), (int)(blankPoint2.Y * 2)) == expected);
                var png = new Imaging.PngBitmapEncoder(); png.Frames.Add(Imaging.BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, "lcd-" + theme + ".png")); png.Save(file);
            }
            text.Text = string.Concat(Enumerable.Repeat("长状态 · long status / ", 10)); display.Refresh(); Measure(host);
            display.RaiseEvent(new Input.MouseEventArgs(Input.Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Input.Mouse.MouseEnterEvent });
            check("LCD palette change preserves the existing hover scroll", ((Media.TranslateTransform)text.RenderTransform).HasAnimatedProperties && text.TextTrimming == Wpf.TextTrimming.None);
            display.RaiseEvent(new Input.MouseEventArgs(Input.Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Input.Mouse.MouseLeaveEvent });
            check("LCD hover leave restores compact ellipsis", !((Media.TranslateTransform)text.RenderTransform).HasAnimatedProperties && text.TextTrimming == Wpf.TextTrimming.CharacterEllipsis);
            check("LCD verification creates no native or visible windows", app.Windows.Cast<Wpf.Window>().All(window => !window.IsVisible && new System.Windows.Interop.WindowInteropHelper(window).Handle == 0));
        }
        finally { owner.Close(); app.Shutdown(); }
    }
    private static void Measure(Wpf.FrameworkElement element)
    { element.Measure(new Wpf.Size(392, double.PositiveInfinity)); element.Arrange(new Wpf.Rect(element.DesiredSize)); element.UpdateLayout(); }
    private static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
    private static Media.Color Pixel(Imaging.BitmapSource source, int x, int y)
    {
        byte[] bytes = new byte[4]; source.CopyPixels(new Wpf.Int32Rect(x, y, 1, 1), bytes, 4, 0);
        return Media.Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
    }
    private static double Contrast(Media.Color first, Media.Color second)
    { double a = Luminance(first), b = Luminance(second); return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05); }
    private static double Luminance(Media.Color color)
    {
        static double Linear(byte channel) { double s = channel / 255d; return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }
}
