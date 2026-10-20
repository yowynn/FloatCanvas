using System.IO;
using System.Text.Json;
using FloatCanvas.App;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;

internal static class StartupDialogTests
{
    internal static void Run(Action<string, bool> check)
    {
        uint native = AppDialog.NativeFallbackFlags(0, Wpf.MessageBoxButton.OKCancel, Wpf.MessageBoxImage.Error);
        check("native startup fallback is topmost and task-modal when no owner exists", (native & 0x00042000U) == 0x00042000U);
        check("native startup fallback retains requested buttons and error icon", (native & 0xFFU) == 0x11U);
        check("native owned fallback is topmost without disabling unrelated thread windows", AppDialog.NativeFallbackFlags((nint)1, Wpf.MessageBoxButton.YesNo, Wpf.MessageBoxImage.Warning) == 0x00040034U);
        if (Wpf.Application.Current is null)
        {
            var early = AppDialog.Create(null, "Readable argument failure", "Startup").Window;
            VerifyDialog(check, "before Application exists", early, Media.Color.FromRgb(0xF8, 0xF9, 0xFB));
            check("early error dialog does not create an Application", Wpf.Application.Current is null);
            early.Close();
        }
        var app = Wpf.Application.Current ?? new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
        var previousResources = app.Resources;
        string previousProfile = ApplicationPaths.ProfileDirectory;
        string profile = Path.Combine(Path.GetTempPath(), "FloatCanvas-StartupDialogTests", Guid.NewGuid().ToString("N"));
        ApplicationPaths.Configure(["--profile", profile]);
        try
        {
            foreach (string mode in new[] { "light", "dark" })
            {
                app.Resources = new Wpf.ResourceDictionary();
                new UserSettings { Theme = mode, Language = "en" }.Save();
                AppTheme.InitializeStartup();
                var color = mode == "light" ? Media.Color.FromRgb(0xF8, 0xF9, 0xFB) : Media.Color.FromRgb(0x24, 0x27, 0x2C);
                check(mode + " primary startup prepares palette before session construction", ((Media.SolidColorBrush)app.Resources["CanvasSurface"]).Color == color);
                var dialog = AppDialog.Create(null, "Readable startup failure", "Startup", Wpf.MessageBoxButton.OKCancel).Window;
                check(mode + " complete theme remains global rather than shadowed by fallback", !dialog.Resources.Contains("CanvasSurface") && !dialog.Resources.Contains("CanvasText"));
                VerifyDialog(check, mode + " startup dialog", dialog, color);
                AppTheme.Apply(mode == "light" ? "dark" : "light");
                var changed = ((Media.SolidColorBrush)app.Resources["CanvasSurface"]).Color;
                check(mode + " dialog retains dynamic palette updates", ((Media.SolidColorBrush)dialog.Background).Color == changed);
                dialog.Close();
            }

            app.Resources = new Wpf.ResourceDictionary();
            app.Resources["CanvasSurface"] = new Media.SolidColorBrush(Media.Color.FromRgb(0x24, 0x27, 0x2C));
            var partial = AppDialog.Create(null, "Readable partial bootstrap failure", "Startup").Window;
            VerifyDialog(check, "partial dark palette", partial, Media.Color.FromRgb(0xF8, 0xF9, 0xFB));
            check("partial dark palette is replaced atomically with readable fallback text", ((Media.SolidColorBrush)partial.Foreground).Color == Media.Color.FromRgb(0x25, 0x2B, 0x36));
            partial.Close();

            app.Resources = new Wpf.ResourceDictionary();
            File.WriteAllText(Path.Combine(profile, "settings.json"), "{invalid settings");
            Exception? original = null;
            try { AppTheme.InitializeStartup(); } catch (JsonException ex) { original = ex; }
            check("malformed settings remain a startup error rather than silently overwritten", original is not null && File.ReadAllText(Path.Combine(profile, "settings.json")) == "{invalid settings");
            var failed = AppDialog.Create(null, original!.ToString(), "Startup").Window;
            VerifyDialog(check, "failed theme bootstrap", failed, Media.Color.FromRgb(0xF8, 0xF9, 0xFB));
            var panel = (Controls.DockPanel)failed.Content;
            var body = (Controls.TextBlock)((Controls.ScrollViewer)panel.Children[1]).Content;
            check("fallback dialog keeps the original exception text", body.Text == original.ToString());
            failed.Close();
            check("startup dialog tests never show or create native windows", app.Windows.Cast<Wpf.Window>().All(window => !window.IsVisible && new System.Windows.Interop.WindowInteropHelper(window).Handle == 0));
        }
        finally
        {
            app.Resources = previousResources;
            ApplicationPaths.Configure(["--profile", previousProfile]);
        }
    }

    private static void VerifyDialog(Action<string, bool> check, string name, Wpf.Window window, Media.Color expectedBackground)
    {
        check(name + " has an opaque explicit window surface", window.Background is Media.SolidColorBrush { Color.A: 255 } background && background.Color == expectedBackground);
        var foreground = ((Media.SolidColorBrush)window.Foreground).Color;
        check(name + " uses contrasting foreground and background", Contrast(foreground, expectedBackground) >= 7);
        var content = (Controls.DockPanel)window.Content;
        var buttons = ((Controls.StackPanel)content.Children[0]).Children.Cast<Controls.Button>().ToArray();
        check(name + " buttons have explicit readable colors", buttons.All(button => button.Background is Media.SolidColorBrush bg && button.Foreground is Media.SolidColorBrush fg && Contrast(bg.Color, fg.Color) >= 7));
        window.Content = null;
        var surface = new Controls.Border { Width = 460, Height = 240, Background = window.Background, Child = content };
        foreach (object key in window.Resources.Keys) surface.Resources[key] = window.Resources[key];
        surface.Measure(new Wpf.Size(460, 240)); surface.Arrange(new Wpf.Rect(0, 0, 460, 240)); surface.UpdateLayout();
        var bitmap = new Imaging.RenderTargetBitmap(460, 240, 96, 96, Media.PixelFormats.Pbgra32);
        bitmap.Render(surface);
        byte[] pixels = new byte[460 * 240 * 4]; bitmap.CopyPixels(pixels, 460 * 4, 0);
        check(name + " paints opaque background pixels", pixels[3] == 255 && pixels[2] == expectedBackground.R && pixels[1] == expectedBackground.G && pixels[0] == expectedBackground.B);
        int textPixels = 0;
        for (int i = 0; i < pixels.Length; i += 4)
            if (pixels[i + 3] == 255 && Math.Abs(pixels[i + 2] - foreground.R) < 20 && Math.Abs(pixels[i + 1] - foreground.G) < 20 && Math.Abs(pixels[i] - foreground.B) < 20) textPixels++;
        check(name + " paints readable text pixels", textPixels > 15);
        surface.Child = null; window.Content = content;
    }

    private static double Contrast(Media.Color first, Media.Color second)
    {
        static double Channel(byte value) { double c = value / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        static double Luminance(Media.Color color) => .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
        double a = Luminance(first), b = Luminance(second); return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}
