using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FloatCanvas.App;
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
            }
        }
        finally { L.Initialize(previousLanguage); AppTheme.Apply("light"); }
    }
}
