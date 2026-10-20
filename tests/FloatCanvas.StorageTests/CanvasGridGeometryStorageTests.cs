using FloatCanvas.Core;
using FloatCanvas.Storage;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class CanvasGridGeometryStorageTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "grid-geometry-storage-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var viewport = new RectD(-1920, -200, 3840, 1440);
        var initial = CanvasCalibration.CreateInitial(viewport, 7, 5);
        check(CanvasCalibration.TryCalibrate(initial, [new(-100, -100), new(100, -100), new(200, 200), new(-200, 200)], viewport,
            out var calibrated, out _), "centered grid storage fixture calibrates");
        calibrated.LockedGridCellSize = 85.125;
        var scene = new SceneDocument { Viewport = viewport, Canvas = new() { Projection = calibrated,
            Grid = new() { Visible = true, FlatCellSize = 73.125, FlatOrigin = new(960, 540) }, DepthPriorityOcclusion = true },
            Elements = [new() { Kind = ElementKind.Rectangle, Width = 50, Height = 30, Transform = Affine2D.Translation(-100, 200) }] };
        var assets = new AssetLibrary(); var store = new ProjectStore();
        string path = Path.Combine(root, "centered.floatcanvas");
        string original = JsonSerializer.Serialize(scene);
        await store.SaveAsync(path, scene, assets);
        var loaded = (await store.LoadAsync(path)).Document;
        check(JsonSerializer.Serialize(loaded) == original, "project roundtrip preserves exact flat grid and dormant projection state");
        using (var archive = ZipFile.OpenRead(path))
        {
            var manifest = await ReadAsync(archive, "manifest.json"); var payload = await ReadAsync(archive, "scene.json");
            check(manifest["Version"]!.GetValue<int>() == 7 && payload["Version"]!.GetValue<int>() == 7,
                "v7 envelope protects exact flat grid semantics from published older readers");
            var grid = payload["Canvas"]!["Grid"]!.AsObject();
            check(grid.ContainsKey("FlatCellSize") && grid.ContainsKey("FlatOrigin"), "new package explicitly persists independent flat grid metadata");
        }
        var capture = scene.Clone(); capture.Kind = DocumentKind.FrozenCapture;
        capture.Canvas.Projection.Enabled = false; capture.Canvas.Grid.FlatOrigin = new(-20, 80);
        var recovery = new RecoveryStore(Path.Combine(root, "recovery"));
        await recovery.SaveAsync(new() { LiveDocument = scene, CaptureDocument = capture, CaptureOrigin = new(4, 8) }, assets);
        var restored = (await recovery.LoadAsync())!.State;
        check(JsonSerializer.Serialize(restored.LiveDocument) == original && JsonSerializer.Serialize(restored.CaptureDocument) == JsonSerializer.Serialize(capture),
            "recovery preserves enabled and disabled flat grids independently");

        foreach (int version in new[] { 1, 2, 3, 4, 5, 6 })
        {
            string legacy = Path.Combine(root, "legacy-v" + version + ".floatcanvas"); File.Copy(path, legacy);
            using (var archive = ZipFile.Open(legacy, ZipArchiveMode.Update))
                foreach (string name in new[] { "manifest.json", "scene.json" })
                {
                    var json = await ReadAsync(archive, name); json["Version"] = version;
                    if (name == "scene.json")
                    {
                        var canvas = json["Canvas"]!;
                        canvas["Grid"]!.AsObject().Remove("FlatCellSize"); canvas["Grid"]!.AsObject().Remove("FlatOrigin");
                        if (version < 6) canvas["Projection"]!.AsObject().Remove("LockedGridCellSize");
                        if (version < 5) canvas.AsObject().Remove("DepthPriorityOcclusion");
                        if (version < 3) json.Remove("Canvas");
                    }
                    await WriteAsync(archive, name, json);
                }
            byte[] before = await File.ReadAllBytesAsync(legacy);
            var migrated = (await store.LoadAsync(legacy)).Document;
            check(migrated.Version == 7 && migrated.Canvas.Grid.FlatCellSize is null && migrated.Canvas.Grid.FlatOrigin is null &&
                (await File.ReadAllBytesAsync(legacy)).AsSpan().SequenceEqual(before), $"v{version} loads through read-only flat-grid fallback without rewriting its file");
            if (version >= 3)
                check(migrated.Canvas.Projection.Matrix == calibrated.Matrix && migrated.Canvas.Projection.Origin == calibrated.Origin &&
                    migrated.Canvas.Projection.CellSize == calibrated.CellSize && migrated.Canvas.Projection.Quad.SequenceEqual(calibrated.Quad) &&
                    JsonSerializer.Serialize(migrated.Elements) == JsonSerializer.Serialize(scene.Elements), $"v{version} migration preserves the original perspective and layer geometry");
            if (version == 6)
                check(migrated.Canvas.Projection.LockedGridCellSize == calibrated.LockedGridCellSize,
                    "v6 migration retains locked spacing while using the new centered grid phase");
        }
        async Task Mutate(string name, Action<JsonObject> edit, bool accepted = false)
        {
            string edited = Path.Combine(root, name + ".floatcanvas"); File.Copy(path, edited);
            using (var archive = ZipFile.Open(edited, ZipArchiveMode.Update))
            { var json = await ReadAsync(archive, "scene.json"); edit(json); await WriteAsync(archive, "scene.json", json); }
            bool rejected = false; try { await store.LoadAsync(edited); } catch (InvalidDataException) { rejected = true; }
            check(rejected != accepted, name);
        }
        await Mutate("v7 requires flat spacing field", n => n["Canvas"]!["Grid"]!.AsObject().Remove("FlatCellSize"));
        await Mutate("v7 requires flat origin field", n => n["Canvas"]!["Grid"]!.AsObject().Remove("FlatOrigin"));
        await Mutate("null flat grid uses legacy fallback", n => { n["Canvas"]!["Grid"]!["FlatCellSize"] = null; n["Canvas"]!["Grid"]!["FlatOrigin"] = null; }, true);
        await Mutate("incomplete flat origin rejected", n => n["Canvas"]!["Grid"]!["FlatOrigin"]!.AsObject().Remove("Y"));
        foreach (double invalid in new[] { 0, -.01, .001, 1_000_001 })
            await Mutate("invalid flat spacing " + invalid, n => n["Canvas"]!["Grid"]!["FlatCellSize"] = invalid);
        byte[] goodPackage = await File.ReadAllBytesAsync(path);
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            foreach (bool invalidOrigin in new[] { false, true })
            {
                var bad = scene.Clone();
                if (invalidOrigin) bad.Canvas.Grid.FlatOrigin = new(invalid, 0); else bad.Canvas.Grid.FlatCellSize = invalid;
                bool rejected = false; try { await store.SaveAsync(path, bad, assets); } catch (InvalidDataException) { rejected = true; }
                check(rejected && (await File.ReadAllBytesAsync(path)).AsSpan().SequenceEqual(goodPackage),
                    "nonfinite flat-grid metadata is rejected before replacing an existing project");
            }
        }
    }

    private static async Task<JsonObject> ReadAsync(ZipArchive archive, string name)
    { using var reader = new StreamReader(archive.GetEntry(name)!.Open()); return JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject(); }
    private static async Task WriteAsync(ZipArchive archive, string name, JsonObject json)
    { archive.GetEntry(name)!.Delete(); using var writer = new StreamWriter(archive.CreateEntry(name).Open()); await writer.WriteAsync(json.ToJsonString()); }
}
