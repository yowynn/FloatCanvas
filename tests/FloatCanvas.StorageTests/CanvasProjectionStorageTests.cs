using FloatCanvas.Core;
using FloatCanvas.Storage;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class CanvasProjectionStorageTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "canvas-storage-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var document = new SceneDocument { Viewport = new(-250, -180, 500, 500) };
        var reference = CanvasCalibration.CreateInitial(document.Viewport, 4, 5);
        check(CanvasCalibration.TryCalibrate(reference, [new(-100, -100), new(100, -100), new(200, 200), new(-200, 200)], document.Viewport,
            out var projection, out _), "storage fixture calibration valid");
        document.Canvas = new() { BackgroundColor = 0x80123456, Projection = projection, Grid = new() { Visible = true, Color = 0x80FFFFFF, Width = 2.5, Pattern = StrokePattern.Dashed } };
        var assets = new AssetLibrary(); string assetId = assets.Add([1, 2, 3, 4]);
        var image = new CanvasElement { Kind = ElementKind.Image, Width = 60, Height = 40, AssetId = assetId,
            ProjectivePlacement = projection.Matrix.Inverse().Compose(Projective2D.FromAffine(Affine2D.Translation(-30, 10))),
            Effects = [new() { Kind = ImageEffectKind.Pixelate, Bounds = new(.1, .2, .3, .4), Amount = 8 }] };
        document.Elements.Add(image); document.Background = image.Clone(true); document.Background.Locked = true;
        var store = new ProjectStore(); string path = Path.Combine(root, "canvas.floatcanvas");
        await store.SaveAsync(path, document, assets);
        var loaded = await store.LoadAsync(path);
        check(JsonSerializer.Serialize(document) == JsonSerializer.Serialize(loaded.Document), "current format roundtrip preserves canvas, matrix, chosen scale, image placement and persistent background exactly");
        check(loaded.Document.Version == SceneDocument.CurrentVersion && loaded.Document.Canvas.Projection.CellSize == projection.CellSize, "load preserves saved normalization without recalibration");
        using (var archive = ZipFile.OpenRead(path))
        {
            using var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open());
            check(JsonNode.Parse(await reader.ReadToEndAsync())!["Version"]!.GetValue<int>() == SceneDocument.CurrentVersion, "current manifest protects older readers");
        }
        async Task Mutate(string name, Action<JsonObject> edit, bool accept = false)
        {
            string edited = Path.Combine(root, name + ".floatcanvas"); File.Copy(path, edited);
            using (var archive = ZipFile.Open(edited, ZipArchiveMode.Update))
            {
                JsonObject scene;
                using (var reader = new StreamReader(archive.GetEntry("scene.json")!.Open())) scene = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
                edit(scene); archive.GetEntry("scene.json")!.Delete();
                using var writer = new StreamWriter(archive.CreateEntry("scene.json").Open()); await writer.WriteAsync(scene.ToJsonString());
            }
            bool rejected = false;
            try { await store.LoadAsync(edited); } catch (InvalidDataException) { rejected = true; }
            check(rejected != accept, name);
        }
        await Mutate("missing canvas rejected", n => n.Remove("Canvas"));
        await Mutate("missing matrix coefficient rejected", n => n["Canvas"]!["Projection"]!["Matrix"]!.AsObject().Remove("M23"));
        await Mutate("missing placement coefficient rejected", n => n["Elements"]![0]!["ProjectivePlacement"]!.AsObject().Remove("M31"));
        await Mutate("null grid rejected", n => n["Canvas"]!["Grid"] = null);
        await Mutate("zero columns rejected", n => n["Canvas"]!["Projection"]!["Columns"] = 0);
        await Mutate("zero cell size rejected", n => n["Canvas"]!["Projection"]!["CellSize"] = 0);
        await Mutate("future calibration algorithm rejected", n => n["Canvas"]!["Projection"]!["AlgorithmVersion"] = CanvasCalibration.AlgorithmVersion + 1);
        await Mutate("metadata matrix disagreement rejected", n => n["Canvas"]!["Projection"]!["Matrix"]!["M13"] = 999);
        await Mutate("invalid grid pattern rejected", n => n["Canvas"]!["Grid"]!["Pattern"] = 99);
        await Mutate("singular placement rejected", n => { var p = n["Elements"]![0]!["ProjectivePlacement"]!; p["M11"] = 0; p["M12"] = 0; p["M13"] = 0; });
        foreach (int version in new[] { 1, 2 })
        {
            string legacy = Path.Combine(root, "legacy" + version + ".floatcanvas");
            var flat = new SceneDocument { Elements = [new() { Kind = ElementKind.Rectangle, Width = 50, Height = 30 }] };
            await store.SaveAsync(legacy, flat, new());
            using (var archive = ZipFile.Open(legacy, ZipArchiveMode.Update))
                foreach (string name in new[] { "manifest.json", "scene.json" })
                {
                    JsonObject json;
                    using (var reader = new StreamReader(archive.GetEntry(name)!.Open())) json = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
                    json["Version"] = version; if (name == "scene.json") json.Remove("Canvas");
                    archive.GetEntry(name)!.Delete(); using var writer = new StreamWriter(archive.CreateEntry(name).Open()); await writer.WriteAsync(json.ToJsonString());
                }
            var migrated = await store.LoadAsync(legacy);
            check(migrated.Document.Version == SceneDocument.CurrentVersion && migrated.Document.Canvas.BackgroundColor == 0 && !migrated.Document.Canvas.Projection.Enabled &&
                !migrated.Document.Canvas.Grid.Visible && migrated.Document.Elements[0].ProjectivePlacement is null, $"v{version} transparently migrates to flat current-format defaults");
        }
        var recovery = new RecoveryStore(Path.Combine(root, "recovery"));
        await recovery.SaveAsync(new() { LiveDocument = document }, assets);
        var recovered = (await recovery.LoadAsync())!;
        check(JsonSerializer.Serialize(recovered.State.LiveDocument) == JsonSerializer.Serialize(document), "recovery roundtrip retains chosen calibration and projected background");
        var changedDisplay = document.Clone();
        changedDisplay.Viewport = new(-1000, -10000, 2000, 20000);
        changedDisplay.Canvas.Projection.Enabled = false;
        string retained = Path.Combine(root, "disabled-on-new-display.floatcanvas");
        await store.SaveAsync(retained, changedDisplay, assets);
        var retainedScene = (await store.LoadAsync(retained)).Document;
        check(!retainedScene.Canvas.Projection.Enabled && retainedScene.Canvas.Projection.Matrix == projection.Matrix,
            "disabled calibration survives a display layout that crosses its old horizon");
        retainedScene.Canvas.Projection.Enabled = true;
        SceneValidation.Validate(retainedScene);
        string enabledPath = Path.Combine(root, "enabled-with-screen-horizon.floatcanvas");
        await store.SaveAsync(enabledPath, retainedScene, assets);
        var enabledLoaded = (await store.LoadAsync(enabledPath)).Document;
        check(enabledLoaded.Canvas.Projection.Enabled && enabledLoaded.Canvas.Projection.Matrix == projection.Matrix &&
            JsonSerializer.Serialize(enabledLoaded) == JsonSerializer.Serialize(retainedScene),
            "enabled calibration with a screen horizon roundtrips without disabling or recalibrating");
        var horizonRecovery = new RecoveryStore(Path.Combine(root, "horizon-recovery"));
        await horizonRecovery.SaveAsync(new() { LiveDocument = retainedScene }, assets);
        var horizonRecovered = (await horizonRecovery.LoadAsync())!.State.LiveDocument;
        check(horizonRecovered.Canvas.Projection.Enabled && JsonSerializer.Serialize(horizonRecovered) == JsonSerializer.Serialize(retainedScene),
            "recovery preserves enabled perspective when a changed display contains the horizon");
        await CanvasPinStorageTests.RunAsync(check);
    }
}
