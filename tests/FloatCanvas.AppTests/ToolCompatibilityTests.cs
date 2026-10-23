using System.IO;
using System.Reflection;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;

/// <summary>Legacy tool activation and explicit emoji sizes exercised through detached editor controls.</summary>
internal static class ToolCompatibilityTests
{
    public static void Run(Action<string, bool> check)
    {
        var app = Wpf.Application.Current ?? new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
        string root = Path.Combine(Path.GetTempPath(), "FloatCanvas-tool-compatibility", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string preference = L.Preference;
        try
        {
            foreach (string language in new[] { "zh-CN", "en" })
            {
                L.Initialize(language);
                string folder = Path.Combine(root, language); Directory.CreateDirectory(folder);
                Triangles(folder, (name, passed) => check(language + " " + name, passed));
                Emoji(folder, (name, passed) => check(language + " " + name, passed));
            }
            check("compatibility checks show no native windows or injected input", app.Windows.Cast<Wpf.Window>().All(window =>
                !window.IsVisible && new System.Windows.Interop.WindowInteropHelper(window).Handle == 0));
        }
        finally { L.Initialize(preference); Directory.Delete(root, recursive: true); }
    }

    private static void Triangles(string root, Action<string, bool> check)
    {
        string path = Path.Combine(root, "presets.json");
        var legacy = new ToolParameters { Tool = EditorTool.Triangle, Symbol = SymbolKind.Heart,
            Style = new() { Stroke = 0x80112233, Fill = 0x40445566, StrokeWidth = 7 } };
        File.WriteAllText(path, JsonSerializer.Serialize(new { Version = 1, Presets = new[] {
            new ToolPreset { Id = "user-legacy-triangle", Name = "Legacy triangle", Parameters = legacy } } }));
        string originalFile = File.ReadAllText(path), originalParameters = JsonSerializer.Serialize(legacy);
        var library = new ToolPresetLibrary(path);
        var session = new EditorSession(); var controller = new EditorController(session) { Tool = EditorTool.Symbol, Symbol = SymbolKind.Star };
        var panel = new ToolOptionsPanel(controller, library, new ColorLibrary(Path.Combine(root, "colors.json")));
        Field<ToolPresetGallery>(panel, "presets").Apply("user-legacy-triangle");
        check("a legacy user triangle preset activates the unified symbol triangle", controller.Tool == EditorTool.Symbol &&
            controller.Symbol == SymbolKind.Triangle && controller.Style.Stroke == 0x80112233 && controller.Style.Fill == 0x40445566 && controller.Style.StrokeWidth == 7);
        check("legacy triangle activation exposes the working symbol gallery", Field<Dictionary<string, Wpf.FrameworkElement>>(panel, "rows")["Symbols"].Visibility == Wpf.Visibility.Visible);
        check("applying a legacy preset does not rewrite its file or mutate the saved values", File.ReadAllText(path) == originalFile &&
            library.User.Single().Parameters.Tool == EditorTool.Triangle && JsonSerializer.Serialize(legacy) == originalParameters);
        Gesture(controller, new(20, 30), new(140, 110));
        var triangle = session.Document.Elements.Single(); var outline = ElementGeometry.Outline(triangle);
        double side = outline[0].DistanceTo(outline[1]);
        check("the upgraded preset draws an editable symbol using the requested nonuniform dimensions", triangle.Kind == ElementKind.Symbol && triangle.Symbol == SymbolKind.Triangle &&
            Math.Abs(outline.Max(p => p.X) - outline.Min(p => p.X) - 120) < 1e-8 && Math.Abs(outline[1].Y - outline[0].Y - 80 * Math.Sqrt(3) / 2) < 1e-8 &&
            session.UndoCount == 1 && session.Undo() && session.Redo());

        controller.Tool = EditorTool.Freehand; controller.Symbol = SymbolKind.Cross; controller.Tool = EditorTool.Triangle;
        check("direct legacy tool activation resolves to triangle rather than the last gallery symbol", controller.Tool == EditorTool.Symbol && controller.Symbol == SymbolKind.Triangle);
        controller.Symbol = SymbolKind.Heart; controller.Tool = EditorTool.Triangle;
        check("legacy activation also selects triangle while symbol tool is already active", controller.Tool == EditorTool.Symbol && controller.Symbol == SymbolKind.Triangle);
        controller.ApplyParameters(legacy);
        check("direct legacy parameter application keeps an independent normalized tool state", controller.Tool == EditorTool.Symbol && controller.Symbol == SymbolKind.Triangle &&
            JsonSerializer.Serialize(legacy) == originalParameters);

        var official = Field<ToolPresetGallery>(panel, "presets").OfficialTiles.Children.OfType<Controls.Button>().Single(button => Equals(button.Tag, "official.triangle"));
        var officialBaseline = Field<Dictionary<string, ToolParameters>>(Field<ToolPresetGallery>(panel, "presets"), "officialBaselines")["official.triangle"].Clone();
        controller.Tool = EditorTool.Symbol; controller.Symbol = SymbolKind.Heart; controller.ConstrainAspect = true;
        official.RaiseEvent(new Wpf.RoutedEventArgs(Controls.Button.ClickEvent));
        check("the official triangle recipe restores its baseline shape and paint", controller.Tool == EditorTool.Symbol && controller.Symbol == SymbolKind.Triangle &&
            !controller.ConstrainAspect && controller.Style.Stroke == officialBaseline.Style.Stroke && controller.Style.Fill == officialBaseline.Style.Fill &&
            Field<Dictionary<string, Wpf.FrameworkElement>>(panel, "rows")["Symbols"].Visibility == Wpf.Visibility.Visible);
        var gallery = Field<System.Windows.Controls.Primitives.UniformGrid>(panel, "symbolGallery");
        gallery.Children.OfType<Controls.Button>().Single(button => Equals(button.Tag, SymbolKind.Star)).RaiseEvent(new Wpf.RoutedEventArgs(Controls.Button.ClickEvent));
        check("the gallery remains usable after either triangle preset", controller.Tool == EditorTool.Symbol && controller.Symbol == SymbolKind.Star);

        var oldElement = new CanvasElement { Kind = ElementKind.Triangle, Width = 120, Height = 60,
            Transform = Affine2D.Translation(30, 40), Style = new() { Fill = 0xFF336699 } };
        var oldDocument = new SceneDocument { Elements = [oldElement] };
        string originalDocument = JsonSerializer.Serialize(oldDocument);
        var oldSession = new EditorSession(oldDocument); oldSession.SetSelection([oldElement.Id]);
        var inspector = new InspectorWindow(() => oldSession, colorLibrary: new ColorLibrary(Path.Combine(root, "old-colors.json")));
        Field<Dictionary<string, Controls.TextBox>>(inspector, "fields")["Angle"].Text = "30";
        Invoke(inspector, "Apply"); var edited = oldSession.Document.Elements.Single();
        check("existing project triangles keep their original geometry and remain editable", edited.Kind == ElementKind.Triangle && edited.Width == 120 && edited.Height == 60 &&
            ElementGeometry.Outline(edited).SequenceEqual(new PointD[] { new(60, 0), new(120, 60), new(0, 60) }) &&
            Math.Abs(Math.Atan2(edited.Transform.M21, edited.Transform.M11) * 180 / Math.PI - 30) < 1e-8 && oldSession.UndoCount == 1);
        check("undo restores an old triangle without migrating the document object type", oldSession.Undo() && oldSession.Document.Elements.Single().Kind == ElementKind.Triangle &&
            JsonSerializer.Serialize(oldSession.Document) == originalDocument);
    }

    private static void Emoji(string root, Action<string, bool> check)
    {
        string path = Path.Combine(root, "emoji-presets.json"); var library = new ToolPresetLibrary(path);
        var parameters = new ToolParameters { Tool = EditorTool.Emoji, Emoji = "👩‍🎨", Style = new() { FontSize = 18, Opacity = .65 } };
        var preset = library.Save("Small emoji", parameters);
        library = new ToolPresetLibrary(path);
        var session = new EditorSession();
        var untouched = new CanvasElement { Kind = ElementKind.Rectangle, Width = 50, Height = 50, Transform = Affine2D.Translation(300, 300) };
        session.Add(untouched); string original = JsonSerializer.Serialize(session.Document.Elements.Single());
        var controller = new EditorController(session) { Tool = EditorTool.Emoji, ScreenOrigin = new(-300, 100) };
        var panel = new ToolOptionsPanel(controller, library, new ColorLibrary(Path.Combine(root, "emoji-colors.json")));
        Field<ToolPresetGallery>(panel, "presets").Apply(preset.Id);
        var numbers = Field<Dictionary<string, Controls.TextBox>>(panel, "numbers");
        check("emoji presets reload the explicit size into the active tool and parameter field", controller.Tool == EditorTool.Emoji &&
            controller.Style.FontSize == 18 && numbers["FontSize"].Text == "18" && controller.Emoji == "👩‍🎨");
        session.SetSelection([]);
        int before = session.UndoCount; Gesture(controller, new(-250, 160), new(-250, 160));
        var element = session.Document.Elements.Last();
        check("emoji insertion uses the displayed preset size for font and object bounds", element.Kind == ElementKind.Text && element.Text == "👩‍🎨" &&
            element.Style.FontFamily == "Segoe UI Emoji" && element.Style.FontSize == 18 && element.Width == 18 && element.Height == 18 &&
            element.Transform == Affine2D.Translation(41, 51) && element.Style.Opacity == .65 && session.UndoCount == before + 1);
        numbers["FontSize"].Text = "12";
        session.SetSelection([]); // This check starts a new insertion; selected-object blank clicks now deselect first.
        Gesture(controller, new(-200, 180), new(-200, 180));
        check("editing the emoji size field changes subsequent objects without resizing earlier ones", controller.Style.FontSize == 12 &&
            session.Document.Elements.Last().Style.FontSize == 12 && session.Document.Elements.Last().Width == 12 && session.Document.Elements[^2].Style.FontSize == 18);
        session.SetSelection([element.Id]);
        var inspector = new InspectorWindow(() => session, colorLibrary: new ColorLibrary(Path.Combine(root, "emoji-inspector-colors.json")));
        var fields = Field<Dictionary<string, Controls.TextBox>>(inspector, "fields");
        check("inserted emoji retains its exact frame size for later property editing", fields["W"].Text == "18" && fields["FontFamily"].Text == "Segoe UI Emoji");
        fields["W"].Text = "36"; before = session.UndoCount; Invoke(inspector, "Apply");
        element = session.Document.Elements.Single(item => item.Id == element.Id);
        check("emoji geometry can be re-edited with one undo step", element.Width == 36 && element.Height == 36 && element.Text == "👩‍🎨" &&
            element.Style.FontFamily == "Segoe UI Emoji" && session.UndoCount == before + 1);
        check("emoji property undo and redo retain the exact explicit frame sizes", session.Undo() && session.Document.Elements.Single(item => item.Id == element.Id).Width == 18 &&
            session.Redo() && session.Document.Elements.Single(item => item.Id == element.Id).Width == 36);
        check("emoji insertion and later editing do not affect unselected objects or saved tool parameters", JsonSerializer.Serialize(session.Document.Elements[0]) == original &&
            controller.Style.FontSize == 12 && library.User.Single().Parameters.Style.FontSize == 18);
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Invoke(object target, string name, params object[] arguments) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);
    private static void Gesture(EditorController controller, PointD from, PointD to)
    {
        foreach (var (kind, point) in new[] { (OverlayPointerKind.Down, from), (OverlayPointerKind.Move, to), (OverlayPointerKind.Up, to) })
            controller.Pointer(new(kind, new(point.X, point.Y), OverlayMouseButton.Left, OverlayModifiers.None));
    }
}
