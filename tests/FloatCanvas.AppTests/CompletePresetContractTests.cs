using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using FloatCanvas.App;
using FloatCanvas.Core;

/// <summary>Exercises complete preset application and old-file defaulting through production entry points.</summary>
internal static class CompletePresetContractTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-complete-presets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            CrossToolNumberParameters(folder, check);
            FactoryDefinitions(folder, check);
            GalleryRestart(folder, check);
            LegacyDefaults(folder, check);
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void CrossToolNumberParameters(string folder, Action<string, bool> check)
    {
        var library = new ToolPresetLibrary(Path.Combine(folder, "cross-tool.json"));
        var editor = new EditorController(new EditorSession()) { Tool = EditorTool.Number, NumberStart = 103, NumberStep = 7 };
        editor.ApplyParameters(library.Apply("official.line"));
        check("an official line preset resets number fields left by the previous number tool", editor.Tool == EditorTool.Line &&
            editor.NumberStart == new ToolParameters().NumberStart && editor.NumberStep == new ToolParameters().NumberStep);
        var personal = new ToolParameters { Tool = EditorTool.Line, NumberStart = 24, NumberStep = 3 };
        var saved = library.Save("Complete line", personal);
        editor.Tool = EditorTool.Number; editor.NumberStart = 103; editor.NumberStep = 7;
        editor.ApplyParameters(library.Apply(saved.Id));
        check("a personal non-number preset restores its saved number fields too", Snapshot(editor.Parameters) == Snapshot(personal));
    }

    private static void FactoryDefinitions(string folder, Action<string, bool> check)
    {
        var library = new ToolPresetLibrary(Path.Combine(folder, "official.json"));
        var edited = Custom();
        string unchanged = Snapshot(edited);
        foreach (var preset in library.Official)
        {
            var applied = library.Apply(preset.Id);
            check(preset.Id + " applies its complete fixed definition without inheriting edited parameters", Snapshot(applied) == Snapshot(preset.Parameters));
            var editor = new EditorController(new EditorSession()); editor.ApplyParameters(edited); editor.ApplyParameters(applied);
            var expected = new EditorController(new EditorSession()); expected.ApplyParameters(preset.Parameters);
            check(preset.Id + " replaces the actual editor's complete working parameters", Snapshot(editor.Parameters) == Snapshot(expected.Parameters));
            applied.Style.Stroke = 0xFF000000; applied.Emoji = "🐱";
            check(preset.Id + " returns independent complete parameter objects", Snapshot(library.Apply(preset.Id)) == Snapshot(preset.Parameters));
        }
        check("official application leaves the caller's working parameters intact", Snapshot(edited) == unchanged);
        check("official highlighter explicitly uses a 24 pixel stroke", library.Apply("official.highlighter").Style.StrokeWidth == 24);
        check("official number explicitly restores red badge white label and current color roles", library.Apply("official.number") is { NumberColorRolesVersion: 1, Style: { Stroke: ColorLibrary.ForegroundRed, Fill: 0xFFFFFFFF } });
    }

    private static void GalleryRestart(string folder, Action<string, bool> check)
    {
        string statePath = Path.Combine(folder, "tool-state.json");
        var library = new ToolPresetLibrary(Path.Combine(folder, "gallery.json"));
        var state = new ToolStateStore(statePath);
        var controller = new EditorController(new EditorSession()) { Tool = EditorTool.Line };
        var gallery = new ToolPresetGallery(controller, library, state); gallery.Applied+=gallery.PrepareForShow; gallery.PrepareForShow();
        gallery.Apply("official.line"); gallery.PrepareForShow(); controller.Style.Stroke = 0xFF252525; controller.Style.StrokeWidth = 17; gallery.PrepareForShow();
        foreach (var preset in library.OfficialFor(EditorTool.Line).Skip(1))
        {
            gallery.Apply(preset.Id); gallery.PrepareForShow();
            check(preset.Id + " restores factory color and width on first click after editing another line preset", controller.Style.Stroke == ColorLibrary.ForegroundRed && controller.Style.StrokeWidth == 4);
        }
        foreach (var preset in library.OfficialFor(EditorTool.Line))
        {
            var legacy = Custom(); legacy.Tool = EditorTool.Line;
            state.OfficialBaselines[preset.Id] = legacy;
        }
        state.PreviewSources[EditorTool.Line] = Custom(); state.PreviewSources[EditorTool.Line].Tool = EditorTool.Line;
        state.Tools[EditorTool.Line] = controller.Parameters; state.Save();
        string bytes = File.ReadAllText(statePath);
        state = new ToolStateStore(statePath);
        controller = new EditorController(new EditorSession()) { Tool = EditorTool.Line };
        gallery = new ToolPresetGallery(controller, library, state);
        foreach (var preset in library.OfficialFor(EditorTool.Line))
        {
            gallery.Apply(preset.Id); gallery.PrepareForShow();
            var expected = new EditorController(new EditorSession()); expected.ApplyParameters(preset.Parameters);
            check(preset.Id + " ignores inherited official cache values after reconstruction", Snapshot(controller.Parameters) == Snapshot(expected.Parameters));
        }
        check("opening and applying official presets does not rewrite the legacy cache file", File.ReadAllText(statePath) == bytes);
        check("official selections still restore through the tool-state store", gallery.SelectedId == "official.dotted-line");
    }

    private static void LegacyDefaults(string folder, Action<string, bool> check)
    {
        string path = Path.Combine(folder, "legacy-user.json");
        var original = Custom(); original.Tool = EditorTool.Line;
        var library = new ToolPresetLibrary(path); var saved = library.Save("Older complete preset", original);
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        var parameters = json["Presets"]![0]!["Parameters"]!.AsObject();
        foreach (string property in new[] { "EffectStrength", "Emoji", "CaptureShape", "CaptureSource", "CaptureBorderWidth", "CaptureBorderColor" }) parameters.Remove(property);
        foreach (string property in new[] { "LinkColors", "StrokePattern", "Opacity", "FontFamily" }) parameters["Style"]!.AsObject().Remove(property);
        File.WriteAllText(path, json.ToJsonString()); string bytes = File.ReadAllText(path);
        library = new ToolPresetLibrary(path);
        var current = Custom(); var restored = library.Apply(saved.Id); var defaults = new ToolParameters();
        check("old personal presets fill missing tool fields with defined defaults", restored.EffectStrength == defaults.EffectStrength && restored.Emoji == defaults.Emoji &&
            restored.CaptureShape == defaults.CaptureShape && restored.CaptureSource == defaults.CaptureSource && restored.CaptureBorderWidth == defaults.CaptureBorderWidth && restored.CaptureBorderColor == defaults.CaptureBorderColor);
        check("old personal presets fill missing nested style fields with defined defaults", restored.Style.LinkColors == defaults.Style.LinkColors && restored.Style.StrokePattern == defaults.Style.StrokePattern &&
            restored.Style.Opacity == defaults.Style.Opacity && restored.Style.FontFamily == defaults.Style.FontFamily);
        check("old personal presets retain explicitly saved attributes", restored.Style.Stroke == original.Style.Stroke && restored.Style.StrokeWidth == original.Style.StrokeWidth && restored.NumberStep == original.NumberStep);
        current.Style.Stroke = 0; current.EffectStrength = 73; current.Emoji = "🐱"; current.Style.StrokePattern = StrokePattern.Dashed;
        var firstEditor = new EditorController(new EditorSession()); firstEditor.ApplyParameters(original); firstEditor.ApplyParameters(restored);
        var secondEditor = new EditorController(new EditorSession()); secondEditor.ApplyParameters(current); secondEditor.ApplyParameters(library.Apply(saved.Id));
        check("missing legacy fields replace differently configured current tools with the same values", Snapshot(firstEditor.Parameters) == Snapshot(secondEditor.Parameters));
        check("defaulting an older preset preserves its original file until an explicit edit", File.ReadAllText(path) == bytes);
        restored.Style.Stroke = 0; restored.NumberStep = 999;
        check("personal application returns an independent complete snapshot", Snapshot(library.Apply(saved.Id)) == Snapshot(library.User.Single().Parameters));
    }

    private static ToolParameters Custom() => new()
    {
        Tool = EditorTool.Rectangle, CornerRadius = 27, Symbol = SymbolKind.Heart, ConstrainAspect = true, EffectStrength = 42,
        Emoji = "🚀", DefaultWidth = 234, DefaultHeight = 123, NumberStart = 103, NumberStep = 7,
        CaptureShape = CaptureShape.PolygonLasso, CaptureSource = CaptureSource.BackgroundAndCanvas, CaptureBorderWidth = 7, CaptureBorderColor = 0x7F123456,
        Style = new() { Stroke = 0x7F112233, Fill = 0x33445566, LinkColors = true, StrokeWidth = 13, Opacity = .42, FontSize = 35, FontFamily = "Arial",
            StartArrowHead = ArrowHeadStyle.Open, EndArrowHead = ArrowHeadStyle.Diamond, ArrowHeadSize = 45, StrokePattern = StrokePattern.Dotted }
    };
    private static string Snapshot(ToolParameters parameters) => JsonSerializer.Serialize(parameters);
}
