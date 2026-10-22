using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatCanvas.App;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

internal static class Preview4SettingsTests
{
    internal static void Run(Action<string, bool> check)
    {
        check("floating logo defaults on for new and legacy settings", new UserSettings().ShowPassThroughLauncher &&
            JsonSerializer.Deserialize<UserSettings>("{}")!.ShowPassThroughLauncher);
        foreach (bool enabled in new[] { false, true })
        {
            var settings = new UserSettings { ShowPassThroughLauncher = enabled, ToolbarX = 999, ToolbarY = 888 };
            var saved = JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(settings))!;
            check($"mascot preference {enabled} persists while window positions reset", saved.ShowPassThroughLauncher == enabled && saved.ToolbarX is null && saved.ToolbarY is null);
        }
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (double scale in new[] { 1d, 1.5, 2d })
        foreach (double mascotScale in new[] { 1d, 1.5, 2d })
        {
            var origin = new OverlayPoint(-1920, 75);
            var position = PassThroughLauncherWindow.BoundPosition(origin, new DpiScale(scale, scale), new DpiScale(mascotScale, mascotScale));
            check($"{scale * 100}% toolbar and {mascotScale * 100}% passive mascot preserve the bottom-right anchor without scaling desktop origin",
                position.X + 75 * mascotScale == origin.X + 100 * scale && position.Y + 75 * mascotScale == origin.Y + 100 * scale);
        }
        string previousLanguage = L.Preference;
        try
        {
            foreach (string language in new[] { "zh-CN", "en" })
            foreach (string theme in new[] { "light", "dark" })
            {
                L.Initialize(language); AppTheme.Apply(theme);
                var window = new AppSettingsWindow();
                var section = window.AddSection(L.Get("Settings.General"), L.Get("Settings.GeneralHint"));
                for (int i = 0; i < 15; i++)
                    AppSettingsWindow.AddSetting(section, L.Get("History.Limit"), new TextBox { Text = "60" }, L.Get("History.LimitHint"));
                var content = (Grid)window.Content;
                content.Measure(new Size(640, 280)); content.Arrange(new Rect(0, 0, 640, 280)); content.UpdateLayout();
                var button = window.SaveButton;
                var bounds = new Rect(button.TranslatePoint(new Point(), content), button.RenderSize);
                var scroll = SettingsWindowFixture.Descendants(window).OfType<ScrollViewer>().Single();
                check($"{language} {theme} settings keep Save outside the overflowing content", scroll.ScrollableHeight > 0 && bounds.Bottom <= 280 && bounds.Top >= scroll.RenderSize.Height);
                scroll.ScrollToEnd(); content.UpdateLayout();
                check($"{language} {theme} scrolling never moves Save", button.TranslatePoint(new Point(), content) == bounds.TopLeft);
                window.Close();
                VerifyMascot(check, language, theme);
            }
        }
        finally { L.Initialize(previousLanguage); AppTheme.Apply("light"); }
    }

    private static void VerifyMascot(Action<string, bool> check, string language, string theme)
    {
        var window = new PassThroughLauncherWindow();
        try
        {
            var surface = (Grid)window.Content;
            surface.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            check($"{language} {theme} passive mascot measures at 75 DIP including its status label", surface.DesiredSize == new Size(75, 75) && window.Width == 75 && window.Height == 75);
            surface.Arrange(new Rect(0, 0, 75, 75)); surface.UpdateLayout();
            var badge = surface.Children.OfType<Border>().Single();
            var label = (TextBlock)badge.Child;
            var badgeBounds = new Rect(badge.TranslatePoint(new Point(), surface), badge.RenderSize);
            WritePreview(surface, language, theme, true);
            check($"{language} {theme} passive status is localized and uses the theme's text contrast",
                label.Text == (language == "en" ? "Pass-through" : "穿透") &&
                ((SolidColorBrush)label.Foreground).Color == ((SolidColorBrush)Application.Current.Resources["CanvasText"]).Color &&
                ((SolidColorBrush)badge.Background).Color == ((SolidColorBrush)Application.Current.Resources["CanvasSurface"]).Color);
            check($"{language} {theme} passive status text is not clipped", label.ActualWidth + 1e-6 >= label.DesiredSize.Width);
            check($"{language} {theme} passive status fits inside the smaller window", new Rect(0, 0, 75, 75).Contains(badgeBounds));
            check($"{language} {theme} status label adds no button or input target", !badge.IsHitTestVisible &&
                SettingsWindowFixture.Descendants(surface).OfType<Button>().SequenceEqual(new[] { window.Launcher }));
            check($"{language} {theme} launcher hit surface shrinks with its artwork",
                window.Launcher.TransformToAncestor(surface).TransformBounds(new Rect(window.Launcher.RenderSize)) == new Rect(0, 0, 75, 75));
            foreach (bool visible in new[] { true, false })
            {
                window.Launcher.SetCanvasVisible(visible);
                check($"{language} {theme} passive mascot preserves the original {(visible ? "open" : "closed")} eye source",
                    ReferenceEquals(((Image)window.Launcher.Content).Source, visible ? AppIcon.ToolbarImage : AppIcon.HiddenToolbarImage));
                if (!visible) WritePreview(surface, language, theme, false);
            }
        }
        finally { window.Close(); }
    }

    private static void WritePreview(FrameworkElement surface, string language, string theme, bool visible)
    {
        if (Environment.GetEnvironmentVariable("FLOATCANVAS_LAUNCHER_PREVIEW_DIR") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory); surface.UpdateLayout();
        foreach (double scale in new[] { 1d, 1.5, 2d })
        {
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(75 * scale), (int)Math.Ceiling(75 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string path = Path.Combine(directory, $"passive-{language}-{theme}-{(visible ? "open" : "closed")}-{scale * 100:0}.png");
            using var file = File.Create(path); encoder.Save(file);
            Console.WriteLine("LAUNCHER_RENDER " + path);
        }
    }
}
