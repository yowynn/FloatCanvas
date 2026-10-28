using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed class CanvasSettingsWindow : Window
{
    private readonly ColorField background = new(ColorRole.Background), gridColor = new(ColorRole.Foreground);
    private readonly SegmentedChoice projection = SegmentedChoicePreviews.CanvasMode();
    private readonly CheckBox gridVisible = new(), depthPriorityOcclusion = new(), gridSizeLock = new();
    private readonly NumericValueField columns = new(1, CanvasCalibration.MaximumGridCount, integerOnly: true) { Unit = L.Get("Numeric.Unit.Column") },
        rows = new(1, CanvasCalibration.MaximumGridCount, integerOnly: true) { Unit = L.Get("Numeric.Unit.Row") },
        width = new(double.Epsilon, 64, sliderMinimum: 1, sliderMaximum: 20, step: .5) { Unit = "px" };
    private readonly NumericValueField gridSize = new(CanvasGridGeometry.MinimumEditableCellSize, CanvasGridGeometry.MaximumCellSize,
        sliderMinimum: 1, sliderMaximum: 400, step: 1) { Unit = "px" };
    private readonly NumericValueField guideOpacity = new(0, 100, integerOnly: true) { Unit = "%" };
    private readonly Grid countPair = new();
    private readonly StackPanel flatSizePanel = new();
    private readonly PropertyChangeRegion projectionOptions, gridCountsRegion;
    private readonly Button pin;
    private readonly CanvasPinSelector pinSelector = new();
    private readonly FrameworkElement pinRow;
    private readonly FrameworkElement gridSizeLockRow;
    private readonly TextBox lockedColumns = new() { IsReadOnly = true }, lockedRows = new() { IsReadOnly = true };
    private readonly TextBlock gridSizeLockHint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 0, 0, 8) };
    private readonly TextBlock pinHint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 0, 0, 8) };
    private readonly Button reset = new();
    internal const double PerspectiveActionsWidth = PropertyPanelLayout.ContentWidth;
    private readonly Button fourPoints = new() { MinHeight = 32, Padding = new(10, 4, 10, 4), Content = L.Get("Canvas.FourPoints") };
    private readonly SegmentedChoice pattern = new();
    private readonly StackPanel perspectiveActions = new() { Margin = new(0, 8, 0, 8) };
    private readonly OverlayContextMenu resetMenu = new();
    private readonly Button resetChoices = new() { Content = "▾", Width = 26, MinHeight = 32, Padding = new(2) };
    private string? selectedResetDevice;
    private readonly CanvasPresetGallery presets;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 10, 0, 0) };
    private bool refreshing, disposing;
    private bool refreshPending, preparingForShow, referenceMonitorsPending, guideOpacityPending = true;
    private double currentGuideOpacity = 1;
    private CanvasSettings current = new();
    private CanvasSettings? pendingDisplayedSettings;
    private IReadOnlyList<MonitorInfo> referenceMonitors = [];
    private string? referenceDevice;
    internal CanvasSettings? DisplayedSettings { get; private set; }
    public event Action<uint>? BackgroundChanged, GridColorChanged;
    internal Func<bool>? BeginColorGesture { get; set; }
    internal event Action<bool>? ColorGestureCompleted;
    public event Action<ColorField>? EyedropperRequested;
    public event Action<bool>? ProjectionChanged, GridVisibilityChanged;
    public event Action<bool>? DepthPriorityOcclusionChanged;
    public event Action<CanvasPinLocation>? PinLocationChanged;
    public event Action<bool>? GridSizeLockChanged;
    public event Action<int, int>? GridCountsChanged;
    public event Action<double>? GridWidthChanged;
    public event Action<double>? GridSizeChanged;
    public event Action<double>? GuideOpacityChanged;
    public bool IsPinned { get; private set; }
    public event Action? PinChanged;
    public bool IsColorPickerOpen => background.IsPickerOpen || gridColor.IsPickerOpen;
    public bool TryCloseColorPicker() => background.TryClosePicker() || gridColor.TryClosePicker();
    public bool OwnsColorPickerInput(DependencyObject? source) => background.OwnsPickerInput(source) || gridColor.OwnsPickerInput(source);
    public void ClosePickers() { background.ClosePicker(); gridColor.ClosePicker(); }
    public event Action<StrokePattern>? GridPatternChanged;
    public event Action? Dismissed;
    public event Action? FourPointsRequested;
    public event Action<CanvasPresetParameters>? PresetApplied;
    public event Action<string?>? ResetDisplaySelected;
    public event Action<string?>? ResetDisplayRequested;
    public event Action<int>? CommandKey;

    public CanvasSettingsWindow(CanvasPresetLibrary? library = null, Func<EditorSession>? historySession = null)
    {
        double preferredWidth = PropertyPanelLayout.ContentWidth + WindowLayout.ContentInset * 2 + 2 + 25 + 196 + WindowLayout.ScrollBarWidth;
        Title = L.Get("Canvas.Title"); Width = preferredWidth;
        Height = 590; MinWidth = PropertyPanelLayout.TwoColumnMinimumWidth; MinHeight = PropertyPanelLayout.TwoColumnMinimumHeight;
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
        AddField(body, "Canvas.Mode", projection);
        System.Windows.Automation.AutomationProperties.SetName(projection, L.Get("Canvas.Mode"));
        var projectionBody = new StackPanel();
        depthPriorityOcclusion.Content = L.Get("Canvas.DepthPriorityOcclusion"); depthPriorityOcclusion.Margin = new(0, 0, 0, 8);
        depthPriorityOcclusion.ToolTip = L.Get("Canvas.DepthPriorityOcclusionHint");
        System.Windows.Automation.AutomationProperties.SetName(depthPriorityOcclusion, L.Get("Canvas.DepthPriorityOcclusion"));
        projectionBody.Children.Add(PropertyPanelLayout.BooleanRow(L.Get("Canvas.DepthPriorityOcclusion"), depthPriorityOcclusion));
        pinRow = PropertyPanelLayout.Row(new PropertyLabel(L.Get("Canvas.Pin")), pinSelector);
        projectionBody.Children.Add(pinRow);
        pinSelector.ToolTip = L.Get("Canvas.PinHelp");
        gridSizeLock.ToolTip = L.Get("Canvas.GridSizeLockHelp");
        System.Windows.Automation.AutomationProperties.SetName(gridSizeLock, L.Get("Canvas.GridSizeLock"));
        gridSizeLockRow = PropertyPanelLayout.BooleanRow(L.Get("Canvas.GridSizeLock"), gridSizeLock);
        projectionBody.Children.Add(gridSizeLockRow);
        var columnPanel = CountField("Canvas.Columns", "C", columns, lockedColumns); var rowPanel = CountField("Canvas.Rows", "R", rows, lockedRows);
        var countBody = new StackPanel();
        countPair.Children.Add(PropertyPanelLayout.Row(new PropertyLabel(L.Get("Property.GridCounts")), PropertyPanelLayout.Pair(columnPanel, rowPanel))); countBody.Children.Add(countPair);
        AddField(flatSizePanel, "Canvas.GridSize", gridSize); countBody.Children.Add(flatSizePanel);
        gridSize.ToolTip = L.Get("Canvas.GridSizeHelp");
        countBody.Children.Add(pinHint);
        gridSizeLockHint.Text = L.Get("Canvas.GridSizeLockCountsHint"); countBody.Children.Add(gridSizeLockHint);
        gridCountsRegion = new(countBody); projectionBody.Children.Add(gridCountsRegion);
        var actions = new Grid {Width=PerspectiveActionsWidth,HorizontalAlignment=HorizontalAlignment.Center};
        actions.SetBinding(MaxWidthProperty, new System.Windows.Data.Binding(nameof(ActualWidth)) { Source = perspectiveActions });
        actions.ColumnDefinitions.Add(new());
        reset.MinHeight = 32; reset.Padding = new(10, 4, 10, 4);
        reset.Content = L.Get("Canvas.ResetOnDisplay");
        reset.ToolTip = resetChoices.ToolTip = L.Get("Canvas.ReferenceDisplayHint");
        resetChoices.ContextMenu = resetMenu; resetMenu.PlacementTarget = resetChoices;
        resetMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        System.Windows.Automation.AutomationProperties.SetName(resetChoices, L.Get("Canvas.ResetChoices"));
        resetChoices.Click += (_, _) => resetMenu.IsOpen = true;
        fourPoints.ToolTip = L.Get("Canvas.FourPointsHint");
        System.Windows.Automation.AutomationProperties.SetName(fourPoints,L.Get("Canvas.FourPoints"));
        actions.Children.Add(fourPoints);
        var resetActions=new Grid {Width=PerspectiveActionsWidth,HorizontalAlignment=HorizontalAlignment.Center,Margin=new(0,6,0,0)};
        resetActions.SetBinding(MaxWidthProperty, new System.Windows.Data.Binding(nameof(ActualWidth)) { Source = perspectiveActions });
        resetActions.ColumnDefinitions.Add(new());resetActions.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        reset.Resources["CanvasButtonCornerRadius"]=new CornerRadius(6,0,0,6);
        reset.BorderThickness = new(0);
        resetChoices.Resources["CanvasButtonCornerRadius"]=new CornerRadius(0,6,6,0);
        resetChoices.Width=34;resetChoices.MinWidth=0;resetChoices.BorderThickness=new(1,0,0,0);
        resetChoices.SetResourceReference(Button.BorderBrushProperty,"CanvasBorder");
        Grid.SetColumn(resetChoices,1);resetActions.Children.Add(reset);resetActions.Children.Add(resetChoices);
        var resetOutline = new Border { Background = null, BorderThickness = new(1), CornerRadius = new(6),
            IsHitTestVisible = false, SnapsToDevicePixels = true };
        resetOutline.SetResourceReference(Border.BorderBrushProperty, "CanvasBorder");
        Grid.SetColumnSpan(resetOutline, 2); resetActions.Children.Add(resetOutline);
        perspectiveActions.Children.Add(actions);perspectiveActions.Children.Add(resetActions); projectionBody.Children.Add(perspectiveActions);
        projectionOptions = new(projectionBody); body.Children.Add(projectionOptions);
        var gridBody = new StackPanel();
        gridBody.Children.Add(PropertyPanelLayout.BooleanRow(L.Get("Canvas.GridVisible"), gridVisible));
        AddField(gridBody, "Canvas.GridColor", gridColor); AddField(gridBody, "Canvas.GridWidth", width);
        foreach (var value in new[] { StrokePattern.Solid, StrokePattern.Dashed, StrokePattern.Dotted })
            SegmentedChoicePreviews.Add(pattern, L.Get("Canvas." + value), value);
        System.Windows.Automation.AutomationProperties.SetName(pattern, L.Get("Canvas.GridPattern"));
        AddField(gridBody, "Canvas.GridPattern", pattern);
        body.Children.Add(gridBody);
        AddField(body, "Canvas.GuideOpacity", guideOpacity);
        guideOpacity.ToolTip = L.Get("Canvas.GuideOpacityHint");
        content.Children.Add(PropertyChangeRegion.ReserveHighlightSpace(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }));
        presets = new CanvasPresetGallery(library);
        presets.Applied += parameters => PresetApplied?.Invoke(parameters);
        presets.Failed += message => status.Text = message;
        var presetBorder = new Border { BorderThickness = new(1, 0, 0, 0), Margin = new(12, 0, 0, 0), Padding = new(12, 0, 0, 0), Child = new ScrollViewer { Content = presets, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        presetBorder.SetResourceReference(Border.BorderBrushProperty, "CanvasBorder");
        Grid.SetColumn(presetBorder, 1); content.Children.Add(presetBorder); layout.Children.Add(content); Content = layout;
        SizeChanged += (_, _) =>
        {
            actions.Width = resetActions.Width = Math.Min(PerspectiveActionsWidth, Math.Max(1, ActualWidth - WindowLayout.ContentInset * 2 - 2));
        };
        AppWindowChrome.Apply(this); AppWindowChrome.SetHelp(this, L.Get("Canvas.Help"), L.Get("Canvas.HelpText") + "\n\n" + L.Get("Canvas.PinHelp") + "\n\n" + L.Get("Canvas.MultiMonitorHelp") + "\n\n" + L.Get("CanvasPreset.Scope"));
        pin = ActionIconButton.Create(PanelActionIcon.Pin, L.Get("ToolbarV2.PinOptions"), () =>
        {
            IsPinned = !IsPinned; RefreshPin(); PinChanged?.Invoke();
        });
        pin.Width = pin.Height = 28; pin.Style = (Style)FindResource("CanvasCaptionButton");
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(pin, true);
        AppWindowChrome.SetHeaderActions(this, pin); ActionIconButton.UpdatePin(pin, false);
        background.ValueChanged += value => { if (!refreshing) BackgroundChanged?.Invoke(value); };
        foreach (var color in new[] { background, gridColor })
        {
            color.CanBeginGesture = () => BeginColorGesture?.Invoke() != false;
            color.GestureCompleted += commit => ColorGestureCompleted?.Invoke(commit);
        }
        background.EyedropperRequested += () => EyedropperRequested?.Invoke(background);
        gridColor.EyedropperRequested += () => EyedropperRequested?.Invoke(gridColor);
        gridColor.ValueChanged += value => { if (!refreshing) GridColorChanged?.Invoke(value); };
        projection.SelectionChanged += (_, _) =>
        {
            if (refreshing || projection.SelectedValue is not bool enabled) return;
            gridCountsRegion.Reset();
            projectionOptions.Change(() => ProjectionChanged?.Invoke(enabled));
        };
        depthPriorityOcclusion.Click += (_, _) =>
        {
            if (!refreshing && current.Projection.Enabled) DepthPriorityOcclusionChanged?.Invoke(depthPriorityOcclusion.IsChecked == true);
            depthPriorityOcclusion.IsChecked = DisplayedSettings?.DepthPriorityOcclusion ?? false;
        };
        pinSelector.SelectionChanged += value =>
        {
            if (!refreshing && current.Projection.Enabled) PinLocationChanged?.Invoke(value);
        };
        gridSizeLock.Click += (_, _) =>
        {
            if (!refreshing && current.Projection.Enabled) GridSizeLockChanged?.Invoke(gridSizeLock.IsChecked == true);
        };
        gridVisible.Click += (_, _) => { if (!refreshing) GridVisibilityChanged?.Invoke(gridVisible.IsChecked == true); };
        reset.Click += (_, _) => ResetDisplayRequested?.Invoke(selectedResetDevice);
        fourPoints.Click+=(_,_)=>FourPointsRequested?.Invoke();
        pattern.SelectionChanged += (_, _) => { if (!refreshing && pattern.SelectedValue is StrokePattern value) GridPatternChanged?.Invoke(value); };
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
        CommitOnExit(guideOpacity.Input, CommitGuideOpacity);
        guideOpacity.SliderValueCommitted += CommitGuideOpacity;
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
            else if (guideOpacity.Input.IsKeyboardFocusWithin) CommitGuideOpacity();
            CommandKey?.Invoke(key);
        });
        IsVisibleChanged += (_, _) => { if (IsVisible) PrepareForShow(); };
        Refresh(new());
    }
    public void Refresh(CanvasSettings settings)
    {
        current = settings.Clone(); pendingDisplayedSettings = settings; refreshPending = true;
        if (IsVisible) PrepareForShow();
        else presets.Refresh(current);
    }
    internal void PrepareForShow()
    {
        bool wasPreparing = preparingForShow; preparingForShow = true;
        try
        {
            if (refreshPending) RefreshControls();
            if (guideOpacityPending && CanRefresh(guideOpacity)) RefreshGuideOpacityControl();
            if (referenceMonitorsPending && CanRefresh(reset)) RefreshReferenceMonitors();
            if (CanRefresh(presets)) presets.PrepareForShow();
        }
        finally { preparingForShow = wasPreparing; }
    }
    private void RefreshControls()
    {
        refreshPending = false; DisplayedSettings = pendingDisplayedSettings;
        bool wasRefreshing = refreshing; refreshing = true;
        try
        {
            if (CanRefresh(background)) { background.Value = current.BackgroundColor; background.PrepareForShow(); }
            if (CanRefresh(gridColor)) { gridColor.Value = current.Grid.Color; gridColor.PrepareForShow(); }
            if (CanRefresh(projection)) projection.SelectedValue = current.Projection.Enabled;
            if (CanRefresh(gridVisible)) gridVisible.IsChecked = current.Grid.Visible;
            RefreshProjectionControls();
            if (CanRefresh(gridSize) && !gridSize.IsKeyboardFocusWithin && !gridSize.IsInteracting) RefreshGridSize(current.Grid.FlatCellSize ?? current.Projection.GridCellSize);
            if (CanRefresh(width) && !width.IsKeyboardFocusWithin && !width.IsInteracting) RefreshWidth(current.Grid.Width);
            if (CanRefresh(pattern)) pattern.SelectedValue = current.Grid.Pattern;
            presets.Refresh(current);
        }
        finally { refreshing = wasRefreshing; }
    }
    public void RefreshGridCounts(CanvasProjectionSettings settings)
    {
        current.Projection = settings.Clone();
        if (!IsVisible)
        {
            refreshPending = true; presets.Refresh(current); return;
        }
        bool wasRefreshing = refreshing; refreshing = true;
        try { RefreshProjectionControls(); }
        finally { refreshing = wasRefreshing; }
    }
    internal void RefreshGuideOpacity(double opacity)
    {
        currentGuideOpacity = Math.Clamp(opacity, 0, 1); guideOpacityPending = true;
        if (IsVisible && CanRefresh(guideOpacity)) RefreshGuideOpacityControl();
    }
    private void RefreshGuideOpacityControl()
    {
        if (guideOpacity.IsKeyboardFocusWithin || guideOpacity.IsInteracting) return;
        bool wasRefreshing = refreshing; refreshing = true;
        try { guideOpacity.Value = currentGuideOpacity * 100; guideOpacityPending = false; }
        finally { refreshing = wasRefreshing; }
    }
    private void CommitGuideOpacity()
    {
        if (refreshing) return;
        if (!guideOpacity.TryGetValue(out double percent)) { guideOpacity.Value = currentGuideOpacity * 100; return; }
        double opacity = percent / 100;
        if (opacity == currentGuideOpacity) return;
        currentGuideOpacity = opacity; GuideOpacityChanged?.Invoke(opacity);
    }
    private void RefreshProjectionControls()
    {
        var settings = current.Projection;
        bool locked = settings.LockedGridCellSize.HasValue;
        depthPriorityOcclusion.Visibility = settings.Enabled ? Visibility.Visible : Visibility.Collapsed;
        countPair.Visibility = pinRow.Visibility = gridSizeLockRow.Visibility = perspectiveActions.Visibility = settings.Enabled ? Visibility.Visible : Visibility.Collapsed;
        flatSizePanel.Visibility = settings.Enabled ? Visibility.Collapsed : Visibility.Visible;
        columns.Visibility = rows.Visibility = !locked ? Visibility.Visible : Visibility.Collapsed;
        lockedColumns.Visibility = lockedRows.Visibility = gridSizeLockHint.Visibility = locked && settings.Enabled ? Visibility.Visible : Visibility.Collapsed;
        if (CanRefresh(depthPriorityOcclusion)) depthPriorityOcclusion.IsChecked = current.DepthPriorityOcclusion;
        if (CanRefresh(gridSizeLock)) gridSizeLock.IsChecked = locked;
        if (CanRefresh(lockedColumns)) RefreshLockedCount(lockedColumns, settings.GridColumns);
        if (CanRefresh(lockedRows)) RefreshLockedCount(lockedRows, settings.GridRows);
        pinSelector.SelectedValue = settings.PinLocation;
        var p = settings.PinGridPoint;
        pinHint.Text = L.Get("Canvas.PinHalfCells");
        pinHint.Visibility = settings.Enabled && !locked && (p.X % 1 != 0 || p.Y % 1 != 0) ? Visibility.Visible : Visibility.Collapsed;
        if (CanRefresh(columns) && !columns.IsKeyboardFocusWithin && !columns.IsInteracting) columns.Value = settings.Columns;
        if (CanRefresh(rows) && !rows.IsKeyboardFocusWithin && !rows.IsInteracting) rows.Value = settings.Rows;
    }
    private bool CanRefresh(FrameworkElement field)
    {
        if (!preparingForShow) return field.IsVisible;
        for (DependencyObject? node = field; node is not null && !ReferenceEquals(node, this);
            node = LogicalTreeHelper.GetParent(node) ?? (node is Visual ? VisualTreeHelper.GetParent(node) : null))
            if (node is UIElement element && element.Visibility != Visibility.Visible) return false;
        return true;
    }
    public void CloseForDispose() { disposing = true; Close(); }
    public void SetReferenceMonitors(IReadOnlyList<MonitorInfo> monitors, string? selectedDevice)
    {
        referenceMonitors = monitors.ToArray(); referenceDevice = selectedDevice; referenceMonitorsPending = true;
        if (IsVisible && CanRefresh(reset)) RefreshReferenceMonitors();
    }
    private void RefreshReferenceMonitors()
    {
        referenceMonitorsPending = false;
        var monitors = referenceMonitors;
        string? selectedDevice = referenceDevice;
        resetMenu.IsOpen = false;
        resetChoices.Visibility = monitors.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        resetChoices.IsEnabled = monitors.Count > 1;
        reset.Resources["CanvasButtonCornerRadius"] = monitors.Count > 1 ? new CornerRadius(6,0,0,6) : new CornerRadius(6);
        selectedResetDevice = monitors.Count > 1 && monitors.Any(m => m.DeviceName == selectedDevice) ? selectedDevice : null;
        resetMenu.Items.Clear();
        var primary = new MenuItem { Header = L.Get("Canvas.PrimaryDisplay"), IsCheckable = true };
        primary.Click += (_, _) => SelectResetDisplay(null);
        resetMenu.Items.Add(primary);
        for (int i = 0; monitors.Count > 1 && i < monitors.Count; i++)
        {
            var monitor = monitors[i]; int number = i + 1;
            var item = new MenuItem { Header = L.Format("Settings.NumberedScreen", number), Tag = monitor.DeviceName,
                IsCheckable = true };
            item.Click += (_, _) => SelectResetDisplay(monitor.DeviceName);
            resetMenu.Items.Add(item);
        }
        RefreshResetSelection();
    }
    private void SelectResetDisplay(string? device)
    {
        bool changed = selectedResetDevice != device;
        selectedResetDevice = referenceDevice = device;
        RefreshResetSelection();
        if (changed) ResetDisplaySelected?.Invoke(device);
    }
    private void RefreshResetSelection()
    {
        reset.Content = L.Get("Canvas.ResetOnDisplay");
        for (int i = 0; i < referenceMonitors.Count; i++)
            if (referenceMonitors[i].DeviceName == selectedResetDevice)
                reset.Content = L.Format("Canvas.ResetOnNumberedDisplay", i + 1);
        foreach (var choice in resetMenu.Items.OfType<MenuItem>())
            choice.IsChecked = Equals(choice.Tag, selectedResetDevice);
    }
    private void RefreshPin() => ActionIconButton.UpdatePin(pin, IsPinned);
    internal void ResetPin() { IsPinned = false; RefreshPin(); }
    private void RefreshGridSize(double value)
    {
        gridSize.SetModelValue(value);
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
