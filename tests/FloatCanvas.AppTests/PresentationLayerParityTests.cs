using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using L = FloatCanvas.Localization.AppText;

internal static class PresentationLayerParityTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check, string folder)
    {
        Names(check); ScopeOwnership(check, folder); LegacyDefaults(check, folder); WindowFit(check, folder);
    }

    private static void Names(Action<string, bool> check)
    {
        string language = L.Preference;
        try
        {
            foreach (string locale in new[] { "zh-CN", "en" })
            {
                L.Initialize(locale);
                var layer = new CanvasElement { Kind = ElementKind.Presentation, Width = 400, Height = 200, Presentation = new() };
                var labels = Enum.GetValues<PresentationDisplayStyle>().Select(style =>
                { layer.Presentation.DisplayStyle = style; return InspectorWindow.ElementName(layer); }).ToArray();
                check(locale + " every keyboard display mode has a distinct meaningful default layer name", labels.Distinct().Count() == 3 && labels.All(name => name != L.Get("Editor.ElementKind.Unknown")));
                layer.Name = "My keyboard";
                check(locale + " style changes never replace a custom layer name", InspectorWindow.ElementName(layer) == "My keyboard");
                var source = new CanvasElement { Kind = ElementKind.Image, WindowSource = new() { BindingIntent = Guid.NewGuid() } };
                check(locale + " a legacy unnamed live window source has a source-specific default name", InspectorWindow.ElementName(source) == L.Get("WindowSource.DefaultName"));
                check(locale + " all authored element kinds resolve a concrete fallback", Enum.GetValues<ElementKind>().All(kind => InspectorWindow.ElementName(new() { Kind = kind }) != L.Get("Editor.ElementKind.Unknown")));
            }
        }
        finally { L.Initialize(language); }
    }

    private static void ScopeOwnership(Action<string, bool> check, string folder)
    {
        var first = Layer(PresentationKeyboardScope.AllApplications); var second = Layer(PresentationKeyboardScope.FloatCanvasOnly);
        var session = new EditorSession(new() { Elements = [first, second], Viewport = new(0, 0, 1200, 900) });
        var controller = new EditorController(session) { Tool = EditorTool.Presentation };
        var panel = new ToolOptionsPanel(controller, new ToolPresetLibrary(Path.Combine(folder, "scope-recipes.json")), new ColorLibrary(Path.Combine(folder, "scope-colors.json")));
        var global = new PresentationGlobalSettings(); string preferences = JsonSerializer.Serialize(global);
        panel.SetPresentationGlobalUi(() => global.Clone()); panel.PrepareForShow();
        var toolSettings = Field<PresentationSettingsPanel>(panel, "presentationSettings");
        Control<SegmentedChoice>(toolSettings, "KeyboardScope").SelectedValue = PresentationKeyboardScope.OutsideFloatCanvas;
        check("tool scope edits change only the next-layer authored settings", controller.Parameters.Presentation!.KeyboardScope == PresentationKeyboardScope.OutsideFloatCanvas && first.Presentation!.KeyboardScope == PresentationKeyboardScope.AllApplications && second.Presentation!.KeyboardScope == PresentationKeyboardScope.FloatCanvasOnly && JsonSerializer.Serialize(global) == preferences && session.UndoCount == 0);
        controller.Pointer(new(FloatCanvas.Windows.OverlayPointerKind.Down, new(700, 350), FloatCanvas.Windows.OverlayMouseButton.Left, FloatCanvas.Windows.OverlayModifiers.Control));
        controller.Pointer(new(FloatCanvas.Windows.OverlayPointerKind.Up, new(700, 350), FloatCanvas.Windows.OverlayMouseButton.Left, FloatCanvas.Windows.OverlayModifiers.Control));
        check("creation copies the same authored keyboard scope shown in tool settings", session.Document.Elements.Last().Presentation!.KeyboardScope == PresentationKeyboardScope.OutsideFloatCanvas);
        session.SetSelection([first.Id]);
        var inspector = new InspectorWindow(() => session, colorLibrary: new ColorLibrary(Path.Combine(folder, "scope-inspector-colors.json")));
        try
        {
            inspector.PrepareForShow(); var settings = Field<PresentationSettingsPanel>(inspector, "presentationSettings");
            int undo = session.UndoCount; string sibling = JsonSerializer.Serialize(second); var original = first.Presentation!.Clone();
            Control<SegmentedChoice>(settings, "KeyboardScope").SelectedValue = PresentationKeyboardScope.OutsideFloatCanvas;
            inspector.CompletePendingEdit();
            check("an existing layer exposes the same input scope without changing sibling layers or personal preferences", session.Document.Elements[0].Presentation!.KeyboardScope == PresentationKeyboardScope.OutsideFloatCanvas && JsonSerializer.Serialize(second) == sibling && JsonSerializer.Serialize(global) == preferences && session.UndoCount == undo + 1);
            check("scope editing retains all unrelated authored keyboard fields", first.Presentation.DisplayStyle == original.DisplayStyle && first.Presentation.KeyHoldMilliseconds == original.KeyHoldMilliseconds && first.Style.FontFamily == "Segoe UI" && first.Width == 400);
            check("existing-layer scope changes support normal undo and redo", session.Undo() && session.Document.Elements[0].Presentation!.KeyboardScope == PresentationKeyboardScope.AllApplications && session.Redo() && session.Document.Elements[0].Presentation!.KeyboardScope == PresentationKeyboardScope.OutsideFloatCanvas);
            string path = Path.Combine(folder, "layer-scope.floatcanvas");
            Task.Run(() => new ProjectStore().SaveAsync(path, session.Document, session.Assets)).GetAwaiter().GetResult();
            var reopened = Task.Run(() => new ProjectStore().LoadAsync(path)).GetAwaiter().GetResult();
            check("authored input scopes survive save and reopen independently for every layer", reopened.Document.Elements.Select(element => element.Presentation!.KeyboardScope).SequenceEqual(new[] { PresentationKeyboardScope.OutsideFloatCanvas, PresentationKeyboardScope.FloatCanvasOnly, PresentationKeyboardScope.OutsideFloatCanvas }));
        }
        finally { inspector.Close(); }
    }

    private static void LegacyDefaults(Action<string, bool> check, string folder)
    {
        var firstUse = new ToolStateStore(Path.Combine(folder, "legacy-scope-first-use.json"), PresentationKeyboardScope.OutsideFloatCanvas);
        var expectedDefault = ToolPresetLibrary.OfficialDefault(EditorTool.Presentation); expectedDefault.Presentation!.KeyboardScope = PresentationKeyboardScope.OutsideFloatCanvas;
        check("scope migration before first keyboard use preserves the official appearance and default region size", JsonSerializer.Serialize(firstUse.Tools[EditorTool.Presentation]) == JsonSerializer.Serialize(expectedDefault));
        string path = Path.Combine(folder, "legacy-scope-state.json"); var store = new ToolStateStore(path);
        store.Tools[EditorTool.Presentation] = new() { Tool = EditorTool.Presentation, Presentation = new() { KeyHoldMilliseconds = 1500 } }; store.Save();
        var raw = JsonNode.Parse(File.ReadAllText(path))!; raw["Tools"]!["Presentation"]!["Presentation"]!.AsObject().Remove("KeyboardScope");
        raw["Tools"]!["Presentation"]!["Presentation"]!["FuturePreference"] = 73; File.WriteAllText(path, raw.ToJsonString());
        string original = File.ReadAllText(path);
        store = new(path, PresentationKeyboardScope.OutsideFloatCanvas);
        check("a legacy personal scope migrates only to the next keyboard tool default while loading stays read-only", store.LegacyKeyboardScopeMigrated && store.Tools[EditorTool.Presentation].Presentation is { KeyboardScope: PresentationKeyboardScope.OutsideFloatCanvas, KeyHoldMilliseconds: 1500 } && File.ReadAllText(path) == original);
        store.Save(); raw = JsonNode.Parse(File.ReadAllText(path))!;
        check("saving a migrated scope preserves unknown sibling preferences", raw["Tools"]!["Presentation"]!["Presentation"]!["KeyboardScope"]!.GetValue<int>() == 1 && raw["Tools"]!["Presentation"]!["Presentation"]!["FuturePreference"]!.GetValue<int>() == 73);
        store = new(path, PresentationKeyboardScope.FloatCanvasOnly);
        check("the old profile cannot override an explicitly authored new tool scope on later launches", !store.LegacyKeyboardScopeMigrated && store.Tools[EditorTool.Presentation].Presentation!.KeyboardScope == PresentationKeyboardScope.OutsideFloatCanvas);
        store.Tools[EditorTool.Presentation].Presentation!.KeyboardScope = PresentationKeyboardScope.AllApplications; store.Save();
        check("an explicit all-applications scope also wins over the legacy personal preference", new ToolStateStore(path, PresentationKeyboardScope.FloatCanvasOnly).Tools[EditorTool.Presentation].Presentation!.KeyboardScope == PresentationKeyboardScope.AllApplications);
        check("a legacy layer never inherits a machine's personal scope", JsonSerializer.Deserialize<PresentationSettings>("{}")!.KeyboardScope == PresentationKeyboardScope.AllApplications);
    }

    private static void WindowFit(Action<string, bool> check, string folder)
    {
        var source = new CanvasElement { Kind = ElementKind.Image, Width = 320, Height = 180, WindowSource = new() { BindingIntent = Guid.NewGuid(), TitleHint = "Source", Fit = WindowSourceFit.Contain } };
        var session = new EditorSession(new() { Elements = [source] }); session.SetSelection([source.Id]);
        var inspector = new InspectorWindow(() => session, colorLibrary: new ColorLibrary(Path.Combine(folder, "fit-colors.json")));
        try
        {
            inspector.PrepareForShow(); var control = Field<SegmentedChoice>(inspector, "windowSourceFit"); Guid binding = source.WindowSource.BindingIntent;
            check("existing window source layers expose their stored fit mode", Equals(control.SelectedValue, WindowSourceFit.Contain) && Field<Dictionary<string, FrameworkElement>>(inspector, "fieldContainers")["WindowSourceFit"].Visibility == Visibility.Visible);
            control.SelectedValue = WindowSourceFit.Stretch; inspector.CompletePendingEdit();
            check("changing window source fit preserves binding crop and geometry in one undo step", source.WindowSource is { Fit: WindowSourceFit.Stretch } && source.WindowSource.BindingIntent == binding && source.Width == 320 && session.UndoCount == 1);
            check("window source fit changes undo and redo", session.Undo() && session.Document.Elements[0].WindowSource!.Fit == WindowSourceFit.Contain && session.Redo() && session.Document.Elements[0].WindowSource!.Fit == WindowSourceFit.Stretch);
        }
        finally { inspector.Close(); }
    }

    private static CanvasElement Layer(PresentationKeyboardScope scope) => new() { Kind = ElementKind.Presentation, Width = 400, Height = 200,
        Presentation = new() { DisplayStyle = PresentationDisplayStyle.Keyboard, KeyboardScope = scope, KeyHoldMilliseconds = 1300 }, Style = new() { FontFamily = "Segoe UI" } };
    private static T Field<T>(object target, string field) => (T)target.GetType().GetField(field, Hidden)!.GetValue(target)!;
    private static T Control<T>(PresentationSettingsPanel panel, string key) where T : FrameworkElement
    {
        var entries = Field<IDictionary>(panel, "entries"); var entry = entries[key]!;
        return (T)entry.GetType().GetProperty("Control")!.GetValue(entry)!;
    }
}
