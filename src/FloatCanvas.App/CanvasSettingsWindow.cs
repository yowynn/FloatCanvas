using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed class CanvasSettingsWindow : Window
{
    private readonly ColorField background = new(ColorRole.Background), gridColor = new(ColorRole.Foreground);
    private readonly CheckBox projection = new(), gridVisible = new();
    private readonly NumericValueField columns = new(1, CanvasCalibration.MaximumGridCount, integerOnly: true),
        rows = new(1, CanvasCalibration.MaximumGridCount, integerOnly: true), width = new(double.Epsilon, 64, sliderMinimum: 1, sliderMaximum: 20, step: .5);
    private readonly Button calibrate = new(), reset = new();
    private readonly ComboBox pattern = new();
    private readonly CanvasPresetGallery presets;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 10, 0, 0) };
    private bool refreshing, disposing;
    private CanvasSettings current = new();
    public event Action<uint>? BackgroundChanged, GridColorChanged;
    public event Action<bool>? ProjectionChanged, GridVisibilityChanged;
    public event Action<int, int>? GridCountsChanged;
    public event Action<double>? GridWidthChanged;
    public event Action<StrokePattern>? GridPatternChanged;
    public event Action? CalibrationToggled, ResetRequested, Dismissed;
    public event Action<CanvasPresetParameters>? PresetApplied;

    public CanvasSettingsWindow(CanvasPresetLibrary? library = null)
    {
        Title = L.Get("Canvas.Title"); Width = 600; Height = 590; MinWidth = 570; MinHeight = 380;
        Topmost = true; ShowInTaskbar = true; Icon = AppIcon.WindowImage;
        if (library is null)
        {
            try { library = new CanvasPresetLibrary(); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { library = CanvasPresetLibrary.OfficialOnly(); status.Text = ex.Message + " " + L.Get("CanvasPreset.ReadOnly"); }
        }
        var layout = new DockPanel { Margin = new Thickness(14, 4, 14, 14) };
        var description = new TextBlock { Text = L.Get("CanvasPreset.Description"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 0, 0, 12) };
        DockPanel.SetDock(description, Dock.Top); layout.Children.Add(description);
        DockPanel.SetDock(status, Dock.Bottom); layout.Children.Add(status);
        var content = new Grid(); content.ColumnDefinitions.Add(new()); content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var body = new StackPanel { Margin = new Thickness(0, 0, 4, 0) };
        AddField(body, "Canvas.Background", background);
        projection.Content = L.Get("Canvas.Projection"); projection.Margin = new(0, 14, 0, 8); body.Children.Add(projection);
        AddField(body, "Canvas.Columns", columns); AddField(body, "Canvas.Rows", rows);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 8, 0, 8) };
        calibrate.MinHeight = reset.MinHeight = 32; calibrate.Padding = reset.Padding = new(10, 4, 10, 4);
        calibrate.Margin = new(0, 0, 8, 0); reset.Content = L.Get("Canvas.Reset"); actions.Children.Add(calibrate); actions.Children.Add(reset); body.Children.Add(actions);
        gridVisible.Content = L.Get("Canvas.GridVisible"); gridVisible.Margin = new(0, 10, 0, 8); body.Children.Add(gridVisible);
        AddField(body, "Canvas.GridColor", gridColor); AddField(body, "Canvas.GridWidth", width);
        foreach (var value in new[] { StrokePattern.Solid, StrokePattern.Dashed, StrokePattern.Dotted })
            pattern.Items.Add(new ComboBoxItem { Content = ToolOptionPreviews.Choice(L.Get("Canvas." + value), value), Tag = value });
        AddField(body, "Canvas.GridPattern", pattern);
        content.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        presets = new CanvasPresetGallery(library);
        presets.Applied += parameters => PresetApplied?.Invoke(parameters);
        presets.Failed += message => status.Text = message;
        var presetBorder = new Border { BorderThickness = new(1, 0, 0, 0), Margin = new(12, 0, 0, 0), Padding = new(12, 0, 0, 0), Child = presets };
        presetBorder.SetResourceReference(Border.BorderBrushProperty, "CanvasBorder");
        Grid.SetColumn(presetBorder, 1); content.Children.Add(presetBorder); layout.Children.Add(content); Content = layout;
        AppWindowChrome.Apply(this); AppWindowChrome.SetHelp(this, L.Get("Canvas.Help"), L.Get("Canvas.HelpText") + "\n\n" + L.Get("CanvasPreset.Scope"));
        background.ValueChanged += value => { if (!refreshing) BackgroundChanged?.Invoke(value); };
        gridColor.ValueChanged += value => { if (!refreshing) GridColorChanged?.Invoke(value); };
        projection.Click += (_, _) => { if (!refreshing) ProjectionChanged?.Invoke(projection.IsChecked == true); };
        gridVisible.Click += (_, _) => { if (!refreshing) GridVisibilityChanged?.Invoke(gridVisible.IsChecked == true); };
        calibrate.Click += (_, _) => CalibrationToggled?.Invoke(); reset.Click += (_, _) => ResetRequested?.Invoke();
        pattern.SelectionChanged += (_, _) => { if (!refreshing && pattern.SelectedItem is ComboBoxItem { Tag: StrokePattern value }) GridPatternChanged?.Invoke(value); };
        CommitOnExit(columns.Input, CommitCounts); CommitOnExit(rows.Input, CommitCounts);
        columns.SliderValueCommitted += () => { if (!refreshing) CommitCounts(); };
        rows.SliderValueCommitted += () => { if (!refreshing) CommitCounts(); };
        void CommitWidth()
        {
            if (refreshing) return;
            if (width.TryGetValue(out double value))
            { if (value != current.Grid.Width) GridWidthChanged?.Invoke(value); }
            else RefreshWidth(current.Grid.Width);
        }
        CommitOnExit(width.Input, CommitWidth);
        width.SliderValueCommitted += () => { if (!refreshing) CommitWidth(); };
        Closing += (_, e) => { if (disposing) return; e.Cancel = true; SessionWindowCoordinator.Hide(this); Dismissed?.Invoke(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { if (!background.TryClosePicker() && !gridColor.TryClosePicker()) { SessionWindowCoordinator.Hide(this); Dismissed?.Invoke(); } e.Handled = true; } };
        Refresh(new(), false);
    }
    public void Refresh(CanvasSettings settings, bool calibrating)
    {
        current = settings.Clone(); refreshing = true;
        try
        {
            background.Value = settings.BackgroundColor; gridColor.Value = settings.Grid.Color;
            projection.IsChecked = settings.Projection.Enabled; gridVisible.IsChecked = settings.Grid.Visible;
            if (!columns.IsKeyboardFocusWithin) columns.Value = settings.Projection.Columns;
            if (!rows.IsKeyboardFocusWithin) rows.Value = settings.Projection.Rows;
            if (!width.IsKeyboardFocusWithin) RefreshWidth(settings.Grid.Width);
            calibrate.Content = L.Get(calibrating ? "Canvas.EndCalibration" : "Canvas.Calibrate");
            pattern.SelectedItem = pattern.Items.Cast<ComboBoxItem>().FirstOrDefault(x => Equals(x.Tag, settings.Grid.Pattern));
            presets.Refresh(settings);
        }
        finally { refreshing = false; }
    }
    public void CloseForDispose() { disposing = true; Close(); }
    private void RefreshWidth(double value)
    {
        width.Value = value;
        if (value < .001) width.Input.Text = value.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
    }
    private void CommitCounts()
    {
        if (refreshing) return;
        if (columns.TryGetValue(out double c) && rows.TryGetValue(out double r))
        { if (c != current.Projection.Columns || r != current.Projection.Rows) GridCountsChanged?.Invoke((int)c, (int)r); }
        else { columns.Value = current.Projection.Columns; rows.Value = current.Projection.Rows; }
    }
    private static void CommitOnExit(TextBox box, Action commit)
    {
        box.LostKeyboardFocus += (_, _) => commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { commit(); e.Handled = true; } };
    }
    private static void AddField(Panel panel, string key, FrameworkElement field)
    {
        var label = new TextBlock { Text = L.Get(key), Margin = new Thickness(0, 6, 0, 4) };
        panel.Children.Add(label); field.MinHeight = 30; panel.Children.Add(field);
        System.Windows.Automation.AutomationProperties.SetName(field, L.Get(key));
        if (field is NumericValueField numeric)
        {
            System.Windows.Automation.AutomationProperties.SetName(numeric.Input, L.Get(key));
            System.Windows.Automation.AutomationProperties.SetName(numeric.Slider, L.Get(key));
        }
    }
}
