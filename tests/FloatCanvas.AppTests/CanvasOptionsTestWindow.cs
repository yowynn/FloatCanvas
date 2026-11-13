using System.Windows;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

/// <summary>Hosts canvas content in the production shared panel for isolated widget tests.</summary>
internal sealed class CanvasOptionsTestWindow : Window
{
    internal readonly CanvasToolOptions Options;
    internal readonly ToolOptionsPanel Panel;
    internal readonly System.Windows.Controls.Button Pin;
    internal CanvasOptionsTestWindow(CanvasPresetLibrary? library = null, Func<EditorSession>? historySession = null)
    {
        var editor = new EditorController(historySession?.Invoke() ?? new()) { Tool = EditorTool.Canvas };
        Panel = new(editor, new ToolPresetLibrary(), ColorLibrary.Shared);
        Options = new(library, historySession); Panel.SetCanvasContent(Options);
        Content = Panel; Width = 699; Height = 590; MinWidth = 640; MinHeight = 420;
        AppWindowChrome.Apply(this);
        Pin = ActionIconButton.Create(PanelActionIcon.Pin, "Pin", () => SetPinned(!IsPinned));
        Pin.Width = Pin.Height = 28; Pin.Style = (Style)FindResource("CanvasCaptionButton"); Pin.Margin = new(3, 0, 3, 0);
        var custom = new System.Windows.Controls.ContentControl();
        var actions = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        actions.Children.Add(custom); actions.Children.Add(Pin); AppWindowChrome.SetHeaderActions(this, actions);
        Panel.HeaderChanged += () => { var header = Panel.Header; custom.Content = header.Actions; Title = header.Title; AppWindowChrome.SetHeaderDetail(this, header.Target, true); AppWindowChrome.SetHelp(this, header.HelpTitle, header.HelpText); };
        ToolOptionsKeyboardRoute.Attach(this, () => { if (Options.TryCloseColorPicker()) return true; if (IsPinned) return false; Hide(); Dismissed?.Invoke(); return true; }, key => CommandKey?.Invoke(key));
        Closed += (_, _) => Options.CancelEditing();
    }
    internal static object Unwrap(object value) => value is CanvasOptionsTestWindow fixture ? fixture.Options : value;
    internal event Action? PinChanged;
    public event Action? FourPointsRequested { add => Options.FourPointsRequested += value; remove => Options.FourPointsRequested -= value; }
    public event Action? FourLinesRequested { add => Options.FourLinesRequested += value; remove => Options.FourLinesRequested -= value; }
    public event Action<bool>? GridSizeLockChanged { add => Options.GridSizeLockChanged += value; remove => Options.GridSizeLockChanged -= value; }
    public event Action<bool>? GridVisibilityChanged { add => Options.GridVisibilityChanged += value; remove => Options.GridVisibilityChanged -= value; }
    internal bool IsPinned { get; private set; }
    internal bool IsColorPickerOpen => Options.IsColorPickerOpen;
    internal CanvasSettings? DisplayedSettings => Options.DisplayedSettings;
    internal event Action? Dismissed;
    public event Action<int>? CommandKey;
    internal void Refresh(CanvasSettings value) => Options.Refresh(value);
    internal void PrepareForShow() { Panel.PrepareForShow(); Options.PrepareForShow(); }
    internal void CloseForDispose() { Options.CancelEditing(); Close(); }
    internal void SetReferenceMonitors(IReadOnlyList<MonitorInfo> monitors, string? device) => Options.SetReferenceMonitors(monitors, device);
    internal void RefreshGridCounts(CanvasProjectionSettings settings) => Options.RefreshGridCounts(settings);
    internal void RefreshGuideOpacity(double value) => Options.RefreshGuideOpacity(value);
    internal void RefreshAfterNumericGesture(CanvasSettings settings) => Options.RefreshAfterNumericGesture(settings);
    internal void SetAuthoringAllowed(bool value) => Options.SetAuthoringAllowed(value);
    internal void SetPinned(bool value) { IsPinned = value; PinChanged?.Invoke(); }
    internal void ClosePickers() => Options.ClosePickers();
    internal Func<bool>? BeginNumericGesture { get => Options.BeginNumericGesture; set => Options.BeginNumericGesture = value; }
    internal Func<bool>? BeginColorGesture { get => Options.BeginColorGesture; set => Options.BeginColorGesture = value; }
    internal event Action<bool>? NumericGestureCompleted { add => Options.NumericGestureCompleted += value; remove => Options.NumericGestureCompleted -= value; }
    internal event Action<bool>? ColorGestureCompleted { add => Options.ColorGestureCompleted += value; remove => Options.ColorGestureCompleted -= value; }
    public event Action<uint>? BackgroundChanged { add => Options.BackgroundChanged += value; remove => Options.BackgroundChanged -= value; }
    public event Action<uint>? GridColorChanged { add => Options.GridColorChanged += value; remove => Options.GridColorChanged -= value; }
    public event Action<bool>? ProjectionChanged { add => Options.ProjectionChanged += value; remove => Options.ProjectionChanged -= value; }
    public event Action<bool>? DepthPriorityOcclusionChanged { add => Options.DepthPriorityOcclusionChanged += value; remove => Options.DepthPriorityOcclusionChanged -= value; }
    public event Action<int,int>? GridCountsChanged { add => Options.GridCountsChanged += value; remove => Options.GridCountsChanged -= value; }
    public event Action<double>? GridWidthChanged { add => Options.GridWidthChanged += value; remove => Options.GridWidthChanged -= value; }
    public event Action<double>? GridSizeChanged { add => Options.GridSizeChanged += value; remove => Options.GridSizeChanged -= value; }
    public event Action<double>? GuideOpacityChanged { add => Options.GuideOpacityChanged += value; remove => Options.GuideOpacityChanged -= value; }
    public event Action<string?>? ResetDisplaySelected { add => Options.ResetDisplaySelected += value; remove => Options.ResetDisplaySelected -= value; }
    public event Action<string?>? ResetDisplayRequested { add => Options.ResetDisplayRequested += value; remove => Options.ResetDisplayRequested -= value; }
    public event Action<CanvasPinLocation>? PinLocationChanged { add => Options.PinLocationChanged += value; remove => Options.PinLocationChanged -= value; }
    public event Action<CanvasPresetParameters>? PresetApplied { add => Options.PresetApplied += value; remove => Options.PresetApplied -= value; }
    public event Action<double,double>? PlaneSizeChanged { add => Options.PlaneSizeChanged += value; remove => Options.PlaneSizeChanged -= value; }
    public event Action<PointD>? GridOriginChanged { add => Options.GridOriginChanged += value; remove => Options.GridOriginChanged -= value; }
}
