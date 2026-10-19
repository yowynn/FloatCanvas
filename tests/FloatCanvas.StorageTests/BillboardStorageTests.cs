using FloatCanvas.Core;
using FloatCanvas.Storage;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class BillboardStorageTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "billboard-storage-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var assets = new AssetLibrary(); string resource = assets.Add([1, 2, 3, 4]);
        var matrix = new Projective2D(1, 0, 0, 0, 1, 0, 0, .005, 1);
        var group = new CanvasGroup { Name = "mixed" };
        var scene = new SceneDocument { Viewport = new(0, 0, 180, 150), Groups = [group], Canvas = new() { Projection = new() { Enabled = true,
            Matrix = matrix, Columns = 100, Rows = 100, CellSize = 1,
            Quad = [new(0, 0), new(100, 0), new(100 / 1.5, 100 / 1.5), new(0, 100 / 1.5)] } }, Elements =
        [
            new() { Kind = ElementKind.Rectangle, Width = 400, Height = 200, IsBillboard = true, BillboardAnchor = new(280, 600),
                BillboardHeight = -80, Transform = Affine2D.Rotation(17), GroupId = group.Id, Style = new() { StrokeWidth = 5 } },
            new() { Kind = ElementKind.Image, Width = 100, Height = 50, AssetId = resource, GroupId = group.Id,
                Transform = Affine2D.Translation(10, 20) }
        ] };
        var store = new ProjectStore(); string path = Path.Combine(root, "billboard.floatcanvas");
        string before = JsonSerializer.Serialize(scene);
        await store.SaveAsync(path, scene, assets);
        var loaded = await store.LoadAsync(path);
        check(JsonSerializer.Serialize(loaded.Document) == before, "current format preserves mixed modes, signed height, angle, anchors and geometry exactly");
        check(loaded.Assets.Get(resource).Data.Span.SequenceEqual(assets.Get(resource).Data.Span), "billboard save preserves source asset bytes");
        using (var zip = ZipFile.OpenRead(path))
        {
            using var manifestReader = new StreamReader(zip.GetEntry("manifest.json")!.Open());
            check(JsonNode.Parse(await manifestReader.ReadToEndAsync())!["Version"]!.GetValue<int>() == SceneDocument.CurrentVersion, "current manifest is rejected by published v3 reader version gate");
        }
        var recovery = new RecoveryStore(Path.Combine(root, "recovery"));
        await recovery.SaveAsync(new() { LiveDocument = scene }, assets);
        check(JsonSerializer.Serialize((await recovery.LoadAsync())!.State.LiveDocument) == before, "workspace recovery preserves full billboard state");
        var disabled = scene.Clone(); disabled.Canvas.Projection.Enabled = false;
        string disabledPath = Path.Combine(root, "disabled.floatcanvas");
        await store.SaveAsync(disabledPath, disabled, assets);
        var disabledLoaded = (await store.LoadAsync(disabledPath)).Document;
        check(disabledLoaded.Elements[0].IsBillboard && disabledLoaded.Elements[0].BillboardHeight == -80 && !disabledLoaded.Canvas.Projection.Enabled,
            "disabled projection persists signed height and mode for later restoration");
        async Task Mutate(string name, Action<JsonObject> edit)
        {
            string edited = Path.Combine(root, name + ".floatcanvas"); File.Copy(path, edited);
            using (var zip = ZipFile.Open(edited, ZipArchiveMode.Update))
            {
                JsonObject json;
                using (var reader = new StreamReader(zip.GetEntry("scene.json")!.Open())) json = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
                edit(json); zip.GetEntry("scene.json")!.Delete();
                using var writer = new StreamWriter(zip.CreateEntry("scene.json").Open()); await writer.WriteAsync(json.ToJsonString());
            }
            bool rejected = false; try { await store.LoadAsync(edited); } catch (InvalidDataException) { rejected = true; }
            check(rejected, name);
        }
        await Mutate("missing required billboard anchor rejected", n => n["Elements"]![0]!.AsObject().Remove("BillboardAnchor"));
        await Mutate("partial billboard anchor rejected", n => n["Elements"]![0]!["BillboardAnchor"]!.AsObject().Remove("Y"));
        await Mutate("missing required billboard height rejected", n => n["Elements"]![0]!.AsObject().Remove("BillboardHeight"));
        await Mutate("billboard behind horizon rejected", n => n["Elements"]![0]!["BillboardAnchor"]!["Y"] = -300);
        var invalid = scene.Clone(); invalid.Elements[0].BillboardHeight = double.NaN;
        bool nanRejected = false; try { SceneValidation.Validate(invalid); } catch (InvalidDataException) { nanRejected = true; }
        check(nanRejected, "nonfinite billboard height rejected");
        // A genuine v3 payload contains no new fields and must still open with exact old geometry.
        var old = new SceneDocument { Elements = [new() { Kind = ElementKind.Rectangle, Width = 100, Height = 60, Transform = Affine2D.Translation(12, 34) }] };
        string legacy = Path.Combine(root, "legacy-v3.floatcanvas"); await store.SaveAsync(legacy, old, new());
        using (var zip = ZipFile.Open(legacy, ZipArchiveMode.Update))
            foreach (string name in new[] { "manifest.json", "scene.json" })
            {
                JsonObject json;
                using (var reader = new StreamReader(zip.GetEntry(name)!.Open())) json = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
                json["Version"] = 3;
                if (name == "scene.json")
                    foreach (var element in json["Elements"]!.AsArray())
                    { element!.AsObject().Remove("IsBillboard"); element.AsObject().Remove("BillboardAnchor"); element.AsObject().Remove("BillboardHeight"); }
                zip.GetEntry(name)!.Delete(); using var writer = new StreamWriter(zip.CreateEntry(name).Open()); await writer.WriteAsync(json.ToJsonString());
            }
        var oldLoaded = await store.LoadAsync(legacy);
        check(oldLoaded.Document.Version == SceneDocument.CurrentVersion && !oldLoaded.Document.Elements[0].IsBillboard &&
            ElementGeometry.Bounds(oldLoaded.Document.Elements[0]) == ElementGeometry.Bounds(old.Elements[0]), "published v3 opens as planar without geometry changes");
    }
}
