using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Color adjustments beside ten-column recent and personal swatches.</summary>
internal sealed class ColorPickerPanel : UserControl
{
    private readonly ColorLibrary library;
    private readonly TextBox hex = new() { Height = PropertyPanelLayout.ControlHeight, MinHeight = PropertyPanelLayout.ControlHeight, Padding = new(8, 0, 8, 0), MaxLength = 9, MinWidth = 130, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly NumericValueField alphaField = new(0, 100) { Unit = "%" };
    private readonly Slider alpha;
    private readonly Border preview = new() { Height = PropertyPanelLayout.ControlHeight, Width = 42, CornerRadius = new(5), Margin = new Thickness(0, 0, 8, 0), BorderThickness = new Thickness(1), Background = Brushes.Transparent };
    private readonly ColorClipboardMenu clipboard;
    private readonly ColorSpectrum spectrum = new();
    private readonly ColorHueSlider hue = new() { Height = 28, Margin = new Thickness(0, 6, 0, 8) };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 5, 0, 0), Visibility = Visibility.Collapsed };
    private readonly PixelTilePanel history = new(10, 28), pinned = new(10, 28);
    private readonly TextBlock historyHeading = Heading("Color.History"), pinnedHeading = Heading("Color.Pinned");
    private bool syncing;
    private bool valueDirty = true, collectionsDirty = true, previewDirty, preparing;
    private uint value, previewColor;
    private uint ClipboardColor => TryGetDraft(out uint color) ? color : previewColor;
    private string? revealSwatch;
    private uint gestureStart;
    internal bool IsInteracting { get; private set; }
    internal bool HasCancellableGesture => IsInteracting && (hue.IsPointerInteracting || alphaField.IsInteracting || spectrum.IsMouseCaptured ||
        !TryGetDraft(out uint color) || color != gestureStart);
    internal Func<bool>? CanBeginGesture { get; set; }
    internal event Action? GestureStarted;
    internal event Action<bool>? GestureCompleted;
    internal Func<Window?>? DialogOwner { get; set; }
    internal FrameworkElement DragHandle { get; }
    internal string ErrorMessage => error.Text;
    public event Action<uint>? ColorChosen;
    public event Action<uint>? DraftChanged;
    public event Action? EyedropperRequested;
    public uint Value
    {
        get => value;
        set
        {
            this.value = value; valueDirty = true;
            if (IsVisible || preparing) SynchronizeValue();
        }
    }

    public ColorPickerPanel(ColorRole role, ColorLibrary library)
    {
        alpha = alphaField.Slider;
        this.library = library;
        SetResourceReference(BackgroundProperty, "CanvasSurface");
        SetResourceReference(ForegroundProperty, "CanvasText");
        var body = new StackPanel { Width = 300, VerticalAlignment = VerticalAlignment.Top };
        // Reserve scrollbar space outside the ten-column grid, even when it appears.
        var collections = new StackPanel { MinWidth = 298 + WindowLayout.ScrollBarWidth,
            VerticalAlignment = VerticalAlignment.Top };
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        columns.ColumnDefinitions.Add(new() { Width = new GridLength(24) });
        columns.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        columns.Children.Add(body);
        var divider = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Center };
        divider.SetResourceReference(Border.BackgroundProperty, "CanvasBorder");
        Grid.SetColumn(divider, 1); columns.Children.Add(divider);
        Grid.SetColumn(collections, 2); columns.Children.Add(collections);
        var title = new TextBlock { Text = L.Get(role == ColorRole.Foreground ? "Color.Foreground" : "Color.Background"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        DragHandle = title;
        body.Children.Add(title);
        collections.Children.Add(historyHeading); collections.Children.Add(history);
        var savedHeader = new DockPanel(); var save = IconButton(PanelActionIcon.Add, "Color.SaveSwatch", SaveSwatch);
        DockPanel.SetDock(save, Dock.Right); savedHeader.Children.Add(save); savedHeader.Children.Add(pinnedHeading); collections.Children.Add(savedHeader);
        collections.Children.Add(new ScrollViewer { Content = pinned, Height = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var customLabel = PropertyPanelLayout.Label(L.Get("Color.Custom")); customLabel.Margin = new(0, 7, 0, 5); body.Children.Add(customLabel);
        body.Children.Add(spectrum);
        AutomationProperties.SetName(hue, L.Get("Color.Hue"));
        hue.SetResourceReference(StyleProperty, "CanvasHueSlider"); body.Children.Add(hue);
        var input = new Grid(); input.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); input.ColumnDefinitions.Add(new ColumnDefinition());
        preview.SetResourceReference(Border.BorderBrushProperty, "CanvasBorder"); input.Children.Add(preview);
        input.Children.Add(hex); Grid.SetColumn(hex, 1); body.Children.Add(input);
        hex.ToolTip = "#RGB / #RRGGBB / #AARRGGBB"; AutomationProperties.SetName(hex, L.Get("Color.Hex"));
        var alphaName = new PropertyLabel(L.Get("Color.Alpha"));
        var alphaRow = PropertyPanelLayout.Row(alphaName, alphaField);
        alphaRow.Margin = new(0, 8, 0, 5); body.Children.Add(alphaRow);
        alphaField.SetScrubAnchor(NumericPropertyKind.Opacity, L.Get("Color.Alpha"));
        alphaField.SetLabel(alphaName); AutomationProperties.SetName(alphaField.Input, L.Get("Color.Alpha"));
        AutomationProperties.SetName(alpha, L.Get("Color.Alpha"));
        var actions = new WrapPanel();
        actions.Children.Add(IconButton(PanelActionIcon.Eyedropper, "Color.Eyedropper", () => EyedropperRequested?.Invoke()));
        actions.Children.Add(IconButton(PanelActionIcon.Transparent, "Color.Transparent", () => Choose(0)));
        body.Children.Add(actions);
        error.SetResourceReference(TextBlock.ForegroundProperty, "CanvasErrorText"); body.Children.Add(error);
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        Content = new ScrollViewer { Margin = WindowLayout.ContentMargin(), Content = columns, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        clipboard = new ColorClipboardMenu(preview, () => ClipboardColor, PasteColor, ShowError);
        preview.ToolTip = L.Get("Color.CopyPreviewHint"); preview.Cursor = Cursors.Hand;
        AutomationProperties.SetName(preview, L.Get("Color.CopyPreviewHint"));
        hex.TextChanged += (_, _) =>
        {
            if (syncing) return;
            if (ColorLibrary.TryParseHtml(hex.Text, out var parsed) && hex.Text.Trim().TrimStart('#').Length == 8)
            {
                syncing = true; alphaField.Value = (parsed >> 24) * 100d / 255; syncing = false;
            }
            ClearInputError(); RefreshPreview();
        };
        hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Run(Apply); e.Handled = true; } };
        hex.GotKeyboardFocus += (_, _) => BeginGesture();
        hex.LostKeyboardFocus += (_, _) => { if (!syncing && !TryRead(out _)) InvalidInput(); EndGesture(true); };
        alphaField.Input.GotKeyboardFocus += (_, _) => BeginGesture();
        alphaField.Input.LostKeyboardFocus += (_, _) => { if (!alphaField.IsInteracting) EndGesture(true); };
        alphaField.CanBeginGesture = BeginGesture;
        alphaField.GestureCompleted += EndGesture;
        spectrum.CanBeginGesture = BeginGesture;
        spectrum.GestureCompleted += EndGesture;
        hue.CanBeginPointerGesture = BeginGesture;
        hue.PointerGestureCompleted += EndGesture;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && IsInteracting) { EndGesture(false); e.Handled = true; } };
        Unloaded += (_, _) => EndGesture(false);
        alphaField.ValueChanged += _ => { if (!syncing) { ClearInputError(); RefreshPreview(); } };
        alphaField.InvalidInput += () => { if (!syncing) InvalidInput(); };
        hue.ValueChanged += (_, _) => { if (!syncing) spectrum.ChangeHue(hue.Value); };
        spectrum.ColorChanged += color => { if (!syncing) hex.Text = ColorLibrary.ToHtml(color, false); };
        preview.MouseLeftButtonDown += (_, e) => { spectrum.Focus(); clipboard.Copy(ColorClipboardFormat.HexArgb); e.Handled = true; };
        Value = role == ColorRole.Background ? 0u : ColorLibrary.ForegroundRed;
        RefreshCollections();
        IsVisibleChanged += (_, _) => { if (IsVisible) PrepareForShow(); };
        Loaded += (_, _) => { if (IsVisible) PrepareForShow(); };
    }

    internal bool BeginGesture() => BeginGesture(null);
    private bool BeginGesture(uint? fallback)
    {
        if (syncing) return false;
        if (IsInteracting) return true;
        if (CanBeginGesture?.Invoke() == false) return false;
        if (!TryGetDraft(out gestureStart))
        {
            if (fallback is not uint color) return false;
            gestureStart = color;
        }
        IsInteracting = true;
        GestureStarted?.Invoke();
        return true;
    }

    internal void EndGesture(bool commit)
    {
        if (!IsInteracting) return;
        IsInteracting = false;
        if (!commit)
        {
            bool wasSyncing = syncing; syncing = true;
            try { alphaField.CancelGesture(); spectrum.CancelGesture(); ReleaseHueCapture(); Value = gestureStart; }
            finally { syncing = wasSyncing; }
            DraftChanged?.Invoke(gestureStart);
        }
        else hue.EndPointerGesture(true);
        GestureCompleted?.Invoke(commit);
    }

    internal void SynchronizeExternalValue(uint color)
    {
        bool wasSyncing = syncing; syncing = true;
        try
        {
            bool active = IsInteracting;
            IsInteracting = false;
            alphaField.CancelGesture(); spectrum.CancelGesture(); ReleaseHueCapture();
            Value = color;
            if (active) GestureCompleted?.Invoke(false);
        }
        finally { syncing = wasSyncing; }
    }

    private void ReleaseHueCapture()
    {
        hue.EndPointerGesture(false);
    }

    internal void PrepareForShow()
    {
        if (preparing) return;
        preparing = true;
        try
        {
            if (valueDirty) SynchronizeValue();
            else if (previewDirty)
            {
                bool wasSyncing = syncing; syncing = true;
                try { RefreshPreview(); }
                finally { syncing = wasSyncing; }
            }
            if (collectionsDirty) RefreshCollections();
        }
        finally { preparing = false; }
    }

    private void SynchronizeValue()
    {
        valueDirty = false;
        bool wasSyncing = syncing; syncing = true;
        try { hex.Text = ColorLibrary.ToHtml(value, false); alphaField.Value = (value >> 24) * 100d / 255; ClearInputError(); RefreshPreview(); }
        finally { syncing = wasSyncing; }
    }

    public void RefreshCollections()
    {
        collectionsDirty = true;
        if (!IsVisible && !preparing) return;
        collectionsDirty = false;
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
            var item = new MenuItem { Header = L.Get(key), Icon = ActionIconButton.Glyph(icon) }; item.Click += (_, _) => { menu.IsOpen = false; Run(action); }; menu.Items.Add(item);
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
    private void PasteColor(uint color)
    {
        if (!BeginGesture(ClipboardColor)) return;
        try { Choose(color); }
        finally { EndGesture(true); }
    }
    internal bool TryGetDraft(out uint color)
    {
        if (valueDirty) { color = value; return true; }
        return TryRead(out color);
    }
    internal void CommitDraft(Action<uint> commit) => Run(() => { if (TryRead(out uint color)) commit(color); });
    private void SaveSwatch()
    {
        if (!TryRead(out uint color)) { InvalidInput(); return; }
        string? name = NamePrompt.Show(this, L.Get("Color.SwatchName"), ColorLibrary.ToHtml(color), owner: DialogOwner?.Invoke());
        if (name is not null) { revealSwatch = library.Save(name, color).Id; RefreshCollections(); }
    }
    private bool TryRead(out uint color)
    {
        if (!ColorLibrary.TryParseHtml(hex.Text, out color) || !alphaField.TryGetValue(out _)) return false;
        color = (color & 0x00FFFFFF) | ((uint)Math.Clamp(Math.Round(alpha.Value * 255 / 100), 0, 255) << 24); return true;
    }
    private void RefreshPreview()
    {
        previewDirty = true;
        if (!IsVisible && !preparing) return;
        previewDirty = false;
        if (TryRead(out var color))
        {
            previewColor = color;
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
        error.Visibility = Visibility.Visible;
        hex.SetResourceReference(BorderBrushProperty, "CanvasErrorText");
        AutomationProperties.SetHelpText(hex, error.Text);
        System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(error)?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
        error.BringIntoView();
    }
    private void ClearInputError()
    {
        error.Text = ""; error.Visibility = Visibility.Collapsed;
        hex.SetResourceReference(BorderBrushProperty, "CanvasBorder"); AutomationProperties.SetHelpText(hex, "");
    }
    internal void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { ShowError(ex.Message); }
    }
    private void ShowError(string message)
    { error.Text = "⚠ " + message; error.Visibility = Visibility.Visible; error.BringIntoView(); }
    private System.Windows.Controls.Button IconButton(PanelActionIcon icon, string key, Action action, string? shortcut = null) =>
        ActionIconButton.Create(icon, L.Get(key), () => Run(action), shortcut);
    private System.Windows.Controls.Button SwatchButton(uint color, string name, Action action)
    {
        var button = new ColorSwatchButton(color) { ToolTip = name + " " + ColorLibrary.ToHtml(color) };
        AutomationProperties.SetName(button, name + " " + ColorLibrary.ToHtml(color)); button.Click += (_, _) => Run(action); return button;
    }
    private static TextBlock Heading(string key)
    { var label = PropertyPanelLayout.SectionTitle(L.Get(key)); label.Margin = new(0, 7, 0, 5); return label; }
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
