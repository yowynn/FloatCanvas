using System.IO;
using System.Text.Json;
using FloatCanvas.App;
using SkiaSharp;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Interop = System.Windows.Interop;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;

/// <summary>Focused detached production client paint checks. No native windows, DWM, or desktop pixels.</summary>
internal static class CornerClientTests
{
    internal static void Run(Action<string, bool> check)
    {
        if (Wpf.Application.Current is not null)
            throw new InvalidOperationException("Run --corner-client-only in its own process.");
        var (output, label) = EvidenceLocation();
        var app = new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
        var results = new List<(string Name, bool Passed)>();
        var observations = new List<object>();
        var windows = new List<Wpf.Window>();
        try
        {
            foreach (string theme in new[] { "light", "dark" })
            {
                AppTheme.Apply(theme);
                var window = new Wpf.Window { Width = 430, Height = 300,
                    Content = new Controls.Border(), ResizeMode = Wpf.ResizeMode.NoResize };
                windows.Add(window); AppWindowChrome.ApplyDialog(window); window.ApplyTemplate();
                var border = (Controls.Border)window.Template.FindName("WindowBorder", window);
                border.Measure(new Wpf.Size(430, 300)); border.Arrange(new Wpf.Rect(0, 0, 430, 300)); border.UpdateLayout();
                var backing = new Media.DrawingVisual();
                using (var drawing = backing.RenderOpen())
                    drawing.DrawRectangle(window.Background, null, new Wpf.Rect(0, 0, 430, 300));
                var rendered = new Imaging.RenderTargetBitmap(430, 300, 96, 96, Media.PixelFormats.Pbgra32);
                rendered.Render(backing); rendered.Render(border);
                string fullPath = Path.Combine(output, $"{label}-client-{theme}-window.png");
                SaveClient(rendered, fullPath);
                var corner = new Imaging.CroppedBitmap(rendered, new Wpf.Int32Rect(0, 260, 40, 40));
                string cornerPath = Path.Combine(output, $"{label}-client-{theme}.png");
                SaveClient(corner, cornerPath);
                byte[] pixels = new byte[430 * 300 * 4]; rendered.CopyPixels(pixels, 430 * 4, 0);
                var edge = ((Media.SolidColorBrush)app.Resources["CanvasBorder"]).Color;
                var surface = ((Media.SolidColorBrush)app.Resources["CanvasSurface"]).Color;
                var secondary = new List<object>();
                // Only the 3..11 DIP interior of the old 12-DIP lower-left client arc.
                // The outer two DIP, and every unknown DWM/native pixel, are excluded.
                int radius = (int)Math.Ceiling(AppWindowChrome.PanelCornerRadius);
                for (int x = 3; x < radius; x++)
                for (int fromBottom = 3; fromBottom < radius; fromBottom++)
                {
                    int y = 299 - fromBottom, offset = (y * 430 + x) * 4;
                    var pixel = new SKColor(pixels[offset + 2], pixels[offset + 1], pixels[offset], pixels[offset + 3]);
                    int edgeDistance = Distance(pixel, edge), surfaceDistance = Distance(pixel, surface);
                    if (edgeDistance <= 12 && edgeDistance + 15 < surfaceDistance)
                        secondary.Add(new { X = x, Y = y, InsetFromBottom = fromBottom, Rgb = $"#{pixel.Red:X2}{pixel.Green:X2}{pixel.Blue:X2}" });
                }
                bool nativeWindowCreated = new Interop.WindowInteropHelper(window).Handle != 0;
                observations.Add(new { Theme = theme, Width = 430, Height = 300, RenderDpi = 96,
                    WpfBorder = new { Thickness = border.BorderThickness.ToString(), Radius = border.CornerRadius.ToString(), Brush = border.BorderBrush.ToString() },
                    SecondaryArcPixels = secondary.Count, SecondaryArcSamples = secondary,
                    NativeWindowCreated = nativeWindowCreated, FullImage = fullPath, CornerImage = cornerPath });
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                    results.Add((theme + " Windows 11 client paint has no second themed arc beyond the outer two DIP", secondary.Count == 0));
                results.Add((theme + " client render creates no native window", !nativeWindowCreated && !window.IsVisible));
            }
        }
        finally
        {
            foreach (var window in windows) window.Close();
            app.Shutdown();
            File.WriteAllText(Path.Combine(output, $"{label}-client-report.json"), JsonSerializer.Serialize(new
            {
                Timestamp = DateTimeOffset.Now, Observations = observations,
                Checks = results.Select(result => new { result.Name, result.Passed }),
                Scope = "Detached production WindowBorder rendered over the real opaque CanvasSurface backing at 96 DPI. No HWND, DWM frame, native shadow, desktop image, input, or user application is involved. This isolates only the WPF client arc and does not prove the final DWM-composited appearance."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        foreach (var result in results) check(result.Name, result.Passed);
    }

    private static int Distance(SKColor pixel, Media.Color color) => Math.Abs(pixel.Red - color.R) + Math.Abs(pixel.Green - color.G) + Math.Abs(pixel.Blue - color.B);

    private static (string Output, string Label) EvidenceLocation()
    {
        string output = Environment.GetEnvironmentVariable("FLOATCANVAS_CORNER_FRAME_DIR") ?? Path.Combine(Path.GetTempPath(), "FloatCanvas-corner-frame");
        string label = Environment.GetEnvironmentVariable("FLOATCANVAS_CORNER_FRAME_LABEL") ?? "current";
        if (label.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("The evidence label must be a filename-safe short label.");
        Directory.CreateDirectory(output); return (output, label);
    }

    private static void SaveClient(Imaging.BitmapSource bitmap, string path)
    { var png = new Imaging.PngBitmapEncoder(); png.Frames.Add(Imaging.BitmapFrame.Create(bitmap)); using var stream = File.Create(path); png.Save(stream); }

}
