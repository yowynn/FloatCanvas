using System.Windows;
using System.Windows.Controls;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class CanvasToolOptions
{
    private readonly StackPanel planeFields = new(), originFields = new();
    private readonly NumericValueField planeWidth = new(.01, 256_000_000, sliderMinimum: 1, sliderMaximum: 2000) { Unit = "px" },
        planeHeight = new(.01, 256_000_000, sliderMinimum: 1, sliderMaximum: 2000) { Unit = "px" },
        gridOriginX = new(-100_000_000, 100_000_000, sliderMinimum: -1000, sliderMaximum: 1000) { Unit = "px" },
        gridOriginY = new(-100_000_000, 100_000_000, sliderMinimum: -1000, sliderMaximum: 1000) { Unit = "px" };
    private readonly AspectRatioButton aspectLock = new();
    private Button rectificationPreview = null!;
    private PointD flatGridReference;
    internal void SetFlatGridReference(PointD point) => flatGridReference = point;
    private PointD DisplayGridOrigin => current.Projection.Enabled
        ? current.Grid.PlaneOrigin ?? current.Projection.PinPoint : current.Grid.FlatOrigin ?? flatGridReference;
    public event Action<double, double>? PlaneSizeChanged;
    public event Action<PointD>? GridOriginChanged;
    public event Action? GridOriginToPinRequested, RectificationPreviewRequested, GridCalibrationRequested;
    private double DisplayGridSize => current.Projection.Enabled
        ? current.Grid.PlaneCellSize ?? current.Projection.GridCellSize
        : current.Grid.FlatCellSize ?? current.Projection.GridCellSize;

    private FrameworkElement BuildPlaneFields()
    {
        foreach (var (field, key) in new[] { (planeWidth, "Canvas.PlaneWidth"), (planeHeight, "Canvas.PlaneHeight") })
        {
            field.SetScrubAnchor(NumericPropertyIcon.ForProperty(key), L.Get(key));
            System.Windows.Automation.AutomationProperties.SetName(field.Input, L.Get(key));
            System.Windows.Automation.AutomationProperties.SetName(field.Slider, L.Get(key));
        }
        planeFields.Children.Add(PropertyPanelLayout.Row(new PropertyLabel(L.Get("Canvas.LogicalSize")), PropertyPanelLayout.Pair(planeWidth, planeHeight, aspectLock)));
        planeWidth.ToolTip = planeHeight.ToolTip = L.Get("Canvas.PlaneSizeHelp");
        var preview = CanvasAction("Canvas.RectificationPreview", "M2,2 L14,4 L12,14 L1,12 Z M5,5 H11 V11 H5 Z");
        preview.Margin = PropertyPanelLayout.RowMargin;
        preview.Click += (_, _) => RectificationPreviewRequested?.Invoke(); rectificationPreview = preview;
        void CommitSize(bool horizontal)
        {
            if (refreshing || !current.Projection.Enabled) return;
            if (!planeWidth.TryGetValue(out double widthValue) || !planeHeight.TryGetValue(out double heightValue))
            { Failed?.Invoke(L.Get("Canvas.InvalidPlaneSize")); RefreshIndependentFields(); return; }
            var old = CanvasProjectionGeometry.ResolvePlaneBounds(current.Projection);
            if (aspectLock.IsChecked == true)
            {
                if (horizontal) heightValue = widthValue * old.Height / old.Width;
                else widthValue = heightValue * old.Width / old.Height;
            }
            if (!CanvasProjectionGeometry.IsValidSize(widthValue, heightValue)) { Failed?.Invoke(L.Get("Canvas.InvalidPlaneSize")); return; }
            Failed?.Invoke("");
            if (widthValue != old.Width || heightValue != old.Height) PlaneSizeChanged?.Invoke(widthValue, heightValue);
        }
        BindGeometryNumber(planeWidth, () => CommitSize(true), "Canvas.InvalidPlaneSize");
        BindGeometryNumber(planeHeight, () => CommitSize(false), "Canvas.InvalidPlaneSize");
        return planeFields;
    }

    private FrameworkElement BuildGridOriginFields()
    {
        foreach (var (field, key) in new[] { (gridOriginX, "Canvas.GridOriginX"), (gridOriginY, "Canvas.GridOriginY") })
        {
            field.SetScrubAnchor(NumericPropertyIcon.ForProperty(key), L.Get(key));
            System.Windows.Automation.AutomationProperties.SetName(field.Input, L.Get(key));
            System.Windows.Automation.AutomationProperties.SetName(field.Slider, L.Get(key));
        }
        var align = CanvasAction("Canvas.GridOriginToPin", "M8,1 V5 M8,11 V15 M1,8 H5 M11,8 H15 M8,5 A3,3 0 1 1 7.99,5");
        align.Width = align.Height = PropertyPanelLayout.ControlHeight; align.MinWidth = align.MinHeight = 0;
        align.Margin = new(4, 0, 4, 0); align.Padding = new(0);
        align.HorizontalAlignment = HorizontalAlignment.Center; align.VerticalAlignment = VerticalAlignment.Center;
        align.Content = CommandGlyphs.Create("M8,1 V5 M8,11 V15 M1,8 H5 M11,8 H15 M8,5 A3,3 0 1 1 7.99,5");
        align.SetResourceReference(StyleProperty, "CanvasPropertyIconButton");
        align.ToolTip = L.Get("Canvas.GridOriginToPin");
        align.Click += (_, _) => GridOriginToPinRequested?.Invoke();
        originFields.Children.Add(PropertyPanelLayout.Row(new PropertyLabel(L.Get("Canvas.GridOrigin")), PropertyPanelLayout.Pair(gridOriginX, gridOriginY, align)));
        var calibrate = CanvasAction("Canvas.GridCalibration", "M2,2 H14 V14 H2 Z M6,2 V14 M10,2 V14 M2,6 H14 M2,10 H14");
        calibrate.Margin = PropertyPanelLayout.RowMargin;
        calibrate.Click += (_, _) => GridCalibrationRequested?.Invoke(); originFields.Children.Insert(0, calibrate);
        void CommitOrigin()
        {
            if (refreshing) return;
            if (gridOriginX.TryGetValue(out double x) && gridOriginY.TryGetValue(out double y))
            {
                var point = new PointD(x, y);
                if (point != DisplayGridOrigin) GridOriginChanged?.Invoke(point);
            }
            else RefreshIndependentFields();
        }
        BindGeometryNumber(gridOriginX, CommitOrigin); BindGeometryNumber(gridOriginY, CommitOrigin);
        return originFields;
    }

    private void BindGeometryNumber(NumericValueField field, Action publish, string invalidMessageKey = "ToolOptions.InvalidNumber")
    {
        CommitOnExit(field.Input, publish);
        field.CanBeginGesture = () => BeginNumericGesture?.Invoke() != false;
        BindTextGesture(field, invalidMessageKey);
        field.ValueChanged += _ => { if (!refreshing) publish(); };
        field.GestureCompleted += commit => NumericGestureCompleted?.Invoke(commit);
    }
    private void RefreshIndependentFields()
    {
        bool visible = current.Projection.Enabled && current.Projection.PlaneBounds is not null;
        planeFields.Visibility = rectificationPreview.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        originFields.Visibility = !current.Projection.Enabled || visible ? Visibility.Visible : Visibility.Collapsed;
        var origin = DisplayGridOrigin;
        gridOriginX.ToolTip = gridOriginY.ToolTip = L.Get(current.Projection.Enabled ? "Canvas.GridSizeHelp" : "Canvas.FlatGridOriginHelp");
        void RefreshNumber(NumericValueField field, double value)
        { if (!field.IsKeyboardFocusWithin && !field.IsInteracting) field.SetModelValue(value); }
        RefreshNumber(gridOriginX, origin.X); RefreshNumber(gridOriginY, origin.Y);
        gridSize.ToolTip = L.Get("Canvas.GridSizeHelp");
        if (!visible) return;
        var bounds = CanvasProjectionGeometry.ResolvePlaneBounds(current.Projection);
        RefreshNumber(planeWidth, bounds.Width); RefreshNumber(planeHeight, bounds.Height);
        gridSize.ToolTip = L.Get("Canvas.GridSizeHelp") + "\n" + L.Format("Canvas.GridCoverage", bounds.Width / DisplayGridSize, bounds.Height / DisplayGridSize);
    }
    private void SynchronizeGeometryNumbers()
    {
        if (current.Projection.PlaneBounds is { } bounds)
        { planeWidth.SetModelValue(bounds.Width); planeHeight.SetModelValue(bounds.Height); }
        var origin = DisplayGridOrigin;
        gridOriginX.SetModelValue(origin.X); gridOriginY.SetModelValue(origin.Y);
    }
}
