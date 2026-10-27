using System.IO;
using System.Reflection;
using FloatCanvas.App;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;
using L = FloatCanvas.Localization.AppText;

internal static class Feedback3ColorTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Wpf.Application.Current ?? new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
        string output = Environment.GetEnvironmentVariable("FLOATCANVAS_COLOR3_PREVIEW_DIR") ?? Path.Combine(Path.GetTempPath(), "FloatCanvas-Color3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var library = new ColorLibrary(Path.Combine(output, "colors.json"));
        library.Remember(0xFF239B65); library.Pin(0xFF954ABE);
        foreach (string theme in new[] { "light", "dark" })
        {
            AppTheme.Apply(theme);
            var picker = new ColorPickerPanel(ColorRole.Foreground, library);
            picker.PrepareForShow();
            var host = new Wpf.Window { Content = picker, Width = 400, Height = 800 };
            picker.SetResourceReference(Controls.Control.BackgroundProperty, "CanvasSurface");
            picker.Measure(new Wpf.Size(400, 1000));
            picker.Arrange(new Wpf.Rect(0, 0, picker.DesiredSize.Width, picker.DesiredSize.Height));
            picker.UpdateLayout();
            foreach ((uint color, string title) in new[] { (0xFF239B65u, "#FF239B65 #FF239B65"), (0xFF954ABEu, "#FF954ABE #FF954ABE") })
            {
                var button = Descendants(picker).OfType<Controls.Button>().Single(b => Equals(b.ToolTip, title));
                var swatch = (Controls.Grid)button.Content;
                Console.WriteLine($"{theme} {title}: button={button.ActualWidth}x{button.ActualHeight}; swatch={swatch.ActualWidth}x{swatch.ActualHeight}");
                var pixels = new Imaging.RenderTargetBitmap((int)Math.Ceiling(button.ActualWidth), (int)Math.Ceiling(button.ActualHeight), 96, 96, Media.PixelFormats.Pbgra32);
                var drawing = new Media.DrawingVisual();
                using (var dc = drawing.RenderOpen()) dc.DrawRectangle(new Media.VisualBrush(button), null, new Wpf.Rect(0, 0, pixels.PixelWidth, pixels.PixelHeight));
                pixels.Render(drawing);
                byte[] sample = new byte[4]; pixels.CopyPixels(new Wpf.Int32Rect(pixels.PixelWidth / 2, pixels.PixelHeight / 2, 1, 1), sample, 4, 0);
                Console.WriteLine("sample BGRA=" + string.Join(",", sample));
                check(theme + " actual palette/history/pin swatch paints color " + color.ToString("X8"), swatch.ActualWidth >= 16 && swatch.ActualHeight >= 16 &&
                    Math.Abs(sample[0] - (byte)color) <= 2 && Math.Abs(sample[1] - (byte)(color >> 8)) <= 2 && Math.Abs(sample[2] - (byte)(color >> 16)) <= 2);
            }
            var bitmap = new Imaging.RenderTargetBitmap((int)Math.Ceiling(picker.ActualWidth), (int)Math.Ceiling(picker.ActualHeight), 96, 96, Media.PixelFormats.Pbgra32);
            bitmap.Render(picker);
            var encoder = new Imaging.PngBitmapEncoder(); encoder.Frames.Add(Imaging.BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(output, "colors-" + theme + ".png"))) encoder.Save(file);
            host.Content = null; host.Close();
        }
        var draft = new ColorPickerPanel(ColorRole.Foreground, library) { Value = 0x80336699 };
        draft.PrepareForShow();
        var spectrum = Field<ColorSpectrum>(draft, "spectrum");
        var hue = Field<Controls.Slider>(draft, "hue");
        var alpha = Field<Controls.Slider>(draft, "alpha");
        var hex = Field<Controls.TextBox>(draft, "hex");
        double originalAlpha = alpha.Value;
        hue.Value = 120;
        draft.PrepareForShow();
        check("inline hue changes the color draft while preserving alpha and original semantic color", draft.Value == 0x80336699 && alpha.Value == originalAlpha &&
            ColorLibrary.TryParseHtml(hex.Text, out uint greenDraft) && ((greenDraft >> 8) & 255) > ((greenDraft >> 16) & 255));
        uint chosen = 0; draft.ColorChosen += color => chosen = color;
        typeof(ColorPickerPanel).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(draft, null);
        check("inline color selection applies its hue and exact original alpha", (chosen >> 24) == 128 && ((chosen >> 8) & 255) > ((chosen >> 16) & 255));
        hue.Value = 360;
        draft.PrepareForShow();
        check("hue strip retains the red endpoint instead of jumping across the panel", hue.Value == 360 && spectrum.Hue == 360);
        check("spectrum covers the primary color corners", ColorSpectrum.ToColor(0, 1, 1) == 0xFFFF0000 && ColorSpectrum.ToColor(120, 1, 1) == 0xFF00FF00 &&
            ColorSpectrum.ToColor(240, 1, 1) == 0xFF0000FF && ColorSpectrum.ToColor(0, 0, 1) == 0xFFFFFFFF && ColorSpectrum.ToColor(0, 1, 0) == 0xFF000000);
        ToolPresetAndColorTests.Run(check);
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static IEnumerable<Wpf.DependencyObject> Descendants(Wpf.DependencyObject root)
    {
        yield return root;
        foreach (var child in Wpf.LogicalTreeHelper.GetChildren(root).OfType<Wpf.DependencyObject>())
            foreach (var descendant in Descendants(child)) yield return descendant;
    }
}
