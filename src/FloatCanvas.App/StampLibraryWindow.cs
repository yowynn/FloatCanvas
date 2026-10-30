using System.IO;
using FloatCanvas.Core;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;
using Input = System.Windows.Input;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed class StampLibraryWindow : Wpf.Window
{
    public Func<EditorSession,Task<StampRenderContext?>>? PrepareRendering { get; set; }
    internal const int DefaultTileColumns = 5;
    private const double TileContentWidth = 112, TileContentMargin = 4, TilePadding = 3, TileBorder = 1, TileMargin = 2;
    internal const double TileFootprint = TileContentWidth + 2 * (TileContentMargin + TilePadding + TileBorder + TileMargin);
    private const double BodyMargin = WindowLayout.ContentInset, ListPadding = 4, ListBorder = 1, ScrollbarWidth = WindowLayout.ScrollBarWidth, WindowBorder = 1;
    internal static double WidthForColumns(int columns, double dpiScale)
    {
        double Border(double value) => Math.Round(value * dpiScale) / dpiScale;
        double tile = Math.Ceiling((TileContentWidth + 2 * (TileContentMargin + TilePadding + Border(TileBorder) + TileMargin)) * dpiScale) / dpiScale;
        double edges = 2 * (BodyMargin + ListPadding + Border(ListBorder) + Border(WindowBorder)) + ScrollbarWidth;
        // Border layout rounds to physical pixels. One further physical pixel
        // prevents a floating-point remainder from wrapping the final full tile.
        return (Math.Ceiling((columns * tile + edges) * dpiScale) + 1) / dpiScale;
    }
    private readonly StampLibrary library;
    private readonly Func<EditorSession> getSession;
    private readonly Action<Guid> selectStamp;
    private readonly Action<Guid>? useSource;
    private readonly Func<bool> canMutate;
    private readonly Controls.TextBox search = new() { Height = 34, MinHeight = 34, MinWidth = 0, Padding = new(28, 5, 8, 5) };
    private readonly StampListBox grid = new() { HorizontalContentAlignment = Wpf.HorizontalAlignment.Stretch };
    private readonly Controls.TextBlock status = new() { TextWrapping = Wpf.TextWrapping.Wrap, Margin = new(0, 6, 0, 0), Visibility = Wpf.Visibility.Collapsed };
    private readonly Controls.WrapPanel slotShelf = new() { HorizontalAlignment = Wpf.HorizontalAlignment.Left, Margin = new(0, 0, 0, 6) };
    private readonly Dictionary<int, Controls.Button> slotButtons = [];
    private readonly Dictionary<int, Controls.Button> slotClearButtons = [];
    private readonly Dictionary<int, Controls.Border> slotFrames = [];
    private readonly Controls.WrapPanel actions = new() { VerticalAlignment = Wpf.VerticalAlignment.Center };
    private readonly Controls.DockPanel commandRow = new() { Margin = new(0, 0, 0, 8) };
    private readonly Controls.Button sourceButton;
    private readonly Dictionary<string, Controls.Button> commandButtons = [];
    private CancellationTokenSource thumbnails = new();
    private Guid? selected;
    private int? selectedSlot;
    private readonly System.Windows.Threading.DispatcherTimer searchDelay = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly SemaphoreSlim thumbnailGate = new(2, 2);
    private readonly Dictionary<(Guid Id, Guid Revision), Imaging.BitmapSource> thumbnailCache = [];
    private readonly Queue<(Guid Id, Guid Revision)> thumbnailOrder = new();
    private readonly Controls.TextBlock emptyText = new() { TextWrapping = Wpf.TextWrapping.Wrap, Margin = new(8) };
    private readonly Controls.Button emptyAction = new() { MinWidth = 100, Margin = new(8) };
    private bool busy, closed;
    private bool preparing, refreshPending = true;
    private string pendingStatus = "";
    private Wpf.Window? dialogOwner;
    public event Action<int>? CommandKey;

    public StampLibraryWindow(StampLibrary library, Func<EditorSession> getSession, Action<Guid> selectStamp, Func<bool>? canMutate = null, Action<Guid>? useSource = null)
    {
        this.library = library; this.getSession = getSession; this.selectStamp = selectStamp; this.canMutate = canMutate ?? (() => true); this.useSource = useSource;
        double initialScale = Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        Title = L.Get("Stamp.LibraryTitle"); Width = WidthForColumns(DefaultTileColumns, initialScale); Height = 650;
        MinWidth = WidthForColumns(DefaultTileColumns - 1, initialScale); MinHeight = 440;
        double initialWidth = Width;
        SourceInitialized += (_, _) =>
        {
            double scale = Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
            MinWidth = WidthForColumns(DefaultTileColumns - 1, scale);
            // Session-restored/user widths retain their existing value.
            if (Math.Abs(Width - initialWidth) < .01) Width = WidthForColumns(DefaultTileColumns, scale);
        };
        FontSize = 13; Topmost = true; Icon = AppIcon.WindowImage;
        var body = new Controls.DockPanel { Margin = WindowLayout.ContentMargin(16, 16) };
        var top = new Controls.StackPanel(); Controls.DockPanel.SetDock(top, Controls.Dock.Top); body.Children.Add(top);
        var searchBox = new Controls.Grid { Width = 220, HorizontalAlignment = Wpf.HorizontalAlignment.Right, VerticalAlignment = Wpf.VerticalAlignment.Center };
        search.Template = SearchTemplate();
        searchBox.Children.Add(search);
        var magnifier = new System.Windows.Shapes.Path { Width = 16, Height = 16, StrokeThickness = 1.5,
            StrokeStartLineCap = Media.PenLineCap.Round, StrokeEndLineCap = Media.PenLineCap.Round, Stretch = Media.Stretch.Uniform,
            Data = Media.Geometry.Parse("M11 11 L15 15 M12.5 7.5 A5 5 0 1 1 2.5 7.5 A5 5 0 1 1 12.5 7.5"),
            HorizontalAlignment = Wpf.HorizontalAlignment.Left, VerticalAlignment = Wpf.VerticalAlignment.Center, Margin = new(9, 0, 0, 0), IsHitTestVisible = false };
        magnifier.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "CanvasMuted"); searchBox.Children.Add(magnifier);
        Wpf.Automation.AutomationProperties.SetName(search, L.Get("Stamp.Search"));
        AddActionGroup(ActionButton("Stamp.SaveSelection", PanelActionIcon.Add, () => RunAsync(() => SaveAsync(overwrite: false)), "Ctrl+N"));
        sourceButton = ActionButton("Stamp.UseSource", PanelActionIcon.Source, UseSelectedSource);
        sourceButton.Visibility = useSource is null ? Wpf.Visibility.Collapsed : Wpf.Visibility.Visible;
        sourceButton.IsEnabled = false;
        AddActionGroup(ActionButton("Stamp.Use", PanelActionIcon.Use, UseSelected, "Enter"), sourceButton);
        AddActionGroup(ActionButton("Stamp.Rename", PanelActionIcon.Rename, () => RunAsync(RenameAsync), "F2"),
            ActionButton("Stamp.Overwrite", PanelActionIcon.Save, () => RunAsync(() => SaveAsync(overwrite: true))),
            ActionButton("Stamp.Delete", PanelActionIcon.Delete, () => RunAsync(DeleteAsync), "Delete"));
        Controls.DockPanel.SetDock(actions, Controls.Dock.Left); commandRow.Children.Add(actions); commandRow.Children.Add(searchBox); top.Children.Add(commandRow);
        top.Children.Add(slotShelf);
        Controls.DockPanel.SetDock(status, Controls.Dock.Bottom); body.Children.Add(status);
        var empty = new Controls.StackPanel(); empty.Children.Add(emptyText); empty.Children.Add(emptyAction);
        Controls.DockPanel.SetDock(empty, Controls.Dock.Top); body.Children.Add(empty);
        emptyAction.Click += (_, _) => { if (search.Text.Length > 0) search.Text = ""; else { CommandKey?.Invoke(0x56); Hide(); } };
        grid.SetResourceReference(StyleProperty, typeof(Controls.ListBox));
        grid.Padding = new(ListPadding); grid.BorderThickness = new(ListBorder);
        grid.ItemsPanel = new Controls.ItemsPanelTemplate(new Wpf.FrameworkElementFactory(typeof(VirtualizingTilePanel)));
        Controls.ScrollViewer.SetCanContentScroll(grid, true); Controls.VirtualizingPanel.SetIsVirtualizing(grid, true);
        Controls.VirtualizingPanel.SetVirtualizationMode(grid, Controls.VirtualizationMode.Recycling);
        grid.PrepareTile = PrepareTile;
        Controls.ScrollViewer.SetHorizontalScrollBarVisibility(grid, Controls.ScrollBarVisibility.Disabled);
        Controls.ScrollViewer.SetVerticalScrollBarVisibility(grid, Controls.ScrollBarVisibility.Visible);
        grid.SelectionChanged += (_, _) => { selected = (grid.SelectedItem as StampEntry)?.Id; RefreshActions(); };
        grid.MouseDoubleClick += (_, e) => { if (e.ChangedButton == Input.MouseButton.Left && selectedSlot is null) UseSelected(); };
        Wpf.Automation.AutomationProperties.SetName(grid, L.Get("Stamp.LibraryTitle"));
        body.Children.Add(grid); Content = body; AppWindowChrome.Apply(this);
        AppWindowChrome.SetHelp(this, L.Get("StampShelf.Help"), L.Get("StampShelf.HelpText"));
        PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Input.Key.System ? e.SystemKey : e.Key;
            var modifiers = Input.Keyboard.Modifiers;
            var source = e.OriginalSource as Wpf.DependencyObject;
            if (e.Handled || (modifiers & (Input.ModifierKeys.Alt | Input.ModifierKeys.Windows)) != 0 || EditorShortcutPolicy.OwnsMenu(source)) return;
            if (OwnsShortcut(key, modifiers, source))
            {
                e.Handled = true;
                if (!AppInputPolicy.IsRepeat(e)) HandleShortcut(key, modifiers, source);
                return;
            }
            // Ctrl+N belongs to this library even while search owns editing keys.
            if (key == Input.Key.N && modifiers == Input.ModifierKeys.Control) return;
            int command = Input.KeyInterop.VirtualKeyFromKey(key);
            bool localInput = IsTextInput(source) || IsTextInput(Input.Keyboard.FocusedElement as Wpf.DependencyObject);
            if (!EditorShortcutPolicy.ShouldRoute(command, modifiers, source, preserveListNavigation: true, localInput: localInput)) return;
            e.Handled = true;
            if (!closed && !busy && this.canMutate() && (!AppInputPolicy.IsRepeat(e) || EditorShortcutPolicy.AllowsRepeat(command, (FloatCanvas.Windows.OverlayModifiers)modifiers))) CommandKey?.Invoke(command);
        };
        searchDelay.Tick += (_, _) => { searchDelay.Stop(); Refresh(); };
        search.TextChanged += (_, _) => { searchDelay.Stop(); refreshPending = true; if (IsVisible) searchDelay.Start(); };
        library.Changed += OnLibraryChanged;
        Closed += (_, _) => { closed = true; searchDelay.Stop(); CloseTileMenus(); thumbnails.Cancel(); thumbnails.Dispose(); library.Changed -= OnLibraryChanged; };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { refreshPending = true; PrepareForShow(); }
            else { searchDelay.Stop(); CloseTileMenus(); thumbnails.Cancel(); refreshPending = true; }
        };
    }

    private sealed class StampListBox : Controls.ListBox
    {
        internal Action<Controls.ListBoxItem, StampEntry>? PrepareTile;
        protected override void PrepareContainerForItemOverride(Wpf.DependencyObject element, object item)
        {
            base.PrepareContainerForItemOverride(element, item);
            if (element is Controls.ListBoxItem container && item is StampEntry entry) PrepareTile?.Invoke(container, entry);
        }
        protected override void ClearContainerForItemOverride(Wpf.DependencyObject element, object item)
        {
            if (element is Controls.ListBoxItem container && container.Resources["thumbnail-lifetime"] is CancellationTokenSource lifetime) { lifetime.Cancel(); lifetime.Dispose(); container.Resources.Remove("thumbnail-lifetime"); }
            base.ClearContainerForItemOverride(element, item);
        }
    }

    private static Controls.ControlTemplate SearchTemplate()
    {
        // TextBox lays out its own padding inside PART_ContentHost. Applying the
        // same padding to the surrounding border would clip a compact search row.
        var chrome = new Wpf.FrameworkElementFactory(typeof(Controls.Border), "SearchChrome");
        chrome.SetValue(Controls.Border.CornerRadiusProperty, new Wpf.CornerRadius(6));
        foreach (var (target, source) in new[] { (Controls.Border.BackgroundProperty, "Background"),
            (Controls.Border.BorderBrushProperty, "BorderBrush"), (Controls.Border.BorderThicknessProperty, "BorderThickness") })
            chrome.SetBinding(target, new System.Windows.Data.Binding(source) { RelativeSource = new(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var host = new Wpf.FrameworkElementFactory(typeof(Controls.ScrollViewer), "PART_ContentHost");
        host.SetValue(Controls.ScrollViewer.FocusableProperty, false); chrome.AppendChild(host);
        var template = new Controls.ControlTemplate(typeof(Controls.TextBox)) { VisualTree = chrome };
        var focus = new Wpf.Trigger { Property = Controls.TextBox.IsKeyboardFocusWithinProperty, Value = true };
        focus.Setters.Add(new Wpf.Setter(Controls.Border.BorderBrushProperty, new Wpf.DynamicResourceExtension("CanvasAccent"), "SearchChrome")); template.Triggers.Add(focus);
        var disabled = new Wpf.Trigger { Property = Controls.TextBox.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Wpf.Setter(OpacityProperty, .45, "SearchChrome")); template.Triggers.Add(disabled);
        return template;
    }

    public void Refresh()
    {
        refreshPending = true;
        if (IsVisible) PrepareForShow();
    }

    internal void PrepareForShow()
    {
        if (closed) return;
        if (preparing || !refreshPending) return;
        preparing = true; refreshPending = false;
        try
        {
            thumbnails.Cancel(); thumbnails.Dispose(); thumbnails = new();
            var token = thumbnails.Token;
            var items = library.Items.Where(item => item.Name.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToArray();
            emptyText.Visibility = emptyAction.Visibility = items.Length == 0 ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
            if (items.Length == 0)
            {
                emptyText.Text = L.Get(library.Items.Count == 0 ? "Audit.EmptyStamps" : "Audit.NoStampMatches");
                emptyAction.Content = L.Get(search.Text.Length > 0 ? "Audit.ClearSearch" : "Audit.SelectObjects");
            }
            Guid? keep = selected;
            RefreshSlots(token);
            CloseTileMenus();
            grid.ItemsSource = items;
            grid.SelectedItem = items.FirstOrDefault(item => item.Id == keep);
            SetBusyControls(); ShowStatus(pendingStatus);
        }
        finally { preparing = false; }
    }

    private void PrepareTile(Controls.ListBoxItem item, StampEntry entry)
    {
        if (item.Resources["thumbnail-lifetime"] is CancellationTokenSource previous) { previous.Cancel(); previous.Dispose(); }
        var linked = CancellationTokenSource.CreateLinkedTokenSource(thumbnails.Token);
        item.Resources["thumbnail-lifetime"] = linked;
        item.Tag = entry.Id;
        int pinnedSlot = FirstPinnedSlot(entry.Id);
        string shortcut = pinnedSlot < 0 ? "" : ((pinnedSlot + 1) % 10).ToString();
        var tile = new Controls.StackPanel { Width = TileContentWidth, Margin = new Wpf.Thickness(TileContentMargin), ToolTip = StampPreviewTooltip.Create(library, entry, shortcut) };
        var artwork = new Controls.Grid();
        var image = new Controls.Image { Height = 82, Width = 100, Stretch = Media.Stretch.Uniform, Margin = new(0, 2, 0, 6) };
        artwork.Children.Add(image);
        var placeholder = new Controls.TextBlock { Text = L.Get("Recovery.PreviewLoading"), FontSize = 11, TextWrapping = Wpf.TextWrapping.Wrap, HorizontalAlignment = Wpf.HorizontalAlignment.Center, VerticalAlignment = Wpf.VerticalAlignment.Center };
        artwork.Children.Add(placeholder);
        if (pinnedSlot >= 0) artwork.Children.Add(Badge(shortcut, Wpf.HorizontalAlignment.Left, "Stamp.PinBadge"));
        tile.Children.Add(artwork);
        tile.Children.Add(new Controls.TextBlock { Text = entry.Name, TextTrimming = Wpf.TextTrimming.CharacterEllipsis, TextAlignment = Wpf.TextAlignment.Center });
        var mode = new Controls.TextBlock { Text = L.Get(entry.Mode == StampSaveMode.EditableGroup ? "StampShelf.Group" : "StampShelf.Raster"), FontSize = 11, TextAlignment = Wpf.TextAlignment.Center, Margin = new(0, 3, 0, 0), Tag = "Stamp.Mode" };
        mode.SetResourceReference(Controls.TextBlock.ForegroundProperty, "CanvasMuted"); tile.Children.Add(mode);
        item.Content = tile; item.Margin = new(TileMargin); item.Padding = new(TilePadding); item.BorderThickness = new(TileBorder);
        Wpf.Automation.AutomationProperties.SetName(item, entry.Name); item.ContextMenu = TileContextMenu(entry.Id);
        if (!item.Resources.Contains("tile-events"))
        {
            item.Resources["tile-events"] = true;
            item.PreviewMouseRightButtonDown += (_, _) => { if (!busy) grid.SelectedItem = grid.Items.Cast<StampEntry>().FirstOrDefault(value => Equals(item.Tag, value.Id)); };
            TileDragReorder.Attach(item, grid,
                (target, after) => { if (item.Tag is Guid id && target.Tag is Guid targetId) RunAsync(() => library.MoveAsync(id, targetId, after)); },
                () => slotButtons.Select(pair => new TileDragReorder.ExternalDropTarget(pair.Value, () => { if (item.Tag is Guid id) RunAsync(() => library.PinAsync(pair.Key, id)); })),
                clicked: () => { if (item.Tag is Guid id) ActivateTile(id); });
        }
        LoadThumbnail(entry, image, item, linked.Token, placeholder);
    }

    private IEnumerable<Controls.ListBoxItem> RealizedTiles() => grid.Items.Cast<StampEntry>().Select(entry => grid.ItemContainerGenerator.ContainerFromItem(entry)).OfType<Controls.ListBoxItem>();

    private void RefreshSlots(CancellationToken token)
    {
        slotShelf.Children.Clear(); slotButtons.Clear(); slotClearButtons.Clear(); slotFrames.Clear();
        for (int index = 0; index < 10; index++)
        {
            int slotIndex = index, digit = (index + 1) % 10;
            var entry = library.Items.FirstOrDefault(item => item.Id == library.Pins[index]);
            var artwork = new Controls.Grid();
            var image = new Controls.Image { Width = 26, Height = 26, Stretch = Media.Stretch.Uniform, Margin = new(2, 14, 2, 0) };
            artwork.Children.Add(image); artwork.Children.Add(Badge(digit.ToString(), Wpf.HorizontalAlignment.Left));
            var button = new SelectionTileButton { Content = artwork, MinWidth = 0, MinHeight = 0, Padding = new(0),
                HorizontalContentAlignment = Wpf.HorizontalAlignment.Stretch, Tag = index,
                ToolTip = entry is null ? L.Format("StampShelf.EmptySlot", digit) : StampPreviewTooltip.Create(library, entry, digit.ToString()) };
            Wpf.Automation.AutomationProperties.SetName(button, L.Format("StampShelf.Slot", digit, entry?.Name ?? L.Get("StampShelf.Empty")));
            button.Click += (_, _) => SelectSlot(slotIndex);
            var clear = new Controls.Button { Content = ActionIconButton.Glyph(PanelActionIcon.Close, 8), Width = 14, Height = 14,
                MinWidth = 0, MinHeight = 0, Padding = new(2), Margin = new(0, 1, 1, 0),
                HorizontalAlignment = Wpf.HorizontalAlignment.Right, VerticalAlignment = Wpf.VerticalAlignment.Top,
                Visibility = entry is null ? Wpf.Visibility.Collapsed : Wpf.Visibility.Visible, ToolTip = L.Format("StampShelf.ClearSlot", digit) };
            clear.SetResourceReference(StyleProperty, "CanvasCaptionButton");
            Wpf.Automation.AutomationProperties.SetName(clear, L.Format("StampShelf.ClearSlot", digit));
            clear.Click += (_, e) => { e.Handled = true; RunAsync(() => library.PinAsync(slotIndex, null)); };
            var content = new Controls.Grid(); content.Children.Add(button); content.Children.Add(clear);
            var frame = new Controls.Border { Child = content, Width = 46, Height = 46, CornerRadius = new(6), BorderThickness = new(2), Margin = new(2), Tag = index };
            slotShelf.Children.Add(frame); slotButtons.Add(index, button); slotClearButtons.Add(index, clear); slotFrames.Add(index, frame);
            if (entry is not null) LoadThumbnail(entry, image, button, token);
        }
        RefreshSlotSelection();
    }

    private static Controls.Border Badge(string text, Wpf.HorizontalAlignment alignment, string? tag = null)
    {
        var number = new Controls.TextBlock { Text = text, FontSize = 11, FontWeight = Wpf.FontWeights.SemiBold, TextAlignment = Wpf.TextAlignment.Center,
            VerticalAlignment = Wpf.VerticalAlignment.Center };
        number.SetResourceReference(Controls.TextBlock.ForegroundProperty, "CanvasText");
        Wpf.UIElement content = number;
        if (tag == "Stamp.PinBadge")
        {
            var pin = ActionIconButton.Glyph(PanelActionIcon.Pin, 12); pin.Margin = new(0, 0, 2, 0);
            var row = new Controls.StackPanel { Orientation = Controls.Orientation.Horizontal }; row.Children.Add(pin); row.Children.Add(number); content = row;
        }
        var badge = new Controls.Border { Child = content, MinWidth = tag is null ? 14 : 18, Padding = tag is null ? new(1, 0, 1, 0) : new(3, 1, 3, 1),
            CornerRadius = new(4), Margin = tag is null ? new(1) : new(2),
            HorizontalAlignment = alignment, VerticalAlignment = Wpf.VerticalAlignment.Top, IsHitTestVisible = false, Tag = tag };
        badge.SetResourceReference(Controls.Border.BackgroundProperty, "CanvasSurface");
        return badge;
    }

    private int FirstPinnedSlot(Guid id)
    {
        for (int index = 0; index < 10; index++)
            if (library.Pins[index] == id) return index;
        return -1;
    }

    private void SelectSlot(int index)
    {
        if (closed || busy || !canMutate()) return;
        selectedSlot = selectedSlot == index ? null : index;
        RefreshSlotSelection();
    }

    private void RefreshSlotSelection()
    {
        if (!IsVisible && !preparing) { refreshPending = true; return; }
        foreach (var (index, frame) in slotFrames)
        {
            frame.SetResourceReference(Controls.Border.BorderBrushProperty, selectedSlot == index ? "CanvasAccent" : "CanvasBorder");
            ((SelectionTileButton)slotButtons[index]).SetSelected(selectedSlot == index);
        }
    }

    private void ActivateTile(Guid id)
    {
        if (selectedSlot is { } index) RunAsync(async () =>
        {
            await library.PinAsync(index, id);
            if (selectedSlot == index) { selectedSlot = null; RefreshSlotSelection(); }
        });
    }

    private Task UnpinFirstAsync(Guid id)
    {
        int index = FirstPinnedSlot(id);
        return index < 0 ? Task.CompletedTask : library.PinAsync(index, null);
    }

    private async void LoadThumbnail(StampEntry entry, Controls.Image image, Wpf.FrameworkElement tile, CancellationToken token, Controls.TextBlock? placeholder = null)
    {
        if (!IsVisible || token.IsCancellationRequested) return;
        if (!tile.IsVisible)
        {
            // Cached results can complete before a newly prepared tile or slot
            // inherits visibility. Resume once its own image joins the visible tree.
            // The image belongs to this content generation; recycled tiles cancel
            // its token and release that image instead of retaining old handlers.
            void Visible(object sender, Wpf.DependencyPropertyChangedEventArgs args)
            {
                if (!image.IsVisible) return;
                image.IsVisibleChanged -= Visible;
                if (!closed && !token.IsCancellationRequested && IsVisible && tile.IsVisible)
                    LoadThumbnail(entry, image, tile, token, placeholder);
            }
            image.IsVisibleChanged += Visible;
            return;
        }
        try
        {
            var key = (entry.Id, entry.Revision);
            if (!thumbnailCache.TryGetValue(key, out var bitmap))
            {
                await thumbnailGate.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (!thumbnailCache.TryGetValue(key, out bitmap))
                    {
                        byte[] bytes = await library.GetThumbnailAsync(entry.Id, token); token.ThrowIfCancellationRequested();
                        _ = FloatCanvas.Rendering.ImageDecodeBudget.ReadPixelCount(bytes);
                        using var stream = new MemoryStream(bytes);
                        using var codec = SkiaSharp.SKCodec.Create(stream);
                        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > 4_194_304)
                            throw new InvalidDataException(L.Get("Audit.ThumbnailFailed"));
                        stream.Position = 0;
                        var decoded = new Imaging.BitmapImage(); decoded.BeginInit(); decoded.CacheOption = Imaging.BitmapCacheOption.OnLoad;
                        if (codec.Info.Width >= codec.Info.Height) decoded.DecodePixelWidth = Math.Min(200, codec.Info.Width); else decoded.DecodePixelHeight = Math.Min(200, codec.Info.Height);
                        decoded.StreamSource = stream; decoded.EndInit(); decoded.Freeze(); bitmap = decoded;
                        thumbnailCache[key] = bitmap; thumbnailOrder.Enqueue(key);
                        while (thumbnailOrder.Count > 64) thumbnailCache.Remove(thumbnailOrder.Dequeue());
                    }
                }
                finally { thumbnailGate.Release(); }
            }
            token.ThrowIfCancellationRequested();
            if (!IsVisible || !tile.IsVisible)
            {
                if (IsVisible) LoadThumbnail(entry, image, tile, token, placeholder);
                return;
            }
            image.Source = bitmap; image.Visibility = Wpf.Visibility.Visible;
            if (placeholder is not null) placeholder.Visibility = Wpf.Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or NotSupportedException or InvalidOperationException or KeyNotFoundException)
        {
            if (!token.IsCancellationRequested && IsVisible && tile.IsVisible)
            {
                tile.ToolTip = L.Get("Audit.ThumbnailFailed") + "\n" + ex.Message;
                if (placeholder is not null) placeholder.Text = "⚠ " + L.Get("Audit.ThumbnailFailed");
                image.Visibility = Wpf.Visibility.Hidden;
                if (tile.ContextMenu is { } menu)
                {
                    var retry = new Controls.MenuItem { Header = L.Get("Audit.Retry") };
                    retry.Click += (_, _) => { menu.Items.Remove(retry); LoadThumbnail(entry, image, tile, token, placeholder); }; menu.Items.Add(retry);
                }
            }
        }
    }

    private void OnLibraryChanged()
    {
        if (Dispatcher.CheckAccess()) Refresh();
        else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(Refresh);
    }
    private void UseSelected()
    {
        if (closed || busy || selected is not { } id || !canMutate() || library.Items.All(entry => entry.Id != id)) return;
        selectStamp(id); SessionWindowCoordinator.Hide(this);
    }
    private void UseSelectedSource()
    {
        if (closed || busy || selected is not { } id || !canMutate() || useSource is null || library.Items.All(item => item.Id != id || item.Mode != StampSaveMode.RasterMerge)) return;
        useSource(id); SessionWindowCoordinator.Hide(this);
    }
    private void RefreshActions()
    {
        if (closed) return;
        if (!IsVisible && !preparing) { refreshPending = true; return; }
        bool ready = !busy && canMutate(), hasItem = library.Items.Any(item => item.Id == selected);
        foreach (var (key, button) in commandButtons)
            if (button.Visibility == Wpf.Visibility.Visible)
                button.IsEnabled = ready && (key == "Stamp.SaveSelection" ? getSession().Selection.Count > 0 && !getSession().IsTransactionActive : hasItem);
        if (sourceButton.Visibility == Wpf.Visibility.Visible)
            sourceButton.IsEnabled = ready && useSource is not null && library.Items.Any(item => item.Id == selected && item.Mode == StampSaveMode.RasterMerge);
        foreach (var (index, button) in slotButtons) button.IsEnabled = ready;
        foreach (var (index, button) in slotClearButtons)
            if (button.Visibility == Wpf.Visibility.Visible) button.IsEnabled = ready && library.Pins[index] is not null;
    }
    internal async Task SaveSelectionFromToolbarAsync(Wpf.Window owner)
    {
        if (closed || busy || !canMutate()) return;
        busy = true; dialogOwner = owner;
        SetBusyControls();
        try { await SaveAsync(overwrite: false); }
        finally { dialogOwner = null; busy = false; SetBusyControls(); }
    }

    private async Task SaveAsync(bool overwrite)
    {
        if (!canMutate()) return;
        var session = getSession();
        if (session.IsTransactionActive) { ShowStatus(L.Get("Inspector.FinishDrawing")); return; }
        if (session.Selection.Count == 0) { ShowStatus(L.Get("Stamp.NoSelection")); return; }
        var existing = overwrite ? library.Items.FirstOrDefault(item => item.Id == selected) : null;
        if (overwrite && existing is null) return;
        var settings = Prompt(existing?.Name ?? L.Get("Stamp.DefaultName"), existing?.Mode ?? StampSaveMode.EditableGroup, showMode: true);
        if (settings is null) return;
        if (!canMutate() || !ReferenceEquals(session, getSession()) || session.IsTransactionActive) return;
        if (existing is not null && !Confirm(L.Format("Stamp.OverwriteQuestion", existing.Name))) return;
        var context=PrepareRendering is null ? null : await PrepareRendering(session);
        if(!canMutate() || !ReferenceEquals(session,getSession()) || session.IsTransactionActive)return;
        var saved = await library.SaveSelectionAsync(session, settings.Value.Name, settings.Value.Mode, existing?.Id,renderContext:context);
        selected = saved.Id; Refresh();
    }
    private async Task RenameAsync()
    {
        if (!canMutate()) return;
        var entry = library.Items.FirstOrDefault(item => item.Id == selected);
        if (entry is null) return;
        var settings = Prompt(entry.Name, entry.Mode, showMode: false);
        if (settings is { } value) await library.RenameAsync(entry.Id, value.Name);
    }
    private async Task DeleteAsync()
    {
        if (!canMutate()) return;
        var entry = library.Items.FirstOrDefault(item => item.Id == selected);
        if (entry is not null && Confirm(L.Format("Stamp.DeleteQuestion", entry.Name))) await library.DeleteAsync(entry.Id);
    }
    private async void RunAsync(Func<Task> action)
    {
        if (closed || busy || !canMutate()) return;
        busy = true; SetBusyControls(); ShowStatus(L.Get("Audit.Working"));
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowStatus(L.Get("Stamp.OperationFailed") + ex.Message); }
        finally { busy = false; SetBusyControls(); if (status.Text == L.Get("Audit.Working")) ShowStatus(""); }
    }

    private void SetBusyControls()
    {
        if (closed) return;
        if (!IsVisible && !preparing) { refreshPending = true; return; }
        actions.IsEnabled = grid.IsEnabled = search.IsEnabled = slotShelf.IsEnabled = !busy;
        RefreshActions();
    }

    private void ShowStatus(string text)
    {
        pendingStatus = text;
        if (!IsVisible && !preparing) return;
        status.Text = text; status.Visibility = string.IsNullOrEmpty(text) ? Wpf.Visibility.Collapsed : Wpf.Visibility.Visible;
    }

    private Controls.Button ActionButton(string key, PanelActionIcon icon, Action action, string? shortcut = null)
    {
        var button = ActionIconButton.Create(icon, L.Get(key), action, shortcut); commandButtons.Add(key, button); return button;
    }

    private void AddActionGroup(params Wpf.UIElement[] controls)
    {
        if (actions.Children.Count > 0)
        {
            var separator = new Controls.Border { Width = 1, Height = 22, Margin = new(8, 4, 8, 4) };
            separator.SetResourceReference(Controls.Border.BackgroundProperty, "CanvasBorder"); actions.Children.Add(separator);
        }
        var group = new Controls.StackPanel { Orientation = Controls.Orientation.Horizontal, VerticalAlignment = Wpf.VerticalAlignment.Center };
        foreach (var control in controls) group.Children.Add(control);
        actions.Children.Add(group);
    }

    private bool HandleShortcut(Input.Key key, Input.ModifierKeys modifiers, Wpf.DependencyObject? source)
    {
        if (closed || busy) return false;
        if (key == Input.Key.F && modifiers == Input.ModifierKeys.Control) { search.Focus(); search.SelectAll(); return true; }
        if (key == Input.Key.Escape && modifiers == Input.ModifierKeys.None && selectedSlot is not null) { selectedSlot = null; RefreshSlotSelection(); return true; }
        if (key == Input.Key.Escape && modifiers == Input.ModifierKeys.None) { SessionWindowCoordinator.Close(this); return true; }
        if (key == Input.Key.N && modifiers == Input.ModifierKeys.Control) { if (canMutate()) RunAsync(() => SaveAsync(overwrite: false)); return true; }
        if (IsTextInput(source) || IsTextInput(Input.Keyboard.FocusedElement as Wpf.DependencyObject) || !canMutate()) return false;
        if (modifiers != Input.ModifierKeys.None || selected is null) return false;
        if (key == Input.Key.Enter) { UseSelected(); return true; }
        if (key == Input.Key.F2) { RunAsync(RenameAsync); return true; }
        if (key == Input.Key.Delete) { RunAsync(DeleteAsync); return true; }
        return false;
    }

    private bool OwnsShortcut(Input.Key key, Input.ModifierKeys modifiers, Wpf.DependencyObject? source)
    {
        if (key is Input.Key.F or Input.Key.N && modifiers == Input.ModifierKeys.Control || key == Input.Key.Escape && modifiers == Input.ModifierKeys.None) return true;
        if (IsTextInput(source) || IsTextInput(Input.Keyboard.FocusedElement as Wpf.DependencyObject)) return false;
        return modifiers == Input.ModifierKeys.None && key is Input.Key.Enter or Input.Key.F2 or Input.Key.Delete;
    }

    private static bool IsTextInput(Wpf.DependencyObject? source)
    {
        for (var current = source; current is not null; current = Wpf.LogicalTreeHelper.GetParent(current) ?? (current is Media.Visual ? Media.VisualTreeHelper.GetParent(current) : null))
            if (current is Controls.Primitives.TextBoxBase or Controls.ComboBox or Controls.PasswordBox) return true;
        return false;
    }

    private OverlayContextMenu TileContextMenu(Guid id) => EntryContextMenu(id,null,null);
    internal OverlayContextMenu CreateToolbarMenu(Guid id,int slot,Wpf.Window owner) => EntryContextMenu(id,slot,owner);
    private OverlayContextMenu EntryContextMenu(Guid id,int? toolbarSlot,Wpf.Window? owner)
    {
        var menu = new OverlayContextMenu();
        void RunEntryAsync(Func<Task> action) => RunAsync(async()=>
        {
            selected=id;var previous=dialogOwner;dialogOwner=owner;
            try {await action();} finally {dialogOwner=previous;}
        });
        void Add(string key, PanelActionIcon icon, Action action, string? shortcut = null)
        {
            var item = new Controls.MenuItem { Header = L.Get(key), Icon = ActionIconButton.Glyph(icon), InputGestureText = shortcut ?? "", Tag = key };
            item.Click += (_, _) => {selected=id;action();}; menu.Items.Add(item);
        }
        // A toolbar slot is already the direct use action. Its context menu is
        // deliberately limited to maintenance; library tiles retain both use paths.
        if (toolbarSlot is null)
        {
            Add("Stamp.Use", PanelActionIcon.Use, UseSelected, "Enter");
            if (useSource is not null) Add("Stamp.UseSource", PanelActionIcon.Source, UseSelectedSource);
            menu.Items.Add(new Controls.Separator());
        }
        Add("Stamp.Rename", PanelActionIcon.Rename, () => RunEntryAsync(RenameAsync), "F2");
        Add("Stamp.Overwrite", PanelActionIcon.Save, () => RunEntryAsync(() => SaveAsync(overwrite: true)));
        Add("Stamp.Delete", PanelActionIcon.Delete, () => RunEntryAsync(DeleteAsync), "Delete");
        menu.Items.Add(new Controls.Separator());
        var pins = new Controls.MenuItem { Header = L.Get("Stamp.Pin"), Icon = ActionIconButton.Glyph(PanelActionIcon.Pin), Tag = "Stamp.Pin" };
        var unpin = new Controls.MenuItem { Header = L.Get("Stamp.Unpin"), Icon = ActionIconButton.Glyph(PanelActionIcon.Unpin), Tag = "Stamp.Unpin" };
        unpin.Click += (_, _) => RunEntryAsync(()=>toolbarSlot is { } slot
            ? library.Pins[slot]==id?library.PinAsync(slot,null):Task.CompletedTask : UnpinFirstAsync(id));
        for (int index = 0; index < 10; index++)
        {
            int slotIndex = index;
            var pin = new Controls.MenuItem { Header = ((index + 1) % 10).ToString(), Tag = index, Icon = ActionIconButton.Glyph(PanelActionIcon.Pin) };
            pin.Click += (_, _) => RunAsync(() => library.PinAsync(slotIndex, id)); pins.Items.Add(pin);
        }
        // Only one pin-related action is presented. Repeated unpins remove the
        // first displayed slot, without touching another stamp's slots.
        if (toolbarSlot is not null || FirstPinnedSlot(id) >= 0) menu.Items.Add(unpin); else menu.Items.Add(pins);
        menu.Opened += (_, _) =>
        {
            var tile = grid.Items.Cast<StampEntry>().FirstOrDefault(item => item.Id == id);
            if (closed || busy || toolbarSlot is null&&tile is null || toolbarSlot is { } slot&&library.Pins[slot]!=id) { menu.IsOpen = false; return; }
            if(toolbarSlot is null)grid.SelectedItem = tile;else selected=id;
            bool ready = canMutate() && library.Items.Any(item => item.Id == id);
            foreach (var item in menu.Items.OfType<Controls.MenuItem>()) item.IsEnabled = ready;
            foreach (var item in menu.Items.OfType<Controls.MenuItem>().Where(item => Equals(item.Tag, "Stamp.UseSource")))
                item.IsEnabled = ready && library.Items.Any(entry => entry.Id == id && entry.Mode == StampSaveMode.RasterMerge);
            unpin.IsEnabled = ready && FirstPinnedSlot(id) >= 0;
        };
        return menu;
    }

    private void CloseTileMenus()
    { foreach (var item in RealizedTiles()) if (item.ContextMenu is { } menu)OverlayContextMenu.SetOpen(menu,false); }

    private (string Name, StampSaveMode Mode)? Prompt(string initialName, StampSaveMode mode, bool showMode)
    {
        var panel = new Controls.StackPanel { Margin = WindowLayout.ContentMargin(18, 18) };
        panel.Children.Add(new Controls.TextBlock { Text = L.Get("Stamp.Name") });
        var name = new Controls.TextBox { Text = initialName, MaxLength = 120, Margin = new(0, 8, 0, 14) }; panel.Children.Add(name);
        var editable = new Controls.RadioButton { Content = L.Get("Stamp.EditableGroup"), IsChecked = mode == StampSaveMode.EditableGroup, Margin = new(0, 0, 0, 10) };
        var raster = new Controls.RadioButton { Content = L.Get("Stamp.RasterMerge"), IsChecked = mode == StampSaveMode.RasterMerge, Margin = new(0, 0, 0, 14) };
        if (showMode) { panel.Children.Add(editable); panel.Children.Add(raster); }
        var buttons = new Controls.StackPanel { Orientation = Controls.Orientation.Horizontal, HorizontalAlignment = Wpf.HorizontalAlignment.Right };
        var save = Button("Stamp.Save", () => { }); save.IsDefault = true; buttons.Children.Add(save);
        var cancel = Button("Stamp.Cancel", () => { }); cancel.IsCancel = true; buttons.Children.Add(cancel); panel.Children.Add(buttons);
        var dialog = Dialog(panel, L.Get(showMode ? "Stamp.SaveSelection" : "Stamp.Rename"));
        NamePrompt.AttachValidation(name, save, panel);
        save.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(name.Text)) SessionWindowCoordinator.SetModalResult(dialog, true); };
        dialog.Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
        return SessionWindowCoordinator.ShowModal(dialog) == true ? (name.Text.Trim(), raster.IsChecked == true ? StampSaveMode.RasterMerge : StampSaveMode.EditableGroup) : null;
    }
    private bool Confirm(string message)
    {
        var panel = new Controls.StackPanel { Margin = WindowLayout.ContentMargin(18, 18) };
        panel.Children.Add(new Controls.TextBlock { Text = message, TextWrapping = Wpf.TextWrapping.Wrap, Margin = new(0, 0, 0, 16) });
        var buttons = new Controls.StackPanel { Orientation = Controls.Orientation.Horizontal, HorizontalAlignment = Wpf.HorizontalAlignment.Right };
        var apply = Button("Stamp.Confirm", () => { }); buttons.Children.Add(apply);
        var cancel = Button("Stamp.Cancel", () => { }); cancel.IsCancel = true; buttons.Children.Add(cancel); panel.Children.Add(buttons);
        var dialog = Dialog(panel, L.Get("Stamp.LibraryTitle"));
        apply.Click += (_, _) => SessionWindowCoordinator.SetModalResult(dialog, true);
        return SessionWindowCoordinator.ShowModal(dialog) == true;
    }
    private Wpf.Window Dialog(object content, string title)
    {
        var dialog = new Wpf.Window { Title = title, Content = content, Owner = dialogOwner ?? this, ShowInTaskbar = false, Topmost = true, Width = 470,
            SizeToContent = Wpf.SizeToContent.Height, ResizeMode = Wpf.ResizeMode.NoResize, WindowStartupLocation = Wpf.WindowStartupLocation.CenterOwner, Icon = AppIcon.WindowImage };
        AppWindowChrome.ApplyDialog(dialog); return dialog;
    }
    private static Controls.Button Button(string key, Action action)
    {
        var button = new Controls.Button { Content = L.Get(key), Padding = new Wpf.Thickness(10, 5, 10, 5), Margin = new Wpf.Thickness(0, 0, 5, 0), MinHeight = 30 };
        button.Click += (_, _) => action(); return button;
    }
}
