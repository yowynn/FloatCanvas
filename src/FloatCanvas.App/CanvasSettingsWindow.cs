using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed class CanvasSettingsWindow : Window
{
    private readonly ColorField background = new(ColorRole.Background), gridColor = new(ColorRole.Foreground);
    private readonly CheckBox projection = new(), gridVisible = new(), gridSizeLock = new(), depthPriorityOcclusion = new();
    private readonly NumericValueField columns = new(1, CanvasCalibration.MaximumGridCount, integerOnly: true) { Unit = L.Get("Numeric.Unit.Column") },
        rows = new(1, CanvasCalibration.MaximumGridCount, integerOnly: true) { Unit = L.Get("Numeric.Unit.Row") },
        width = new(double.Epsilon, 64, sliderMinimum: 1, sliderMaximum: 20, step: .5) { Unit = "px" };
    private readonly NumericValueField gridSize = new(CanvasGridGeometry.MinimumEditableCellSize, CanvasGridGeometry.MaximumCellSize,
        sliderMinimum: 1, sliderMaximum: 400, step: 1) { Unit = "px" };
    private readonly Grid countPair = new();
    private readonly StackPanel flatSizePanel = new();
    private readonly Button pin;
    private readonly TextBox lockedColumns = new() { IsReadOnly = true, MinHeight = 30 },
        lockedRows = new() { IsReadOnly = true, MinHeight = 30 };
    private readonly TextBlock gridSizeLockHint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 0, 0, 8) };
    private readonly Button calibrate = new(), reset = new();
    internal const double PerspectiveActionsWidth = 314;
    private readonly Button fourPoints = new() {Width=34,MinWidth=0,MinHeight=32,Padding=new(7),ToolTip=L.Get("Canvas.FourPoints")};
    private readonly ComboBox pattern = new();
    private readonly StackPanel perspectiveActions = new() { Margin = new(0, 8, 0, 8) };
    private readonly OverlayContextMenu resetMenu = new();
    private readonly Button resetChoices = new() { Content = "▾", Width = 26, MinHeight = 32, Padding = new(2) };
    private string? selectedResetDevice;
    private readonly CanvasPresetGallery presets;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 10, 0, 0) };
    private bool refreshing, disposing;
    private CanvasSettings current = new();
    internal CanvasSettings? DisplayedSettings { get; private set; }
    public event Action<uint>? BackgroundChanged, GridColorChanged;
    public event Action<bool>? ProjectionChanged, GridVisibilityChanged, GridSizeLockChanged;
    public event Action<bool>? DepthPriorityOcclusionChanged;
    public event Action<int, int>? GridCountsChanged;
    public event Action<double>? GridWidthChanged;
    public event Action<double>? GridSizeChanged;
    public bool IsPinned { get; private set; }
    public event Action? PinChanged;
    public bool IsColorPickerOpen => background.IsPickerOpen || gridColor.IsPickerOpen;
    public bool TryCloseColorPicker() => background.TryClosePicker() || gridColor.TryClosePicker();
    public bool OwnsColorPickerInput(DependencyObject? source) => background.OwnsPickerInput(source) || gridColor.OwnsPickerInput(source);
    public void ClosePickers() { background.ClosePicker(); gridColor.ClosePicker(); }
    public event Action<StrokePattern>? GridPatternChanged;
    public event Action? CalibrationToggled, Dismissed;
    public event Action? FourPointsRequested;
    public event Action<CanvasPresetParameters>? PresetApplied;
    public event Action<string?>? ResetDisplayRequested;
    public event Action<int>? CommandKey;

    public CanvasSettingsWindow(CanvasPresetLibrary? library = null, Func<EditorSession>? historySession = null)
    {
        double preferredWidth = PropertyPanelLayout.ContentWidth + WindowLayout.ContentInset * 2 + 2 + 25 + 196 + WindowLayout.ScrollBarWidth;
        Title = L.Get("Canvas.Title"); Width = preferredWidth;
        Height = 590; MinWidth = 280; MinHeight = 240;
        Topmost = true; ShowInTaskbar = false; Icon = AppIcon.WindowImage;
        if (library is null)
        {
            try { library = new CanvasPresetLibrary(); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { library = CanvasPresetLibrary.OfficialOnly(); status.Text = ex.Message + " " + L.Get("CanvasPreset.ReadOnly"); }
        }
        if (historySession is not null) library.HistorySession = historySession;
        var layout = new DockPanel { Margin = WindowLayout.ContentMargin(4, 14) };
        var description = new TextBlock { Text = L.Get("CanvasPreset.Description"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 0, 0, 12) };
        DockPanel.SetDock(description, Dock.Top); layout.Children.Add(description);
        DockPanel.SetDock(status, Dock.Bottom); layout.Children.Add(status);
        var content = new Grid(); content.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var body = new StackPanel();
        AddField(body, "Canvas.Background", background);
        body.Children.Add(PropertyPanelLayout.BooleanRow(L.Get("Canvas.Projection"), projection));
        depthPriorityOcclusion.Content = L.Get("Canvas.DepthPriorityOcclusion"); depthPriorityOcclusion.Margin = new(0, 0, 0, 8);
        depthPriorityOcclusion.ToolTip = L.Get("Canvas.DepthPriorityOcclusionHint");
        System.Windows.Automation.AutomationProperties.SetName(depthPriorityOcclusion, L.Get("Canvas.DepthPriorityOcclusion"));
        body.Children.Add(PropertyPanelLayout.BooleanRow(L.Get("Canvas.DepthPriorityOcclusion"), depthPriorityOcclusion));
        gridSizeLock.Content = L.Get("Canvas.GridSizeLock"); gridSizeLock.Margin = new(0, 0, 0, 8);
        gridSizeLock.ToolTip = L.Get("Canvas.GridSizeLockHelp"); ToolTipService.SetShowOnDisabled(gridSizeLock, true);
        body.Children.Add(PropertyPanelLayout.BooleanRow(L.Get("Canvas.GridSizeLock"), gridSizeLock));
        var rowPanel = CountField("Canvas.Rows", "R", rows, lockedRows); var columnPanel = CountField("Canvas.Columns", "C", columns, lockedColumns);
        countPair.Children.Add(PropertyPanelLayout.Row(new PropertyLabel(L.Get("Property.GridCounts")), PropertyPanelLayout.Pair(rowPanel, columnPanel))); body.Children.Add(countPair);
        AddField(flatSizePanel, "Canvas.GridSize", gridSize); body.Children.Add(flatSizePanel);
        gridSize.ToolTip = L.Get("Canvas.GridSizeHelp");
        gridSizeLockHint.Text = L.Get("Canvas.GridSizeLockCountsHint"); body.Children.Add(gridSizeLockHint);
        var actions = new Grid {Width=PerspectiveActionsWidth,HorizontalAlignment=HorizontalAlignment.Left};
        actions.ColumnDefinitions.Add(new());actions.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        calibrate.MinHeight = reset.MinHeight = 32; calibrate.Padding = reset.Padding = new(10, 4, 10, 4);
        calibrate.Margin = new(0, 0, 6, 0); reset.Content = L.Get("Canvas.ResetOnDisplay");
        reset.ToolTip = resetChoices.ToolTip = L.Get("Canvas.ReferenceDisplayHint");
        resetChoices.ContextMenu = resetMenu; resetMenu.PlacementTarget = resetChoices;
        resetMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        System.Windows.Automation.AutomationProperties.SetName(resetChoices, L.Get("Canvas.ResetChoices"));
        resetChoices.Click += (_, _) => resetMenu.IsOpen = true;
        fourPoints.Content=CommandGlyphs.Create(CommandGlyphs.Canvas);
        System.Windows.Automation.AutomationProperties.SetName(fourPoints,L.Get("Canvas.FourPoints"));
        Grid.SetColumn(fourPoints,1);actions.Children.Add(calibrate); actions.Children.Add(fourPoints);
        var resetActions=new Grid {Width=PerspectiveActionsWidth,HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,6,0,0)};
        resetActions.ColumnDefinitions.Add(new());resetActions.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        reset.Resources["CanvasButtonCornerRadius"]=new CornerRadius(6,0,0,6);
        resetChoices.Resources["CanvasButtonCornerRadius"]=new CornerRadius(0,6,6,0);
        resetChoices.Width=34;resetChoices.MinWidth=0;resetChoices.BorderThickness=new(1,0,0,0);
        resetChoices.SetResourceReference(Button.BorderBrushProperty,"CanvasBorder");
        Grid.SetColumn(resetChoices,1);resetActions.Children.Add(reset);resetActions.Children.Add(resetChoices);
        perspectiveActions.Children.Add(actions);perspectiveActions.Children.Add(resetActions); body.Children.Add(perspectiveActions);
        body.Children.Add(PropertyPanelLayout.BooleanRow(L.Get("Canvas.GridVisible"), gridVisible));
        AddField(body, "Canvas.GridColor", gridColor); AddField(body, "Canvas.GridWidth", width);
        foreach (var value in new[] { StrokePattern.Solid, StrokePattern.Dashed, StrokePattern.Dotted })
            pattern.Items.Add(new ComboBoxItem { Content = ToolOptionPreviews.Choice(L.Get("Canvas." + value), value), Tag = value });
        AddField(body, "Canvas.GridPattern", pattern);
        content.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        presets = new CanvasPresetGallery(library);
        presets.Applied += parameters => PresetApplied?.Invoke(parameters);
        presets.Failed += message => status.Text = message;
        var presetBorder = new Border { BorderThickness = new(1, 0, 0, 0), Margin = new(12, 0, 0, 0), Padding = new(12, 0, 0, 0), Child = new ScrollViewer { Content = presets, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        presetBorder.SetResourceReference(Border.BorderBrushProperty, "CanvasBorder");
        Grid.SetColumn(presetBorder, 1); content.Children.Add(presetBorder); layout.Children.Add(content); Content = layout;
        SizeChanged += (_, _) =>
        {
            bool compact = ActualWidth < preferredWidth - 1;
            actions.Width = resetActions.Width = Math.Min(PerspectiveActionsWidth, Math.Max(1, ActualWidth - WindowLayout.ContentInset * 2 - 2));
            content.RowDefinitions.Clear(); content.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); content.RowDefinitions.Add(new() { Height = GridLength.Auto });
            content.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            content.ColumnDefinitions[1].Width = compact ? new GridLength(0) : GridLength.Auto;
            Grid.SetColumn(presetBorder, compact ? 0 : 1); Grid.SetRow(presetBorder, compact ? 1 : 0);
            presetBorder.MaxHeight = compact ? 180 : double.PositiveInfinity;
            presetBorder.Margin = compact ? new(0, 8, 0, 0) : new(12, 0, 0, 0);
            presetBorder.Padding = compact ? new(0, 6, 0, 0) : new(12, 0, 0, 0);
            presetBorder.BorderThickness = compact ? new(0, 1, 0, 0) : new(1, 0, 0, 0);
        };
        AppWindowChrome.Apply(this); AppWindowChrome.SetHelp(this, L.Get("Canvas.Help"), L.Get("Canvas.HelpText") + "\n\n" + L.Get("Canvas.GridSizeLockHelp") + "\n\n" + L.Get("Canvas.MultiMonitorHelp") + "\n\n" + L.Get("CanvasPreset.Scope"));
        pin = ActionIconButton.Create(PanelActionIcon.Pin, L.Get("ToolbarV2.PinOptions"), () =>
        {
            IsPinned = !IsPinned; RefreshPin(); PinChanged?.Invoke();
        });
        pin.Width = pin.Height = 28; pin.Style = (Style)FindResource("CanvasCaptionButton");
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(pin, true);
        AppWindowChrome.SetHeaderActions(this, pin); ActionIconButton.UpdatePin(pin, false);
        background.ValueChanged += value => { if (!refreshing) BackgroundChanged?.Invoke(value); };
        gridColor.ValueChanged += value => { if (!refreshing) GridColorChanged?.Invoke(value); };
        projection.Click += (_, _) => { if (!refreshing) ProjectionChanged?.Invoke(projection.IsChecked == true); };
        depthPriorityOcclusion.Click += (_, _) =>
        {
            if (!refreshing && current.Projection.Enabled) DepthPriorityOcclusionChanged?.Invoke(depthPriorityOcclusion.IsChecked == true);
            depthPriorityOcclusion.IsChecked = DisplayedSettings?.DepthPriorityOcclusion ?? false;
        };
        gridSizeLock.Click += (_, _) => { if (!refreshing && current.Projection.Enabled) GridSizeLockChanged?.Invoke(gridSizeLock.IsChecked == true); };
        gridVisible.Click += (_, _) => { if (!refreshing) GridVisibilityChanged?.Invoke(gridVisible.IsChecked == true); };
        calibrate.Click += (_, _) => CalibrationToggled?.Invoke(); reset.Click += (_, _) => ResetDisplayRequested?.Invoke(selectedResetDevice);
        fourPoints.Click+=(_,_)=>FourPointsRequested?.Invoke();
        pattern.SelectionChanged += (_, _) => { if (!refreshing && pattern.SelectedItem is ComboBoxItem { Tag: StrokePattern value }) GridPatternChanged?.Invoke(value); };
        CommitOnExit(columns.Input, CommitCounts); CommitOnExit(rows.Input, CommitCounts);
        columns.SliderValueCommitted += () => { if (!refreshing) CommitCounts(); };
        rows.SliderValueCommitted += () => { if (!refreshing) CommitCounts(); };
        void CommitGridSize()
        {
            if (refreshing || current.Projection.Enabled) return;
            if (gridSize.TryGetValue(out double value))
            { if (value != (current.Grid.FlatCellSize ?? current.Projection.GridCellSize)) GridSizeChanged?.Invoke(value); }
            else RefreshGridSize(current.Grid.FlatCellSize ?? current.Projection.GridCellSize);
        }
        CommitOnExit(gridSize.Input, CommitGridSize);
        gridSize.SliderValueCommitted += () => { if (!refreshing) CommitGridSize(); };
        void CommitWidth()
        {
            if (refreshing) return;
            if (width.TryGetValue(out double value))
            { if (value != current.Grid.Width) GridWidthChanged?.Invoke(value); }
            else RefreshWidth(current.Grid.Width);
        }
        CommitOnExit(width.Input, CommitWidth);
        width.SliderValueCommitted += () => { if (!refreshing) CommitWidth(); };
        Closing += (_, e) => { if (disposing) return; e.Cancel = true; ResetPin(); SessionWindowCoordinator.Hide(this); Dismissed?.Invoke(); };
        ToolOptionsKeyboardRoute.Attach(this, () =>
        {
            if (TryCloseColorPicker()) return true;
            if (IsPinned) return false;
            SessionWindowCoordinator.Hide(this); Dismissed?.Invoke(); return true;
        }, () => background.IsPickerOpen || gridColor.IsPickerOpen, key =>
        {
            if (columns.Input.IsKeyboardFocusWithin || rows.Input.IsKeyboardFocusWithin) CommitCounts();
            else if (gridSize.Input.IsKeyboardFocusWithin) CommitGridSize();
            else if (width.Input.IsKeyboardFocusWithin) CommitWidth();
            CommandKey?.Invoke(key);
        });
        Refresh(new(), false);
    }
    public void Refresh(CanvasSettings settings, bool calibrating)
    {
        DisplayedSettings = settings; current = settings.Clone(); refreshing = true;
        try
        {
            background.Value = settings.BackgroundColor; gridColor.Value = settings.Grid.Color;
            projection.IsChecked = settings.Projection.Enabled; gridVisible.IsChecked = settings.Grid.Visible;
            depthPriorityOcclusion.IsChecked = settings.DepthPriorityOcclusion;
            perspectiveActions.Visibility = settings.Projection.Enabled ? Visibility.Visible : Visibility.Collapsed;
            flatSizePanel.Visibility = settings.Projection.Enabled ? Visibility.Collapsed : Visibility.Visible;
            RefreshGridCounts(settings.Projection);
            if (!gridSize.IsKeyboardFocusWithin && !gridSize.IsInteracting) RefreshGridSize(settings.Grid.FlatCellSize ?? settings.Projection.GridCellSize);
            if (!width.IsKeyboardFocusWithin && !width.IsInteracting) RefreshWidth(settings.Grid.Width);
            calibrate.Content = L.Get(calibrating ? "Canvas.EndCalibration" : "Canvas.Calibrate");
            pattern.SelectedItem = pattern.Items.Cast<ComboBoxItem>().FirstOrDefault(x => Equals(x.Tag, settings.Grid.Pattern));
            presets.Refresh(settings);
        }
        finally { refreshing = false; }
    }
    public void RefreshGridCounts(CanvasProjectionSettings settings)
    {
        current.Projection = settings.Clone();
        bool wasRefreshing = refreshing; refreshing = true;
        try
        {
            bool locked = settings.LockedGridCellSize.HasValue;
            depthPriorityOcclusion.Visibility = settings.Enabled ? Visibility.Visible : Visibility.Collapsed;
            countPair.Visibility = gridSizeLock.Visibility = settings.Enabled ? Visibility.Visible : Visibility.Collapsed;
            gridSizeLock.IsChecked = locked; gridSizeLock.IsEnabled = settings.Enabled;
            columns.Visibility = rows.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
            lockedColumns.Visibility = lockedRows.Visibility = gridSizeLockHint.Visibility = locked && settings.Enabled ? Visibility.Visible : Visibility.Collapsed;
            if (locked)
            {
                RefreshLockedCount(lockedColumns, settings.GridColumns);
                RefreshLockedCount(lockedRows, settings.GridRows);
            }
            if (locked || !columns.IsKeyboardFocusWithin && !columns.IsInteracting) columns.Value = settings.Columns;
            if (locked || !rows.IsKeyboardFocusWithin && !rows.IsInteracting) rows.Value = settings.Rows;
        }
        finally { refreshing = wasRefreshing; }
    }
    public void CloseForDispose() { disposing = true; Close(); }
    public void SetReferenceMonitors(IReadOnlyList<MonitorInfo> monitors, string? selectedDevice)
    {
        resetMenu.IsOpen = false;
        resetChoices.Visibility = Visibility.Visible;
        resetChoices.IsEnabled = monitors.Count > 1;
        selectedResetDevice = monitors.Any(m => m.DeviceName == selectedDevice) ? selectedDevice : null;
        resetMenu.Items.Clear(); reset.Content = L.Get("Canvas.ResetOnDisplay");
        for (int i = 0; i < monitors.Count; i++)
        {
            var monitor = monitors[i]; int number = i + 1;
            var item = new MenuItem { Header = L.Format("Settings.NumberedScreen", number), Tag = monitor.DeviceName,
                IsCheckable = true, IsChecked = monitor.DeviceName == selectedResetDevice || selectedResetDevice is null && i == 0 };
            item.Click += (_, _) =>
            {
                selectedResetDevice = monitor.DeviceName;
                reset.Content = L.Format("Canvas.ResetOnNumberedDisplay", number);
                ResetDisplayRequested?.Invoke(selectedResetDevice);
            };
            resetMenu.Items.Add(item);
            if (monitor.DeviceName == selectedResetDevice) reset.Content = L.Format("Canvas.ResetOnNumberedDisplay", number);
        }
    }
    private void RefreshPin() => ActionIconButton.UpdatePin(pin, IsPinned);
    internal void ResetPin() { IsPinned = false; RefreshPin(); }
    private void RefreshGridSize(double value)
    {
        gridSize.Value = Math.Clamp(value, CanvasGridGeometry.MinimumEditableCellSize, CanvasGridGeometry.MaximumCellSize);
        gridSize.Input.Text = value.ToString("G", System.Globalization.CultureInfo.CurrentCulture);
    }
    private void RefreshWidth(double value)
    {
        width.Value = value;
        if (value < .001) width.Input.Text = value.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
    }
    private void CommitCounts()
    {
        if (refreshing || !current.Projection.Enabled || current.Projection.LockedGridCellSize.HasValue) return;
        if (columns.TryGetValue(out double c) && rows.TryGetValue(out double r))
        { if (c != current.Projection.Columns || r != current.Projection.Rows) GridCountsChanged?.Invoke((int)c, (int)r); }
        else { columns.Value = current.Projection.Columns; rows.Value = current.Projection.Rows; }
    }
    private static void RefreshLockedCount(TextBox field, double value)
    {
        // Computed coverage is not an editable calibration count and has no 1..256 bound.
        field.Text = value.ToString(value is >= .001 and < 1_000_000 ? "0.###" : "G4", System.Globalization.CultureInfo.CurrentCulture);
        field.ToolTip = L.Get("Canvas.GridSizeLockCountsHint") + "\n" + value.ToString("G17", System.Globalization.CultureInfo.CurrentCulture);
    }
    private static Grid CountField(string key, string symbol, NumericValueField editable, TextBox readOnly)
    {
        var container = new Grid(); container.Children.Add(editable); container.Children.Add(readOnly);
        editable.SetScrubAnchor(NumericPropertyIcon.ForProperty(symbol), L.Get(key));
        readOnly.Height = readOnly.MinHeight = PropertyPanelLayout.ControlHeight;
        readOnly.Padding = new(26, 0, 26, 0); readOnly.VerticalContentAlignment = VerticalAlignment.Center;
        var caption = new NumericPropertyIcon(NumericPropertyIcon.ForProperty(symbol)) { Margin = new(4, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        caption.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(Visibility)) { Source = readOnly }); container.Children.Add(caption);
        foreach (var target in new FrameworkElement[] { editable, editable.Input, editable.Slider, readOnly })
            System.Windows.Automation.AutomationProperties.SetName(target, L.Get(key));
        return container;
    }
    private static void CommitOnExit(TextBox box, Action commit)
    {
        box.LostKeyboardFocus += (_, _) => commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { commit(); e.Handled = true; } };
    }
    private static void AddField(Panel panel, string key, FrameworkElement field, NumericValueField? numeric = null)
    {
        numeric ??= field as NumericValueField;
        var label = new PropertyLabel(L.Get(key == "Canvas.Background" ? "Property.Background" : key)) { ToolTip = L.Get(key) };
        var row = PropertyPanelLayout.Row(label, field); panel.Children.Add(row);
        field.MinHeight = 34;
        if (numeric is not null)
        {
            numeric.SetScrubAnchor(NumericPropertyIcon.ForProperty(key), L.Get(key));
            numeric.SetLabel(label);
        }
        System.Windows.Automation.AutomationProperties.SetName(field, L.Get(key));
        if (numeric is not null)
        {
            System.Windows.Automation.AutomationProperties.SetName(numeric.Input, L.Get(key));
            System.Windows.Automation.AutomationProperties.SetName(numeric.Slider, L.Get(key));
        }
    }
}
