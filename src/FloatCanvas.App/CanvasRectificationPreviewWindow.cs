using System.IO;
using System.Windows;
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
    private readonly Image rectified = new() { Stretch = Stretch.Uniform };
    private readonly NumericValueField width = new(.01, 256_000_000, sliderMaximum: 2000), height = new(.01, 256_000_000, sliderMaximum: 2000);
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 6) };
    private bool refreshing;
    internal CanvasProjectionSettings Projection { get; private set; }
    internal bool Applied { get; private set; }
    internal bool InsertScreenshot { get; private set; }
    internal CanvasRectificationPreviewWindow(Window? owner, CaptureRasterResult raster, CanvasProjectionSettings projection)
    {
        source = raster.PngBytes; sourceBounds = raster.Bounds; Projection = projection.Clone();
        Title = L.Get("Canvas.RectificationPreview"); Owner = owner; Topmost = true; ShowInTaskbar = false;
        Width = 940; Height = 650; MinWidth = 600; MinHeight = 420; WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        var body = new DockPanel { Margin = new(18) };
        var controls = new StackPanel(); DockPanel.SetDock(controls, Dock.Top); body.Children.Add(controls);
        controls.Children.Add(new TextBlock { Text = L.Get("Canvas.RectificationHelp"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) });
        var choices = new ComboBox { MinHeight = 28, Margin = new(0, 0, 0, 8) };
        foreach (var preset in CanvasAspectPresets.All) choices.Items.Add(new ComboBoxItem { Content = L.Get(preset.Key), Tag = preset });
        choices.SelectedIndex = 0;
        choices.SelectionChanged += (_, _) =>
        {
            if (choices.SelectedItem is ComboBoxItem { Tag: CanvasAspectPreset { Aspect: { } ratio } } &&
                CanvasProjectionGeometry.TrySetAspect(Projection, ratio, out var next, out _)) { Projection = next; RefreshImage(); }
        };
        controls.Children.Add(choices);
        var sizes = new Grid(); sizes.ColumnDefinitions.Add(new()); sizes.ColumnDefinitions.Add(new());
        var widthPanel = new StackPanel(); widthPanel.Children.Add(new TextBlock { Text = L.Get("Canvas.PlaneWidth") }); widthPanel.Children.Add(width);
        var heightPanel = new StackPanel(); heightPanel.Children.Add(new TextBlock { Text = L.Get("Canvas.PlaneHeight") }); heightPanel.Children.Add(height);
        sizes.Children.Add(widthPanel); Grid.SetColumn(heightPanel, 1); sizes.Children.Add(heightPanel); controls.Children.Add(sizes);
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
            field.Input.LostKeyboardFocus += (_, _) => Commit();
            field.Input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
            field.SliderValueCommitted += () => Commit();
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom); body.Children.Add(buttons);
        void Button(string key, Action action)
        {
            var button = new Button { Content = L.Get(key), Padding = new(12, 5, 12, 5), Margin = new(6, 0, 0, 0) };
            button.Click += (_, _) => action(); buttons.Children.Add(button);
        }
        Button("Canvas.SwapOrientation", () => { var bounds = CanvasProjectionGeometry.ResolvePlaneBounds(Projection); if (CanvasProjectionGeometry.TryResize(Projection, bounds.Height, bounds.Width, out var next, out _)) { Projection = next; RefreshImage(); } });
        Button("Canvas.ApplyPlane", () => { if (!Commit()) return; Applied = true; SessionWindowCoordinator.Close(this); });
        Button("Canvas.InsertRectifiedCapture", () => { if (!Commit()) return; Applied = InsertScreenshot = true; SessionWindowCoordinator.Close(this); });
        Button("Dialog.Cancel", () => SessionWindowCoordinator.Close(this));
        DockPanel.SetDock(status, Dock.Bottom); body.Children.Add(status);
        var images = new Grid(); images.ColumnDefinitions.Add(new()); images.ColumnDefinitions.Add(new());
        FrameworkElement Frame(string key, Image image) => new GroupBox { Header = L.Get(key), Content = image, Margin = new(4) };
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
            rectified.Source = Decode(CanvasRectificationRenderer.Render(source, sourceBounds, Projection));
            status.Text = L.Format("Canvas.RectificationDimensions", bounds.Width, bounds.Height, bounds.Width / bounds.Height);
        }
        finally { refreshing = false; }
    }
    internal static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream;
        bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }
}
