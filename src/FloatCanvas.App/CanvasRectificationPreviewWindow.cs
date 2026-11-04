using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed class CanvasRectificationPreviewWindow : Window
{
    private readonly byte[] source;
    private readonly RectD sourceBounds;
    private readonly CanvasViewTransform viewing;
    private readonly Image rectified = new() { Stretch = Stretch.Uniform };
    private readonly NumericValueField width = new(.01, 256_000_000, sliderMaximum: 2000), height = new(.01, 256_000_000, sliderMaximum: 2000);
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 6) };
    private bool refreshing;
    internal CanvasProjectionSettings Projection { get; private set; }
    internal bool Applied { get; private set; }
    internal bool InsertScreenshot { get; private set; }
    internal CanvasRectificationPreviewWindow(Window? owner, CaptureRasterResult raster, CanvasProjectionSettings projection,
        CanvasViewTransform? viewing = null)
    {
        source = raster.PngBytes; sourceBounds = raster.Bounds; Projection = projection.Clone();
        this.viewing = viewing ?? CanvasViewTransform.Identity;
        Title = L.Get("Canvas.RectificationPreview"); Owner = owner; Topmost = true; ShowInTaskbar = false;
        Width = 940; Height = 650; MinWidth = 600; MinHeight = 420; WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        var body = new DockPanel { Margin = WindowLayout.ContentMargin() };
        var controls = new StackPanel(); DockPanel.SetDock(controls, Dock.Top); body.Children.Add(controls);
        controls.Children.Add(new TextBlock { Text = L.Get("Canvas.RectificationHelp"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) });
        var choices = new ComboBox();
        foreach (var preset in CanvasAspectPresets.All) choices.Items.Add(new ComboBoxItem { Content = L.Get(preset.Key), Tag = preset });
        choices.SelectedIndex = 0;
        choices.SelectionChanged += (_, _) =>
        {
            if (choices.SelectedItem is ComboBoxItem { Tag: CanvasAspectPreset { Aspect: { } ratio } } &&
                CanvasProjectionGeometry.TrySetAspect(Projection, ratio, out var next, out _)) { Projection = next; RefreshImage(); }
        };
        AutomationProperties.SetName(choices, L.Get("Canvas.TargetAspect"));
        controls.Children.Add(PropertyPanelLayout.Row(new PropertyLabel(L.Get("Canvas.TargetAspect")), choices));
        foreach (var (field, key, kind) in new[] { (width, "Canvas.PlaneWidth", NumericPropertyKind.Width), (height, "Canvas.PlaneHeight", NumericPropertyKind.Height) })
        {
            field.SetScrubAnchor(kind, L.Get(key)); AutomationProperties.SetName(field.Input, L.Get(key)); AutomationProperties.SetName(field.Slider, L.Get(key));
        }
        controls.Children.Add(PropertyPanelLayout.Row(new PropertyLabel(L.Get("Property.Size")), PropertyPanelLayout.Pair(width, height)));
        width.Unit = height.Unit = "px"; width.ToolTip = L.Get("Canvas.PlaneWidth"); height.ToolTip = L.Get("Canvas.PlaneHeight");
        bool Commit()
        {
            if (refreshing) return false;
            if (width.TryGetValue(out double w) && height.TryGetValue(out double h) && CanvasProjectionGeometry.TryResize(Projection, w, h, out var next, out _))
            { Projection = next; RefreshImage(); return true; }
            status.Text = L.Get("Canvas.InvalidPlaneSize"); return false;
        }
        foreach (var field in new[] { width, height })
        {
            field.ValueChanged += _ => { if (field.IsInteracting) Commit(); };
            field.Input.LostKeyboardFocus += (_, _) => Commit();
            field.Input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
            field.SliderValueCommitted += () => Commit();
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom); body.Children.Add(buttons);
        void Button(string key, Action action)
        {
            var button = new Button { Content = L.Get(key), MinHeight = PropertyPanelLayout.ControlHeight,
                IsDefault = key == "Canvas.ApplyPlane", IsCancel = key == "Dialog.Cancel", Margin = new(6, 0, 0, 0) };
            button.Click += (_, _) => action(); buttons.Children.Add(button);
        }
        Button("Canvas.SwapOrientation", () => { var bounds = CanvasProjectionGeometry.ResolvePlaneBounds(Projection); if (CanvasProjectionGeometry.TryResize(Projection, bounds.Height, bounds.Width, out var next, out _)) { Projection = next; RefreshImage(); } });
        Button("Canvas.ApplyPlane", () => { if (!Commit()) return; Applied = true; SessionWindowCoordinator.Close(this); });
        Button("Canvas.InsertRectifiedCapture", () => { if (!Commit()) return; Applied = InsertScreenshot = true; SessionWindowCoordinator.Close(this); });
        Button("Dialog.Cancel", () => SessionWindowCoordinator.Close(this));
        DockPanel.SetDock(status, Dock.Bottom); body.Children.Add(status);
        var images = new Grid(); images.ColumnDefinitions.Add(new()); images.ColumnDefinitions.Add(new());
        FrameworkElement Frame(string key, Image image)
        {
            var frame = new DockPanel { Margin = new(4) };
            var title = PropertyPanelLayout.SectionTitle(L.Get(key)); title.Margin = new(0, 0, 0, 6); DockPanel.SetDock(title, Dock.Top); frame.Children.Add(title);
            var border = new Border { Child = image, BorderThickness = new(1), CornerRadius = new(6), Padding = new(4) };
            border.SetResourceReference(Border.BackgroundProperty, "CanvasInput"); border.SetResourceReference(Border.BorderBrushProperty, "CanvasBorder");
            frame.Children.Add(border); return frame;
        }
        images.Children.Add(Frame("Canvas.SourcePreview", new Image { Source = Decode(source), Stretch = Stretch.Uniform }));
        var correctedFrame = Frame("Canvas.CorrectedPreview", rectified); Grid.SetColumn(correctedFrame, 1); images.Children.Add(correctedFrame);
        body.Children.Add(images); Content = body;
        AppTheme.EnsureDialogPalette(this); AppWindowChrome.ApplyDialog(this);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { SessionWindowCoordinator.Close(this); e.Handled = true; } };
        RefreshImage();
    }
    private void RefreshImage()
    {
        refreshing = true;
        try
        {
            var bounds = CanvasProjectionGeometry.ResolvePlaneBounds(Projection);
            width.SetModelValue(bounds.Width); height.SetModelValue(bounds.Height);
            rectified.Source = Decode(CanvasRectificationRenderer.Render(source, sourceBounds, ViewingProjection(Projection, viewing)));
            status.Text = L.Format("Canvas.RectificationDimensions", bounds.Width, bounds.Height, bounds.Width / bounds.Height);
        }
        finally { refreshing = false; }
    }
    internal static CanvasProjectionSettings ViewingProjection(CanvasProjectionSettings projection, CanvasViewTransform viewing)
    {
        var result = projection.Clone();
        result.Matrix = Projective2D.FromAffine(viewing.Matrix).Compose(viewing.ProjectPlane(result.Matrix));
        var originalInverse = projection.Matrix.Inverse();
        result.Quad = result.Quad.Select(point => result.Matrix.TransformPoint(originalInverse.TransformPoint(point))).ToList();
        return result;
    }
    internal static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream;
        bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }
}
