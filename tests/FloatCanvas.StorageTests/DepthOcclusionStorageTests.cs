using FloatCanvas.Core;
using FloatCanvas.Storage;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class DepthOcclusionStorageTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "depth-occlusion-storage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var scene = new SceneDocument { Viewport = new(0, 0, 300, 200), Canvas = new() { DepthPriorityOcclusion = true,
            Projection = new() { Enabled = true, Columns = 10, Rows = 10, CellSize = 1,
                Quad = [new(0, 0), new(10, 0), new(10, 10), new(0, 10)] } },
            Elements = [new() { Kind = ElementKind.Rectangle, Width = 30, Height = 40, IsBillboard = true,
                BillboardAnchor = new(120, 100), BillboardHeight = -12, Style = new() { StrokeWidth = 5 } }] };
        var assets = new AssetLibrary(); var store = new ProjectStore();
        string path = Path.Combine(root, "depth.floatcanvas"); string original = JsonSerializer.Serialize(scene);
        await store.SaveAsync(path, scene, assets);
        var loaded = (await store.LoadAsync(path)).Document;
        check(loaded.Canvas.DepthPriorityOcclusion && JsonSerializer.Serialize(loaded) == original,
            "current project roundtrip preserves depth occlusion together with billboard geometry");
        using (var archive = ZipFile.OpenRead(path))
        {
            var manifest = await ReadAsync(archive, "manifest.json"); var payload = await ReadAsync(archive, "scene.json");
            check(manifest["Version"]!.GetValue<int>() == SceneDocument.CurrentVersion && payload["Version"]!.GetValue<int>() == SceneDocument.CurrentVersion,
                "depth-aware packages use the current format so published v4 readers reject rather than misrender");
        }
        var disabled = scene.Clone(); disabled.Canvas.Projection.Enabled = false;
        string disabledPath = Path.Combine(root, "disabled.floatcanvas");
        await store.SaveAsync(disabledPath, disabled, assets);
        var restored = (await store.LoadAsync(disabledPath)).Document;
        check(restored.Canvas.DepthPriorityOcclusion && !restored.Canvas.Projection.Enabled,
            "saving while perspective is disabled retains the depth option for later restoration");
        restored.Canvas.Projection.Enabled = true; SceneValidation.Validate(restored);
        check(restored.Canvas.DepthPriorityOcclusion, "restored perspective retains the depth choice without resetting geometry");
        var recovery = new RecoveryStore(Path.Combine(root, "recovery"));
        var capture = disabled.Clone(); capture.Kind = DocumentKind.FrozenCapture;
        await recovery.SaveAsync(new() { LiveDocument = scene, CaptureDocument = capture, CaptureOrigin = new(15, 20) }, assets);
        var workspace = (await recovery.LoadAsync())!.State;
        check(workspace.LiveDocument.Canvas.DepthPriorityOcclusion && workspace.CaptureDocument!.Canvas.DepthPriorityOcclusion &&
            !workspace.CaptureDocument.Canvas.Projection.Enabled && workspace.CaptureOrigin == new PointD(15, 20),
            "recovery preserves independent active and disabled depth choices for both workspace documents");
        foreach (int version in new[] { 1, 2, 3, 4 })
        {
            string legacy = Path.Combine(root, "legacy-v" + version + ".floatcanvas"); File.Copy(path, legacy);
            using (var archive = ZipFile.Open(legacy, ZipArchiveMode.Update))
                foreach (string name in new[] { "manifest.json", "scene.json" })
                {
                    var json = await ReadAsync(archive, name); json["Version"] = version;
                    if (name == "scene.json")
                    {
                        json["Canvas"]!.AsObject().Remove("DepthPriorityOcclusion");
                        if (version < 3) json.Remove("Canvas");
                        if (version < 4)
                            foreach (var element in json["Elements"]!.AsArray())
                            { element!.AsObject().Remove("IsBillboard"); element.AsObject().Remove("BillboardAnchor"); element.AsObject().Remove("BillboardHeight"); }
                    }
                    await WriteAsync(archive, name, json);
                }
            var migrated = (await store.LoadAsync(legacy)).Document;
            check(migrated.Version == SceneDocument.CurrentVersion && !migrated.Canvas.DepthPriorityOcclusion && migrated.Elements[0].IsBillboard == (version == 4) &&
                migrated.Elements[0].Width == scene.Elements[0].Width && migrated.Elements[0].Height == scene.Elements[0].Height,
                $"published v{version} loads with depth ordering off and its original placement semantics");
        }
        async Task Reject(string name, Action<JsonObject> edit)
        {
            string invalid = Path.Combine(root, name + ".floatcanvas"); File.Copy(path, invalid);
            using (var archive = ZipFile.Open(invalid, ZipArchiveMode.Update))
            { var payload = await ReadAsync(archive, "scene.json"); edit(payload); await WriteAsync(archive, "scene.json", payload); }
            bool rejected = false; try { await store.LoadAsync(invalid); } catch (InvalidDataException) { rejected = true; }
            check(rejected, name);
        }
        await Reject("current format requires explicit depth option", n => n["Canvas"]!.AsObject().Remove("DepthPriorityOcclusion"));
        await Reject("depth option rejects null", n => n["Canvas"]!["DepthPriorityOcclusion"] = null);
        await Reject("depth option rejects nonboolean data", n => n["Canvas"]!["DepthPriorityOcclusion"] = "true");
        await Reject("v4 payload cannot hide in current envelope", n => n["Version"] = 4);
    }

    private static async Task<JsonObject> ReadAsync(ZipArchive archive, string name)
    { using var reader = new StreamReader(archive.GetEntry(name)!.Open()); return JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject(); }
    private static async Task WriteAsync(ZipArchive archive, string name, JsonObject json)
    { archive.GetEntry(name)!.Delete(); using var writer = new StreamWriter(archive.CreateEntry(name).Open()); await writer.WriteAsync(json.ToJsonString()); }
}
