using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

internal static class UnifiedToolOptionsTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, string output)
    {
        Directory.CreateDirectory(output);
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        RetiredToolTests.Run(check);
        foreach (string language in new[] { "zh-CN", "en" })
        foreach (string theme in new[] { "light", "dark" })
        {
            L.Initialize(language); AppTheme.Apply(theme);
            var controller = new EditorController(new()) { Tool = EditorTool.Canvas };
            var panel = new ToolOptionsPanel(controller, new ToolPresetLibrary(), ColorLibrary.Shared);
            var library = new CanvasPresetLibrary(Path.Combine(output, $"presets-{language}-{theme}.json"));
            var canvas = new CanvasToolOptions(library); panel.SetCanvasContent(canvas);
            var window = new Window { Content = panel, Width = 699, Height = 520, MinWidth = 640, MinHeight = 420,
                ShowActivated = false, ShowInTaskbar = false, Left = -12000, Top = -12000 };
            AppWindowChrome.Apply(window);
            panel.HeaderChanged += () =>
            { var header = panel.Header; window.Title = header.Title; AppWindowChrome.SetHeaderActions(window, header.Actions ?? new ContentControl()); AppWindowChrome.SetHeaderDetail(window, header.Target, true); AppWindowChrome.SetHelp(window, header.HelpTitle, header.HelpText); };
            try
            {
                canvas.TargetLabel = L.Get("RootCanvas.Main");
                window.Show(); panel.PrepareForShow(); Pump();
                var viewport = Field<ScrollViewer>(panel, "parameterScroll");
                var status = Field<TextBlock>(panel, "status");
                var gallery = Field<CanvasPresetGallery>(canvas, "presets");
                CheckMargins(language + theme + " canvas", canvas);
                check(language + theme + " canvas is content in the common tool panel", ReferenceEquals(window.Content, panel) && ReferenceEquals(viewport.Content, canvas) && canvas is IToolOptionsContent);
                check(language + theme + " empty status does not reserve a blank footer", status.Visibility == Visibility.Collapsed);
                window.Height = 1100; Pump();
                var contentRoot = (FrameworkElement)panel.Content;
                check(language + theme + " tall tool windows keep their content at the top",
                    panel.TranslatePoint(new(), window).Y < 60 && contentRoot.TranslatePoint(new(), panel).Y <= 20);
                panel.Error("fixed footer"); Pump();
                check(language + theme + " status remains at the bottom of a tall content area",
                    Math.Abs(panel.ActualHeight - status.TranslatePoint(new(0, status.ActualHeight), panel).Y - 12) < 1 && panel.ActualHeight > 1000);
                Save(window, Path.Combine(output, $"tall-canvas-{language}-{theme}.png"));
                panel.ClearStatus(); window.Height = 520; Pump();
                viewport.ApplyTemplate();
                var scrollbar = (System.Windows.Controls.Primitives.ScrollBar)viewport.Template.FindName("PART_VerticalScrollBar", viewport);
                var grid = (Grid)VisualTreeHelper.GetParent(scrollbar);
                check(language + theme + " parameters have a permanent external 12 DIP scrollbar slot", grid.ColumnDefinitions[1].Width == new GridLength(12));
                double width = viewport.ViewportWidth;
                canvas.Refresh(new CanvasSettings { Projection = new() { Enabled = true } }); canvas.PrepareForShow(); Pump();
                check(language + theme + " scrollbar changes leave the parameter width stable", Math.Abs(viewport.ViewportWidth - width) < .1);
                check(language + theme + " two independent scroll viewers avoid nested preset scrolling", LogicalDescendants(panel).OfType<ScrollViewer>().Count() == 2);
                var title = (TextBlock)window.Template.FindName("LeadingTitle", window);
                var target = (TextBlock)window.Template.FindName("HeaderDetail", window);
                check(language + theme + " target immediately follows the fixed title", title.Visibility == Visibility.Visible && target.TranslatePoint(new(), window).X >= title.TranslatePoint(new(title.ActualWidth, 0), window).X && window.Title == L.Get("Tool.Canvas"));
                check(language + theme + " canvas help uses the common caption action", ((Button)window.Template.FindName("Help", window)).Visibility == Visibility.Visible);
                gallery.PrepareForShow();
                check(language + theme + " canvas presets use the same gallery implementation", gallery is PresetGallery<CanvasPreset> && gallery.OfficialTiles.Children.Count > 0);
                string selected = gallery.SelectedId;
                canvas.Refresh(new CanvasSettings { BackgroundColor = 0xFF123456, Grid = new() { Width = 7 } }); canvas.PrepareForShow(); Pump();
                check(language + theme + " canvas edits retain the preset with a modification marker", gallery.SelectedId == selected && gallery.OfficialTiles.Children.OfType<SelectionTileButton>().Any(tile => Equals(tile.Tag, selected) && tile.IsModified));
                panel.Error("test error"); Pump();
                check(language + theme + " errors use the fixed shared footer", status.Visibility == Visibility.Visible && status.Text == "⚠ test error" && ReferenceEquals(status.Parent, panel.Content));
                panel.ClearStatus();
                library.Save("spacing fixture 0", new()); gallery.PrepareForShow(); Pump();
                var userScroll = LogicalDescendants(gallery).OfType<ScrollViewer>().Single();
                double presetWidth = userScroll.ViewportWidth;
                check(language + theme + " sparse presets do not need a scrollbar", userScroll.ComputedVerticalScrollBarVisibility == Visibility.Collapsed);
                for (int i = 1; i < 80; i++) library.Save("spacing fixture " + i, new());
                gallery.PrepareForShow(); Pump();
                check(language + theme + $" preset scrollbar does not shift content width ({presetWidth:0.##}/{userScroll.ViewportWidth:0.##}, {userScroll.ComputedVerticalScrollBarVisibility})", userScroll.ComputedVerticalScrollBarVisibility == Visibility.Visible && Math.Abs(userScroll.ViewportWidth - presetWidth) < .1);
                foreach (bool perspective in new[] { false, true })
                {
                canvas.Refresh(new CanvasSettings { Projection = new() { Enabled = perspective }, Grid = new() { Width = 7 } }); canvas.PrepareForShow(); Pump();
                foreach (int size in new[] { 640, 699 })
                foreach (double scale in new[] { 1d, 1.5, 2d })
                {
                    window.Width = size; VisualTreeHelper.SetRootDpi(window, new DpiScale(scale, scale));
                    panel.InvalidateMeasure(); panel.InvalidateArrange(); window.UpdateLayout(); Pump();
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale), (int)Math.Ceiling(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(output, $"canvas-{language}-{theme}-{size}-{scale:0.0}-{(perspective ? "perspective" : "flat")}.png")); encoder.Save(stream);
                }
                }
                foreach (var tool in Enum.GetValues<EditorTool>().Where(tool => tool is not (EditorTool.Select or EditorTool.Stamp or EditorTool.Canvas)))
                {
                    controller.Tool = tool; panel.PrepareForShow(); Pump();
                    window.Height = 1100; Pump();
                    check(language + theme + tool + " tall tool content stays top-aligned",
                        panel.TranslatePoint(new(), window).Y < 60 && ((FrameworkElement)viewport.Content).TranslatePoint(new(), panel).Y <= 20);
                    if (tool == EditorTool.Rectangle) Save(window, Path.Combine(output, $"tall-rectangle-{language}-{theme}.png"));
                    window.Height = 520; Pump();
                    CheckMargins(language + theme + tool, (FrameworkElement)viewport.Content);
                    check(language + theme + tool + " uses the common parameter and preset host", !ReferenceEquals(viewport.Content, canvas) && LogicalDescendants(panel).OfType<ScrollViewer>().Count() == 2);
                    check(language + theme + tool + " clears previous canvas help", ((Button)window.Template.FindName("Help", window)).Visibility == Visibility.Collapsed);
                    if (tool == EditorTool.Highlighter)
                    {
                        var rows = Field<Dictionary<string, FrameworkElement>>(panel, "rows");
                        var row = (Grid)rows["StrokePattern"];
                        var options = row.Children.OfType<SegmentedChoice>().Single();
                        window.Width = window.MinWidth; Pump();
                        check(language + theme + " highlighter keeps all four patterns at minimum width",
                            options.Items.Count == 4 && Grid.GetRow(options) == (language == "en" ? 1 : 0) &&
                            options.Items.All(item => ((StackPanel)item.Content).Orientation == Orientation.Horizontal));
                        window.Width = ToolOptionsPanel.PreferredWidth + 2; Pump();
                        check(language + theme + " highlighter brings its caption and four patterns inline at default width",
                            Grid.GetRow(options) == 0 && options.Items.All(item => ((StackPanel)item.Content).Orientation == Orientation.Horizontal));
                    }
                    if (tool == EditorTool.Presentation)
                    {
                        var headings = Descendants(panel).OfType<TextBlock>().Where(text => Equals(text.Tag, "ToolSection")).Select(text => text.Text);
                        check(language + theme + " keyboard sections use the display and appearance headings",
                            headings.SequenceEqual([L.Get("ToolOptions.Section.Display"), L.Get("ToolOptions.Section.Appearance")]));
                    }
                }

                void CheckMargins(string name, FrameworkElement parameters)
                {
                    var presetHost = Field<Border>(panel, "presetHost");
                    var divider = (Border)presetHost.Parent;
                    double left = parameters.TranslatePoint(new(), panel).X;
                    double right = divider.TranslatePoint(new(), panel).X - parameters.TranslatePoint(new(parameters.ActualWidth, 0), panel).X;
                    var presets = (FrameworkElement)presetHost.Child;
                    double presetLeft = presets.TranslatePoint(new(), panel).X - divider.TranslatePoint(new(), panel).X - divider.BorderThickness.Left;
                    var presetScroll = LogicalDescendants(presets).OfType<ScrollViewer>().Single();
                    presetScroll.ApplyTemplate();
                    var presetBar = (System.Windows.Controls.Primitives.ScrollBar)presetScroll.Template.FindName("PART_VerticalScrollBar", presetScroll);
                    var presetGrid = (Grid)VisualTreeHelper.GetParent(presetBar);
                    double pixel = 1 / VisualTreeHelper.GetDpi(panel).DpiScaleX + .01;
                    check(name + " reserves two units at both parameter edges", Math.Abs(left - 24) < pixel && Math.Abs(right - 24) < pixel);
                    check(name + $" presets keep one unit after the divider and a permanent scrollbar slot ({presetLeft:0.##}/{presetGrid.ColumnDefinitions[1].Width})", Math.Abs(presetLeft - 12) < pixel && presetGrid.ColumnDefinitions[1].Width == new GridLength(12));
                    check(name + " bottom and outer preset edge retain one unit", ((FrameworkElement)panel.Content).Margin.Bottom == 12 && Math.Abs(panel.ActualWidth - presets.TranslatePoint(new(presets.ActualWidth, 0), panel).X - 12) < pixel);
                }
            }
            finally { canvas.CancelEditing(); window.Close(); }
        }
        L.Initialize("zh-CN");
    }
    private static T Field<T>(object value, string key) => (T)value.GetType().GetField(key, Private)!.GetValue(value)!;
    private static void Save(Window window, string path)
    {
        var scale = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * scale.DpiScaleY), scale.PixelsPerInchX, scale.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
    { yield return root; foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach (var item in LogicalDescendants(child)) yield return item; }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { yield return root; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child; }
    private static void Pump() { Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
}
