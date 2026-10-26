using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed class ReportIssueWindow : Window
{
    private readonly Dictionary<string, string> entries;
    internal string Summary { get; }
    internal Button CopyButton { get; } = new() { Content = L.Get("Feedback.Copy") };
    internal Button OpenButton { get; } = new() { Content = L.Get("Audit.OpenIssuePage") };

    internal ReportIssueWindow(bool enhanced, Action openIssue, Action<string>? copyText = null, bool regular = false)
    {
        Title = L.Get("Audit.ReportIssue"); Width = 760; Height = 620; MinWidth = 440; MinHeight = 300;
        if (regular) OverlayWindowScope.UseRegularWindow(this);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        entries = DiagnosticBundle.Prepare(ApplicationPaths.ProfileDirectory, enhanced, false);
        using var environment = JsonDocument.Parse(entries["environment.json"]);
        var info = environment.RootElement;
        string system = $"FloatCanvas {info.GetProperty("Version").GetString()}\nWindows {info.GetProperty("OS").GetString()}\n{info.GetProperty("Runtime").GetString()}";
        Summary = "## " + L.Get("Feedback.System") + "\n" + system + "\n\n## " + L.Get("Feedback.Environment") + "\n```json\n" + entries["environment.json"] + "\n```";
        var body = new Grid { Margin = WindowLayout.ContentMargin(16, 16) };
        body.RowDefinitions.Add(new()); body.RowDefinitions.Add(new() { Height = GridLength.Auto }); body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var sections = new StackPanel();
        AddSection("Feedback.Steps", new MarkdownView { Text = L.Get("Feedback.Instructions"), Height = 230 }, true);
        AddSection("Feedback.System", new MarkdownView { Text = system, Height = 140 });
        var diagnostics = new StackPanel();
        diagnostics.Children.Add(new MarkdownView { Text = "```json\n" + entries["environment.json"] + "\n```", Height = 230 });
        var logs = new CheckBox { Content = L.Get("Engineering.IncludeLogs"), IsChecked = true, Margin = new Thickness(8) }; diagnostics.Children.Add(logs);
        var export = new Button { Content = L.Get("Engineering.Export"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(8) }; diagnostics.Children.Add(export);
        AddSection("Feedback.Environment", diagnostics);
        body.Children.Add(new ScrollViewer { Content = sections, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) }; status.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted");
        Grid.SetRow(status, 1); body.Children.Add(status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var button in new[] { CopyButton, OpenButton }) { button.Margin = new Thickness(0, 0, 8, 0); actions.Children.Add(button); }
        var close = new Button { Content = L.Get("Dialog.Cancel"), IsCancel = true }; actions.Children.Add(close);
        Grid.SetRow(actions, 2); body.Children.Add(actions); Content = body;
        close.Click += (_, _) => SessionWindowCoordinator.Close(this);
        bool Copy()
        {
            try { (copyText ?? Clipboard.SetText)(Summary); status.Text = L.Get("Feedback.Copied"); return true; }
            catch (System.Runtime.InteropServices.COMException error) { status.Text = error.Message; return false; }
        }
        CopyButton.Click += (_, _) => Copy();
        OpenButton.Click += (_, _) => { if (Copy()) openIssue(); };
        export.Click += async (_, _) =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = WindowTitles.Format(L.Get("Engineering.ExportDiagnostics"), OverlayWindowScope.IsRegularWindow(this)), Filter = "ZIP (*.zip)|*.zip", FileName = "FloatCanvas-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".zip", AddExtension = true, DefaultExt = ".zip" };
            if (SessionWindowCoordinator.ShowNativeModal(this, () => dialog.ShowDialog(this)) != true) return;
            export.IsEnabled = false;
            try
            {
                bool includeLogs = logs.IsChecked == true;
                await Task.Run(() => DiagnosticBundle.Export(dialog.FileName, DiagnosticBundle.Prepare(ApplicationPaths.ProfileDirectory, enhanced, includeLogs)));
                status.Text = L.Get("Engineering.ExportComplete");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { status.Text = error.Message; }
            finally { export.IsEnabled = true; }
        };

        void AddSection(string key, FrameworkElement content, bool expanded = false)
        {
            sections.Children.Add(new Expander { Header = L.Get(key), Content = content, IsExpanded = expanded,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(8) });
        }
    }
}
