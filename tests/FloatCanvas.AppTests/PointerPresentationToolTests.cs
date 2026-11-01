using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class PointerPresentationToolTests
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    internal static void Run(Action<string, bool> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-pointer-tool", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try { PointerPresentationBehaviorTests.Run(check); Parameters(check); PassiveController(check); Presets(folder, check); WorkingState(folder, check); ProfileMigration(folder, check); }
        finally { Directory.Delete(folder, recursive: true); }
    }
    private static void Parameters(Action<string, bool> check)
    {
        check("mouse presentation appends its enum without reinterpreting keyboard tool caches", (int)EditorTool.Presentation == 23 && (int)EditorTool.PointerPresentation == 24);
        var value = new ToolParameters { Tool = EditorTool.PointerPresentation };
        check("mouse tool has a valid independent effect configuration", value.IsValid() && value.PointerPresentation is { ColorAlphaVersion: 1 } && value.Presentation is null && ToolPresetLibrary.Family(value.Tool) == value.Tool);
        value.PointerPresentation!.Enabled = true; value.PointerPresentation.RightClick.Color = 0x80446688;
        var copy = value.Clone(); copy.PointerPresentation!.RightClick.Color = 0; copy.PointerPresentation.Enabled = false;
        check("mouse parameter snapshots own nested styles and the working master bit", value.PointerPresentation.RightClick.Color == 0x80446688 && value.PointerPresentation.Enabled);
        value.PointerPresentation = null; value.NormalizeAppearance();
        check("missing mouse tool configuration normalizes to stable defaults", value.PointerPresentation is not null && value.IsValid());
        value.PointerPresentation!.TrailWidthPixels = double.NaN; value.NormalizeAppearance();
        check("mouse parameters reject malformed effect geometry after normalization", !value.IsValid());
        check("mouse tool glyph differs from the quick master switch artwork", CommandGlyphs.Tool(EditorTool.PointerPresentation) == CommandGlyphs.PresentationPointerSettings && CommandGlyphs.Tool(EditorTool.PointerPresentation) != CommandGlyphs.PresentationPointer);
    }
    private static void PassiveController(Action<string, bool> check)
    {
        var element = new CanvasElement { Kind = ElementKind.Rectangle, Width = 100, Height = 80 };
        var session = new EditorSession(new SceneDocument { Elements = [element] }); session.SetSelection([element.Id]);
        var editor = new EditorController(session) { BackgroundPickingEnabled = true };
        string before = JsonSerializer.Serialize(session.Document); int undo = session.UndoCount;
        editor.ApplyParameters(new() { Tool = EditorTool.PointerPresentation, PointerPresentation = new() { CursorColor = 0x7F112233 } });
        foreach (var modifiers in new[] { OverlayModifiers.None, OverlayModifiers.Control, OverlayModifiers.Shift, OverlayModifiers.Alt })
            foreach (var button in new[] { OverlayMouseButton.Left, OverlayMouseButton.Right, OverlayMouseButton.Middle })
                foreach (var kind in new[] { OverlayPointerKind.Down, OverlayPointerKind.Move, OverlayPointerKind.Up, OverlayPointerKind.DoubleClick })
                    editor.Pointer(new(kind, new(50, 40), button, modifiers));
        check("mouse tool pointer gestures never create edit move select or commit document content", JsonSerializer.Serialize(session.Document) == before && session.UndoCount == undo && !session.IsTransactionActive && session.Selection.SetEquals([element.Id]));
        check("right-click and temporary-move modifiers keep the passive mouse tool active", editor.Tool == EditorTool.PointerPresentation);
        var snapshot = editor.Parameters; snapshot.PointerPresentation!.CursorColor = 0;
        check("controller exports an independent mouse configuration snapshot", editor.PointerPresentation!.CursorColor == 0x7F112233);
    }
    private static void Presets(string folder, Action<string, bool> check)
    {
        string path = Path.Combine(folder, "presets.json"); var library = new ToolPresetLibrary(path);
        var official = library.OfficialFor(EditorTool.PointerPresentation);
        check("official mouse presets are independent halo click and trail recipes", official.Select(p => p.Id).SequenceEqual(new[] { "official.presentation-pointer-halo", "official.presentation-pointer-clicks", "official.presentation-pointer-trail" }) && official.All(p => p.Parameters.IsValid() && p.Parameters.PointerPresentation is { Enabled: false, ColorAlphaVersion: 1 }));
        check("mouse recipes enable their named effects with distinct complete configurations", official[0].Parameters.PointerPresentation is { CursorEnabled: true, ClicksEnabled: false, TrailEnabled: false } && official[1].Parameters.PointerPresentation is { CursorEnabled: false, ClicksEnabled: true, TrailEnabled: false } && official[2].Parameters.PointerPresentation is { CursorEnabled: true, ClicksEnabled: false, TrailEnabled: true });
        var applied = library.Apply(official[1].Id); applied.PointerPresentation!.LeftClick.Color = 0;
        check("applying or editing one recipe cannot rewrite an official mouse definition", library.Apply(official[1].Id).PointerPresentation!.LeftClick.Color != 0);
        var source = new ToolParameters { Tool = EditorTool.PointerPresentation, PointerPresentation = new() { Enabled = true, CursorColor = 0x77112233, TrailEnabled = true, LeftClick = new() { Color = 0x55445566, Shape = PresentationClickShape.Disc } } };
        var saved = library.Save("Pointer custom", source);
        check("saving an effect recipe excludes the live master without changing its caller", !saved.Parameters.PointerPresentation!.Enabled && source.PointerPresentation.Enabled);
        var restored = new ToolPresetLibrary(path).Apply(saved.Id);
        check("personal mouse recipes retain colors alpha geometry and nested click styles across restart", restored.Tool == EditorTool.PointerPresentation && restored.PointerPresentation is { CursorColor: 0x77112233, TrailEnabled: true, LeftClick.Color: 0x55445566, LeftClick.Shape: PresentationClickShape.Disc, Enabled: false });
        source.PointerPresentation.RightClick.DurationMilliseconds = 1000; library.Update(saved.Id, source);
        check("updating a mouse recipe also leaves the global master out of the saved effect", !new ToolPresetLibrary(path).Apply(saved.Id).PointerPresentation!.Enabled && source.PointerPresentation.Enabled);
    }
    private static void WorkingState(string folder, Action<string, bool> check)
    {
        string path = Path.Combine(folder, "working.json"); var store = new ToolStateStore(path);
        store.Tools[EditorTool.PointerPresentation] = new() { Tool = EditorTool.PointerPresentation, PointerPresentation = new() { CursorColor = 0x66112233, TrailWidthPixels = 9 } };
        store.Buttons[EditorTool.Blur] = EditorTool.PointerPresentation;
        store.Selections[EditorTool.PointerPresentation] = "official.presentation-pointer-halo"; store.Save();
        var restored = new ToolStateStore(path);
        check("working mouse tool has its own family and survives utility-button persistence", restored.Tools[EditorTool.PointerPresentation].PointerPresentation is { CursorColor: 0x66112233, TrailWidthPixels: 9 } && restored.Buttons[EditorTool.Blur] == EditorTool.PointerPresentation && restored.Selections[EditorTool.PointerPresentation] == "official.presentation-pointer-halo");
        var json = JsonNode.Parse(File.ReadAllText(path))!; json["Tools"]!["PointerPresentation"]!["PointerPresentation"]!["FutureEffect"] = new JsonObject { ["Keep"] = 17 }; File.WriteAllText(path, json.ToJsonString());
        restored = new ToolStateStore(path); restored.Tools[EditorTool.PointerPresentation].PointerPresentation!.LeftClick.Color = 0x44334455; restored.Save();
        check("editing known mouse working parameters preserves unknown nested configuration", JsonNode.Parse(File.ReadAllText(path))!["Tools"]!["PointerPresentation"]!["PointerPresentation"]!["FutureEffect"]!["Keep"]!.GetValue<int>() == 17);
    }
    private static void ProfileMigration(string folder, Action<string, bool> check)
    {
        string path = Path.Combine(folder, "legacy-settings.json");
        var raw = new JsonObject
        {
            ["Theme"] = "dark", ["FutureRoot"] = new JsonObject { ["Keep"] = 31 },
            ["PresentationGlobal"] = new JsonObject
            {
                ["KeyboardScope"] = 2, ["FutureGlobal"] = "retained",
                ["Pointer"] = new JsonObject
                {
                    ["Enabled"] = true, ["CursorColor"] = 0x80112233u, ["CursorOpacity"] = .5,
                    ["TrailColor"] = 0x80445566u, ["TrailOpacity"] = .25, ["CursorRadiusPixels"] = 29,
                    ["FuturePointer"] = new JsonObject { ["Keep"] = 41 },
                    ["LeftClick"] = new JsonObject { ["Color"] = 0x7F778899u, ["Opacity"] = .5, ["FutureClick"] = 51 }
                }
            }
        };
        File.WriteAllText(path, raw.ToJsonString()); var compatible = new CompatibleConfiguration(path); var settings = compatible.Read<UserSettings>(Options);
        check("legacy profile effect opacity migrates into each color alpha once", settings.PresentationGlobal.Pointer is { CursorColor: 0x40112233, TrailColor: 0x20445566, LeftClick.Color: 0x40778899, CursorOpacity: 1, TrailOpacity: 1, LeftClick.Opacity: 1, ColorAlphaVersion: 1 });
        check("legacy alpha migration preserves the user's master scope radius and unrelated settings", settings.PresentationGlobal.Pointer.Enabled && settings.PresentationGlobal.KeyboardScope == PresentationKeyboardScope.FloatCanvasOnly && settings.PresentationGlobal.Pointer.CursorRadiusPixels == 29 && settings.Theme == "dark");
        settings.Theme = "light"; compatible.Save(settings, Options);
        var rewritten = JsonNode.Parse(File.ReadAllText(path))!;
        check("saving an unrelated preference writes canonical alpha and the migration marker", rewritten["PresentationGlobal"]!["Pointer"]!["ColorAlphaVersion"]!.GetValue<int>() == 1 && rewritten["PresentationGlobal"]!["Pointer"]!["CursorOpacity"]!.GetValue<double>() == 1 && rewritten["PresentationGlobal"]!["Pointer"]!["CursorColor"]!.GetValue<uint>() == 0x40112233);
        check("alpha migration preserves unknown profile root global pointer and button fields", rewritten["FutureRoot"]!["Keep"]!.GetValue<int>() == 31 && rewritten["PresentationGlobal"]!["FutureGlobal"]!.GetValue<string>() == "retained" && rewritten["PresentationGlobal"]!["Pointer"]!["FuturePointer"]!["Keep"]!.GetValue<int>() == 41 && rewritten["PresentationGlobal"]!["Pointer"]!["LeftClick"]!["FutureClick"]!.GetValue<int>() == 51);
        for (int iteration = 0; iteration < 3; iteration++) { compatible = new(path); settings = compatible.Read<UserSettings>(Options); compatible.Save(settings, Options); }
        check("profile reloads and repeated saves never darken migrated colors again", settings.PresentationGlobal.Pointer.CursorColor == 0x40112233 && settings.PresentationGlobal.Pointer.LeftClick.Color == 0x40778899 && settings.PresentationGlobal.Pointer.Enabled);
        string minimal = Path.Combine(folder, "minimal-settings.json"); File.WriteAllText(minimal, "{\"PresentationGlobal\":{\"Pointer\":{\"Enabled\":true}}}");
        var minimalSettings = new CompatibleConfiguration(minimal).Read<UserSettings>(Options);
        check("omitted old opacity and color fields migrate from the original visible defaults", minimalSettings.PresentationGlobal.Pointer.CursorColor == 0x3DFFD54F && minimalSettings.PresentationGlobal.Pointer.TrailColor == 0xE6FFD54F && minimalSettings.PresentationGlobal.Pointer.Enabled);
        string current = Path.Combine(folder, "current-settings.json"); File.WriteAllText(current, "{\"PresentationGlobal\":{\"Pointer\":{\"ColorAlphaVersion\":1,\"CursorColor\":2148606515,\"TrailColor\":1078216294}}}");
        var currentSettings = new CompatibleConfiguration(current).Read<UserSettings>(Options);
        check("new alpha-only profile colors are not interpreted using legacy implicit opacity", currentSettings.PresentationGlobal.Pointer.CursorColor == 2148606515u && currentSettings.PresentationGlobal.Pointer.TrailColor == 1078216294u);
    }
}
