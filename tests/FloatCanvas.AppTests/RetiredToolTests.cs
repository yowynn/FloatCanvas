using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class RetiredToolTests
{
    internal static void Run(Action<string, bool> check)
    {
        check("removed tool and effect have no runtime enum entry", !Enum.IsDefined(typeof(EditorTool), 17) && !Enum.IsDefined(typeof(ImageEffectKind), 2));
        check("remaining serialized tool identities do not shift", (int)EditorTool.CaptureRegion == 18 && (int)EditorTool.PointerPresentation == 24);
        check("unknown tools cannot become executable parameters", !new ToolParameters { Tool = (EditorTool)17 }.IsValid());
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-UnknownTools", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "presets.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { Version = 1, Presets = new[] {
                new ToolPreset { Id = "user-unknown-tool", Name = "Unknown", Parameters = new ToolParameters { Tool = (EditorTool)17 } },
                new ToolPreset { Id = "user-current-blur", Name = "Blur", Parameters = new ToolParameters { Tool = EditorTool.Blur } } } }));
            var presets = new ToolPresetLibrary(path);
            check("unknown tool recipes are skipped instead of mapped to another tool", presets.User.Count == 1 && presets.User[0].Id == "user-current-blur");
            presets.Rename("user-current-blur", "Renamed blur");
            using var saved = JsonDocument.Parse(File.ReadAllText(path));
            check("saving known recipes drops unknown tool records", saved.RootElement.GetProperty("Presets").GetArrayLength() == 1);
            string statePath = Path.Combine(folder, "state.json");
            var state = new ToolStateStore(statePath); state.Tools[EditorTool.Blur] = new() { Tool = EditorTool.Blur }; state.Save();
            var json = JsonNode.Parse(File.ReadAllText(statePath))!;
            json["Tools"]!["17"] = JsonSerializer.SerializeToNode(new ToolParameters { Tool = (EditorTool)17 });
            json["Buttons"]!["Blur"] = 17;
            json["Selections"]!["17"] = "unknown.selection";
            json["OfficialBaselines"]!["unknown.baseline"] = JsonSerializer.SerializeToNode(new ToolParameters { Tool = (EditorTool)17 });
            File.WriteAllText(statePath, json.ToJsonString());
            var restored = new ToolStateStore(statePath);
            check("unknown cached tool records are discarded", !restored.Tools.ContainsKey((EditorTool)17) && !restored.Buttons.Values.Contains((EditorTool)17) && restored.OfficialBaselines.Count == 0);
            restored.Save();
            check("saving an otherwise unchanged tool cache also removes unknown records", !File.ReadAllText(statePath).Contains("unknown.baseline"));
            restored.CanvasPresetSelection = "official.transparent"; restored.Save();
            check("shared tool state persists canvas preset selection", new ToolStateStore(statePath).CanvasPresetSelection == "official.transparent");
            check("saving tool cache does not restore ignored records", !File.ReadAllText(statePath).Contains("unknown.baseline"));
            string unrelatedPath = Path.Combine(folder, "unrelated.json");
            File.WriteAllText(unrelatedPath, "{\"Value\":\"before\",\"Tools\":{\"FutureMetadata\":\"keep\"}}");
            var compatibility = new CompatibleConfiguration(unrelatedPath); var options = new JsonSerializerOptions();
            var unrelated = compatibility.Read<UnrelatedConfiguration>(options); unrelated.Value = "after"; compatibility.Save(unrelated, options);
            check("unknown metadata in unrelated configuration is preserved", JsonNode.Parse(File.ReadAllText(unrelatedPath))!["Tools"]!["FutureMetadata"]!.GetValue<string>() == "keep");
        }
        finally { Directory.Delete(folder, true); }
    }
    private sealed class UnrelatedConfiguration { public string Value { get; set; } = ""; }

}
