using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class PropertyChangeRegionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check, string? output = null)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        output ??= Path.Combine(ApplicationPaths.ProfileDirectory, "property-change-renders");
        Directory.CreateDirectory(output);
        foreach (string theme in new[] { "light", "dark" })
        {
            AppTheme.Apply(theme);
            Canvas(check, output, theme);
            Colors(check, output, theme, inspector: false);
            Colors(check, output, theme, inspector: true);
        }
    }

    private static void Canvas(Action<string, bool> check, string output, string theme)
    {
        var settings = new CanvasSettings();
        var window = new CanvasSettingsWindow(CanvasPresetLibrary.OfficialOnly());
        window.ProjectionChanged += enabled => { settings.Projection.Enabled = enabled; window.Refresh(settings, false); };
        window.GridVisibilityChanged += visible => { settings.Grid.Visible = visible; window.Refresh(settings, false); };
        window.GridSizeLockChanged += locked => { settings.Projection.LockedGridCellSize = locked ? settings.Projection.CellSize : null; window.Refresh(settings, false); };
        var region = Field<PropertyChangeRegion>(window, "projectionOptions");
        var counts = Field<PropertyChangeRegion>(window, "gridCountsRegion");
        try
        {
            Show(window);
            var projection = Field<CheckBox>(window, "projection");
            var flat = Field<StackPanel>(window, "flatSizePanel");
            var actions = Field<StackPanel>(window, "perspectiveActions");
            check(theme + " canvas opens without a permanent dependency cue", region.CueStrength == 0 && counts.CueStrength == 0);
            region.Change(() => { }); Pump(25);
            check(theme + " unchanged content does not start a cue", region.CueStrength == 0);
            Toggle(projection); Pump(65);
            check(theme + " perspective toggle highlights one block and replaces flat controls", region.CueStrength > 0 && flat.Visibility == Visibility.Collapsed && actions.Visibility == Visibility.Visible);
            CheckBounds(check, theme + " perspective", region, (FrameworkElement)projection.Parent);
            Save(window, Path.Combine(output, theme + "-perspective.png"));
            Pump(1100);
            check(theme + " perspective cue clears and restores automatic height", region.CueStrength == 0 && double.IsNaN(region.Height));
            window.Refresh(settings, false); Pump(25);
            check(theme + " regular canvas refresh does not replay the cue", region.CueStrength == 0);

            var gridColor = Field<ColorField>(window, "gridColor");
            var gridRow = (FrameworkElement)gridColor.Parent;
            Toggle(Field<CheckBox>(window, "gridVisible")); Pump(40);
            check(theme + " grid toggle keeps its fixed fields visible and ungrouped", gridRow.IsVisible && AncestorRegion(gridRow) is null && region.CueStrength == 0 && counts.CueStrength == 0);
            Toggle(Field<CheckBox>(window, "gridSizeLock")); Pump(65);
            check(theme + " grid lock cues only the changing counts region", counts.CueStrength > 0 && region.CueStrength == 0 && Field<TextBox>(window, "lockedColumns").IsVisible);
            Toggle(projection); Pump(20); Toggle(projection); Pump(300);
            check(theme + " rapid perspective toggles settle at the latest layout", settings.Projection.Enabled && actions.IsVisible && !flat.IsVisible && double.IsNaN(region.Height) && Math.Abs(region.ActualHeight - region.Child.DesiredSize.Height) < 1);
            window.Hide(); Pump(20);
            check(theme + " hiding cancels both transient regions", region.CueStrength == 0 && counts.CueStrength == 0 && double.IsNaN(region.Height));
            settings.Projection.Enabled = false; window.Refresh(settings, false); window.Show(); Pump(50);
            check(theme + " reopening synchronizes without a cue", flat.IsVisible && !actions.IsVisible && region.CueStrength == 0);
        }
        finally { window.CloseForDispose(); }
    }

    private static void Colors(Action<string, bool> check, string output, string theme, bool inspector)
    {
        var session = new EditorSession();
        var shape = new CanvasElement { Kind = ElementKind.Rectangle, Width = 100, Height = 60, Style = new() { Stroke = 0xFF3366AA, Fill = 0xFF778899 } };
        session.Add(shape); session.SetSelection([shape.Id]);
        var controller = new EditorController(session) { Tool = EditorTool.Rectangle };
        controller.Style.LinkColors = false;
        string folder = Path.Combine(output, "profiles", theme + (inspector ? "-inspector" : "-tools"));
        object owner;
        Window window;
        if (inspector) window = (InspectorWindow)(owner = new InspectorWindow(() => session));
        else
        {
            var panel = new ToolOptionsPanel(controller, new ToolPresetLibrary(Path.Combine(folder, "tools.json")), new ColorLibrary(Path.Combine(folder, "colors.json")));
            owner = panel;
            window = new Window { Content = panel, Width = ToolOptionsPanel.PreferredWidth + 2, Height = 620 };
            AppTheme.UseRegularControls(window);
        }
        string name = theme + (inspector ? " inspector" : " tools");
        try
        {
            Show(window);
            if (owner is InspectorWindow properties) properties.PrepareForShow();
            if (owner is ToolOptionsPanel tools) tools.PrepareForShow();
            window.UpdateLayout();
            var region = Field<PropertyChangeRegion>(owner, "linkedColorsRegion");
            var toggle = Field<CheckBox>(owner, "linkColors");
            var rows = Field<Dictionary<string, FrameworkElement>>(owner, inspector ? "fieldContainers" : "rows");
            var fill = rows[inspector ? "Fill" : "Background"];
            uint savedFill = inspector ? shape.Style.Fill : controller.Style.Fill;
            check(name + " starts with flat, unhighlighted color rows", region.CueStrength == 0 && fill.IsVisible && !ReferenceEquals(toggle.Parent, region.Child));
            Toggle(toggle); Pump(65);
            check(name + " link hides the independent color and cues the replacement", fill.Visibility == Visibility.Collapsed && region.CueStrength > 0);
            CheckBounds(check, name, region, (FrameworkElement)toggle.Parent);
            Save(window, Path.Combine(output, theme + (inspector ? "-inspector-linked.png" : "-tools-linked.png")));
            Toggle(toggle); Pump(300);
            check(name + " unlink reveals the full color block with no stranded height", fill.IsVisible && region.CueStrength > 0 && double.IsNaN(region.Height));
            check(name + " linking preserves the independent fill color", (inspector ? shape.Style.Fill : controller.Style.Fill) == savedFill);
            Pump(800);
            check(name + " color cue disappears automatically", region.CueStrength == 0);
            window.Width = inspector ? 300 : PropertyPanelLayout.TwoColumnMinimumWidth; window.UpdateLayout();
            Toggle(toggle); Pump(65);
            CheckBounds(check, name + " narrow", region, (FrameworkElement)toggle.Parent);
            window.Hide(); Pump(20);
            check(name + " hidden color region releases animation state", region.CueStrength == 0 && double.IsNaN(region.Height));
        }
        finally
        {
            window.Close();
            // Inspector.Close deliberately hides its reusable production window.
            // This fixture owns the HWND and must release it after its last use.
            if (window is InspectorWindow)
                System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(window).Handle)?.Dispose();
        }
    }

    private static void CheckBounds(Action<string, bool> check, string name, PropertyChangeRegion region, FrameworkElement trigger)
    {
        var child = (FrameworkElement)region.Child;
        double left = child.TranslatePoint(new(), region).X;
        check(name + " adds ten DIP outside both edges without indenting fields", Math.Abs(left - 10) < .01 && Math.Abs(region.ActualWidth - child.ActualWidth - 20) < .01 &&
            Math.Abs(child.TranslatePoint(new(), trigger).X) < 1);
        var viewport = Ancestor<ScrollContentPresenter>(region);
        var bounds = region.TransformToAncestor(viewport).TransformBounds(new Rect(region.RenderSize));
        check(name + " preserves the cue inside the scroll viewport", bounds.Left >= -.5 && bounds.Right <= viewport.ActualWidth + .5);
        var drawing = VisualTreeHelper.GetDrawing(region);
        check(name + " draws a single continuous rectangle for the changed block", drawing is not null && CountShapes(drawing) == 1 && Math.Abs(drawing.Bounds.Width - region.ActualWidth) < 1 && Math.Abs(drawing.Bounds.Height - region.ActualHeight) < 1);
    }

    private static int CountShapes(Drawing drawing) => drawing is GeometryDrawing ? 1 : drawing is DrawingGroup group ? group.Children.Sum(CountShapes) : 0;

    private static void Show(Window window)
    {
        window.ShowActivated = false; window.ShowInTaskbar = false; window.Topmost = false; window.Opacity = 0;
        window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -10000; window.Top = -10000;
        window.Show(); Pump(40); window.UpdateLayout();
    }

    private static void Save(Window window, string path)
    {
        Pump(180);
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(content.ActualWidth)), Math.Max(1, (int)Math.Ceiling(content.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(window.TryFindResource("CanvasSurface") as Brush ?? SystemColors.WindowBrush, null, new Rect(content.RenderSize));
            context.DrawRectangle(new VisualBrush(content), null, new Rect(content.RenderSize));
        }
        bitmap.Render(drawing);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); png.Save(file);
    }

    private static PropertyChangeRegion? AncestorRegion(DependencyObject child)
    {
        for (var parent = VisualTreeHelper.GetParent(child); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is PropertyChangeRegion region) return region;
        return null;
    }

    private static T Ancestor<T>(DependencyObject child) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(child); parent is not null; parent = VisualTreeHelper.GetParent(parent)) if (parent is T match) return match;
        throw new InvalidOperationException(typeof(T).Name + " not found");
    }

    private static void Toggle(CheckBox toggle) { toggle.IsChecked = toggle.IsChecked != true; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => frame.Continue = false; timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
    }
}
