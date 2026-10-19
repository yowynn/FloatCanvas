using FloatCanvas.Core;
using FloatCanvas.Storage;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class CanvasGridLockStorageTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "grid-lock-storage-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var scene = new SceneDocument { Viewport = new(-250, -180, 500, 500) };
        var initial = CanvasCalibration.CreateInitial(scene.Viewport, 4, 5);
        initial.LockedGridCellSize = initial.CellSize;
        check(CanvasCalibration.TryCalibrate(initial, [new(-100, -100), new(100, -100), new(210, 200), new(-200, 200)], scene.Viewport,
            out var calibrated, out _), "locked storage fixture calibrates");
        scene.Canvas.Projection = calibrated; scene.Canvas.DepthPriorityOcclusion = true;
        scene.Elements.Add(new() { Kind = ElementKind.Rectangle, Width = 60, Height = 80, IsBillboard = true,
            BillboardAnchor = calibrated.Origin + new PointD(2 * calibrated.GridCellSize, 3 * calibrated.GridCellSize), BillboardHeight = 40 });
        var store = new ProjectStore(); var assets = new AssetLibrary(); string path = Path.Combine(root, "locked.floatcanvas");
        string original = JsonSerializer.Serialize(scene);
        await store.SaveAsync(path, scene, assets);
        var loaded = (await store.LoadAsync(path)).Document;
        check(JsonSerializer.Serialize(loaded) == original && loaded.Canvas.Projection.GridColumns == calibrated.GridColumns &&
            loaded.Canvas.Projection.GridRows == calibrated.GridRows, "project roundtrip preserves lock, fractional coverage, depth setting and billboard exactly");
        using (var archive = ZipFile.OpenRead(path))
        {
            var manifest = await ReadAsync(archive, "manifest.json"); var payload = await ReadAsync(archive, "scene.json");
            check(manifest["Version"]!.GetValue<int>() == 6 && payload["Version"]!.GetValue<int>() == 6,
                "v6 package version prevents published v5 readers from silently losing locked grid semantics");
            check(payload["Canvas"]!["Projection"]!.AsObject().ContainsKey("LockedGridCellSize") &&
                !payload["Canvas"]!["Projection"]!.AsObject().ContainsKey("GridColumns"), "package persists the lock rather than rounded display counts");
        }
        var capture = scene.Clone(); capture.Kind = DocumentKind.FrozenCapture; capture.Canvas.Projection.Enabled = false;
        var recovery = new RecoveryStore(Path.Combine(root, "recovery"));
        await recovery.SaveAsync(new() { LiveDocument = scene, CaptureDocument = capture, CaptureOrigin = new(4, 8) }, assets);
        var restored = (await recovery.LoadAsync())!.State;
        check(JsonSerializer.Serialize(restored.LiveDocument) == original &&
            JsonSerializer.Serialize(restored.CaptureDocument) == JsonSerializer.Serialize(capture), "recovery preserves enabled and disabled projection locks independently");
        var disabled = capture.Clone(); disabled.Kind = DocumentKind.LiveOverlay;
        string disabledPath = Path.Combine(root, "disabled.floatcanvas");
        await store.SaveAsync(disabledPath, disabled, assets);
        check((await store.LoadAsync(disabledPath)).Document.Canvas.Projection.LockedGridCellSize == initial.CellSize,
            "project saved with perspective disabled retains its grid lock");

        foreach (int version in new[] { 1, 2, 3, 4, 5 })
        {
            var legacyScene = new SceneDocument { Canvas = new() { DepthPriorityOcclusion = version == 5 } };
            string legacy = Path.Combine(root, "legacy-v" + version + ".floatcanvas"); await store.SaveAsync(legacy, legacyScene, assets);
            using (var archive = ZipFile.Open(legacy, ZipArchiveMode.Update))
                foreach (string name in new[] { "manifest.json", "scene.json" })
                {
                    var json = await ReadAsync(archive, name); json["Version"] = version;
                    if (name == "scene.json")
                    {
                        json["Canvas"]!["Projection"]!.AsObject().Remove("LockedGridCellSize");
                        if (version < 5) json["Canvas"]!.AsObject().Remove("DepthPriorityOcclusion");
                        if (version < 3) json.Remove("Canvas");
                    }
                    await WriteAsync(archive, name, json);
                }
            var migrated = (await store.LoadAsync(legacy)).Document;
            check(migrated.Version == SceneDocument.CurrentVersion && migrated.Canvas.Projection.LockedGridCellSize is null &&
                migrated.Canvas.DepthPriorityOcclusion == (version == 5), $"v{version} loads unlocked and retains its prior depth setting");
        }
        async Task Mutate(string name, Action<JsonObject> edit, bool accepted = false)
        {
            string edited = Path.Combine(root, name + ".floatcanvas"); File.Copy(path, edited);
            using (var archive = ZipFile.Open(edited, ZipArchiveMode.Update))
            { var json = await ReadAsync(archive, "scene.json"); edit(json); await WriteAsync(archive, "scene.json", json); }
            bool rejected = false; try { await store.LoadAsync(edited); } catch (InvalidDataException) { rejected = true; }
            check(rejected != accepted, name);
        }
        await Mutate("v6 requires explicit lock state", n => n["Canvas"]!["Projection"]!.AsObject().Remove("LockedGridCellSize"));
        await Mutate("null lock retains unlocked behavior", n => n["Canvas"]!["Projection"]!["LockedGridCellSize"] = null, true);
        await Mutate("zero locked spacing rejected", n => n["Canvas"]!["Projection"]!["LockedGridCellSize"] = 0);
        await Mutate("negative locked spacing rejected", n => n["Canvas"]!["Projection"]!["LockedGridCellSize"] = -10);
        await Mutate("undersized locked spacing rejected", n => n["Canvas"]!["Projection"]!["LockedGridCellSize"] = .001);
        await Mutate("oversized locked spacing rejected", n => n["Canvas"]!["Projection"]!["LockedGridCellSize"] = 1_000_001);
        await Mutate("nonnumeric locked spacing rejected", n => n["Canvas"]!["Projection"]!["LockedGridCellSize"] = "NaN");
        await Mutate("old payload cannot hide in v6 envelope", n => n["Version"] = 5);
        byte[] goodPackage = await File.ReadAllBytesAsync(path);
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            var invalidScene = scene.Clone(); invalidScene.Canvas.Projection.LockedGridCellSize = invalid;
            bool rejected = false; try { await store.SaveAsync(path, invalidScene, assets); } catch (InvalidDataException) { rejected = true; }
            check(rejected && (await File.ReadAllBytesAsync(path)).AsSpan().SequenceEqual(goodPackage), "nonfinite lock rejected before replacing a good project");
        }
    }

    private static async Task<JsonObject> ReadAsync(ZipArchive archive, string name)
    { using var reader = new StreamReader(archive.GetEntry(name)!.Open()); return JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject(); }
    private static async Task WriteAsync(ZipArchive archive, string name, JsonObject json)
    { archive.GetEntry(name)!.Delete(); using var writer = new StreamWriter(archive.CreateEntry(name).Open()); await writer.WriteAsync(json.ToJsonString()); }
}
