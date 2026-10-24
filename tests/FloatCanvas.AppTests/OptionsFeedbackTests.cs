using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using L = FloatCanvas.Localization.AppText;

internal static class OptionsFeedbackTests
{
    public static void Run(Action<string, bool> check)
    {
        _ = Wpf.Application.Current ?? new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        DefaultSizes(check);
        NumericInput(check);
        string old = L.Preference;
        string path = Path.Combine(Path.GetTempPath(), "FloatCanvas-options-feedback-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        try
        {
            foreach (string locale in new[] { "zh-CN", "en" }) { L.Initialize(locale); Galleries(path, locale, check); }
            PersistedDefaults(path, check);
        }
        finally { L.Initialize(old); Directory.Delete(path, true); }
        ToolOptionsPanelTests.Run(check);
        CreationSelectionTests.Run(check);
    }

    private static void DefaultSizes(Action<string, bool> check)
    {
        foreach (var tool in new[] { EditorTool.Rectangle, EditorTool.RoundedRectangle, EditorTool.Ellipse, EditorTool.Symbol })
        {
            var session = new EditorSession(); var controller = new EditorController(session) { Tool = tool, DefaultWidth = 96, DefaultHeight = 64, Symbol = SymbolKind.Star };
            Click(controller, new(300, 200)); var element = session.Document.Elements.Single(); var first = element.Id;
            bool line = ElementGeometry.IsLine(element.Kind);
            check(tool + " click creates a centered object with configured dimensions or line length", controller.Tool == tool && session.Selection.SetEquals([first]) && session.UndoCount == 1 &&
                (line ? element.Points.SequenceEqual([new PointD(0, 0), new PointD(96, 0)]) && element.Transform == Affine2D.Translation(252, 200) :
                    element.Width == 96 && element.Height == 64 && element.Transform == Affine2D.Translation(252, 168)));
            string before = JsonSerializer.Serialize(session.Document);
            Click(controller, new(600, 400));
            check(tool + " another blank click creates and selects another default-sized object", session.Document.Elements.Count == 2 && session.Selection.SetEquals([session.Document.Elements[^1].Id]) && session.UndoCount == 2);
            check(tool + " default-size insertion is exactly undoable with previous selection", session.Undo() && JsonSerializer.Serialize(session.Document) == before && session.Selection.SetEquals([first]));
            Click(controller, new(300, 200), OverlayModifiers.Control);
            check(tool + " Ctrl still creates a default-sized overlapping object", session.Document.Elements.Count == 2 && session.Document.Elements[^1].Id != first && session.Selection.SetEquals([session.Document.Elements[^1].Id]));
            session.SetSelection([]);
            Send(controller, OverlayPointerKind.Down, new(800, 500)); Send(controller, OverlayPointerKind.Move, new(880, 540)); Send(controller, OverlayPointerKind.Up, new(880, 540));
            element = session.Document.Elements[^1];
            check(tool + " dragging overrides default dimensions with the actual gesture", line ? element.Points[^1] == new PointD(80, 40) : element.Width == 80 && element.Height == 40);
        }
        var square = new EditorController(new EditorSession()) { Tool = EditorTool.Ellipse, ConstrainAspect = true, DefaultWidth = 90, DefaultHeight = 50 };
        Click(square, new(100, 100));
        check("constrained default-size shapes use the configured width for both axes", square.Session.Document.Elements.Single() is { Width: 90, Height: 90 });
        var parameters = new ToolParameters { DefaultWidth = 99, DefaultHeight = 77 };
        var copy = parameters.Clone(); copy.DefaultWidth = 123;
        check("default dimensions clone independently", parameters.DefaultWidth == 99 && copy.DefaultHeight == 77);
        check("invalid default dimensions are rejected before application", !new ToolParameters { DefaultWidth = 0 }.IsValid() && !new ToolParameters { DefaultHeight = double.NaN }.IsValid());
    }

    private static void NumericInput(Action<string, bool> check)
    {
        var input = new Controls.TextBox { Text = "12" }; var field = new NumericValueField(input, 0, 1000, 0, 100, .5);
        int changes = 0, invalid = 0; double latest = 0;
        field.ValueChanged += v => { changes++; latest = v; }; field.InvalidInput += () => invalid++;
        field.Value = 25;
        check("numeric programmatic synchronization updates text and slider silently", field.Input == input && input.Text == "25" && field.Slider.Value == 25 && changes == 0);
        input.Text = "37.5";
        check("typed numeric input synchronizes the slider and emits once", field.Value == 37.5 && field.Slider.Value == 37.5 && changes == 1 && latest == 37.5);
        field.Slider.Value = 42.1257;
        check("slider input synchronizes precise text and emits one normalized value", input.Text == 42.126.ToString("0.###", CultureInfo.CurrentCulture) && field.Value == 42.126 && changes == 2 && latest == 42.126);
        input.Text = "750";
        check("precise typed values remain available beyond the slider common range", field.Value == 750 && field.Slider.Value == 100 && field.TryGetValue(out var value) && value == 750);
        int before = changes; input.Text = "invalid";
        check("invalid numeric drafts do not corrupt the last value or emit a semantic edit", field.Value == 750 && input.Text == "invalid" && invalid == 1 && changes == before && !field.TryGetValue(out _));
        field.Value = 12.5;
        check("numeric controls retain normal IME-enabled textbox editing", Wpf.Input.InputMethod.GetIsInputMethodEnabled(field.Input) && field.Input.Text == 12.5.ToString("0.###", CultureInfo.CurrentCulture));
    }

    private static void Galleries(string path, string locale, Action<string, bool> check)
    {
        Feedback4OptionsTests.Galleries(path, locale + "-options", check);
        Feedback4OptionsTests.Parameters(path, locale + "-options", check);
        var controller = new EditorController(new EditorSession()) { Tool = EditorTool.Rectangle };
        var panel = new ToolOptionsPanel(controller, new ToolPresetLibrary(Path.Combine(path, locale + "-preview-presets.json")), new ColorLibrary(Path.Combine(path, locale + "-preview-colors.json")));
        RenderPreviews(panel, controller, locale);
    }

    private static void PersistedDefaults(string path, Action<string, bool> check)
    {
        string file = Path.Combine(path, "defaults.json"); var library = new ToolPresetLibrary(file);
        var p = new ToolParameters { Tool = EditorTool.Rectangle, DefaultWidth = 321, DefaultHeight = 123 };
        var preset = library.Save("Dimensions", p); var read = new ToolPresetLibrary(file).Apply(preset.Id);
        check("user presets persist complete default dimensions", read.DefaultWidth == 321 && read.DefaultHeight == 123);
        var official = library.Apply("official.rounded-rectangle");
        check("official geometry presets restore their fixed default dimensions", official.DefaultWidth == 144 && official.DefaultHeight == 108);
        var legacy = JsonSerializer.Deserialize<ToolParameters>("{\"Tool\":5}")!;
        check("existing presets missing default dimensions gain safe initial values", legacy.DefaultWidth == 160 && legacy.DefaultHeight == 100 && legacy.IsValid());
    }

    private static void RenderPreviews(ToolOptionsPanel panel, EditorController controller, string locale)
    {
        string? output = Environment.GetEnvironmentVariable("FLOATCANVAS_OPTIONS_FEEDBACK_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        foreach (string theme in new[] { "light", "dark" })
        {
            AppTheme.Apply(theme);
            Pump();
            foreach (var tool in new[] { EditorTool.Rectangle, EditorTool.Line })
            {
                controller.Tool = tool; panel.Refresh(); panel.Measure(new Wpf.Size(520, double.PositiveInfinity)); panel.Arrange(new Wpf.Rect(panel.DesiredSize)); panel.UpdateLayout();
                Pump(); panel.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(780, (int)Math.Ceiling(panel.ActualHeight * 1.5), 144, 144, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(panel); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, $"options-{locale}-{theme}-{tool}.png")); encoder.Save(file);
            }
        }
    }
    private static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static void Click(EditorController controller, PointD point, OverlayModifiers modifiers = OverlayModifiers.None)
    { Send(controller, OverlayPointerKind.Down, point, modifiers); Send(controller, OverlayPointerKind.Up, point, modifiers); }
    private static void Send(EditorController controller, OverlayPointerKind kind, PointD point, OverlayModifiers modifiers = OverlayModifiers.None) => controller.Pointer(new(kind, new(point.X, point.Y), OverlayMouseButton.Left, modifiers));
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
}
