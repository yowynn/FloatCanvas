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
        rows = new(1, CanvasCalibration.MaximumGridCount, integerOnly: true), width = new(1, 20, step: .5);
    private readonly Button calibrate = new(), reset = new();
    private readonly ComboBox pattern = new();
    private bool refreshing, disposing;
    private CanvasSettings current = new();
    public event Action<uint>? BackgroundChanged, GridColorChanged;
    public event Action<bool>? ProjectionChanged, GridVisibilityChanged;
    public event Action<int, int>? GridCountsChanged;
    public event Action<double>? GridWidthChanged;
    public event Action<StrokePattern>? GridPatternChanged;
    public event Action? CalibrationToggled, ResetRequested, Dismissed;

    public CanvasSettingsWindow()
    {
        Title = L.Get("Canvas.Title"); Width = 340; Height = 590; MinWidth = 300; MinHeight = 330;
        Topmost = true; ShowInTaskbar = true; Icon = AppIcon.WindowImage;
        var body = new StackPanel { Margin = new Thickness(14, 4, 14, 14) };
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
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AppWindowChrome.Apply(this); AppWindowChrome.SetHelp(this, L.Get("Canvas.Help"), L.Get("Canvas.HelpText"));
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
            else width.Value = current.Grid.Width;
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
            if (!width.IsKeyboardFocusWithin) width.Value = settings.Grid.Width;
            calibrate.Content = L.Get(calibrating ? "Canvas.EndCalibration" : "Canvas.Calibrate");
            pattern.SelectedItem = pattern.Items.Cast<ComboBoxItem>().FirstOrDefault(x => Equals(x.Tag, settings.Grid.Pattern));
        }
        finally { refreshing = false; }
    }
    public void CloseForDispose() { disposing = true; Close(); }
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
