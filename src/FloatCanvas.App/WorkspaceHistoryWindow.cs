using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed class WorkspaceHistoryWindow : Window
{
    private readonly WorkspaceRecoveryHistory history;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim previewGate = new(1, 1);
    private readonly ListBox list = new() { MinHeight = 110, MaxHeight = 420, Margin = new(0, 0, 0, 14) };

    internal WorkspaceRecoveryEntry? SelectedEntry => (list.SelectedItem as ListBoxItem)?.Tag as WorkspaceRecoveryEntry;

    internal WorkspaceHistoryWindow(WorkspaceRecoveryHistory history, IReadOnlyList<WorkspaceRecoveryEntry> entries)
    {
        this.history = history;
        Title = L.Get("Recovery.HistoryTitle"); Width = 620; SizeToContent = SizeToContent.Height;
        ShowInTaskbar = false; Topmost = true; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Icon = AppIcon.WindowImage;
        var panel = new StackPanel { Margin = new(18) };
        panel.Children.Add(new TextBlock { Text = L.Get("Recovery.HistoryHint"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) });
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        foreach (var entry in entries) list.Items.Add(CreateItem(entry));
        list.SelectedIndex = 0;
        panel.Children.Add(list);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var restore = new Button { Content = L.Get("Recovery.Restore"), IsDefault = true, MinWidth = 90,
            Padding = new(12, 6, 12, 6), Margin = new(0, 0, 8, 0) };
        restore.Click += (_, _) => DialogResult = SelectedEntry is not null;
        buttons.Children.Add(restore);
        buttons.Children.Add(new Button { Content = L.Get("Recovery.Cancel"), IsCancel = true, MinWidth = 90, Padding = new(12, 6, 12, 6) });
        panel.Children.Add(buttons);
        Content = panel;
        AppWindowChrome.ApplyDialog(this);
        Closed += (_, _) => lifetime.Cancel();
    }

    private ListBoxItem CreateItem(WorkspaceRecoveryEntry entry)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new(160) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1, GridUnitType.Star) });
        var thumbnail = ColorPickerPanel.CreateSwatch(0);
        thumbnail.Width = 160; thumbnail.Height = 90;
        var image = new Image { Stretch = Stretch.Uniform };
        var placeholder = new TextBlock { Text = L.Get("Recovery.PreviewLoading"), FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(75, 81, 89)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        thumbnail.Children.Add(image); thumbnail.Children.Add(placeholder); row.Children.Add(thumbnail);
        var text = new StackPanel { Margin = new(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = entry.SavedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), FontSize = 14, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = L.Get(entry.IsCurrent ? "Recovery.LatestWorkspace" : "Recovery.HistoryWorkspace"), Margin = new(0, 7, 0, 0), Opacity = .7 });
        Grid.SetColumn(text, 1); row.Children.Add(text);
        var item = new ListBoxItem { Tag = entry, Content = row, Padding = new(10, 8, 10, 8), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        bool requested = false;
        item.Loaded += async (_, _) =>
        {
            if (requested) return;
            requested = true;
            await LoadPreviewAsync(entry, image, placeholder);
        };
        return item;
    }

    private async Task LoadPreviewAsync(WorkspaceRecoveryEntry entry, Image image, TextBlock placeholder)
    {
        var token = lifetime.Token;
        try
        {
            await previewGate.WaitAsync(token);
            byte[]? bytes;
            try { bytes = await history.LoadPreviewAsync(entry, token); }
            finally { previewGate.Release(); }
            token.ThrowIfCancellationRequested();
            if (bytes is null) { placeholder.Text = L.Get("Recovery.NoPreview"); return; }
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            // Stored thumbnails are already bounded. A fixed decode width would greatly
            // upscale a very narrow portrait image instead of limiting its memory.
            bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            image.Source = bitmap; placeholder.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
            FormatException or NotSupportedException or InvalidOperationException or ArgumentException or OverflowException or KeyNotFoundException)
        {
            if (!token.IsCancellationRequested) placeholder.Text = L.Get("Recovery.NoPreview");
        }
    }
}
