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
        foreach (uint dpi in new uint[] { 96,144,192 })
        {
            var monitor=new MonitorInfo("left",new(-1920,0,1920,1080),new(-1920,0,1920,1040),dpi,dpi,false);
            var passive=SessionWindowLayout.ToolbarOrigin(monitor,94,94,-80,200);
            var editing=SessionWindowLayout.ToolbarOrigin(monitor,680,95,passive.X,passive.Y);
            check($"{dpi} DPI entering editing near the right edge fits the whole toolbar",editing.X<passive.X&&editing.X+680*dpi/96d<=monitor.Bounds.Right&&editing.Y==passive.Y);
            var returned=SessionWindowLayout.ToolbarOrigin(monitor,94,94,editing.X,editing.Y);
            check($"{dpi} DPI passive mode retains the fitted shared origin instead of restoring a detached position",returned==editing);
            var normal=SessionWindowLayout.ToolbarOrigin(monitor,680,95,-1800,200);
            check($"{dpi} DPI ordinary shared positions do not move on mode changes",normal==new OverlayPoint(-1800,200)&&SessionWindowLayout.ToolbarOrigin(monitor,94,94,normal.X,normal.Y)==normal);
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
            check($"{language} {theme} passive and editing mascots both measure at 94 DIP", surface.DesiredSize == new Size(94, 94) && window.Width == 94 && window.Height == 94&&window.Launcher.LayoutTransform==Transform.Identity);
            surface.Arrange(new Rect(0, 0, 94, 94)); surface.UpdateLayout();
            WritePreview(surface, language, theme, true);
            check($"{language} {theme} crown artwork conveys pass-through without a text overlay", surface.Children.Count == 1 &&
                SettingsWindowFixture.Descendants(surface).OfType<Button>().SequenceEqual(new[] { window.Launcher }));
            check($"{language} {theme} passive hit surface matches the editing artwork",
                window.Launcher.TransformToAncestor(surface).TransformBounds(new Rect(window.Launcher.RenderSize)) == new Rect(0, 0, 94, 94));
            foreach (bool visible in new[] { true, false })
            {
                window.Launcher.SetCanvasVisible(visible);
                check($"{language} {theme} passive mascot uses its held-crown {(visible ? "open" : "closed")} eye source",
                    ReferenceEquals(((Image)window.Launcher.Content).Source, visible ? AppIcon.PassThroughToolbarImage : AppIcon.HiddenPassThroughToolbarImage));
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
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(94 * scale), (int)Math.Ceiling(94 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string path = Path.Combine(directory, $"passive-{language}-{theme}-{(visible ? "open" : "closed")}-{scale * 100:0}.png");
            using var file = File.Create(path); encoder.Save(file);
            Console.WriteLine("LAUNCHER_RENDER " + path);
        }
    }
}
