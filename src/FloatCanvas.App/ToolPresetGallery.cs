using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>One selected recipe per tool, with identical compact official and personal tiles.</summary>
internal sealed class ToolPresetGallery : UserControl
{
    private readonly EditorController controller;
    private readonly ToolPresetLibrary library;
    private readonly Dictionary<EditorTool, string> selections = [];
    private readonly Dictionary<string, ToolParameters> officialBaselines = [];
    private readonly Dictionary<EditorTool, ToolParameters> previewSources = [];
    internal readonly PixelTilePanel OfficialTiles = new(5, 36, 4), UserTiles = new(5, 36, 4);
    private readonly TextBlock empty = new() { Text = L.Get("ToolOptions.EmptyUser"), FontSize = 11, Opacity = .7, Margin = new(2, 6, 0, 0) };
    private readonly ScrollViewer userScroll;
    private readonly StackPanel headings;
    private readonly Grid userArea = new();
    private EditorTool? observedTool;
    internal string SelectedId => selections.GetValueOrDefault(ToolPresetLibrary.Family(controller.Tool)) ?? "";
    internal event Action? Applied;
    internal event Action<string>? Failed;

    internal ToolPresetGallery(EditorController controller, ToolPresetLibrary library)
    {
        this.controller = controller; this.library = library;
        var body = new DockPanel { MinWidth = 196 + SystemParameters.VerticalScrollBarWidth, LastChildFill = true };
        headings = new StackPanel();
        var officialHeader = new DockPanel { Height = 34, Margin = new(0, 0, 0, 4) };
        officialHeader.Children.Add(Heading("ToolOptions.Official"));
        headings.Children.Add(officialHeader); headings.Children.Add(OfficialTiles);
        DockPanel.SetDock(headings, Dock.Top); body.Children.Add(headings);
        var userHeader = new DockPanel { Height = 34, Margin = new(0, 12, 0, 4) };
        var save = ActionIconButton.Create(PanelActionIcon.Add, L.Get("ToolOptions.NewPresetHint"), Save);
        DockPanel.SetDock(save, Dock.Right); userHeader.Children.Add(save); userHeader.Children.Add(Heading("ToolOptions.User")); headings.Children.Add(userHeader);
        userArea.Children.Add(empty);
        userScroll = new ScrollViewer { Content = UserTiles, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        userArea.Children.Add(userScroll); body.Children.Add(userArea); Content = body; Refresh();
    }

    protected override Size MeasureOverride(Size constraint)
    {
        headings.Measure(new Size(constraint.Width, double.PositiveInfinity));
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleY;
        double row = Math.Round(UserTiles.TileSize * dpi, MidpointRounding.AwayFromZero) / dpi;
        double gap = Math.Round(UserTiles.Gap * dpi, MidpointRounding.AwayFromZero) / dpi;
        // Four rows set the desired height. More recipes scroll rather than growing
        // the window; Arrange can still stretch the viewport beside taller options.
        userArea.MinHeight = 4 * row + 3 * gap;
        return base.MeasureOverride(new Size(constraint.Width, headings.DesiredSize.Height + userArea.MinHeight));
    }

    internal void Refresh(bool revealSelection = false)
    {
        var tool = ToolPresetLibrary.Family(controller.Tool); var official = library.OfficialFor(tool); var users = library.UserFor(tool);
        if (!previewSources.ContainsKey(tool)) previewSources[tool] = controller.Parameters;
        var focus = TileCollection.Capture(this, Tiles());
        bool toolChanged = observedTool != tool; observedTool = tool;
        if (!official.Concat(users).Any(p => p.Id == SelectedId)) selections[tool] = official.First().Id;
        if (SelectedId.StartsWith("official.", StringComparison.Ordinal) && !officialBaselines.ContainsKey(SelectedId))
            officialBaselines[SelectedId] = library.Apply(SelectedId, controller.Parameters);
        TileCollection.Synchronize(OfficialTiles, official.Select(p => p.Id), id => Tile(official.Single(p => p.Id == id)), (button, id) => UpdateTile(button, official.Single(p => p.Id == id)));
        TileCollection.Synchronize(UserTiles, users.Select(p => p.Id), id =>
        {
            var preset = users.Single(p => p.Id == id);
            var button = Tile(preset); button.ContextMenu = Menu(preset);
            TileDragReorder.Attach(button, UserTiles, (target, after) => Run(() => { library.Move(preset.Id, (string)target.Tag, after); Refresh(); }));
            button.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.F2 && library.User.FirstOrDefault(item => item.Id == id) is { } current) { Rename(current); e.Handled = true; }
                else if (e.Key == Key.Delete) { Delete(preset); e.Handled = true; }
            };
            return button;
        }, (button, id) => { var preset = users.Single(p => p.Id == id); UpdateTile(button, preset); button.ContextMenu = Menu(preset); });
        empty.Visibility = users.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        userScroll.Visibility = users.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        TileCollection.Restore(Tiles(), focus, SelectedId);
        if (revealSelection || toolChanged) RevealSelection();
    }

    internal void Apply(string id) => Run(() =>
    {
        var tool = ToolPresetLibrary.Family(controller.Tool);
        if (!library.OfficialFor(tool).Concat(library.UserFor(tool)).Any(p => p.Id == id)) return;
        selections[tool] = id;
        if (id.StartsWith("official.", StringComparison.Ordinal) && !officialBaselines.ContainsKey(id)) officialBaselines[id] = library.Apply(id, controller.Parameters);
        controller.ApplyParameters(officialBaselines.TryGetValue(id, out var baseline) ? baseline.Clone() : library.Apply(id, controller.Parameters));
        Refresh(revealSelection: true); Applied?.Invoke();
    });
    internal void SaveNamed(string name)
    {
        var saved = library.Save(name, controller.Parameters); selections[ToolPresetLibrary.Family(controller.Tool)] = saved.Id; Refresh(revealSelection: true);
    }
    private void Save()
    {
        string? name = NamePrompt.Show(this, L.Get("ToolOptions.Name"), L.Get("Tool." + controller.Tool));
        if (name is not null) Run(() => SaveNamed(name));
    }
    private void Rename(ToolPreset preset)
    {
        string? name = NamePrompt.Show(this, L.Get("ToolOptions.Rename"), preset.Name);
        if (name is not null) Run(() => { library.Rename(preset.Id, name); Refresh(); });
    }
    internal void Delete(ToolPreset preset) => Run(() =>
    {
        bool selected = SelectedId == preset.Id; library.Delete(preset.Id);
        if (selected) { selections[ToolPresetLibrary.Family(controller.Tool)] = library.OfficialFor(controller.Tool).First().Id; Apply(SelectedId); }
        else Refresh();
    });
    private Button Tile(ToolPreset preset)
    {
        var button = new Button { Tag = preset.Id, MinWidth = 0, MinHeight = 0, Padding = new(4), Margin = new(0),
            BorderThickness = new(1),
            ToolTip = preset.Name, Content = ToolParameterIcon.Create(PreviewParameters(preset), 24) };
        button.SetResourceReference(BorderBrushProperty, preset.Id == SelectedId ? "CanvasAccent" : "CanvasBorder");
        ToolTipService.SetInitialShowDelay(button, 600); AutomationProperties.SetName(button, preset.Name);
        button.Click += (_, _) => Apply(preset.Id); return button;
    }
    private void UpdateTile(Button button, ToolPreset preset)
    {
        button.ToolTip = preset.Name; AutomationProperties.SetName(button, preset.Name);
        button.Content = ToolParameterIcon.Create(PreviewParameters(preset), 24);
        button.SetResourceReference(BorderBrushProperty, preset.Id == SelectedId ? "CanvasAccent" : "CanvasBorder");
    }
    private ToolParameters PreviewParameters(ToolPreset preset) => !preset.IsOfficial ? preset.Parameters :
        officialBaselines.GetValueOrDefault(preset.Id) ?? library.Apply(preset.Id, previewSources[ToolPresetLibrary.Family(preset.Parameters.Tool)]);
    private IEnumerable<Button> Tiles() => OfficialTiles.Children.OfType<Button>().Concat(UserTiles.Children.OfType<Button>());
    private void RevealSelection() => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
    {
        if (!IsVisible) return;
        var selected = Tiles().FirstOrDefault(button => Equals(button.Tag, SelectedId));
        selected?.BringIntoView();
    }));
    private ContextMenu Menu(ToolPreset preset)
    {
        var menu = new ContextMenu();
        void Add(string key, PanelActionIcon icon, Action action, string shortcut = "")
        { var item = new MenuItem { Header = L.Get(key), Icon = ActionIconButton.Glyph(icon), InputGestureText = shortcut }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Add("ToolOptions.Overwrite", PanelActionIcon.Save, () => Run(() => { library.Update(preset.Id, controller.Parameters); Refresh(); }));
        Add("ToolOptions.Rename", PanelActionIcon.Rename, () => Rename(preset), "F2");
        Add("ToolOptions.Delete", PanelActionIcon.Delete, () => Delete(preset), "Delete"); return menu;
    }
    private void Run(Action action) { try { action(); } catch (Exception ex) { Failed?.Invoke(ex.Message); } }
    private static TextBlock Heading(string key) => new() { Text = L.Get(key), FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
}
