using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class CanvasToolOptions : UserControl, IToolOptionsContent
{
    private readonly ColorField background = new(ColorRole.Background), gridColor = new(ColorRole.Foreground);
    private readonly SegmentedChoice projection = SegmentedChoicePreviews.CanvasMode();
    private readonly CheckBox gridVisible = new(), gridSizeLock = new();
    private readonly NumericValueField columns = new(1, CanvasCalibration.MaximumGridCount, sliderMaximum: 32, integerOnly: true) { Unit = L.Get("Numeric.Unit.Column") },
        rows = new(1, CanvasCalibration.MaximumGridCount, sliderMaximum: 32, integerOnly: true) { Unit = L.Get("Numeric.Unit.Row") },
        width = new(double.Epsilon, 64, sliderMinimum: .5, sliderMaximum: 8, step: .5) { Unit = "px" };
    private readonly NumericValueField gridSize = new(CanvasGridGeometry.MinimumEditableCellSize, CanvasGridGeometry.MaximumCellSize,
        sliderMinimum: 1, sliderMaximum: 400, step: 1) { Unit = "px" };
    private readonly Grid countPair = new();
    private readonly StackPanel flatSizePanel = new();
    private readonly StackPanel authoringControls = new();
    internal void SetTargetDescription(string text)
    {
        background.ToolTip = gridColor.ToolTip = projection.ToolTip = text;
        System.Windows.Automation.AutomationProperties.SetHelpText(this, text);
    }
    private readonly PropertyChangeRegion projectionOptions, gridCountsRegion, gridAppearanceRegion;
    private readonly StackPanel gridAppearanceFields = new();
    private readonly CanvasPinSelector pinSelector = new();
    private readonly FrameworkElement pinRow;
    private readonly FrameworkElement gridSizeLockRow;
    private readonly TextBox lockedColumns = new() { IsReadOnly = true }, lockedRows = new() { IsReadOnly = true };
    private readonly Button reset = new();
    private readonly Button fourPoints = CanvasAction("Canvas.FourPoints", "M2,3 H4 V5 H2 Z M12,2 H14 V4 H12 Z M1,12 H3 V14 H1 Z M13,12 H15 V14 H13 Z M4,4 L12,3 M3,5 L2,12 M14,4 V12 M3,13 H13");
    private readonly SegmentedChoice pattern = new();
    private readonly StackPanel perspectiveActions = new() { Margin = PropertyPanelLayout.RowMargin };
    private readonly OverlayContextMenu resetMenu = new();
    private readonly Button resetChoices = new() { Content = "▾", Width = 26, Height = PropertyPanelLayout.ControlHeight, Padding = new(2) };
    private string? selectedResetDevice;
    private readonly CanvasPresetGallery presets;
    private bool refreshing;
    private bool refreshPending, preparingForShow, referenceMonitorsPending;
    private CanvasSettings current = new();
    private CanvasSettings? pendingDisplayedSettings;
    private IReadOnlyList<MonitorInfo> referenceMonitors = [];
    private string? referenceDevice;
    internal CanvasSettings? DisplayedSettings { get; private set; }
    private string? initialError;
    internal event Action<string>? Failed;
    internal event Action? HeaderChanged;
    internal string TargetLabel { get; set; } = "";
    public FrameworkElement Parameters => this;
    public FrameworkElement Presets => presets;
    public ToolOptionsHeader Header => new(L.Get("Tool.Canvas"), TargetLabel, L.Get("Canvas.Help"),
        L.Get("Canvas.HelpText") + "\n\n" + L.Get("Canvas.PinHelp") + "\n\n" + L.Get("Canvas.MultiMonitorHelp") + "\n\n" + L.Get("CanvasPreset.Scope"), headerActions);
    internal void NotifyHeaderChanged() => HeaderChanged?.Invoke();
    private void BindTextGesture(NumericValueField field)
    {
        field.Input.GotKeyboardFocus += (_, _) => { if (!refreshing && IsVisible && !field.IsInteracting) field.BeginGesture(); };
        field.Input.LostKeyboardFocus += (_, _) => { if (field.IsInteracting) field.EndGesture(true); };
        field.Input.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && field.IsInteracting) field.EndGesture(true); };
        field.InvalidInput += () => { if (!refreshing) Failed?.Invoke(L.Get("ToolOptions.InvalidNumber")); };
    }
    public void CancelEditing()
    {
        ClosePickers(); resetMenu.IsOpen = false; guideOpacity.EndPointerGesture();
        foreach (var field in new[] { columns, rows, gridSize, width, planeWidth, planeHeight, gridOriginX, gridOriginY })
            if (field.IsInteracting) field.EndGesture(false);
    }
    public event Action<uint>? BackgroundChanged, GridColorChanged;
    internal Func<bool>? BeginColorGesture { get; set; }
    internal event Action<bool>? ColorGestureCompleted;
    internal Func<bool>? BeginNumericGesture { get; set; }
    internal event Action<bool>? NumericGestureCompleted;
    public event Action<ColorField>? EyedropperRequested;
    public event Action<bool>? ProjectionChanged, GridVisibilityChanged;
    public event Action<bool>? DepthPriorityOcclusionChanged;
    public event Action<CanvasPinLocation>? PinLocationChanged;
    public event Action<bool>? GridSizeLockChanged;
    public event Action<int, int>? GridCountsChanged;
    public event Action<double>? GridWidthChanged;
    public event Action<double>? GridSizeChanged;
    public event Action<double>? GuideOpacityChanged;
    public bool IsColorPickerOpen => background.IsPickerOpen || gridColor.IsPickerOpen;
    public bool TryCloseColorPicker() => background.TryClosePicker() || gridColor.TryClosePicker();
    public bool OwnsColorPickerInput(DependencyObject? source) => background.OwnsPickerInput(source) || gridColor.OwnsPickerInput(source);
    public void ClosePickers() { background.ClosePicker(); gridColor.ClosePicker(); }
    public event Action<StrokePattern>? GridPatternChanged;
    public event Action? FourPointsRequested, FourLinesRequested;
    public event Action<CanvasPresetParameters>? PresetApplied;
    public event Action<string?>? ResetDisplaySelected;
    public event Action<string?>? ResetDisplayRequested;

    public CanvasToolOptions(CanvasPresetLibrary? library = null, Func<EditorSession>? historySession = null, ToolStateStore? stateStore = null)
    {
        if (library is null)
        {
            try { library = new CanvasPresetLibrary(); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { library = CanvasPresetLibrary.OfficialOnly(); initialError = ex.Message + " " + L.Get("CanvasPreset.ReadOnly"); }
        }
        if (historySession is not null) library.HistorySession = historySession;
        PropertyPanelLayout.Scope(authoringControls);
        InitializePreviewControls();

        AddSection(authoringControls, "Canvas.Section.BackgroundGrid");
        AddField(authoringControls, "Canvas.Background", background);
        authoringControls.Children.Add(PropertyPanelLayout.BooleanRow(L.Get("Canvas.GridVisible"), gridVisible));
        AddField(gridAppearanceFields, "Canvas.GridColor", gridColor); AddField(gridAppearanceFields, "Canvas.GridWidth", width);
        foreach (var value in new[] { StrokePattern.Solid, StrokePattern.Dashed, StrokePattern.Dotted })
            SegmentedChoicePreviews.Add(pattern, L.Get("Canvas." + value), value);
        System.Windows.Automation.AutomationProperties.SetName(pattern, L.Get("Canvas.GridPattern"));
        AddField(gridAppearanceFields, "Canvas.GridPattern", pattern);
        gridAppearanceRegion = new(gridAppearanceFields); authoringControls.Children.Add(gridAppearanceRegion);
        AddSection(authoringControls, "Canvas.Section.PerspectiveGrid");
        AddField(authoringControls, "Canvas.Mode", projection);
        System.Windows.Automation.AutomationProperties.SetName(projection, L.Get("Canvas.Mode"));
        var projectionBody = new StackPanel();
        pinRow = PropertyPanelLayout.Row(new PropertyLabel(L.Get("Canvas.Pin")), pinSelector);


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
        countPair.ToolTip = L.Get("Canvas.GridSizeLockCountsHint");
        gridCountsRegion = new(countBody); projectionBody.Children.Add(gridCountsRegion);
        var actions = new Grid { HorizontalAlignment=HorizontalAlignment.Stretch };
        actions.SetBinding(MaxWidthProperty, new System.Windows.Data.Binding(nameof(ActualWidth)) { Source = perspectiveActions });
        actions.ColumnDefinitions.Add(new());
        ConfigureCanvasAction(reset, "Canvas.ResetOnDisplay", "M3,5 A6,6 0 1 1 2,10 M1,1 V6 H6");
        reset.ToolTip = resetChoices.ToolTip = L.Get("Canvas.ReferenceDisplayHint");
        resetChoices.ContextMenu = resetMenu; resetMenu.PlacementTarget = resetChoices;
        resetMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        System.Windows.Automation.AutomationProperties.SetName(resetChoices, L.Get("Canvas.ResetChoices"));
        resetChoices.Click += (_, _) => resetMenu.IsOpen = true;
        fourPoints.ToolTip = L.Get("Canvas.FourPointsHint");
        System.Windows.Automation.AutomationProperties.SetName(fourPoints,L.Get("Canvas.FourPoints"));
        actions.ColumnDefinitions.Add(new());
        var fourLines = CanvasAction("Canvas.FourLines", "M1,4 L15,2 M1,12 L15,14 M4,1 L2,15 M12,1 L14,15");
        fourPoints.Margin = new(0, 0, 3, 0); fourLines.Margin = new(3, 0, 0, 0);
        fourLines.ToolTip = L.Get("Canvas.FourLinesHint");
        fourLines.Click += (_, _) => FourLinesRequested?.Invoke();
        Grid.SetColumn(fourLines, 1); actions.Children.Add(fourPoints); actions.Children.Add(fourLines);
        var resetActions=new Grid {HorizontalAlignment=HorizontalAlignment.Stretch,Margin=PropertyPanelLayout.RowMargin};
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
        perspectiveActions.Children.Add(actions);

        projectionBody.Children.Insert(0, perspectiveActions);
        var geometry = BuildPlaneFields();
        projectionBody.Children.Insert(1, geometry);
        projectionBody.Children.Insert(2, pinRow);
        projectionBody.Children.Add(BuildGridOriginFields());
        projectionGridCalibration = (FrameworkElement)originFields.Children[0];
        originFields.Children.RemoveAt(0); projectionBody.Children.Insert(3, projectionGridCalibration);
        projectionBody.Children.Add(rectificationPreview);
        projectionBody.Children.Add(resetActions);
        resetActions.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(Visibility)) { Source = perspectiveActions });
        projectionOptions = new(projectionBody);
        authoringControls.Children.Add(projectionOptions);

        presets = new CanvasPresetGallery(library, stateStore);
        presets.CanvasApplied += parameters => PresetApplied?.Invoke(parameters);
        presets.Failed += message => Failed?.Invoke(message);
        Content = authoringControls;
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
            foreach (var field in new[] { planeWidth, planeHeight, gridOriginX, gridOriginY, gridSize, columns, rows })
                if (field.IsInteracting) field.EndGesture(false);
            projectionOptions.Change(() => ProjectionChanged?.Invoke(enabled));
        };
        pinSelector.SelectionChanged += value =>
        {
            if (!refreshing && current.Projection.Enabled) PinLocationChanged?.Invoke(value);
        };
        gridSizeLock.Click += (_, _) =>
        {
            if (!refreshing && current.Projection.Enabled && current.Projection.PlaneBounds is null) GridSizeLockChanged?.Invoke(gridSizeLock.IsChecked == true);
        };
        gridVisible.Click += (_, _) =>
        {
            if (refreshing) return;
            bool visible = gridVisible.IsChecked == true;
            gridColor.ClosePicker();
            if (width.IsInteracting) width.EndGesture(true);
            gridAppearanceRegion.Change(() => GridVisibilityChanged?.Invoke(visible));
        };
        reset.Click += (_, _) => ResetDisplayRequested?.Invoke(selectedResetDevice);
        fourPoints.Click+=(_,_)=>FourPointsRequested?.Invoke();
        pattern.SelectionChanged += (_, _) => { if (!refreshing && pattern.SelectedValue is StrokePattern value) GridPatternChanged?.Invoke(value); };
        CommitOnExit(columns.Input, CommitCounts); CommitOnExit(rows.Input, CommitCounts);
        void CommitGridSize()
        {
            if (refreshing || current.Projection.Enabled && current.Projection.PlaneBounds is null) return;
            if (gridSize.TryGetValue(out double value))
            { if (gridSize.IsInteracting || value != DisplayGridSize) GridSizeChanged?.Invoke(value); }
            else RefreshGridSize(DisplayGridSize);
        }
        CommitOnExit(gridSize.Input, CommitGridSize);
        void CommitWidth()
        {
            if (refreshing) return;
            if (width.TryGetValue(out double value))
            { if (width.IsInteracting || value != current.Grid.Width) GridWidthChanged?.Invoke(value); }
            else RefreshWidth(current.Grid.Width);
        }
        CommitOnExit(width.Input, CommitWidth);
        foreach (var (field, publish) in new (NumericValueField, Action)[]
        { (columns, CommitCounts), (rows, CommitCounts), (gridSize, CommitGridSize), (width, CommitWidth) })
        {
            field.CanBeginGesture = () => BeginNumericGesture?.Invoke() != false;
            BindTextGesture(field);
            field.ValueChanged += _ => { if (!refreshing) publish(); };
            field.GestureCompleted += commit => NumericGestureCompleted?.Invoke(commit);
        }
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) PrepareForShow();
            else CancelEditing();
        };
        Refresh(new());
    }
    public void Refresh(CanvasSettings settings)
    {
        current = settings.Clone(); pendingDisplayedSettings = settings; refreshPending = true;
        if (IsVisible) PrepareForShow();
        else presets.Refresh(current);
    }
    internal void RefreshAfterNumericGesture(CanvasSettings settings)
    {
        Refresh(settings);
        // The completed drag must follow the owning document even if its rail
        // keeps keyboard focus, or an external undo invalidated the gesture.
        columns.SetModelValue(settings.Projection.Columns);
        rows.SetModelValue(settings.Projection.Rows);
        gridSize.SetModelValue(DisplayGridSize);
        width.SetModelValue(settings.Grid.Width);
        SynchronizeGeometryNumbers();
    }
    public void PrepareForShow()
    {
        if (initialError is { } error) { initialError = null; Failed?.Invoke(error); }
        bool wasPreparing = preparingForShow; preparingForShow = true;
        try
        {
            if (refreshPending) RefreshControls();
            if (referenceMonitorsPending && CanRefresh(reset)) RefreshReferenceMonitors();
            presets.PrepareForShow();
        }
        finally { preparingForShow = wasPreparing; }
    }
    private void RefreshControls()
    {
        refreshPending = false; DisplayedSettings = pendingDisplayedSettings;
        bool wasRefreshing = refreshing; refreshing = true;
        try
        {
            foreach (UIElement row in gridAppearanceFields.Children)
                row.Visibility = current.Grid.Visible ? Visibility.Visible : Visibility.Collapsed;
            if (CanRefresh(background)) { background.Value = current.BackgroundColor; background.PrepareForShow(); }
            if (CanRefresh(gridColor)) { gridColor.Value = current.Grid.Color; gridColor.PrepareForShow(); }
            if (CanRefresh(projection)) projection.SelectedValue = current.Projection.Enabled;
            if (CanRefresh(gridVisible)) gridVisible.IsChecked = current.Grid.Visible;
            RefreshProjectionControls();
            if (CanRefresh(gridSize) && !gridSize.IsKeyboardFocusWithin && !gridSize.IsInteracting) RefreshGridSize(DisplayGridSize);
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
    private FrameworkElement? projectionGridCalibration;
    private void RefreshProjectionControls()
    {
        var settings = current.Projection;
        bool independent = settings.PlaneBounds is not null;
        bool locked = settings.LockedGridCellSize.HasValue;
        SynchronizeDepthSwitch();
        countPair.Visibility = pinRow.Visibility = gridSizeLockRow.Visibility = perspectiveActions.Visibility = settings.Enabled ? Visibility.Visible : Visibility.Collapsed;
        flatSizePanel.Visibility = settings.Enabled ? Visibility.Collapsed : Visibility.Visible;
        if (independent)
        {
            countPair.Visibility = gridSizeLockRow.Visibility = Visibility.Collapsed;
            flatSizePanel.Visibility = Visibility.Visible;
        }
        columns.Visibility = rows.Visibility = !locked ? Visibility.Visible : Visibility.Collapsed;
        lockedColumns.Visibility = lockedRows.Visibility = locked && settings.Enabled ? Visibility.Visible : Visibility.Collapsed;
        if (CanRefresh(gridSizeLock)) gridSizeLock.IsChecked = locked;
        if (CanRefresh(lockedColumns)) RefreshLockedCount(lockedColumns, settings.GridColumns);
        if (CanRefresh(lockedRows)) RefreshLockedCount(lockedRows, settings.GridRows);
        pinSelector.SelectedValue = settings.PinLocation;
        var p = settings.PinGridPoint;
        pinSelector.ToolTip = L.Get("Canvas.PinHelp") + (!independent && settings.Enabled && !locked && (p.X % 1 != 0 || p.Y % 1 != 0) ? "\n" + L.Get("Canvas.PinHalfCells") : "");
        if (CanRefresh(columns) && !columns.IsKeyboardFocusWithin && !columns.IsInteracting) columns.Value = settings.Columns;
        if (CanRefresh(rows) && !rows.IsKeyboardFocusWithin && !rows.IsInteracting) rows.Value = settings.Rows;
        RefreshIndependentFields();
        if (projectionGridCalibration is not null) projectionGridCalibration.Visibility = settings.Enabled && independent ? Visibility.Visible : Visibility.Collapsed;
    }
    private bool CanRefresh(FrameworkElement field)
    {
        if (!preparingForShow) return field.IsVisible;
        for (DependencyObject? node = field; node is not null && !ReferenceEquals(node, this);
            node = LogicalTreeHelper.GetParent(node) ?? (node is Visual ? VisualTreeHelper.GetParent(node) : null))
            if (node is UIElement element && element.Visibility != Visibility.Visible) return false;
        return true;
    }
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
        string label = L.Get("Canvas.ResetOnDisplay");
        for (int i = 0; i < referenceMonitors.Count; i++)
            if (referenceMonitors[i].DeviceName == selectedResetDevice)
                label = L.Format("Canvas.ResetOnNumberedDisplay", i + 1);
        SetCanvasActionContent(reset, label, "M3,5 A6,6 0 1 1 2,10 M1,1 V6 H6");
        foreach (var choice in resetMenu.Items.OfType<MenuItem>())
            choice.IsChecked = Equals(choice.Tag, selectedResetDevice);
    }
    private static void AddSection(Panel parent, string key)
    {
        var title = ToolOptionsLayout.Section(key, parent.Children.Count == 0);
        parent.Children.Add(title);
    }

    private static Button CanvasAction(string key, string geometry)
    {
        var button = new Button(); ConfigureCanvasAction(button, key, geometry); return button;
    }

    private static void ConfigureCanvasAction(Button button, string key, string geometry)
    {
        button.Height = button.MinHeight = PropertyPanelLayout.ControlHeight;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.Padding = new(8, 0, 8, 0);
        SetCanvasActionContent(button, L.Get(key), geometry);
    }

    private static void SetCanvasActionContent(Button button, string label, string geometry)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var icon = CommandGlyphs.Create(geometry); icon.Width = icon.Height = 16;
        icon.Margin = new(0, 0, 7, 0); content.Children.Add(icon);
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        button.Content = content;
        System.Windows.Automation.AutomationProperties.SetName(button, label);
    }
    private void RefreshGridSize(double value)
    {
        gridSize.SetModelValue(value);
    }
    private void RefreshWidth(double value)
    {
        width.Value = value;
    }
    private void CommitCounts()
    {
        if (refreshing || !current.Projection.Enabled || current.Projection.PlaneBounds is not null || current.Projection.LockedGridCellSize.HasValue) return;
        if (columns.TryGetValue(out double c) && rows.TryGetValue(out double r))
        { if (columns.IsInteracting || rows.IsInteracting || c != current.Projection.Columns || r != current.Projection.Rows) GridCountsChanged?.Invoke((int)c, (int)r); }
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
