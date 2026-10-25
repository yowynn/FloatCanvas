using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed class WorkspaceHistoryWindow : Window
{
    private readonly WorkspaceRecoveryHistory history;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim previewGate = new(1, 1);
    private readonly ListBox list = new() { MinHeight = 0, Margin = new(0, 0, 0, 12), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    internal WorkspaceRecoveryEntry? SelectedEntry => list.SelectedItem as WorkspaceRecoveryEntry;

    internal WorkspaceHistoryWindow(WorkspaceRecoveryHistory history, IReadOnlyList<WorkspaceRecoveryEntry>? entries = null)
    {
        this.history = history;
        Title = L.Get("Recovery.HistoryTitle"); Width = 620; Height = 560; MinHeight = 220; MinWidth = 280;
        MaxHeight = Math.Max(220, SystemParameters.WorkArea.Height); Height = Math.Min(Height, MaxHeight);
        ShowInTaskbar = false; Topmost = true; ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Icon = AppIcon.WindowImage;
        var panel = new Grid { Margin = WindowLayout.ContentMargin(18, 18) };
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto }); panel.RowDefinitions.Add(new()); panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var hint = new TextBlock { Text = L.Get("Recovery.HistoryHint"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) };
        panel.Children.Add(hint);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetCanContentScroll(list, true); VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        var factory = new FrameworkElementFactory(typeof(ContentControl));
        factory.SetValue(FrameworkElement.MinHeightProperty, 106d);
        factory.SetBinding(ContentControl.ContentProperty, new Binding { Converter = new RowConverter(this) });
        list.ItemTemplate = new DataTemplate { VisualTree = factory };
        Grid.SetRow(list, 1); panel.Children.Add(list);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var restore = new Button { Content = L.Get("Recovery.Restore"), IsDefault = true, MinWidth = 90,
            IsEnabled = false, Padding = new(12, 6, 12, 6), Margin = new(0, 0, 8, 0) };
        restore.Click += (_, _) => SessionWindowCoordinator.SetModalResult(this, SelectedEntry is not null);
        list.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton != System.Windows.Input.MouseButton.Left || !restore.IsEnabled ||
                e.OriginalSource is not DependencyObject source || ItemsControl.ContainerFromElement(list, source) is not ListBoxItem) return;
            e.Handled = true;
            restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, restore));
        };
        list.SelectionChanged += (_, _) => restore.IsEnabled = SelectedEntry is not null;
        buttons.Children.Add(restore);
        buttons.Children.Add(new Button { Content = L.Get("Recovery.Cancel"), IsCancel = true, MinWidth = 90, Padding = new(12, 6, 12, 6) });
        Grid.SetRow(buttons, 2); panel.Children.Add(buttons); Content = panel;
        AppWindowChrome.ApplyDialog(this);
        Closed += (_, _) => lifetime.Cancel();
        if (entries is not null) { list.ItemsSource = entries; list.SelectedIndex = 0; }
        else Loaded += async (_, _) =>
        {
            hint.Text = L.Get("Recovery.PreviewLoading");
            try
            {
                var loaded = await history.GetEntriesAsync(lifetime.Token);
                if (lifetime.IsCancellationRequested) return;
                list.ItemsSource = loaded; list.SelectedIndex = 0;
                hint.Text = L.Get(loaded.Count == 0 ? "Recovery.NoHistory" : "Recovery.HistoryHint");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!lifetime.IsCancellationRequested) hint.Text = "⚠ " + ex.Message; }
        };
    }

    private sealed class RowConverter(WorkspaceHistoryWindow owner) : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            value is WorkspaceRecoveryEntry entry ? owner.CreateItem(entry) : null;
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
    }

    private Grid CreateItem(WorkspaceRecoveryEntry entry)
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
        text.Children.Add(new TextBlock { Text = entry.SourceFile is not null ? (entry.IsCurrent ? L.Get("Recovery.LatestWorkspace") + " · " : "") + Path.GetFileName(entry.SourceFile) : L.Get(entry.IsCurrent ? "Recovery.LatestWorkspace" : "Recovery.HistoryWorkspace"), Margin = new(0, 7, 0, 0), Opacity = .7 });
        Grid.SetColumn(text, 1); row.Children.Add(text);
        row.Margin = new(10, 8, 10, 8);
        CancellationTokenSource? previewLifetime = null;
        row.Loaded += async (_, _) =>
        {
            previewLifetime?.Cancel(); previewLifetime?.Dispose();
            previewLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            await LoadPreviewAsync(entry, image, placeholder, previewLifetime.Token);
        };
        row.Unloaded += (_, _) => { previewLifetime?.Cancel(); previewLifetime?.Dispose(); previewLifetime = null; };
        return row;
    }

    private async Task LoadPreviewAsync(WorkspaceRecoveryEntry entry, Image image, TextBlock placeholder, CancellationToken token)
    {
        try
        {
            await previewGate.WaitAsync(token);
            byte[]? bytes;
            try { bytes = await history.LoadPreviewAsync(entry, token); }
            finally { previewGate.Release(); }
            token.ThrowIfCancellationRequested();
            if (bytes is null) { placeholder.Text = L.Get("Recovery.NoPreview"); return; }
            _ = FloatCanvas.Rendering.ImageDecodeBudget.ReadPixelCount(bytes);
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
