using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatCanvas.App;
using FloatCanvas.Core;

/// <summary>Real WPF layout and raster checks on detached trees; no desktop windows or OS input.</summary>
internal static class PaletteLayoutTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly double[] Scales = [1, 1.25, 1.5, 1.75, 2, 2.25, 2.5, 3];

    internal static void Run(Action<string, bool> check)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var windows = app.Windows.Cast<Window>().ToArray();
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-palette-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            AppTheme.Apply("light");
            foreach (double scale in Scales)
                foreach (var metric in new[] { (Columns: 5, Size: 17d, Gap: 2d), (Columns: 10, Size: 28d, Gap: 2d), (Columns: 5, Size: 36d, Gap: 4d) })
                    PixelSpacing(scale, metric.Columns, metric.Size, metric.Gap, check);
            DpiChange(check);
            var colors = new ColorLibrary(Path.Combine(folder, "colors.json"));
            for (uint i = 0; i < 72; i++)
            {
                uint color = 0xFF000000 | (i * 7919 & 0xFFFFFF);
                colors.Save("Color " + i, color); colors.Remember(color);
            }
            var controller = new EditorController(new EditorSession()) { Tool = EditorTool.Line };
            var recipes = new ToolPresetLibrary(Path.Combine(folder, "presets.json"));
            for (int i = 0; i < 61; i++) recipes.Save("Recipe " + i, controller.Parameters);
            var sparse = new ToolPresetLibrary(Path.Combine(folder, "sparse-presets.json"));
            foreach (var tool in new[] { EditorTool.Freehand, EditorTool.Number })
            {
                controller.Tool = tool; sparse.Save("Only recipe", controller.Parameters);
                for (int i = 0; i < 21; i++) recipes.Save(tool + " " + i, controller.Parameters);
            }
            controller.Tool = EditorTool.Line;
            foreach (string theme in new[] { "light", "dark" })
            {
                AppTheme.Apply(theme);
                foreach (double scale in Scales)
                {
                    ProductionPanels(theme, scale, colors, controller, recipes, check);
                    PresetViewport(theme, scale, colors, controller, sparse, recipes, check);
                    foreach (var role in new[] { ColorRole.Foreground, ColorRole.Background })
                        ColorPopupFooter(theme, scale, role, folder, check);
                }
            }
            check("palette layout fixtures do not create desktop windows", app.Windows.Cast<Window>().SequenceEqual(windows));
        }
        finally { Directory.Delete(folder, true); AppTheme.Apply("light"); }
    }

    private static void ColorPopupFooter(string theme, double scale, ColorRole role, string folder, Action<string, bool> check)
    {
        string name = $"{theme} {scale * 100:0}% {role} popup";
        var field = new ColorField(role, new ColorLibrary(Path.Combine(folder, "empty-footer-colors.json")));
        var root = (FrameworkElement)field.PickerRoot;
        check(name + " has no duplicate border below the window caption", root is Border { BorderThickness.Top: 0 });
        var picker = Field<ColorPickerPanel>(field, "picker");
        var error = Field<TextBlock>(picker, "error");
        var hex = Field<TextBox>(picker, "hex");
        var columns = (Grid)((ScrollViewer)picker.Content).Content;
        var body = columns.Children.OfType<StackPanel>().First();
        var actions = body.Children.OfType<WrapPanel>().Single();
        Layout(root, scale);
        double normalHeight = root.ActualHeight;
        check(name + " has only normal padding below its final actions", error.Visibility == Visibility.Collapsed &&
            root.ActualHeight - Bounds(actions, root).Bottom <= columns.Margin.Bottom + 2);

        hex.Text = "invalid color";
        typeof(ColorPickerPanel).GetMethod("Apply", Private)!.Invoke(picker, null);
        Layout(root, scale);
        check(name + " expands for a visible invalid-color error", error.Visibility == Visibility.Visible &&
            error.ActualHeight > 0 && root.ActualHeight > normalHeight && error.Text.Length > 0);
        hex.Text = "#123456"; Layout(root, scale);
        check(name + " shrinks back after correcting the color", error.Visibility == Visibility.Collapsed && Near(root.ActualHeight, normalHeight));

        picker.Run(() => throw new IOException("fixture write failed")); Layout(root, scale);
        check(name + " expands for a resource-save error", error.Visibility == Visibility.Visible &&
            error.Text.Contains("fixture write failed") && root.ActualHeight > normalHeight);
        picker.Value = 0xFF123456; Layout(root, scale);
        check(name + " releases error space after resynchronizing valid input", error.Visibility == Visibility.Collapsed &&
            Near(root.ActualHeight, normalHeight) && root.ActualHeight - Bounds(actions, root).Bottom <= columns.Margin.Bottom + 2);
    }

    private static void PixelSpacing(double scale, int columns, double size, double gap, Action<string, bool> check)
    {
        var panel = new PixelTilePanel(columns, size, gap);
        for (int i = 0; i < columns * 2; i++) panel.Children.Add(new Border { Background = Brushes.Red });
        // A fractional parent origin exercises the case that independently rounded child margins mishandle.
        var canvas = new Canvas(); Canvas.SetLeft(panel, .5 / scale); Canvas.SetTop(panel, .5 / scale); canvas.Children.Add(panel);
        var root = new Border { Child = canvas, Width = columns * (size + gap) + 8, Height = 2 * (size + gap) + 8 };
        Layout(root, scale);
        string name = $"{scale * 100:0}% {columns}-column {size}-DIP tiles";
        var bounds = panel.Children.Cast<FrameworkElement>().Select(tile => Bounds(tile, root)).ToArray();
        check(name + " uses the requested DPI on each actual child", panel.Children.Cast<Visual>().All(tile => Near(VisualTreeHelper.GetDpi(tile).DpiScaleX, scale)));
        check(name + " has one device-pixel size and one device-pixel gap", EqualGeometry(bounds, columns, scale, gap));
        var pixels = Raster(root, scale); int y = (int)Math.Floor((bounds[0].Top + bounds[0].Height / 2) * scale);
        var runs = Runs(pixels, y);
        int[] gaps = runs.Zip(runs.Skip(1), (left, right) => right.Start - left.End - 1).ToArray();
        check(name + " renders equal visible gaps from a fractional origin", runs.Count == columns && gaps.Length == columns - 1 && gaps.Distinct().Count() == 1 && Near(gaps[0], (bounds[1].Left - bounds[0].Right) * scale));
    }

    private static void DpiChange(Action<string, bool> check)
    {
        var panel = new PixelTilePanel(10, 17, 2);
        for (int i = 0; i < 20; i++) panel.Children.Add(new Border());
        var root = new Border { Child = panel }; Layout(root, 1.25);
        double oldWidth = panel.DesiredSize.Width; Layout(root, 1.75);
        var bounds = panel.Children.Cast<FrameworkElement>().Select(tile => Bounds(tile, panel)).ToArray();
        check("layout after a detached-tree DPI change recomputes cell geometry", !Near(oldWidth, panel.DesiredSize.Width) &&
            panel.Children.Cast<Visual>().All(tile => Near(VisualTreeHelper.GetDpi(tile).DpiScaleX, 1.75)) && EqualGeometry(bounds, 10, 1.75, 2));
    }

    private static void ProductionPanels(string theme, double scale, ColorLibrary colors, EditorController controller, ToolPresetLibrary recipes, Action<string, bool> check)
    {
        string name = $"{theme} {scale * 100:0}%";
        var field = new ColorField(ColorRole.Foreground, colors) { Width = 300 };
        Layout(field, scale);
        var quick = Visuals(field).OfType<PixelTilePanel>().Single();
        check(name + " quick palette retains five columns and two rows", Rows(quick).SequenceEqual([5, 5]));
        CheckSwatches(quick.Children.OfType<Button>(), name + " quick palette", scale, check);
        var trigger = Field<Button>(field, "button");
        var chrome = trigger.Template.FindName("Chrome", trigger) as Border;
        check(name + " current-color strip retains the common rounded button chrome and a border", chrome is { } && chrome.CornerRadius.TopLeft > 0 && trigger.BorderThickness.Left > 0);
        var face = Field<Grid>(field, "swatch"); var inset = face.TranslatePoint(new Point(), trigger);
        check(name + " current color fills the area directly inside its button outline", chrome is not null &&
            Math.Abs(inset.X - chrome.BorderThickness.Left) * scale <= .51 && Math.Abs(inset.Y - chrome.BorderThickness.Top) * scale <= .51 &&
            Math.Abs(trigger.ActualWidth - inset.X - face.ActualWidth - chrome.BorderThickness.Right) * scale <= .51 &&
            Math.Abs(trigger.ActualHeight - inset.Y - face.ActualHeight - chrome.BorderThickness.Bottom) * scale <= .51 &&
            face.Clip is RectangleGeometry clip && clip.RadiusX > 0 && Near(clip.RadiusX + inset.X, chrome.CornerRadius.TopLeft));
        field.Value = 0xFFFF0000; Layout(field, scale);
        var colorRuns = Runs(Raster(trigger, scale), (int)Math.Floor(trigger.ActualHeight * scale / 2));
        check(name + " current-color pixels leave no button-background band inside the outline", chrome is not null && colorRuns.Count > 0 &&
            Math.Abs(colorRuns[0].Start - chrome.BorderThickness.Left * scale) <= 1);
        var picker = new ColorPickerPanel(ColorRole.Foreground, colors); Layout(picker, scale);
        check(name + " places color adjustments to the left of both swatch collections",
            Bounds(Field<ColorSpectrum>(picker, "spectrum"), picker).Right + 12 <= Bounds(Field<PixelTilePanel>(picker, "history"), picker).Left &&
            Bounds(Field<PixelTilePanel>(picker, "history"), picker).Left == Bounds(Field<PixelTilePanel>(picker, "pinned"), picker).Left);
        check(name + " detached color picker resolves the theme text color", picker.Foreground is SolidColorBrush actualText &&
            Application.Current.FindResource("CanvasText") is SolidColorBrush expectedText && actualText.Color == expectedText.Color);
        var history = Field<Panel>(picker, "history"); var pinned = Field<Panel>(picker, "pinned");
        check(name + " history lays out ten colors in each full row", Rows(history).SequenceEqual([10, 10]));
        check(name + " saved palette retains ten columns with a vertical scrollbar", Rows(pinned).Take(3).SequenceEqual([10, 10, 10]) && Scroll(pinned).ComputedVerticalScrollBarVisibility == Visibility.Visible && FirstRowFits(pinned));
        CheckSwatches(history.Children.OfType<Button>().Concat(pinned.Children.OfType<Button>()), name + " secondary palette", scale, check);
        var focused = pinned.Children.OfType<Button>().First(); picker.RefreshCollections();
        check(name + " palette refresh preserves the existing interactive tile", ReferenceEquals(focused, pinned.Children.OfType<Button>().First()));
        var options = new ToolOptionsPanel(controller, recipes, colors); Layout(options, scale);
        var gallery = Field<ToolPresetGallery>(options, "presets");
        check(name + " official presets occupy one five-tile row", Rows(gallery.OfficialTiles).SequenceEqual([5]));
        check(name + " personal presets retain five columns with a vertical scrollbar", Rows(gallery.UserTiles).Take(4).SequenceEqual([5, 5, 5, 5]) && Scroll(gallery.UserTiles).ComputedVerticalScrollBarVisibility == Visibility.Visible && FirstRowFits(gallery.UserTiles));
    }

    private static void PresetViewport(string theme, double scale, ColorLibrary colors, EditorController controller, ToolPresetLibrary sparse, ToolPresetLibrary dense, Action<string, bool> check)
    {
        string name = $"{theme} {scale * 100:0}%";
        controller.Tool = EditorTool.Freehand;
        var sparsePanel = new ToolOptionsPanel(controller, sparse, colors); Layout(sparsePanel, scale);
        var sparseGallery = Field<ToolPresetGallery>(sparsePanel, "presets");
        var sparseScroll = Field<ScrollViewer>(sparseGallery, "userScroll");
        var densePanel = new ToolOptionsPanel(controller, dense, colors); Layout(densePanel, scale);
        var denseGallery = Field<ToolPresetGallery>(densePanel, "presets");
        var denseScroll = Field<ScrollViewer>(denseGallery, "userScroll");
        var tiles = denseGallery.UserTiles.Children.OfType<Button>().ToArray();
        double fourthRowBottom = tiles[19].TranslatePoint(new Point(0, tiles[19].ActualHeight), denseGallery.UserTiles).Y;
        check(name + " compact tools reserve four personal-preset rows even with one saved recipe", sparseScroll.ViewportHeight + .51 / scale >= fourthRowBottom &&
            denseScroll.ViewportHeight + .51 / scale >= fourthRowBottom);
        check(name + " a large preset library does not determine the panel height", Math.Abs(sparsePanel.ActualHeight - densePanel.ActualHeight) * scale <= 1);
        double compactHeight = densePanel.ActualHeight, compactViewport = denseScroll.ViewportHeight;
        controller.Tool = EditorTool.Number; densePanel.Refresh(); Layout(densePanel, scale);
        var border = Ancestors(denseGallery).OfType<Border>().First();
        check(name + " additional tool properties grow the personal-preset viewport into the available height", densePanel.ActualHeight > compactHeight + 10 &&
            denseScroll.ViewportHeight > compactViewport + 10 && Near(denseGallery.ActualHeight, border.ActualHeight));
        controller.Tool = EditorTool.Line;
    }

    private static void CheckSwatches(IEnumerable<Button> values, string name, double scale, Action<string, bool> check)
    {
        var buttons = values.ToArray();
        check(name + " uses square color tiles with an independent visible weak border", buttons.Length > 0 && buttons.All(button =>
            button.BorderThickness.Left > 0 && button.BorderBrush is SolidColorBrush { Opacity: > 0, Color.A: > 0 and < 150 } &&
            button.Template.FindName("Chrome", button) is Border { CornerRadius: var corners } && corners == new CornerRadius(0)));
        check(name + " has actual DPI-aware arranged square hit targets", buttons.All(button => Near(VisualTreeHelper.GetDpi(button).DpiScaleX, scale) &&
            button.ActualWidth > 0 && Near(button.ActualWidth, button.ActualHeight) && Near(button.ActualWidth * scale, Math.Round(button.ActualWidth * scale))));
    }

    private static bool EqualGeometry(Rect[] bounds, int columns, double scale, double gap)
    {
        var widths = bounds.Select(rect => rect.Width * scale).ToArray();
        var heights = bounds.Select(rect => rect.Height * scale).ToArray();
        if (widths.Any(width => !Near(width, Math.Round(width)) || !Near(width, widths[0])) ||
            heights.Any(height => !Near(height, Math.Round(height)) || !Near(height, heights[0]))) return false;
        for (int i = 0; i < bounds.Length; i++)
        {
            double horizontal = (bounds[1].Left - bounds[0].Right) * scale;
            double vertical = (bounds[columns].Top - bounds[0].Bottom) * scale;
            if (!Near(horizontal, Math.Round(horizontal)) || Math.Abs(horizontal - gap * scale) > .5001 ||
                !Near(vertical, Math.Round(vertical)) || Math.Abs(vertical - gap * scale) > .5001) return false;
            if (i % columns > 0 && !Near((bounds[i].Left - bounds[i - 1].Right) * scale, horizontal)) return false;
            if (i >= columns && !Near((bounds[i].Top - bounds[i - columns].Bottom) * scale, vertical)) return false;
        }
        return true;
    }

    private static bool FirstRowFits(Panel panel)
    {
        var presenter = Ancestors(panel).OfType<ScrollContentPresenter>().First();
        double top = panel.Children.OfType<FrameworkElement>().First().TranslatePoint(new Point(), panel).Y;
        return panel.Children.OfType<FrameworkElement>().Where(tile => Near(tile.TranslatePoint(new Point(), panel).Y, top)).All(tile =>
        { var point = tile.TranslatePoint(new Point(), presenter); return point.X >= -.01 && point.X + tile.ActualWidth <= presenter.ActualWidth + .01; });
    }
    private static ScrollViewer Scroll(Panel panel) => Ancestors(panel).OfType<ScrollViewer>().First();
    private static IEnumerable<DependencyObject> Ancestors(DependencyObject root)
    { for (var parent = VisualTreeHelper.GetParent(root); parent is not null; parent = VisualTreeHelper.GetParent(parent)) yield return parent; }
    private static int[] Rows(Panel panel) => panel.Children.OfType<FrameworkElement>().Where(tile => tile.Visibility == Visibility.Visible).GroupBy(tile => Math.Round(tile.TranslatePoint(new Point(), panel).Y, 5)).Select(row => row.Count()).ToArray();
    private static Rect Bounds(FrameworkElement element, Visual root) => new(element.TranslatePoint(new Point(), (UIElement)root), element.RenderSize);
    private static T Field<T>(object source, string name) => (T)source.GetType().GetField(name, Private)!.GetValue(source)!;
    private static bool Near(double first, double second) => Math.Abs(first - second) < .01;
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    { yield return root; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child; }
    private static void Layout(FrameworkElement root, double scale)
    {
        // Materialize templates before changing the detached root DPI. Otherwise
        // pre-created ContentControl children can retain their default DPI cache.
        root.Measure(new Size(1600, 1600)); root.Arrange(new Rect(0, 0, root.DesiredSize.Width, root.DesiredSize.Height)); root.UpdateLayout();
        VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));
        foreach (var element in Visuals(root).OfType<FrameworkElement>()) { element.InvalidateMeasure(); element.InvalidateArrange(); }
        root.Measure(new Size(1600, 1600)); root.Arrange(new Rect(0, 0, root.DesiredSize.Width, root.DesiredSize.Height)); root.UpdateLayout();
    }
    private static RenderTargetBitmap Raster(FrameworkElement root, double scale)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root); return bitmap;
    }
    private static List<(int Start, int End)> Runs(BitmapSource pixels, int y)
    {
        var row = new byte[pixels.PixelWidth * 4]; pixels.CopyPixels(new Int32Rect(0, y, pixels.PixelWidth, 1), row, row.Length, 0);
        var runs = new List<(int Start, int End)>(); int start = -1;
        for (int x = 0; x <= pixels.PixelWidth; x++)
        {
            bool red = x < pixels.PixelWidth && row[x * 4 + 2] > 220 && row[x * 4 + 1] < 60 && row[x * 4] < 60 && row[x * 4 + 3] > 240;
            if (red && start < 0) start = x;
            if (!red && start >= 0) { runs.Add((start, x - 1)); start = -1; }
        }
        return runs;
    }
}
