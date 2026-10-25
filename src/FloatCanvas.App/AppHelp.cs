using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal static class AppHelp
{
    internal static void OpenGuide() => ShowGuide(false);
    internal static void OpenShortcuts() => ShowGuide(true);
    internal static void ReportIssue()
    {
        string version = typeof(AppHelp).Assembly.GetName().Version?.ToString() ?? "";
        string summary = L.Get("Audit.FeedbackHint") + "\n\nFloatCanvas " + version + "\nWindows " + Environment.OSVersion.Version;
        ShowText(L.Get("Audit.ReportIssue"), summary, () => Process.Start(new ProcessStartInfo("https://github.com/yowynn/FloatCanvas/issues/new") { UseShellExecute = true }));
    }
    private static void ShowGuide(bool shortcuts)
    {
        ShowText(L.Get(shortcuts ? "Settings.Shortcuts" : "Audit.UserGuide"), ReadGuide(shortcuts));
    }
    internal static string ReadGuide(bool shortcuts)
    {
        bool english = L.CurrentLanguage == "en";
        string name = english ? "User-Guide.en.md" : "User-Guide.md";
        string? path = new[] { Path.Combine(AppContext.BaseDirectory, name), Path.Combine(AppContext.BaseDirectory, "Docs", name) }.FirstOrDefault(File.Exists);
        string text = path is not null ? File.ReadAllText(path) : L.Get("Audit.GuideUnavailable");
        if (shortcuts && path is not null)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            int start = Array.FindIndex(lines, line => line.StartsWith("## ") && (line.Contains("快捷键") || line.Contains("shortcut", StringComparison.OrdinalIgnoreCase)));
            if (start >= 0) text = string.Join("\n", lines.Skip(start).TakeWhile((line, index) => index == 0 || !line.StartsWith("## ")));
            else
            {
                start = Array.FindIndex(lines, line => line.StartsWith("| ") && (line.Contains("快捷键") || line.Contains("shortcut", StringComparison.OrdinalIgnoreCase)));
                if (start >= 0) text = string.Join("\n", lines.Skip(start).TakeWhile(line => line.StartsWith('|')));
            }
        }
        return text;
    }
    internal static void ShowText(string title, string text, Action? open = null, Window? owner = null)
    {
        owner ??= Application.Current?.Windows.Cast<Window>().FirstOrDefault(window => window.IsActive);
        var content = new Grid { Margin = WindowLayout.ContentMargin(16, 16) };
        content.RowDefinitions.Add(new()); content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var reader = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        content.Children.Add(reader);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 10, 0, 0) };
        var copy = new Button { Content = L.Get("Audit.Copy"), Margin = new(0, 0, 8, 0) };
        copy.Click += (_, _) => { try { Clipboard.SetText(reader.Text); } catch (System.Runtime.InteropServices.COMException) { } }; buttons.Children.Add(copy);
        if (open is not null) { var action = new Button { Content = L.Get("Audit.OpenIssuePage"), Margin = new(0, 0, 8, 0) }; action.Click += (_, _) => open(); buttons.Children.Add(action); }
        buttons.Children.Add(new Button { Content = L.Get("Dialog.Cancel"), IsCancel = true }); Grid.SetRow(buttons, 1); content.Children.Add(buttons);
        var window = new Window { Title = title, Content = content, Owner = owner, Width = 660, Height = 560,
            MinWidth = 280, MinHeight = 220, MaxHeight = Math.Max(220, SystemParameters.WorkArea.Height), ShowInTaskbar = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner };
        AppWindowChrome.ApplyDialog(window); SessionWindowCoordinator.ShowModal(window);
    }
}
