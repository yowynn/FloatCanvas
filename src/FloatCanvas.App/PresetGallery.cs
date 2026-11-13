using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Shared presentation and interaction for typed preset repositories.</summary>
internal abstract class PresetGallery<T> : UserControl
{
    internal readonly PixelTilePanel OfficialTiles = new(5, 36, 4), UserTiles = new(5, 36, 4);
    private readonly StackPanel headings = new() { Margin = new(0, 0, WindowLayout.ScrollBarWidth, 0) };
    private readonly Grid userArea = new();
    private readonly TextBlock empty = new() { Text = L.Get("ToolOptions.EmptyUser"), FontSize = 11, Margin = new(0, 6, WindowLayout.ScrollBarWidth, 0) };
    private readonly ScrollViewer userScroll;
    private readonly Button save;
    protected bool revealPending;
    internal event Action? Applied;
    internal event Action<string>? Failed;
    protected abstract IReadOnlyList<T> Official { get; }
    protected abstract IReadOnlyList<T> Users { get; }
    internal abstract string SelectedId { get; }
    protected abstract void Select(string id);
    protected abstract string Id(T preset);
    protected abstract string Caption(T preset);
    protected abstract FrameworkElement Preview(T preset);
    protected abstract string PreviewIdentity(T preset);
    protected abstract bool Modified(T preset);
    protected abstract void ApplyPreset(T preset);
    protected abstract void SavePreset(string name);
    protected abstract void UpdatePreset(T preset);
    protected abstract void RenamePreset(T preset, string name);
    protected abstract void DeletePreset(T preset);
    protected abstract void MovePreset(T preset, T target, bool after);
    protected abstract string SuggestedName { get; }
    protected virtual bool CanEdit => true;

    protected PresetGallery()
    {
        var body = new DockPanel { MinWidth = 196 + WindowLayout.ScrollBarWidth };
        var officialHeader = Header("ToolOptions.Official"); headings.Children.Add(officialHeader); headings.Children.Add(OfficialTiles);
        var userHeader = Header("ToolOptions.User"); userHeader.Margin = new(0, 12, 0, 4);
        save = ActionIconButton.Create(PanelActionIcon.Add, L.Get("ToolOptions.NewPresetHint"), () =>
        { if (NamePrompt.Show(this, L.Get("ToolOptions.Name"), SuggestedName) is { } name) Run(() => SaveNamed(name)); });
        DockPanel.SetDock(save, Dock.Right); userHeader.Children.Insert(0, save); headings.Children.Add(userHeader);
        DockPanel.SetDock(headings, Dock.Top); body.Children.Add(headings);
        userArea.Children.Add(empty);
        userScroll = ToolOptionsLayout.ScrollWithReservedGutter(); userScroll.Content = UserTiles;
        userArea.Children.Add(userScroll); body.Children.Add(userArea); Content = body;
        IsVisibleChanged += (_, _) => { if (IsVisible) PrepareForShow(); };
    }

    protected override Size MeasureOverride(Size constraint)
    {
        headings.Measure(new(constraint.Width, double.PositiveInfinity));
        double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleY;
        userArea.MinHeight = 4 * Math.Round(36 * dpi, MidpointRounding.AwayFromZero) / dpi + 3 * Math.Round(4 * dpi, MidpointRounding.AwayFromZero) / dpi;
        return base.MeasureOverride(new(constraint.Width, headings.DesiredSize.Height + userArea.MinHeight));
    }

    internal void Refresh(bool revealSelection = false)
    { revealPending |= revealSelection; if (IsVisible) PrepareForShow(); }

