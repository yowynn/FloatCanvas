using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed record WindowSourceCandidate(object Target, string Title, string ProcessName, string WindowClass, string? UnavailableReasonKey = null)
{
    public string Display => (string.IsNullOrWhiteSpace(Title) ? L.Get("WindowSource.Untitled") : Title) + "\n" + ProcessName;
}

/// <summary>Explicit, injected metadata selection. Creating or rendering this dialog never enumerates windows or captures pixels.</summary>
internal sealed class WindowSourcePickerWindow : Window
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<WindowSourceCandidate>>> refresh;
    private readonly TextBox search = new() { Height = PropertyPanelLayout.ControlHeight, MinHeight = PropertyPanelLayout.ControlHeight, Margin = new(0, 0, 8, 0) };
    private readonly ListBox candidates = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) };
    private readonly Button select = new() { Content = L.Get("WindowSource.Select"), IsDefault = true, MinHeight = PropertyPanelLayout.ControlHeight, MinWidth = 100, IsEnabled = false };
    private WindowSourceCandidate[] catalog = [];
    private CancellationTokenSource? request;
    private long generation;
    private bool closed, loading;
    private string? queryError;
    internal WindowSourceCandidate? SelectedCandidate { get; private set; }
    internal bool Accepted { get; private set; }

    internal WindowSourcePickerWindow(Func<CancellationToken, Task<IReadOnlyList<WindowSourceCandidate>>> refresh)
    {
        this.refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        Title = L.Get("WindowSource.PickerTitle"); Width = 620; Height = 490; MinWidth = 320; MinHeight = 340;
        ShowInTaskbar = false; Topmost = true; Icon = AppIcon.WindowImage; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var body = new DockPanel { Margin = WindowLayout.ContentMargin(16) };
        var hint = new TextBlock { Text = L.Get("WindowSource.PickerHint"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) };
        DockPanel.SetDock(hint, Dock.Top); body.Children.Add(hint);
        var header = new Grid { Margin = new(0, 0, 0, 8) }; header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var reload = new Button { Content = L.Get("WindowSource.Refresh"), MinHeight = PropertyPanelLayout.ControlHeight };
        header.Children.Add(search); header.Children.Add(reload); Grid.SetColumn(reload, 1); DockPanel.SetDock(header, Dock.Top); body.Children.Add(header);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = L.Get("Dialog.Cancel"), IsCancel = true, MinHeight = PropertyPanelLayout.ControlHeight, MinWidth = 96, Margin = new(0, 0, 8, 0) };
        buttons.Children.Add(cancel); buttons.Children.Add(select); DockPanel.SetDock(buttons, Dock.Bottom); body.Children.Add(buttons);
        DockPanel.SetDock(status, Dock.Bottom); body.Children.Add(status); body.Children.Add(candidates); Content = body;
        status.SetResourceReference(TextBlock.ForegroundProperty, "CanvasMuted");
        VirtualizingPanel.SetIsVirtualizing(candidates, true); VirtualizingPanel.SetVirtualizationMode(candidates, VirtualizationMode.Recycling); ScrollViewer.SetCanContentScroll(candidates, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(candidates, ScrollBarVisibility.Disabled);
        var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetBinding(TextBlock.TextProperty, new Binding(nameof(WindowSourceCandidate.Display)));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); candidates.ItemTemplate = new DataTemplate { VisualTree = text };
        var itemStyle = new Style(typeof(ListBoxItem), (Style)FindResource(typeof(ListBoxItem))); itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7)));
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        itemStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(nameof(WindowSourceCandidate.WindowClass)))); candidates.ItemContainerStyle = itemStyle;
        AutomationProperties.SetName(search, L.Get("WindowSource.Search")); AutomationProperties.SetName(candidates, L.Get("WindowSource.PickerTitle"));
        reload.Click += async (_, _) => await RefreshCandidatesAsync(); search.TextChanged += (_, _) => Filter();
        candidates.SelectionChanged += (_, _) => RefreshSelection();
        select.Click += (_, _) => { if (select.IsEnabled && SelectedCandidate is not null) { Accepted = true; SessionWindowCoordinator.Close(this); } };
        cancel.Click += (_, _) => SessionWindowCoordinator.Close(this);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { SessionWindowCoordinator.Close(this); e.Handled = true; } };
        Closed += (_, _) => { closed = true; generation++; request?.Cancel(); request?.Dispose(); request = null; };
        AppTheme.EnsureDialogPalette(this); AppWindowChrome.ApplyDialog(this); Filter();
    }

    internal Task RefreshCandidatesAsync(CancellationToken cancellationToken = default) =>
        Dispatcher.InvokeAsync(() => RefreshCandidatesCoreAsync(cancellationToken)).Task.Unwrap();

    private async Task RefreshCandidatesCoreAsync(CancellationToken cancellationToken)
    {
        if (closed) return;
        long expected = ++generation; request?.Cancel(); request?.Dispose();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); request = cancellation;
        loading = true; queryError = null; catalog = []; SelectedCandidate = null; Accepted = false; Filter();
        try
        {
            var result = await refresh(cancellation.Token);
            if (closed || expected != generation || cancellation.IsCancellationRequested) return;
            catalog = result.Take(4096).ToArray();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch
        {
            if (!closed && expected == generation) queryError = "WindowSource.QueryFailed";
            return;
        }
        finally
        {
            if (!closed && expected == generation) { loading = false; Filter(); }
            if (ReferenceEquals(request, cancellation)) request = null;
            cancellation.Dispose();
        }
    }

    private void Filter()
    {
        string query = search.Text.Trim();
        var visible = catalog.Where(item => query.Length == 0 || item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            item.ProcessName.Contains(query, StringComparison.CurrentCultureIgnoreCase) || item.WindowClass.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        candidates.ItemsSource = visible; RefreshSelection();
    }
    private void RefreshSelection()
    {
        SelectedCandidate = candidates.SelectedItem as WindowSourceCandidate;
        select.IsEnabled = !loading && SelectedCandidate is { UnavailableReasonKey: null };
        string? key = loading ? "WindowSource.Loading" : queryError ?? SelectedCandidate?.UnavailableReasonKey ??
            (catalog.Length == 0 ? "WindowSource.NoWindows" : candidates.Items.Count == 0 ? "WindowSource.NoMatches" : null);
        status.Text = key is null ? "" : L.Get(key); status.Visibility = key is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
