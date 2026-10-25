using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed class CanvasPresetGallery : UserControl
{
    private readonly CanvasPresetLibrary library;
    internal readonly PixelTilePanel OfficialTiles = new(5, 36, 4), UserTiles = new(5, 36, 4);
    private readonly TextBlock empty = new() { Text = L.Get("ToolOptions.EmptyUser"), FontSize = 11, Opacity = .7, Margin = new(2, 6, 0, 0) };
    private readonly ScrollViewer userScroll;
    private CanvasSettings current = new();
    // Session-only selection evidence; definitions, previews and application never use these snapshots.
    private CanvasPresetParameters? selectedSnapshot, selectedDefinition;
    internal string SelectedId { get; private set; } = "";
    internal event Action<CanvasPresetParameters>? Applied;
    internal event Action<string>? Failed;

    internal CanvasPresetGallery(CanvasPresetLibrary library)
    {
        this.library = library;
        var body = new DockPanel { MinWidth = 196 + WindowLayout.ScrollBarWidth, LastChildFill = true };
        var headings = new StackPanel();
        headings.Children.Add(Header("ToolOptions.Official")); headings.Children.Add(OfficialTiles);
        var userHeader = Header("ToolOptions.User"); userHeader.Margin = new(0, 12, 0, 4);
        var save = ActionIconButton.Create(PanelActionIcon.Add, L.Get("CanvasPreset.New"), Save);
        save.IsEnabled = library.CanEdit;
        if (!library.CanEdit) { save.ToolTip = L.Get("CanvasPreset.ReadOnly"); ToolTipService.SetShowOnDisabled(save, true); }
        DockPanel.SetDock(save, Dock.Right); userHeader.Children.Insert(0, save); headings.Children.Add(userHeader);
        DockPanel.SetDock(headings, Dock.Top); body.Children.Add(headings);
        var userArea = new Grid { MinHeight = 156 }; userArea.Children.Add(empty);
        userScroll = new ScrollViewer { Content = UserTiles, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        userArea.Children.Add(userScroll); body.Children.Add(userArea); Content = body;
        Refresh(new());
    }

    internal void Refresh(CanvasSettings settings)
    {
        current = settings.Clone(); var official = library.Official; var users = library.User;
        var selected = library.All.FirstOrDefault(p => p.Id == SelectedId); var actual = CanvasPresetParameters.From(current);
        if (selected is null || selectedDefinition is { } definition && (!definition.Matches(selected.Parameters) || !selected.Parameters.Matches(definition)) ||
            !(selectedSnapshot ?? selected.Parameters).Matches(actual) || selectedSnapshot is { } applied && !actual.Matches(applied))
        { SelectedId = ""; selectedSnapshot = selectedDefinition = null; }
        var focus = TileCollection.Capture(this, Tiles());
        TileCollection.Synchronize(OfficialTiles, official.Select(p => p.Id), id => Tile(official.Single(p => p.Id == id)),
            (button, id) => UpdateTile(button, official.Single(p => p.Id == id)));
        TileCollection.Synchronize(UserTiles, users.Select(p => p.Id), id =>
        {
            var preset = users.Single(p => p.Id == id); var button = Tile(preset); button.ContextMenu = Menu(preset);
            TileDragReorder.Attach(button, UserTiles, (target, after) => Run(() => { library.Move(id, (string)target.Tag, after); Refresh(current); }));
            button.PreviewKeyDown += (_, e) =>
            {
                if (Keyboard.Modifiers != ModifierKeys.None || e.Key is not (Key.F2 or Key.Delete)) return;
                e.Handled = true; if (e.IsRepeat) return;
                if (e.Key == Key.F2 && library.User.FirstOrDefault(item => item.Id == id) is { } latest) Rename(latest);
                else if (e.Key == Key.Delete) Delete(id);
            };
            return button;
        }, (button, id) => { var preset = users.Single(p => p.Id == id); UpdateTile(button, preset); button.ContextMenu = Menu(preset); });
        empty.Visibility = users.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        userScroll.Visibility = users.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        TileCollection.Restore(Tiles(), focus, SelectedId);
    }

    internal void Apply(string id) => Run(() =>
    {
        var preset = library.All.FirstOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException(id);
        SelectedId = id; selectedSnapshot = selectedDefinition = null;
        foreach (var button in Tiles()) button.SetResourceReference(BorderBrushProperty, Equals(button.Tag, id) ? "CanvasAccent" : "CanvasBorder");
        Applied?.Invoke(preset.Parameters.Clone());
        if (SelectedId == id) { selectedSnapshot = CanvasPresetParameters.From(current); selectedDefinition = preset.Parameters.Clone(); }
    });
    internal void SaveNamed(string name)
    {
        var saved = library.Save(name, current); SelectedId = saved.Id; selectedSnapshot = selectedDefinition = saved.Parameters.Clone(); Refresh(current); RevealSelection();
    }
    internal void Delete(string id) => Run(() => { library.Delete(id); if (SelectedId == id) { SelectedId = ""; selectedSnapshot = selectedDefinition = null; } Refresh(current); });
    private void Save()
    {
        string? name = NamePrompt.Show(this, L.Get("CanvasPreset.Name"), L.Get("CanvasPreset.DefaultName"));
        if (name is not null) Run(() => SaveNamed(name));
    }
    private void Rename(CanvasPreset preset)
    {
        string? name = NamePrompt.Show(this, L.Get("ToolOptions.Rename"), preset.Name);
        if (name is not null) Run(() => { library.Rename(preset.Id, name); Refresh(current); });
    }
    private Button Tile(CanvasPreset preset)
    {
        var button = new SelectionTileButton { Tag = preset.Id, MinWidth = 0, MinHeight = 0, Padding = new(4), Margin = new(0), BorderThickness = new(1) };
        ToolTipService.SetInitialShowDelay(button, 600); button.Click += (_, _) => Apply(preset.Id); UpdateTile(button, preset); return button;
    }
    private void UpdateTile(Button button, CanvasPreset preset)
    {
        button.ToolTip = preset.Name; AutomationProperties.SetName(button, preset.Name);
        button.Content = Preview(preset.Parameters);
        ((SelectionTileButton)button).SetSelected(preset.Id == SelectedId);
    }
    private OverlayContextMenu Menu(CanvasPreset preset)
    {
        var menu = new OverlayContextMenu();
        void Add(string key, PanelActionIcon icon, Action action, string shortcut = "")
        { var item = new MenuItem { Header = L.Get(key), Icon = ActionIconButton.Glyph(icon), InputGestureText = shortcut }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Add("ToolOptions.Overwrite", PanelActionIcon.Save, () => Run(() => { library.Update(preset.Id, current); SelectedId = preset.Id; selectedSnapshot = selectedDefinition = CanvasPresetParameters.From(current); Refresh(current); }));
        Add("ToolOptions.Rename", PanelActionIcon.Rename, () => Rename(preset), "F2");
        Add("ToolOptions.Delete", PanelActionIcon.Delete, () => Delete(preset.Id), "Delete"); return menu;
    }
    private IEnumerable<Button> Tiles() => OfficialTiles.Children.OfType<Button>().Concat(UserTiles.Children.OfType<Button>());
    private void RevealSelection() => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
    { if (IsVisible) Tiles().FirstOrDefault(button => Equals(button.Tag, SelectedId))?.BringIntoView(); }));
    private void Run(Action action) { try { action(); } catch (Exception ex) { Failed?.Invoke(ex.Message); } }
    private static DockPanel Header(string key)
    {
        var panel = new DockPanel { Height = 34, Margin = new(0, 0, 0, 4) };
        panel.Children.Add(PropertyPanelLayout.SectionTitle(L.Get(key))); return panel;
    }
    private static Image Preview(CanvasPresetParameters parameters)
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            for (int y = 0; y < 24; y += 6)
                for (int x = 0; x < 24; x += 6)
                    context.DrawRectangle((x + y) % 12 == 0 ? Brushes.White : Brushes.LightGray, null, new Rect(x, y, 6, 6));
            var background = new SolidColorBrush(ToColor(parameters.BackgroundColor));
            context.DrawRectangle(background, null, new Rect(0, 0, 24, 24));
            if (parameters.GridVisible)
            {
                var pen = new Pen(new SolidColorBrush(ToColor(parameters.GridColor)), Math.Min(2, parameters.GridWidth))
                { DashStyle = parameters.GridPattern switch { StrokePattern.Dashed => DashStyles.Dash, StrokePattern.Dotted => DashStyles.Dot, _ => DashStyles.Solid } };
                for (int i = 6; i < 24; i += 6)
                { context.DrawLine(pen, new(i, 0), new(i, 24)); context.DrawLine(pen, new(0, i), new(24, i)); }
            }
            context.DrawRectangle(null, new Pen(Brushes.Gray, .75), new Rect(.5, .5, 23, 23));
        }
        drawing.Freeze(); var image = new DrawingImage(drawing); image.Freeze(); return new() { Source = image, Width = 24, Height = 24 };
    }
    private static Color ToColor(uint color) => Color.FromArgb((byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color);
}
