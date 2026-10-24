using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Detached popup content: role colors, exact custom ARGB, shared history and removable pins.</summary>
internal sealed class ColorPickerPanel : UserControl
{
    private readonly ColorLibrary library;
    private readonly TextBox hex = new() { MinHeight = 32, MaxLength = 9, MinWidth = 130, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Slider alpha = new() { Minimum = 0, Maximum = 100, Width = 175, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock alphaLabel = new() { Width = 48, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border preview = new() { Height = 28, Width = 42, Margin = new Thickness(0, 0, 8, 0), BorderThickness = new Thickness(1) };
    private readonly ColorSpectrum spectrum = new();
    private readonly Slider hue = new() { Minimum = 0, Maximum = 360, Height = 20, Margin = new Thickness(0, 4, 0, 3) };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 5, 0, 0) };
    private readonly PixelTilePanel history = new(10, 28), pinned = new(10, 28);
    private readonly TextBlock historyHeading = Heading("Color.History"), pinnedHeading = Heading("Color.Pinned");
    private bool syncing;
    private readonly Button undo;
    private uint value;
    private string? revealSwatch;
    internal Func<Window?>? DialogOwner { get; set; }
    public event Action<uint>? ColorChosen;
    public event Action<uint>? DraftChanged;
    public event Action? EyedropperRequested;
    public uint Value
    {
        get => value;
        set
        {
            this.value = value; syncing = true;
            try { hex.Text = ColorLibrary.ToHtml(value, false); alpha.Value = (value >> 24) * 100d / 255; ClearInputError(); RefreshPreview(); }
            finally { syncing = false; }
        }
    }

    public ColorPickerPanel(ColorRole role, ColorLibrary library)
    {
        this.library = library;
        SetResourceReference(BackgroundProperty, "CanvasSurface");
        SetResourceReference(ForegroundProperty, "CanvasText");
        // Reserve scrollbar space outside the ten-column color grid, even when it appears.
        var body = new StackPanel { MinWidth = 298 + SystemParameters.VerticalScrollBarWidth, Margin = new Thickness(12) };
        var title = new TextBlock { Text = L.Get(role == ColorRole.Foreground ? "Color.Foreground" : "Color.Background"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        body.Children.Add(title);
        body.Children.Add(historyHeading); body.Children.Add(history);
        var savedHeader = new DockPanel(); var save = IconButton(PanelActionIcon.Add, "Color.SaveSwatch", SaveSwatch);
        DockPanel.SetDock(save, Dock.Right); savedHeader.Children.Add(save); savedHeader.Children.Add(pinnedHeading); body.Children.Add(savedHeader);
        body.Children.Add(new ScrollViewer { Content = pinned, Height = 84, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        body.Children.Add(Heading("Color.Custom"));
        body.Children.Add(spectrum);
        AutomationProperties.SetName(hue, L.Get("Color.Hue"));
        var hueColors = new GradientStopCollection
        {
            new(Colors.Red, 0), new(Colors.Yellow, 1d / 6), new(Colors.Lime, 2d / 6), new(Colors.Cyan, .5),
            new(Colors.Blue, 4d / 6), new(Colors.Magenta, 5d / 6), new(Colors.Red, 1)
        };
        var hueTrack = new Border { Background = new LinearGradientBrush(hueColors, 0), CornerRadius = new CornerRadius(3), Height = 5,
            Margin = new Thickness(7, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        var hueRow = new Grid(); hueRow.Children.Add(hueTrack); hueRow.Children.Add(hue); body.Children.Add(hueRow);
        var input = new Grid(); input.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); input.ColumnDefinitions.Add(new ColumnDefinition());
        preview.SetResourceReference(Border.BorderBrushProperty, "CanvasBorder"); input.Children.Add(preview);
        input.Children.Add(hex); Grid.SetColumn(hex, 1); body.Children.Add(input);
        hex.ToolTip = "#RGB / #RRGGBB / #AARRGGBB"; AutomationProperties.SetName(hex, L.Get("Color.Hex"));
        var alphaRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 5) };
        var alphaName = new TextBlock { Text = L.Get("Color.Alpha"), Width = 55, VerticalAlignment = VerticalAlignment.Center };
        alphaRow.Children.Add(alphaName); alphaRow.Children.Add(alpha); alphaRow.Children.Add(alphaLabel); body.Children.Add(alphaRow);
        AutomationProperties.SetName(alpha, L.Get("Color.Alpha"));
        var actions = new WrapPanel();
        actions.Children.Add(IconButton(PanelActionIcon.Eyedropper, "Color.Eyedropper", () => EyedropperRequested?.Invoke()));
        actions.Children.Add(IconButton(PanelActionIcon.Transparent, "Color.Transparent", () => Choose(0)));
        body.Children.Add(actions);
        undo = new Button { Content = L.Get("Audit.UndoResource"), MinHeight = 28 };
        undo.Click += (_, _) => Run(() => { library.UndoResourceChange(); RefreshCollections(); }); body.Children.Add(undo);
        error.SetResourceReference(TextBlock.ForegroundProperty, "CanvasErrorText"); body.Children.Add(error);
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        Content = new ScrollViewer { Content = body, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        hex.TextChanged += (_, _) =>
        {
            if (syncing) return;
            if (ColorLibrary.TryParseHtml(hex.Text, out var parsed) && hex.Text.Trim().TrimStart('#').Length == 8)
            {
                syncing = true; alpha.Value = (parsed >> 24) * 100d / 255; syncing = false;
            }
            ClearInputError(); RefreshPreview();
        };
        hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Run(Apply); e.Handled = true; } };
        hex.LostKeyboardFocus += (_, _) => { if (!syncing && !TryRead(out _)) InvalidInput(); };
        alpha.ValueChanged += (_, _) => { if (!syncing) RefreshPreview(); };
        hue.ValueChanged += (_, _) => { if (!syncing) spectrum.ChangeHue(hue.Value); };
        spectrum.ColorChanged += color => { if (!syncing) hex.Text = ColorLibrary.ToHtml(color, false); };
        preview.MouseLeftButtonDown += (_, e) => { spectrum.Focus(); e.Handled = true; };
        Value = role == ColorRole.Background ? 0u : ColorLibrary.ForegroundRed;
        RefreshCollections();
    }

    public void RefreshCollections()
    {
        undo.IsEnabled = library.CanUndoResourceChange;
        var focus = TileCollection.Capture(this, history.Children.OfType<Button>().Concat(pinned.Children.OfType<Button>()));
        TileCollection.Synchronize(history, library.History.Select(color => color.ToString("X8")), id =>
        {
            uint color = uint.Parse(id, System.Globalization.NumberStyles.HexNumber); var button = SwatchButton(color, ColorLibrary.ToHtml(color), () => Choose(color)); button.Tag = id; return button;
        }, (_, _) => { });
        var saved = library.Swatches.ToDictionary(color => color.Id, StringComparer.Ordinal);
        TileCollection.Synchronize(pinned, library.Swatches.Select(color => color.Id), id =>
        {
            var button = SwatchButton(saved[id].Value, saved[id].Name, () =>
            { if (library.Swatches.FirstOrDefault(color => color.Id == id) is { } current) Choose(current.Value); });
            button.Tag = id;
            TileDragReorder.Attach(button, pinned, (target, after) => Run(() => { library.Move(id, (string)target.Tag, after); RefreshCollections(); }));
            button.PreviewKeyDown += (_, e) => { if (e.Key != Key.Delete) return; Run(() => { library.Delete(id); RefreshCollections(); }); e.Handled = true; };
            return button;
        }, (button, id) =>
        {
            var color = saved[id]; button.Content = CreateSwatch(color.Value); button.ToolTip = color.Name + " " + ColorLibrary.ToHtml(color.Value);
            AutomationProperties.SetName(button, (string)button.ToolTip); button.ContextMenu = SavedColorMenu(color);
        });
        historyHeading.Visibility = history.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        pinnedHeading.Text = L.Get("Color.MySwatches");
        TileCollection.Restore(history.Children.OfType<Button>().Concat(pinned.Children.OfType<Button>()), focus,
            pinned.Children.OfType<Button>().FirstOrDefault()?.Tag as string ?? history.Children.OfType<Button>().FirstOrDefault()?.Tag as string);
        if (revealSwatch is not null) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (!IsVisible) return;
            var button = pinned.Children.OfType<Button>().FirstOrDefault(tile => Equals(tile.Tag, revealSwatch));
            button?.BringIntoView(); revealSwatch = null;
        }));
    }
    private OverlayContextMenu SavedColorMenu(SavedColor saved)
    {
        var menu = new OverlayContextMenu();
        void Add(string key, PanelActionIcon icon, Action action)
        {
            var item = new MenuItem { Header = L.Get(key), Icon = ActionIconButton.Glyph(icon) }; item.Click += (_, _) => Run(action); menu.Items.Add(item);
        }
        Add("ToolOptions.Overwrite", PanelActionIcon.Save, () => { if (TryRead(out uint color)) { library.Update(saved.Id, color); RefreshCollections(); } });
        Add("ToolOptions.Rename", PanelActionIcon.Rename, () =>
        {
            string? name = NamePrompt.Show(this, L.Get("Color.SwatchName"), saved.Name, owner: DialogOwner?.Invoke());
            if (name is not null) { library.Rename(saved.Id, name); RefreshCollections(); }
        });
        Add("ToolOptions.Delete", PanelActionIcon.Delete, () => { library.Delete(saved.Id); RefreshCollections(); }); return menu;
    }
    private void Apply() { if (TryRead(out uint color)) ColorChosen?.Invoke(color); else InvalidInput(); }
    private void Choose(uint color) { Value = color; DraftChanged?.Invoke(color); }
    internal bool TryGetDraft(out uint color) => TryRead(out color);
    internal void CommitDraft(Action<uint> commit) => Run(() => { if (TryRead(out uint color)) commit(color); });
    private void SaveSwatch()
    {
        if (!TryRead(out uint color)) { InvalidInput(); return; }
        string? name = NamePrompt.Show(this, L.Get("Color.SwatchName"), ColorLibrary.ToHtml(color), owner: DialogOwner?.Invoke());
        if (name is not null) { revealSwatch = library.Save(name, color).Id; RefreshCollections(); }
    }
    private bool TryRead(out uint color)
    {
        if (!ColorLibrary.TryParseHtml(hex.Text, out color)) return false;
        color = (color & 0x00FFFFFF) | ((uint)Math.Clamp(Math.Round(alpha.Value * 255 / 100), 0, 255) << 24); return true;
    }
    private void RefreshPreview()
    {
        alphaLabel.Text = Math.Round(alpha.Value).ToString(System.Globalization.CultureInfo.CurrentCulture) + "%";
        if (TryRead(out var color))
        {
            preview.Child = CreateSwatch(color);
            bool wasSyncing = syncing; syncing = true;
            try
            {
                // Preserve the user's position on the circular hue strip and avoid
                // round-tripping the live saturation/value gesture through 8-bit RGB.
                if ((spectrum.SelectedColor & 0xFFFFFF) != (color & 0xFFFFFF)) { spectrum.SelectedColor = color; hue.Value = spectrum.Hue; }
            }
            finally { syncing = wasSyncing; }
            if (!syncing) DraftChanged?.Invoke(color);
        }
    }
    private void InvalidInput()
    {
        error.Text = "⚠ " + L.Get("Color.InvalidHex");
        hex.SetResourceReference(BorderBrushProperty, "CanvasErrorText");
        AutomationProperties.SetHelpText(hex, error.Text);
        System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(error)?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
        error.BringIntoView();
    }
    private void ClearInputError()
    {
        error.Text = ""; hex.SetResourceReference(BorderBrushProperty, "CanvasBorder"); AutomationProperties.SetHelpText(hex, "");
    }
    internal void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { error.Text = "⚠ " + ex.Message; }
    }
    private System.Windows.Controls.Button IconButton(PanelActionIcon icon, string key, Action action, string? shortcut = null) =>
        ActionIconButton.Create(icon, L.Get(key), () => Run(action), shortcut);
    private System.Windows.Controls.Button SwatchButton(uint color, string name, Action action)
    {
        var button = new ColorSwatchButton(color) { ToolTip = name + " " + ColorLibrary.ToHtml(color) };
        AutomationProperties.SetName(button, name + " " + ColorLibrary.ToHtml(color)); button.Click += (_, _) => Run(action); return button;
    }
    private static TextBlock Heading(string key) => new() { Text = L.Get(key), FontSize = 11, Opacity = .75, Margin = new Thickness(0, 7, 0, 5) };
    internal static Grid CreateSwatch(uint color)
    {
        var grid = new Grid { IsHitTestVisible = false };
        var checker = new DrawingGroup();
        checker.Children.Add(new GeometryDrawing(Brushes.White,null,new RectangleGeometry(new Rect(0,0,8,8))));
        checker.Children.Add(new GeometryDrawing(Brushes.LightGray,null,Geometry.Parse("M0,0 H4 V4 H0 Z M4,4 H8 V8 H4 Z")));
        var brush = new DrawingBrush(checker) {TileMode=TileMode.Tile,ViewportUnits=BrushMappingMode.Absolute,Viewport=new Rect(0,0,8,8),Stretch=Stretch.None};
        brush.Freeze();
        grid.Children.Add(new Border {Background=brush});
        grid.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb((byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color)) });
        return grid;
    }
}
