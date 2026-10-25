using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

/// <summary>Selected and modified preset chrome must never replace or paint over its artwork.</summary>
internal static class PresetSelectionAppearanceTests
{
    internal static void Run(Action<string, bool> check)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var windows = app.Windows.Cast<Window>().ToArray();
        string? output = Environment.GetEnvironmentVariable("FLOATCANVAS_SELECTION_PREVIEW_DIR");
        if (output is not null) Directory.CreateDirectory(output);
        try
        {
            foreach (string theme in new[] { "light", "dark", "high-contrast" })
            {
                AppTheme.Apply(theme == "high-contrast" ? "light" : theme);
                if (theme == "high-contrast") AppTheme.ApplyPalette(app.Resources, false, true);
                foreach (double scale in new[] { 1d, 1.25, 1.5, 1.75, 2, 2.25, 2.5, 3 })
                {
                    string name = theme + " " + scale * 100 + "%";
                    var artwork = ToolParameterIcon.Create(new ToolParameters { Tool = EditorTool.Highlighter, Style = new() { Stroke = 0xFFE03E3E, StrokeWidth = 24 } }, 24);
                    var tile = new SelectionTileButton { Width = 36, Height = 36, MinWidth = 0, MinHeight = 0, Padding = new Thickness(4), BorderThickness = new Thickness(1), Content = artwork };
                    var root = new Border { Child = tile, Width = 36, Height = 36 };
                    root.SetResourceReference(Border.BackgroundProperty, "CanvasSurface");
                    tile.SetSelected(false); Layout(root, scale);
                    var before = Raster(root, scale);
                    var previewBounds = new Rect(((FrameworkElement)artwork).TranslatePoint(new Point(), root), ((FrameworkElement)artwork).RenderSize);
                    check(name + " leaves the original preview and dimensions intact", ReferenceEquals(tile.Content, artwork) && Near(previewBounds.Width, 24) && Near(previewBounds.Height, 24));
                    tile.SetSelected(true); Layout(root, scale);
                    var selected = Raster(root, scale);
                    var corner = tile.Template.FindName("ModifiedCorner", tile) as System.Windows.Shapes.Path;
                    check(name + " provides an inset triangular modification corner", corner is not null);
                    check(name + " uses selection border without a check or content wrapper", ReferenceEquals(tile.Content, artwork) && !tile.IsModified && corner!.Visibility == Visibility.Collapsed &&
                        AutomationProperties.GetItemStatus(tile) == L.Get("Audit.Selected") && Equals(tile.BorderBrush, app.Resources["CanvasAccent"]));
                    check(name + " selection leaves all preview pixels unchanged", SamePreview(before, selected, previewBounds, scale));

                    tile.SetSelected(true, modified: true); Layout(root, scale);
                    var modified = Raster(root, scale);
                    var cornerBounds = new Rect(corner!.TranslatePoint(new Point(), root), corner.RenderSize);
                    check(name + " keeps the rounded triangular corner entirely inside the upper-right tile bounds", corner.Visibility == Visibility.Visible && !corner.IsHitTestVisible &&
                        cornerBounds.Width >= 8 && cornerBounds.Height >= 8 && Near(cornerBounds.Right, root.ActualWidth) && Near(cornerBounds.Top, 0) &&
                        new Rect(root.RenderSize).Contains(cornerBounds) && corner.Data.FillContains(new Point(8, 4)) &&
                        !corner.Data.FillContains(new Point(2, 8)) && Equals(corner.Fill, app.Resources["CanvasAccent"]));
                    check(name + " modified chrome keeps the original preview pixels and accessible state", SamePreview(before, modified, previewBounds, scale) &&
                        AutomationProperties.GetItemStatus(tile) == L.Get("Audit.SelectedModified"));
                    check(name + " modification corner is actually rasterized", ColorAt(modified, cornerBounds.Right - 3, cornerBounds.Top + 3, scale) == ((SolidColorBrush)corner.Fill).Color);
                    if (output is not null && scale is 1 or 1.5 or 2 or 3)
                    {
                        Save(selected, System.IO.Path.Combine(output, theme + "-" + scale * 100 + "-selected.png"));
                        Save(modified, System.IO.Path.Combine(output, theme + "-" + scale * 100 + "-modified.png"));
                    }
                    tile.SetSelected(false, modified: true); Layout(root, scale);
                    check(name + " clearing selection also clears the modification corner", !tile.IsModified && corner.Visibility == Visibility.Collapsed &&
                        ReferenceEquals(tile.Content, artwork) && AutomationProperties.GetItemStatus(tile) == "");
                }
            }
            check("preset selection appearance fixtures do not create native windows", app.Windows.Cast<Window>().SequenceEqual(windows));
        }
        finally { AppTheme.Apply("light"); }
    }

    private static void Layout(FrameworkElement root, double scale)
    {
        root.Measure(new Size(100, 100)); root.Arrange(new Rect(root.DesiredSize)); root.UpdateLayout();
        VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));
        foreach (var value in Visuals(root).OfType<FrameworkElement>()) { value.InvalidateMeasure(); value.InvalidateArrange(); }
        root.Measure(new Size(100, 100)); root.Arrange(new Rect(root.DesiredSize)); root.UpdateLayout();
    }
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var value in Visuals(VisualTreeHelper.GetChild(root, i))) yield return value;
    }
    private static bool Near(double first, double second) => Math.Abs(first - second) < .05;
    private static RenderTargetBitmap Raster(FrameworkElement root, double scale)
    {
        var result = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        result.Render(root); return result;
    }
    private static bool SamePreview(BitmapSource first, BitmapSource second, Rect region, double scale)
    {
        var pixels = new Int32Rect((int)Math.Ceiling(region.Left * scale), (int)Math.Ceiling(region.Top * scale),
            (int)Math.Floor(region.Width * scale), (int)Math.Floor(region.Height * scale));
        var a = new byte[pixels.Width * pixels.Height * 4]; var b = new byte[a.Length];
        first.CopyPixels(pixels, a, pixels.Width * 4, 0); second.CopyPixels(pixels, b, pixels.Width * 4, 0);
        return a.SequenceEqual(b);
    }
    private static Color ColorAt(BitmapSource pixels, double x, double y, double scale)
    {
        var sample = new byte[4]; pixels.CopyPixels(new Int32Rect((int)(x * scale), (int)(y * scale), 1, 1), sample, 4, 0);
        return Color.FromArgb(sample[3], sample[2], sample[1], sample[0]);
    }
    private static void Save(BitmapSource pixels, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(pixels));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
