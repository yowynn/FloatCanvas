using System.Windows;
using System.Windows.Controls;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class CanvasSettingsWindow
{
    private readonly StackPanel planeFields = new(), originFields = new();
    private readonly NumericValueField planeWidth = new(.01, 256_000_000, sliderMinimum: 1, sliderMaximum: 2000) { Unit = "px" },
        planeHeight = new(.01, 256_000_000, sliderMinimum: 1, sliderMaximum: 2000) { Unit = "px" },
        gridOriginX = new(-100_000_000, 100_000_000, sliderMinimum: -1000, sliderMaximum: 1000) { Unit = "px" },
        gridOriginY = new(-100_000_000, 100_000_000, sliderMinimum: -1000, sliderMaximum: 1000) { Unit = "px" };
    private readonly ComboBox aspectPreset = new() { MinHeight = 28 };
    private readonly CheckBox aspectLock = new();
    private readonly TextBlock coverage = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 8) };
    public event Action<double, double>? PlaneSizeChanged;
    public event Action<double>? PlaneAspectChanged;
    public event Action<PointD>? GridOriginChanged;
    public event Action? GridOriginToPinRequested, RectificationPreviewRequested, GridCalibrationRequested;
    private double DisplayGridSize => current.Projection.Enabled
        ? current.Grid.PlaneCellSize ?? current.Projection.GridCellSize
        : current.Grid.FlatCellSize ?? current.Projection.GridCellSize;

    private FrameworkElement BuildPlaneFields()
    {
        AddField(planeFields, "Canvas.PlaneWidth", planeWidth);
        AddField(planeFields, "Canvas.PlaneHeight", planeHeight);
        aspectLock.Content = L.Get("Canvas.AspectLock"); aspectLock.Margin = new(0, 0, 0, 8);
        planeFields.Children.Add(aspectLock);
        foreach (var preset in CanvasAspectPresets.All)
            aspectPreset.Items.Add(new ComboBoxItem { Content = L.Get(preset.Key), Tag = preset });
        AddField(planeFields, "Canvas.TargetAspect", aspectPreset);
        var swap = new Button { Content = L.Get("Canvas.SwapOrientation"), MinHeight = 28, Margin = new(0, 0, 0, 6) };
        swap.Click += (_, _) => { if (!refreshing && current.Projection.Enabled) PlaneAspectChanged?.Invoke(planeHeight.Value / planeWidth.Value); };
        planeFields.Children.Add(swap);
        planeFields.Children.Add(new TextBlock { Text = L.Get("Canvas.PlaneSizeHelp"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 0, 0, 8) });
        var preview = new Button { Content = L.Get("Canvas.RectificationPreview"), MinHeight = 30, Margin = new(0, 0, 0, 8) };
        preview.Click += (_, _) => RectificationPreviewRequested?.Invoke(); planeFields.Children.Add(preview);
        aspectPreset.SelectionChanged += (_, _) =>
        {
            if (!refreshing && current.Projection.Enabled && aspectPreset.SelectedItem is ComboBoxItem { Tag: CanvasAspectPreset preset } && preset.Aspect is { } ratio)
                PlaneAspectChanged?.Invoke(ratio);
        };
        void CommitSize(bool horizontal)
        {
            if (refreshing || !current.Projection.Enabled) return;
            if (!planeWidth.TryGetValue(out double widthValue) || !planeHeight.TryGetValue(out double heightValue))
            { status.Text = L.Get("Canvas.InvalidPlaneSize"); RefreshIndependentFields(); return; }
            var old = CanvasProjectionGeometry.ResolvePlaneBounds(current.Projection);
            if (aspectLock.IsChecked == true)
            {
                if (horizontal) heightValue = widthValue * old.Height / old.Width;
                else widthValue = heightValue * old.Width / old.Height;
            }
            if (!CanvasProjectionGeometry.IsValidSize(widthValue, heightValue)) { status.Text = L.Get("Canvas.InvalidPlaneSize"); return; }
            status.Text = "";
            if (widthValue != old.Width || heightValue != old.Height) PlaneSizeChanged?.Invoke(widthValue, heightValue);
        }
        BindGeometryNumber(planeWidth, () => CommitSize(true)); BindGeometryNumber(planeHeight, () => CommitSize(false));
        return planeFields;
    }

    private FrameworkElement BuildGridOriginFields()
    {
        AddField(originFields, "Canvas.GridOriginX", gridOriginX); AddField(originFields, "Canvas.GridOriginY", gridOriginY);
        var align = new Button { Content = L.Get("Canvas.GridOriginToPin"), MinHeight = 28 };
        align.Click += (_, _) => GridOriginToPinRequested?.Invoke(); originFields.Children.Add(align);
        originFields.Children.Add(coverage);
        var calibrate = new Button { Content = L.Get("Canvas.GridCalibration"), MinHeight = 30, Margin = new(0, 0, 0, 8) };
        calibrate.Click += (_, _) => GridCalibrationRequested?.Invoke(); originFields.Children.Add(calibrate);
        void CommitOrigin()
        {
            if (refreshing || !current.Projection.Enabled) return;
            if (gridOriginX.TryGetValue(out double x) && gridOriginY.TryGetValue(out double y))
            {
                var point = new PointD(x, y);
                if (point != (current.Grid.PlaneOrigin ?? current.Projection.PinPoint)) GridOriginChanged?.Invoke(point);
            }
            else RefreshIndependentFields();
        }
        BindGeometryNumber(gridOriginX, CommitOrigin); BindGeometryNumber(gridOriginY, CommitOrigin);
        return originFields;
    }

    private void BindGeometryNumber(NumericValueField field, Action publish)
    {
        CommitOnExit(field.Input, publish); field.SliderValueCommitted += publish;
        field.CanBeginGesture = () => BeginNumericGesture?.Invoke() != false;
        field.ValueChanged += _ => { if (field.IsInteracting) publish(); };
        field.GestureCompleted += commit => NumericGestureCompleted?.Invoke(commit);
    }
    private void RefreshIndependentFields()
    {
        bool visible = current.Projection.Enabled && current.Projection.PlaneBounds is not null;
        planeFields.Visibility = originFields.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        var bounds = CanvasProjectionGeometry.ResolvePlaneBounds(current.Projection);
        var origin = current.Grid.PlaneOrigin ?? current.Projection.PinPoint;
        void RefreshNumber(NumericValueField field, double value)
        { if (!field.IsKeyboardFocusWithin && !field.IsInteracting) field.SetModelValue(value); }
        RefreshNumber(planeWidth, bounds.Width); RefreshNumber(planeHeight, bounds.Height);
        RefreshNumber(gridOriginX, origin.X); RefreshNumber(gridOriginY, origin.Y);
        coverage.Text = L.Format("Canvas.GridCoverage", bounds.Width / DisplayGridSize, bounds.Height / DisplayGridSize);
        bool previous = refreshing; refreshing = true;
        try
        {
            double ratio = bounds.Width / bounds.Height;
            aspectPreset.SelectedIndex = CanvasAspectPresets.All.ToList().FindIndex(p => p.Aspect is { } aspect && Math.Abs(aspect - ratio) < 1e-7);
            if (aspectPreset.SelectedIndex < 0) aspectPreset.SelectedIndex = 0;
        }
        finally { refreshing = previous; }
    }
    private void SynchronizeGeometryNumbers()
    {
        if (current.Projection.PlaneBounds is not { } bounds) return;
        planeWidth.SetModelValue(bounds.Width); planeHeight.SetModelValue(bounds.Height);
        var origin = current.Grid.PlaneOrigin ?? current.Projection.PinPoint;
        gridOriginX.SetModelValue(origin.X); gridOriginY.SetModelValue(origin.Y);
    }
}
