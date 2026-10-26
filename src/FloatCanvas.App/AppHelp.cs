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
        var window = new ReportIssueWindow(false, () => ExternalLink.Open("https://github.com/yowynn/FloatCanvas/issues/new"), regular: true);
        OverlayWindowScope.UseRegularWindow(window); AppWindowChrome.ApplyDialog(window); window.Show();
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
    internal static void ShowText(string title, string text, Window? owner = null)
    {
        owner ??= Application.Current?.Windows.Cast<Window>().FirstOrDefault(window => window.IsActive);
        bool regular = owner is null || !SessionWindowCoordinator.IsEditingWindow(owner);
        var window = CreateTextWindow(title, text, regular: regular);
        window.Owner = owner;
        if (owner is null || OverlayWindowScope.IsRegularWindow(owner)) OverlayWindowScope.UseRegularWindow(window);
        AppWindowChrome.ApplyDialog(window); SessionWindowCoordinator.ShowModal(window);
    }

    internal static Window CreateTextWindow(string title, string text, Action<string>? openLink = null, bool regular = false)
    {
        var content = new Grid { Margin = WindowLayout.ContentMargin(16, 16) };
        content.RowDefinitions.Add(new()); content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var reader = new MarkdownView { Text = text };
        if (openLink is not null) reader.OpenLink = openLink;
        content.Children.Add(reader);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 10, 0, 0) };
        var copy = new Button { Content = L.Get("Audit.Copy"), Margin = new(0, 0, 8, 0) };
        copy.Click += (_, _) => { try { Clipboard.SetText(reader.Text); } catch (System.Runtime.InteropServices.COMException) { } }; buttons.Children.Add(copy);
        var close = new Button { Content = L.Get("Dialog.Cancel"), IsCancel = true }; buttons.Children.Add(close); Grid.SetRow(buttons, 1); content.Children.Add(buttons);
        var window = new Window { Title = title, Content = content, Width = 760, Height = 600,
            MinWidth = 280, MinHeight = 220, MaxHeight = Math.Max(220, SystemParameters.WorkArea.Height), ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen };
        if (regular) OverlayWindowScope.UseRegularWindow(window);
        close.Click += (_, _) => SessionWindowCoordinator.Close(window);
        return window;
    }
}
