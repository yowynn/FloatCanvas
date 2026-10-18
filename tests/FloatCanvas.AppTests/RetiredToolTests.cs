using System.IO;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class RetiredToolTests
{
    internal static void Run(Action<string, bool> check)
    {
        var session = new EditorSession();
        var controller = new EditorController(session);
        controller.Tool = EditorTool.Redact;
        check("legacy solid tool cannot be activated through direct tool selection", controller.Tool == EditorTool.Blur);
        controller.ApplyParameters(new ToolParameters { Tool = EditorTool.Redact });
        check("legacy solid preset parameters cannot restore the retired authoring tool", controller.Tool == EditorTool.Blur);
        ImageEffectKind? requested = null;
        controller.DesktopEffectRequested += (_, kind, _, _) => requested = kind;
        foreach (var kind in new[] { OverlayPointerKind.Down, OverlayPointerKind.Move, OverlayPointerKind.Up })
            controller.Pointer(new(kind, kind == OverlayPointerKind.Down ? new OverlayPoint(10, 10) : new OverlayPoint(60, 60), OverlayMouseButton.Left, OverlayModifiers.None));
        check("drawing with restored legacy tool requests current blur instead of solid cover", requested == ImageEffectKind.Blur);
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-RetiredTools", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "presets.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { Version = 1, Presets = new[] {
                new ToolPreset { Id = "user-old-redact", Name = "Old solid", Parameters = new ToolParameters { Tool = EditorTool.Redact } },
                new ToolPreset { Id = "user-current-blur", Name = "Blur", Parameters = new ToolParameters { Tool = EditorTool.Blur } } } }));
            var presets = new ToolPresetLibrary(path);
            check("retired official and user solid presets are absent from executable galleries", !presets.All.Any(preset => preset.Parameters.Tool == EditorTool.Redact) && presets.User.Single().Id == "user-current-blur");
            bool rejected = false;
            try { presets.Save("Retired authoring", new ToolParameters { Tool = EditorTool.Redact }); }
            catch (ArgumentException) { rejected = true; }
            check("new user recipes cannot author the retired tool", rejected);
            presets.Rename("user-current-blur", "Renamed blur");
            using var saved = JsonDocument.Parse(File.ReadAllText(path));
            check("editing another preset preserves legacy user data on disk", saved.RootElement.GetProperty("Presets").GetArrayLength() == 2);
            string statePath = Path.Combine(folder, "state.json");
            var state = new ToolStateStore(statePath);
            state.Tools[EditorTool.Redact] = new ToolParameters { Tool = EditorTool.Redact };
            state.Buttons[EditorTool.Blur] = EditorTool.Redact;
            state.Selections[EditorTool.Redact] = "official.redact";
            state.OfficialBaselines["official.redact"] = new ToolParameters { Tool = EditorTool.Redact };
            state.Save();
            var restored = new ToolStateStore(statePath);
            check("legacy cached tool state is readable without restoring retired tool routes", !restored.Tools.ContainsKey(EditorTool.Redact) && !restored.Buttons.Values.Contains(EditorTool.Redact) &&
                !restored.Selections.ContainsKey(EditorTool.Redact) && !restored.OfficialBaselines.ContainsKey("official.redact"));
        }
        finally { Directory.Delete(folder, true); }
    }
}
