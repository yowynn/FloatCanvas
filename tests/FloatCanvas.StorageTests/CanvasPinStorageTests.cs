using FloatCanvas.Core;
using FloatCanvas.Storage;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class CanvasPinStorageTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-pin-storage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var scene = new SceneDocument { Viewport = new(0, 0, 1000, 800) };
        var initial = CanvasCalibration.CreateInitial(scene.Viewport, 9, 9);
        check(CanvasCalibration.TryCalibrate(initial, [new(100, 150), new(800, 100), new(900, 700), new(50, 700)], scene.Viewport, out var pin, out _), "pin storage fixture calibrates");
        scene.Canvas.Projection = pin;
        CanvasCalibration.SetEnabled(scene.Canvas, false);
        var original = JsonSerializer.Serialize(scene);
        var store = new ProjectStore(); var assets = new AssetLibrary(); var path = Path.Combine(folder, "pin.floatcanvas");
        await store.SaveAsync(path, scene, assets);
        check(JsonSerializer.Serialize((await store.LoadAsync(path)).Document) == original, "pin and flat placement roundtrip exactly");
        var lockedScene = scene.Clone(); lockedScene.Canvas.Projection.LockedGridCellSize = 73.1;
        var lockedPath = Path.Combine(folder, "locked-pin.floatcanvas");
        await store.SaveAsync(lockedPath, lockedScene, assets);
        check(JsonSerializer.Serialize((await store.LoadAsync(lockedPath)).Document) == JsonSerializer.Serialize(lockedScene),
            "v8 retains locked grid spacing with pin and ordinary placement");
        using (var zip = ZipFile.OpenRead(path))
        {
            var payload = await Read(zip, "scene.json");
            check(payload["Version"]!.GetValue<int>() == 8 && !payload["Canvas"]!["Projection"]!.AsObject().ContainsKey("LockedGridCellSize"), "v8 writes explicit pins and omits old lock");
        }
        var legacy = Path.Combine(folder, "v7.floatcanvas"); File.Copy(path, legacy);
        using (var zip = ZipFile.Open(legacy, ZipArchiveMode.Update))
            foreach (var name in new[] { "scene.json", "manifest.json" })
            {
                var json = await Read(zip, name); json["Version"] = 7;
                if (name == "scene.json")
                {
                    var projection = json["Canvas"]!["Projection"]!.AsObject();
                    projection.Remove("PinLocation"); projection.Remove("PinPlanePoint"); projection.Remove("ReferenceBounds"); projection["AlgorithmVersion"] = 1; projection["LockedGridCellSize"] = 73;
                    json["Canvas"]!["Grid"]!.AsObject().Remove("FlatTranslation");
                }
                await Write(zip, name, json);
            }
        var before = await File.ReadAllBytesAsync(legacy);
        var migrated = (await store.LoadAsync(legacy)).Document;
        check(migrated.Canvas.Projection.Matrix == pin.Matrix && migrated.Canvas.Projection.PinPoint.DistanceTo(pin.PinPoint) < 1e-6 && migrated.Canvas.Projection.LockedGridCellSize == 73,
            "v7 upgrade retains content matrix, pin and locked spacing");
        var after = await File.ReadAllBytesAsync(legacy);
        check(before.AsSpan().SequenceEqual(after), "loading legacy input never rewrites it");
        await store.SaveAsync(path, migrated, assets);
        check(JsonSerializer.Serialize((await store.LoadAsync(path)).Document) == JsonSerializer.Serialize(migrated), "migration is stable after saving and reopening");
        var recovery = new RecoveryStore(Path.Combine(folder, "recovery"));
        await recovery.SaveAsync(new() { LiveDocument = scene, CaptureOrigin = new(5, 6) }, assets);
        check(JsonSerializer.Serialize((await recovery.LoadAsync())!.State.LiveDocument) == original, "recovery retains flat placement and pin");
        foreach (string field in new[] { "PinLocation", "PinPlanePoint", "ReferenceBounds" })
        {
            var invalid = Path.Combine(folder, field + ".floatcanvas"); File.Copy(path, invalid);
            using (var zip = ZipFile.Open(invalid, ZipArchiveMode.Update))
            { var json = await Read(zip, "scene.json"); json["Canvas"]!["Projection"]!.AsObject().Remove(field); await Write(zip, "scene.json", json); }
            bool rejected = false; try { await store.LoadAsync(invalid); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "v8 requires " + field);
        }
    }
    private static async Task<JsonObject> Read(ZipArchive zip, string name)
    { using var reader = new StreamReader(zip.GetEntry(name)!.Open()); return JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject(); }
    private static async Task Write(ZipArchive zip, string name, JsonObject json)
    { zip.GetEntry(name)!.Delete(); using var writer = new StreamWriter(zip.CreateEntry(name).Open()); await writer.WriteAsync(json.ToJsonString()); }
}
