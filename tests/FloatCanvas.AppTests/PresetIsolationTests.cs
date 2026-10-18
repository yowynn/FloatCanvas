using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

/// <summary>Exercises the production tool switch and gallery without showing windows or injecting input.</summary>
internal static class PresetIsolationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const uint EditedPen = 0xFFFF00AA, EditedTarget = 0xFF00CC66, DefaultStroke = 0xFFE03E3E;

    internal static void Run(Action<string, bool> check)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var windows = app.Windows.Cast<Window>().ToArray();
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-preset-isolation-" + Guid.NewGuid().ToString("N"));
        string language = L.Preference;
        Directory.CreateDirectory(folder);
        try
        {
            L.Initialize("zh-CN"); AppTheme.Apply("light");
            foreach (var target in new[] { EditorTool.Rectangle, EditorTool.Ellipse, EditorTool.Highlighter, EditorTool.Line, EditorTool.Arrow, EditorTool.Text, EditorTool.Number })
                FirstVisit(folder, target, check);
            ExplicitRecipeContract(folder, check);
            var layoutLibrary = new ToolPresetLibrary(Path.Combine(folder, "layout.json"));
            foreach (var tool in new[] { EditorTool.Line, EditorTool.Freehand, EditorTool.Number })
                for (int i = 0; i < 26; i++) layoutLibrary.Save(tool + " recipe " + i, new ToolParameters { Tool = tool });
            var colors = new ColorLibrary(Path.Combine(folder, "colors.json"));
            foreach (string theme in new[] { "light", "dark" })
            {
                AppTheme.Apply(theme);
                foreach (double scale in new[] { 1d, 1.25, 1.5, 1.75, 2, 2.25, 2.5, 3 })
                    HeadingLayout(layoutLibrary, colors, theme, scale, check);
            }
            check("preset isolation fixtures do not create desktop windows", app.Windows.Cast<Window>().SequenceEqual(windows));
        }
        finally { L.Initialize(language); AppTheme.Apply("light"); Directory.Delete(folder, true); }
    }

    private static void FirstVisit(string folder, EditorTool target, Action<string, bool> check)
    {
        var controller = new EditorController(new EditorSession());
        string path = Path.Combine(folder, target + ".json");
        var library = new ToolPresetLibrary(path);
        library.Save("Persistence sentinel", new ToolParameters());
        string fileBefore = Hash(File.ReadAllBytes(path));
        string definitionsBefore = JsonSerializer.Serialize(library.Official);
        var gallery = new ToolPresetGallery(controller, library);
        var session = Desktop(controller, library);
        var baselines = Field<Dictionary<string, ToolParameters>>(gallery, "officialBaselines");
        string penBaseline = JsonSerializer.Serialize(baselines["official.freehand"]);
        string penPreview = PreviewHashes(gallery)["official.freehand"];
        controller.Style.Stroke = EditedPen; controller.Style.StrokePattern = StrokePattern.Dotted;
        controller.Style.StrokeWidth = 13; controller.Style.Fill = 0xAA331144;
        gallery.Refresh();
        check(target + " editing the pen preserves its original official baseline and preview", penBaseline == JsonSerializer.Serialize(baselines["official.freehand"]) && penPreview == PreviewHashes(gallery)["official.freehand"]);

        Activate(session, target); gallery.Refresh();
        check(target + " first visit uses its independent default color and solid line", controller.Style.Stroke == DefaultStroke && controller.Style.StrokePattern == StrokePattern.Solid);
        check(target + " first visit does not inherit the previous tools width or fill", controller.Style.Fill == new ToolParameters().Style.Fill &&
            Math.Abs(controller.Style.StrokeWidth - (target == EditorTool.Highlighter ? 24 : new ToolParameters().Style.StrokeWidth)) < .001);
        check(target + " first selected official baseline uses independent defaults", baselines[gallery.SelectedId].Style.Stroke == DefaultStroke && baselines[gallery.SelectedId].Style.StrokePattern == StrokePattern.Solid);
        var previews = PreviewHashes(gallery);
        string baselineBefore = JsonSerializer.Serialize(baselines[gallery.SelectedId]);
        controller.Style.Stroke = EditedTarget; controller.Style.StrokePattern = StrokePattern.Dashed; controller.Style.StrokeWidth = 9;
        gallery.Refresh();
        check(target + " parameter editing preserves every official thumbnail including unselected recipes", previews.OrderBy(pair => pair.Key).SequenceEqual(PreviewHashes(gallery).OrderBy(pair => pair.Key)));
        check(target + " temporary editing does not rewrite its official baseline", baselineBefore == JsonSerializer.Serialize(baselines[gallery.SelectedId]));

        Activate(session, EditorTool.Freehand); gallery.Refresh();
        check(target + " switching back restores the edited pen state", controller.Style.Stroke == EditedPen && controller.Style.StrokePattern == StrokePattern.Dotted && controller.Style.StrokeWidth == 13);
        Activate(session, target); gallery.Refresh();
        check(target + " revisiting restores its own latest edited state", controller.Style.Stroke == EditedTarget && controller.Style.StrokeWidth == 9 &&
            controller.Style.StrokePattern == (target == EditorTool.Arrow ? StrokePattern.Solid : StrokePattern.Dashed));
        check(target + " tool switching does not write user recipes or official definitions", fileBefore == Hash(File.ReadAllBytes(path)) && definitionsBefore == JsonSerializer.Serialize(library.Official));
    }

    private static void ExplicitRecipeContract(string folder, Action<string, bool> check)
    {
        var controller = new EditorController(new EditorSession()) { Tool = EditorTool.Rectangle };
        var library = new ToolPresetLibrary(Path.Combine(folder, "explicit.json"));
        var gallery = new ToolPresetGallery(controller, library);
        controller.Style.Stroke = 0xBE456789; controller.Style.Fill = 0x77123456; controller.Style.StrokeWidth = 11;
        controller.Style.StrokePattern = StrokePattern.Dotted;
        gallery.Apply("official.rounded-rectangle");
        check("explicit first use of another official recipe preserves the active tools edited colors and width", controller.Style.Stroke == 0xBE456789 && controller.Style.Fill == 0x77123456 && controller.Style.StrokeWidth == 11 && controller.CornerRadius == 12);
        controller.Style.Stroke = 0xFF000000; controller.Style.StrokeWidth = 21; gallery.Refresh();
        gallery.Apply("official.rounded-rectangle");
        check("clicking an explicitly applied official recipe restores its own session baseline", controller.Style.Stroke == 0xBE456789 && controller.Style.StrokeWidth == 11 && controller.CornerRadius == 12);
    }

    private static void HeadingLayout(ToolPresetLibrary library, ColorLibrary colors, string theme, double scale, Action<string, bool> check)
    {
        var controller = new EditorController(new EditorSession()) { Tool = EditorTool.Line };
        var panel = new ToolOptionsPanel(controller, library, colors); Layout(panel, scale);
        var gallery = Field<ToolPresetGallery>(panel, "presets");
        var headings = Visuals(gallery).OfType<TextBlock>().Where(text => text.Text == L.Get("ToolOptions.Official") || text.Text == L.Get("ToolOptions.User")).ToArray();
        var officialHeading = headings.Single(text => text.Text == L.Get("ToolOptions.Official"));
        var userHeading = headings.Single(text => text.Text == L.Get("ToolOptions.User"));
        double officialGap = FirstTop(gallery.OfficialTiles, gallery) - Bottom(officialHeading, gallery);
        double userGap = FirstTop(gallery.UserTiles, gallery) - Bottom(userHeading, gallery);
        string name = $"{theme} {scale * 100:0}% preset headings";
        check(name + " have equal visual label-to-first-tile spacing", Math.Abs(officialGap - userGap) * scale <= 1.01 && officialGap > 0);
        check(name + " preserve actual DPI on both labels and tiles", new Visual[] { officialHeading, userHeading, gallery.OfficialTiles.Children[0], gallery.UserTiles.Children[0] }.All(visual => Math.Abs(VisualTreeHelper.GetDpi(visual).DpiScaleX - scale) < .001));
        check(name + " retain five official and personal columns", Rows(gallery.OfficialTiles).SequenceEqual([5]) && Rows(gallery.UserTiles).Take(4).SequenceEqual([5, 5, 5, 5]));
        var scroll = Field<ScrollViewer>(gallery, "userScroll");
        double fourthBottom = gallery.UserTiles.Children.OfType<FrameworkElement>().ElementAt(19).TranslatePoint(new Point(0, 36), gallery.UserTiles).Y;
        check(name + " retain the minimum four-row personal viewport and scrolling", scroll.ViewportHeight + .51 / scale >= fourthBottom && scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible);
        controller.Tool = EditorTool.Freehand; panel.Refresh(); Layout(panel, scale);
        double oldHeight = scroll.ViewportHeight;
        controller.Tool = EditorTool.Number; panel.Refresh(); Layout(panel, scale);
        check(name + " let the personal viewport expand beside taller tool properties", scroll.ViewportHeight > oldHeight + 10);
    }

    private static DesktopSession Desktop(EditorController controller, ToolPresetLibrary library)
    {
        var session = (DesktopSession)RuntimeHelpers.GetUninitializedObject(typeof(DesktopSession));
        Set(session, "_canvasCalibration", new CanvasCalibrationController());
        Set(session, "_editor", controller); Set(session, "_toolPresets", library); Set(session, "_settings", new UserSettings());
        Set(session, "_status", new TextBlock()); Set(session, "_host", RuntimeHelpers.GetUninitializedObject(typeof(OverlayHost)));
        Set(session, "_toolbarToolButtons", new Dictionary<EditorTool, Button>());
        Set(session, "_toolbarToolMemory", new Dictionary<EditorTool, ToolParameters>());
        Set(session, "_toolbarButtonMemory", new Dictionary<EditorTool, EditorTool>());
        return session;
    }
    private static void Activate(DesktopSession session, EditorTool tool) => typeof(DesktopSession).GetMethod("ActivateTool", Private)!.Invoke(session, [tool]);
    private static Dictionary<string, string> PreviewHashes(ToolPresetGallery gallery) => gallery.OfficialTiles.Children.OfType<Button>().ToDictionary(button => (string)button.Tag,
        button => { var image = (BitmapSource)((Image)button.Content).Source; var bytes = new byte[image.PixelHeight * image.PixelWidth * 4]; image.CopyPixels(bytes, image.PixelWidth * 4, 0); return Hash(bytes); });
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static double FirstTop(Panel panel, UIElement root) => panel.Children.OfType<FrameworkElement>().First().TranslatePoint(new Point(), root).Y;
    private static double Bottom(FrameworkElement element, UIElement root) => element.TranslatePoint(new Point(0, element.ActualHeight), root).Y;
    private static int[] Rows(Panel panel) => panel.Children.OfType<FrameworkElement>().GroupBy(tile => Math.Round(tile.TranslatePoint(new Point(), panel).Y, 5)).Select(row => row.Count()).ToArray();
    private static T Field<T>(object source, string name) => (T)source.GetType().GetField(name, Private)!.GetValue(source)!;
    private static void Set(object source, string name, object value) => source.GetType().GetField(name, Private)!.SetValue(source, value);
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    { yield return root; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child; }
    private static void Layout(FrameworkElement root, double scale)
    {
        root.Measure(new Size(1600, 1600)); root.Arrange(new Rect(0, 0, root.DesiredSize.Width, root.DesiredSize.Height)); root.UpdateLayout();
        VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));
        foreach (var element in Visuals(root).OfType<FrameworkElement>()) { element.InvalidateMeasure(); element.InvalidateArrange(); }
        root.Measure(new Size(1600, 1600)); root.Arrange(new Rect(0, 0, root.DesiredSize.Width, root.DesiredSize.Height)); root.UpdateLayout();
    }
}