    internal virtual void PrepareForShow()
    {
        var official = Official; var users = Users;
        if (!official.Concat(users).Any(p => Id(p) == SelectedId)) Select(official.Count > 0 ? Id(official[0]) : "");
        var focus = TileCollection.Capture(this, Tiles());
        Sync(OfficialTiles, official, false); Sync(UserTiles, users, true);
        empty.Visibility = users.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        userScroll.Visibility = users.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        save.IsEnabled = CanEdit;
        if (!CanEdit) { save.ToolTip = L.Get("CanvasPreset.ReadOnly"); ToolTipService.SetShowOnDisabled(save, true); }
        TileCollection.Restore(Tiles(), focus, SelectedId);
        if (revealPending) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        { if (IsVisible) Tiles().FirstOrDefault(tile => Equals(tile.Tag, SelectedId))?.BringIntoView(); }));
        revealPending = false;

        void Sync(PixelTilePanel panel, IReadOnlyList<T> presets, bool user)
        {
            var byId = presets.ToDictionary(Id);
            TileCollection.Synchronize(panel, byId.Keys, id =>
            {
                var button = new SelectionTileButton { Tag = id, MinWidth = 0, MinHeight = 0, Padding = new(4), Margin = new(0), BorderThickness = new(1) };
                ToolTipService.SetInitialShowDelay(button, 600);
                button.Click += (_, _) => Apply(id);
                if (user)
                {
                    TileDragReorder.Attach(button, panel, (target, after) => Run(() =>
                    { MovePreset(Users.First(p => Id(p) == id), Users.First(p => Id(p) == (string)target.Tag), after); Refresh(); }));
                    button.PreviewKeyDown += (_, e) =>
                    {
                        if (Keyboard.Modifiers != ModifierKeys.None || e.Key is not (Key.F2 or Key.Delete)) return;
                        e.Handled = true; if (AppInputPolicy.IsRepeat(e)) return;
                        if (Users.FirstOrDefault(p => Id(p) == id) is not { } current) return;
                        if (e.Key == Key.F2) Rename(current); else Run(() => Delete(current));
                    };
                }
                return button;
            }, (button, id) =>
            {
                T preset = byId[id]; bool modified = id == SelectedId && Modified(preset);
                button.ToolTip = Caption(preset) + (modified ? "\n" + L.Get("Audit.SelectedModified") : "");
                AutomationProperties.SetName(button, Caption(preset));
                var previewKey = (PreviewIdentity(preset), TryFindResource("CanvasText"), L.Get("ToolOptions.Official"));
                if (!Equals(button.Resources["PresetPreviewIdentity"], previewKey))
                { button.Content = Preview(preset); button.Resources["PresetPreviewIdentity"] = previewKey; }
                ((SelectionTileButton)button).SetSelected(id == SelectedId, modified);
                if (user) button.ContextMenu = Menu(preset);
            });
        }
    }

    internal void Apply(string id) => Run(() =>
    {
        var preset = Official.Concat(Users).First(p => Id(p) == id);
        Select(id); ApplyPreset(preset); Refresh(true); Applied?.Invoke();
    });
    internal void SaveNamed(string name) { SavePreset(name); Refresh(true); }
    internal void Delete(T preset)
    {
        bool selected = SelectedId == Id(preset);
        DeletePreset(preset);
        if (selected) Select(Official.Count > 0 ? Id(Official[0]) : "");
        Refresh(true);
    }
    private void Rename(T preset)
    { if (NamePrompt.Show(this, L.Get("ToolOptions.Rename"), Caption(preset)) is { } name) Run(() => { RenamePreset(preset, name); Refresh(); }); }
    private OverlayContextMenu Menu(T preset)
    {
        var menu = new OverlayContextMenu();
        void Add(string key, PanelActionIcon icon, Action action, string shortcut = "")
        { var item = new MenuItem { Header = L.Get(key), Icon = ActionIconButton.Glyph(icon), InputGestureText = shortcut, IsEnabled = CanEdit }; item.Click += (_, _) => Run(action); menu.Items.Add(item); }
        Add("ToolOptions.Overwrite", PanelActionIcon.Save, () => { UpdatePreset(preset); Refresh(); });
        Add("ToolOptions.Rename", PanelActionIcon.Rename, () => Rename(preset), "F2");
        Add("ToolOptions.Delete", PanelActionIcon.Delete, () => Delete(preset), "Delete"); return menu;
    }
    protected void Run(Action action) { try { action(); } catch (Exception ex) { Failed?.Invoke(ex.Message); } }
    private IEnumerable<Button> Tiles() => OfficialTiles.Children.OfType<Button>().Concat(UserTiles.Children.OfType<Button>());
    private static DockPanel Header(string key)
    { var panel = new DockPanel { Height = 34, Margin = new(0, 0, 0, 4) }; panel.Children.Add(PropertyPanelLayout.SectionTitle(L.Get(key))); return panel; }
}
