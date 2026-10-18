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
    private const int PageSize = 48;
    private readonly StampLibrary library;
    private readonly Func<EditorSession> getSession;
    private readonly Action<Guid> selectStamp;
    private readonly Action<Guid>? useSource;
    private readonly Func<bool> canMutate;
    private readonly Controls.TextBox search = new() { MinWidth = 220, Margin = new(8, 0, 0, 0) };
    private readonly Controls.ListBox grid = new() { HorizontalContentAlignment = Wpf.HorizontalAlignment.Stretch };
    private readonly Controls.TextBlock status = new() { TextWrapping = Wpf.TextWrapping.Wrap, Margin = new(0, 8, 0, 0), MinHeight = 22 };
    private readonly Controls.TextBlock pageLabel = new() { VerticalAlignment = Wpf.VerticalAlignment.Center, Margin = new(10, 0, 10, 0) };
    private readonly Controls.ComboBox slot = new() { MinWidth = 50, Margin = new(8, 0, 0, 0) };
    private readonly Controls.WrapPanel actions = new() { Margin = new(0, 8, 0, 10) };
    private readonly Controls.Button previous, next;
    private readonly Controls.Button sourceButton;
    private readonly Dictionary<string, Controls.Button> commandButtons = [];
    private CancellationTokenSource thumbnails = new();
    private Guid? selected;
    private int page;
    private int pageCount = 1;
    private bool busy, closed;
    private Wpf.Window? dialogOwner;

    public StampLibraryWindow(StampLibrary library, Func<EditorSession> getSession, Action<Guid> selectStamp, Func<bool>? canMutate = null, Action<Guid>? useSource = null)
    {
        this.library = library; this.getSession = getSession; this.selectStamp = selectStamp; this.canMutate = canMutate ?? (() => true); this.useSource = useSource;
        Title = L.Get("Stamp.LibraryTitle"); Width = 840; Height = 650; MinWidth = 550; MinHeight = 440;
        FontSize = 13; Topmost = true; Icon = AppIcon.WindowImage;
        var body = new Controls.DockPanel { Margin = new Wpf.Thickness(16) };
        var top = new Controls.StackPanel(); Controls.DockPanel.SetDock(top, Controls.Dock.Top); body.Children.Add(top);
        top.Children.Add(new Controls.TextBlock { Text = L.Get("Stamp.LibraryHint"), TextWrapping = Wpf.TextWrapping.Wrap });
        var searchRow = new Controls.StackPanel { Orientation = Controls.Orientation.Horizontal, Margin = new(0, 12, 0, 0) };
        searchRow.Children.Add(new Controls.TextBlock { Text = L.Get("Stamp.Search"), VerticalAlignment = Wpf.VerticalAlignment.Center });
        searchRow.Children.Add(search); top.Children.Add(searchRow);
        Wpf.Automation.AutomationProperties.SetName(search, L.Get("Stamp.Search"));
        AddActionGroup(ActionButton("Stamp.SaveSelection", PanelActionIcon.Add, () => RunAsync(() => SaveAsync(overwrite: false)), "Ctrl+N"));
        sourceButton = ActionButton("Stamp.UseSource", PanelActionIcon.Source, UseSelectedSource);
        sourceButton.Visibility = useSource is null ? Wpf.Visibility.Collapsed : Wpf.Visibility.Visible;
        sourceButton.IsEnabled = false;
        AddActionGroup(ActionButton("Stamp.Use", PanelActionIcon.Use, UseSelected, "Enter"), sourceButton);
        AddActionGroup(ActionButton("Stamp.Rename", PanelActionIcon.Rename, () => RunAsync(RenameAsync), "F2"),
            ActionButton("Stamp.Overwrite", PanelActionIcon.Save, () => RunAsync(() => SaveAsync(overwrite: true))),
            ActionButton("Stamp.Delete", PanelActionIcon.Delete, () => RunAsync(DeleteAsync), "Delete"));
        foreach (string digit in new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" }) slot.Items.Add(digit);
        slot.SelectedIndex = 0;
        Wpf.Automation.AutomationProperties.SetName(slot, L.Get("Stamp.ShortcutSlot"));
        AddActionGroup(slot, ActionButton("Stamp.Pin", PanelActionIcon.Pin, () => { if (selected is { } id) RunAsync(() => library.PinAsync(slot.SelectedIndex, id)); }),
            ActionButton("Stamp.Unpin", PanelActionIcon.Unpin, () => RunAsync(() => library.PinAsync(slot.SelectedIndex, null))));
        slot.SelectionChanged += (_, _) => RefreshActions();
        top.Children.Add(actions);
        var bottom = new Controls.StackPanel(); Controls.DockPanel.SetDock(bottom, Controls.Dock.Bottom); body.Children.Add(bottom);
        var pagination = new Controls.StackPanel { Orientation = Controls.Orientation.Horizontal, HorizontalAlignment = Wpf.HorizontalAlignment.Center, Margin = new(0, 10, 0, 0) };
        previous = ActionIconButton.Create(PanelActionIcon.Previous, L.Get("Stamp.PreviousPage"), () => ChangePage(-1));
        next = ActionIconButton.Create(PanelActionIcon.Next, L.Get("Stamp.NextPage"), () => ChangePage(1));
        pagination.Children.Add(previous); pagination.Children.Add(pageLabel); pagination.Children.Add(next);
        bottom.Children.Add(pagination); bottom.Children.Add(status);
        grid.ItemsPanel = new Controls.ItemsPanelTemplate(new Wpf.FrameworkElementFactory(typeof(Controls.WrapPanel)));
        Controls.ScrollViewer.SetHorizontalScrollBarVisibility(grid, Controls.ScrollBarVisibility.Disabled);
        grid.SelectionChanged += (_, _) => { selected = (grid.SelectedItem as Controls.ListBoxItem)?.Tag as Guid?; RefreshActions(); };
        grid.MouseDoubleClick += (_, e) => { if (e.ChangedButton == Input.MouseButton.Left) UseSelected(); };
        Wpf.Automation.AutomationProperties.SetName(grid, L.Get("Stamp.LibraryTitle"));
        body.Children.Add(grid); Content = body; AppWindowChrome.Apply(this);
        PreviewKeyDown += (_, e) => { var key = e.Key == Input.Key.System ? e.SystemKey : e.Key; if (HandleShortcut(key, Input.Keyboard.Modifiers, e.OriginalSource as Wpf.DependencyObject)) e.Handled = true; };
        search.TextChanged += (_, _) => { page = 0; Refresh(); };
        library.Changed += OnLibraryChanged;
        Closed += (_, _) => { closed = true; CloseTileMenus(); thumbnails.Cancel(); thumbnails.Dispose(); library.Changed -= OnLibraryChanged; };
        Refresh();
    }

    public void Refresh()
    {
        if (closed) return;
        thumbnails.Cancel(); thumbnails.Dispose(); thumbnails = new();
        var token = thumbnails.Token;
        var items = library.Items.Where(item => item.Name.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)).OrderByDescending(item => item.UpdatedAtUtc).ToArray();
        int pages = pageCount = Math.Max(1, (items.Length + PageSize - 1) / PageSize);
        page = Math.Clamp(page, 0, pages - 1);
        Guid? keep = selected;
        CloseTileMenus(); grid.Items.Clear();
        foreach (var entry in items.Skip(page * PageSize).Take(PageSize))
        {
            int pinnedSlot = library.Pins.ToList().FindIndex(id => id == entry.Id);
            string shortcut = pinnedSlot < 0 ? "" : ((pinnedSlot + 1) % 10).ToString();
            var tile = new Controls.StackPanel { Width = 112, Margin = new Wpf.Thickness(4), ToolTip = StampPreviewTooltip.Create(library, entry, shortcut) };
            var image = new Controls.Image { Height = 82, Width = 100, Stretch = Media.Stretch.Uniform, Margin = new(0, 2, 0, 6) };
            tile.Children.Add(image);
            tile.Children.Add(new Controls.TextBlock { Text = entry.Name, TextTrimming = Wpf.TextTrimming.CharacterEllipsis, TextAlignment = Wpf.TextAlignment.Center });
            var item = new Controls.ListBoxItem { Content = tile, Tag = entry.Id, Margin = new Wpf.Thickness(2), Padding = new Wpf.Thickness(3) };
            Wpf.Automation.AutomationProperties.SetName(item, entry.Name);
            item.ContextMenu = TileContextMenu(entry.Id);
            item.PreviewMouseRightButtonDown += (_, _) => { if (!busy) grid.SelectedItem = item; };
            grid.Items.Add(item);
            if (entry.Id == keep) grid.SelectedItem = item;
            LoadThumbnail(entry, image, item, token);
        }
        pageLabel.Text = L.Format("Stamp.Page", page + 1, pages, items.Length);
        RefreshActions();
    }

    private async void LoadThumbnail(StampEntry entry, Controls.Image image, Controls.ListBoxItem tile, CancellationToken token)
    {
        try
        {
            byte[] bytes = await library.GetThumbnailAsync(entry.Id, token);
            token.ThrowIfCancellationRequested();
            using var stream = new MemoryStream(bytes);
            var bitmap = new Imaging.BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = Imaging.BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            image.Source = bitmap;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or FormatException or NotSupportedException or InvalidOperationException or KeyNotFoundException)
        {
            if (!token.IsCancellationRequested)
            {
                tile.ToolTip = ex.Message;
                if (tile.Content is Controls.StackPanel content)
                    content.Children[0] = new Controls.TextBlock { Text = "⚠", Height = 82, FontSize = 32, TextAlignment = Wpf.TextAlignment.Center, VerticalAlignment = Wpf.VerticalAlignment.Center };
            }
        }
    }

    private void OnLibraryChanged()
    {
        if (Dispatcher.CheckAccess()) Refresh();
        else Dispatcher.BeginInvoke(Refresh);
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
        bool ready = !busy && canMutate(), hasItem = library.Items.Any(item => item.Id == selected);
        foreach (var (key, button) in commandButtons)
            button.IsEnabled = ready && (key == "Stamp.SaveSelection" ? getSession().Selection.Count > 0 && !getSession().IsTransactionActive :
                key == "Stamp.Unpin" ? slot.SelectedIndex >= 0 && library.Pins[slot.SelectedIndex] is not null : hasItem);
        sourceButton.IsEnabled = ready && useSource is not null && library.Items.Any(item => item.Id == selected && item.Mode == StampSaveMode.RasterMerge);
        previous.IsEnabled = !busy && page > 0; next.IsEnabled = !busy && page + 1 < pageCount;
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
        if (session.IsTransactionActive) { status.Text = L.Get("Inspector.FinishDrawing"); return; }
        if (session.Selection.Count == 0) { status.Text = L.Get("Stamp.NoSelection"); return; }
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
        busy = true; SetBusyControls(); status.Text = "";
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { status.Text = L.Get("Stamp.OperationFailed") + ex.Message; }
        finally { busy = false; SetBusyControls(); }
    }

    private void SetBusyControls()
    {
        if (closed) return;
        actions.IsEnabled = grid.IsEnabled = search.IsEnabled = !busy;
        RefreshActions();
    }

    private void ChangePage(int delta) { if (closed || busy) return; page += delta; Refresh(); }

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
        if (IsTextInput(source) || IsTextInput(Input.Keyboard.FocusedElement as Wpf.DependencyObject) || !canMutate()) return false;
        if (key == Input.Key.N && modifiers == Input.ModifierKeys.Control) { RunAsync(() => SaveAsync(overwrite: false)); return true; }
        if (modifiers != Input.ModifierKeys.None || selected is null) return false;
        if (key == Input.Key.Enter) { UseSelected(); return true; }
        if (key == Input.Key.F2) { RunAsync(RenameAsync); return true; }
        if (key == Input.Key.Delete) { RunAsync(DeleteAsync); return true; }
        return false;
    }

    private static bool IsTextInput(Wpf.DependencyObject? source)
    {
        for (var current = source; current is not null; current = Wpf.LogicalTreeHelper.GetParent(current) ?? (current is Media.Visual ? Media.VisualTreeHelper.GetParent(current) : null))
            if (current is Controls.Primitives.TextBoxBase or Controls.ComboBox or Controls.PasswordBox) return true;
        return false;
    }

    private Controls.ContextMenu TileContextMenu(Guid id)
    {
        var menu = new Controls.ContextMenu();
        void Add(string key, PanelActionIcon icon, Action action, string? shortcut = null)
        {
            var item = new Controls.MenuItem { Header = L.Get(key), Icon = ActionIconButton.Glyph(icon), InputGestureText = shortcut ?? "", Tag = key };
            item.Click += (_, _) => action(); menu.Items.Add(item);
        }
        Add("Stamp.Use", PanelActionIcon.Use, UseSelected, "Enter");
        if (useSource is not null) Add("Stamp.UseSource", PanelActionIcon.Source, UseSelectedSource);
        menu.Items.Add(new Controls.Separator());
        Add("Stamp.Rename", PanelActionIcon.Rename, () => RunAsync(RenameAsync), "F2");
        Add("Stamp.Overwrite", PanelActionIcon.Save, () => RunAsync(() => SaveAsync(overwrite: true)));
        Add("Stamp.Delete", PanelActionIcon.Delete, () => RunAsync(DeleteAsync), "Delete");
        menu.Items.Add(new Controls.Separator());
        var pins = new Controls.MenuItem { Header = L.Get("Stamp.Pin"), Icon = ActionIconButton.Glyph(PanelActionIcon.Pin), Tag = "Stamp.Pin" };
        var unpins = new Controls.MenuItem { Header = L.Get("Stamp.Unpin"), Icon = ActionIconButton.Glyph(PanelActionIcon.Unpin), Tag = "Stamp.Unpin" };
        for (int i = 0; i < 10; i++)
        {
            int index = i; string digit = ((i + 1) % 10).ToString();
            var pin = new Controls.MenuItem { Header = digit, Tag = index };
            pin.Click += (_, _) => RunAsync(() => library.PinAsync(index, id)); pins.Items.Add(pin);
            var unpin = new Controls.MenuItem { Header = digit, Tag = index };
            unpin.Click += (_, _) => RunAsync(() => library.PinAsync(index, null)); unpins.Items.Add(unpin);
        }
        menu.Items.Add(pins); menu.Items.Add(unpins);
        menu.Opened += (_, _) =>
        {
            var tile = grid.Items.Cast<Controls.ListBoxItem>().FirstOrDefault(item => Equals(item.Tag, id));
            if (tile is null || closed || busy) { menu.IsOpen = false; return; }
            grid.SelectedItem = tile;
            bool ready = canMutate() && library.Items.Any(item => item.Id == id);
            foreach (var item in menu.Items.OfType<Controls.MenuItem>()) item.IsEnabled = ready;
            foreach (var item in menu.Items.OfType<Controls.MenuItem>().Where(item => Equals(item.Tag, "Stamp.UseSource")))
                item.IsEnabled = ready && library.Items.Any(entry => entry.Id == id && entry.Mode == StampSaveMode.RasterMerge);
            foreach (var item in unpins.Items.OfType<Controls.MenuItem>()) item.IsEnabled = ready && library.Pins[(int)item.Tag] == id;
            unpins.IsEnabled = ready && library.Pins.Contains(id);
        };
        return menu;
    }

    private void CloseTileMenus()
    { foreach (var item in grid.Items.OfType<Controls.ListBoxItem>()) if (item.ContextMenu is { } menu) menu.IsOpen = false; }

    private (string Name, StampSaveMode Mode)? Prompt(string initialName, StampSaveMode mode, bool showMode)
    {
        var panel = new Controls.StackPanel { Margin = new Wpf.Thickness(18) };
        panel.Children.Add(new Controls.TextBlock { Text = L.Get("Stamp.Name") });
        var name = new Controls.TextBox { Text = initialName, MaxLength = 120, Margin = new(0, 8, 0, 14) }; panel.Children.Add(name);
        var editable = new Controls.RadioButton { Content = L.Get("Stamp.EditableGroup"), IsChecked = mode == StampSaveMode.EditableGroup, Margin = new(0, 0, 0, 10) };
        var raster = new Controls.RadioButton { Content = L.Get("Stamp.RasterMerge"), IsChecked = mode == StampSaveMode.RasterMerge, Margin = new(0, 0, 0, 14) };
        if (showMode) { panel.Children.Add(editable); panel.Children.Add(raster); }
        var buttons = new Controls.StackPanel { Orientation = Controls.Orientation.Horizontal, HorizontalAlignment = Wpf.HorizontalAlignment.Right };
        var save = Button("Stamp.Save", () => { }); save.IsDefault = true; buttons.Children.Add(save);
        var cancel = Button("Stamp.Cancel", () => { }); cancel.IsCancel = true; buttons.Children.Add(cancel); panel.Children.Add(buttons);
        var dialog = Dialog(panel, L.Get(showMode ? "Stamp.SaveSelection" : "Stamp.Rename"));
        save.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(name.Text)) dialog.DialogResult = true; };
        dialog.Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
        return SessionWindowCoordinator.ShowModal(dialog) == true ? (name.Text.Trim(), raster.IsChecked == true ? StampSaveMode.RasterMerge : StampSaveMode.EditableGroup) : null;
    }
    private bool Confirm(string message)
    {
        var panel = new Controls.StackPanel { Margin = new Wpf.Thickness(18) };
        panel.Children.Add(new Controls.TextBlock { Text = message, TextWrapping = Wpf.TextWrapping.Wrap, Margin = new(0, 0, 0, 16) });
        var buttons = new Controls.StackPanel { Orientation = Controls.Orientation.Horizontal, HorizontalAlignment = Wpf.HorizontalAlignment.Right };
        var apply = Button("Stamp.Confirm", () => { }); buttons.Children.Add(apply);
        var cancel = Button("Stamp.Cancel", () => { }); cancel.IsCancel = true; buttons.Children.Add(cancel); panel.Children.Add(buttons);
        var dialog = Dialog(panel, L.Get("Stamp.LibraryTitle"));
        apply.Click += (_, _) => dialog.DialogResult = true;
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
