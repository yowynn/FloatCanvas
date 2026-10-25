using System.Windows;
using System.Windows.Controls;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal static class DiagnosticExportWindow
{
    internal static void ShowFor(Window? owner, bool enhanced)
    {
        var panel = new Grid { Margin = WindowLayout.ContentMargin(16, 16) };
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto }); panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new()); panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var hint = new TextBlock { Text = L.Get("Engineering.DiagnosticHint"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) }; panel.Children.Add(hint);
        var logs = new CheckBox { Content = L.Get("Engineering.IncludeLogs"), IsChecked = true, Margin = new(0, 0, 0, 10) }; Grid.SetRow(logs, 1); panel.Children.Add(logs);
        var preview = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(preview, 2); panel.Children.Add(preview);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 10, 0, 0) }; Grid.SetRow(footer, 3); panel.Children.Add(footer);
        var export = new Button { Content = L.Get("Engineering.Export"), Margin = new(0, 0, 8, 0) }; footer.Children.Add(export);
        footer.Children.Add(new Button { Content = L.Get("Dialog.Cancel"), IsCancel = true });
        var window = new Window { Title = L.Get("Engineering.ExportDiagnostics"), Content = panel, Width = 700, Height = 560, MinWidth = 300, MinHeight = 240,
            MaxWidth = SystemParameters.WorkArea.Width, MaxHeight = SystemParameters.WorkArea.Height, Owner = owner, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Dictionary<string, string> entries = [];
        async Task Refresh()
        {
            export.IsEnabled = false; logs.IsEnabled = false;
            try
            {
                bool include = logs.IsChecked == true;
                entries = await Task.Run(() => DiagnosticBundle.Prepare(ApplicationPaths.ProfileDirectory, enhanced, include));
                preview.Text = string.Join("\n\n", entries.Select(entry => "--- " + entry.Key + " ---\n" + entry.Value)); export.IsEnabled = true;
            }
            catch (Exception error) { preview.Text = error.Message; }
            finally { logs.IsEnabled = true; }
        }
        logs.Click += async (_, _) => await Refresh(); window.Loaded += async (_, _) => await Refresh();
        export.Click += async (_, _) =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "ZIP (*.zip)|*.zip", FileName = "FloatCanvas-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".zip", AddExtension = true, DefaultExt = ".zip" };
            if (dialog.ShowDialog(window) != true) return;
            export.IsEnabled = false; logs.IsEnabled = false;
            var snapshot = entries;
            try { await Task.Run(() => DiagnosticBundle.Export(dialog.FileName, snapshot)); AppDialog.Show(window, L.Get("Engineering.ExportComplete"), window.Title, MessageBoxButton.OK, MessageBoxImage.Information); }
            catch (Exception error) { AppDialog.Show(window, error.Message, L.Get("App.OperationFailed"), MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { export.IsEnabled = true; logs.IsEnabled = true; }
        };
        AppWindowChrome.ApplyDialog(window); SessionWindowCoordinator.ShowModal(window);
    }
}
