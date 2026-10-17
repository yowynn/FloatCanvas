using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.Json;
using FloatCanvas.Core;
using FloatCanvas.Storage;

internal static class FormatVersionTests
{
    public static async Task RunAsync(ProjectStore store, string root, Action<bool, string> check)
    {
        var assets = new AssetLibrary();
        string assetId = assets.Add([1, 2, 3, 4]);
        var legacy = new SceneDocument
        {
            Version = 1, Viewport = new(-320, 40, 1280, 720), LayerOpacity = .6, NextNumber = 12,
            Elements =
            [
                new() { Kind = ElementKind.Arrow, Points = [new(20, 30), new(180, 50)],
                    Style = new() { Stroke = 0x88123456, StrokeWidth = 7, Opacity = .75, ArrowHeadStyle = ArrowHeadStyle.Open, ArrowHeadSize = 23 } },
                new() { Kind = ElementKind.RoundedRectangle, Width = 90, Height = 60,
                    Transform = Affine2D.Translation(-120, 60).Compose(Affine2D.Rotation(23)), Style = new() { Fill = 0x77224466 } },
                new() { Kind = ElementKind.Image, AssetId = assetId, Width = 100, Height = 80, ImageCrop = new(.1, .2, .7, .6),
                    Effects = [new() { Kind = ImageEffectKind.Pixelate, Bounds = new(.1, .1, .3, .3), Amount = 8 }] },
                new() { Kind = ElementKind.Text, Text = "旧工程 editable", Width = 100, Height = 35,
                    Style = new() { FontFamily = "Segoe UI", FontSize = 19, TextAlignment = TextAlignment.Right } }
            ]
        };
        string path = Path.Combine(root, "legacy-version-migration.floatcanvas");
        await store.SaveAsync(path, legacy, assets);
        check(legacy.Version == 1, "saving a caller-owned v1 scene does not mutate its version");
        check(!LegacyV1ReaderAccepts(path), "new save envelope is rejected by the published v1 reader version gate");
        using (var zip = ZipFile.OpenRead(path))
        {
            check(await VersionAsync(zip, "manifest.json") == 2 && await VersionAsync(zip, "scene.json") == 2,
                "every new project save explicitly writes v2 envelope and v2 scene");
        }
        await ConvertToV1Async(path, "scene.json", n => RemoveNewFields(n));
        check(LegacyV1ReaderAccepts(path), "legacy fixture satisfies the real v1 manifest and scene version gates");
        var loaded = await store.LoadAsync(path);
        var expected = legacy.Clone(); expected.Version = 2;
        check(loaded.Document.Version == 2 && JsonSerializer.Serialize(loaded.Document) == JsonSerializer.Serialize(expected),
            "v1 scene migrates in memory preserving IDs, transforms, alpha, arrow styles, text, crops and effects");
        check(loaded.Assets.Get(assetId).Data.Span.SequenceEqual(assets.Get(assetId).Data.Span) &&
            ElementGeometry.ArrowStyle(loaded.Document.Elements[0]) == ArrowHeadStyle.Open &&
            ElementGeometry.CornerRadius(loaded.Document.Elements[1]) == 12,
            "v1 migration retains original resource bytes and legacy arrow/radius defaults");
        var session = new EditorSession(loaded.Document, loaded.Assets);
        session.SetSelection(session.Document.Elements.Take(2).Select(e => e.Id)); session.GroupSelection("after migration");
        session.UpdateSelection(e => e.Style.StrokePattern = StrokePattern.Dashed);
        session.Document.Elements[0].Style.StartArrowHead = ArrowHeadStyle.Diamond;
        session.Document.Elements[1].CornerRadius = 17;
        await store.SaveAsync(path, session.Document, session.Assets);
        var reloaded = await store.LoadAsync(path);
        check(!LegacyV1ReaderAccepts(path) && JsonSerializer.Serialize(reloaded.Document) == JsonSerializer.Serialize(session.Document),
            "v1-open-edit-save writes v2 and roundtrips new groups, dashes, independent heads and radius without loss");

        byte[] complete = await File.ReadAllBytesAsync(path);
        var future = session.Document.Clone(); future.Version = 99;
        bool rejected = false;
        try { await store.SaveAsync(path, future, session.Assets); } catch (InvalidDataException) { rejected = true; }
        check(rejected && (await File.ReadAllBytesAsync(path)).AsSpan().SequenceEqual(complete),
            "future scene version is rejected before replacing a good project");

        var recovery = new RecoveryStore(Path.Combine(root, "v1-migration-recovery"));
        var state = new WorkspaceState { LiveDocument = legacy.Clone(), CaptureDocument = legacy.CreateCapture(new(0, 0, 300, 200), assetId), CaptureOrigin = new(-20, 40) };
        state.CaptureDocument.Version = 1;
        await recovery.SaveAsync(state, assets);
        check(!LegacyV1ReaderAccepts(recovery.CurrentPath), "new recovery envelope also rejects legacy readers");
        await ConvertToV1Async(recovery.CurrentPath, "workspace.json", n =>
        {
            RemoveNewFields(n["LiveDocument"]!.AsObject());
            RemoveNewFields(n["CaptureDocument"]!.AsObject());
        });
        var restored = (await recovery.LoadAsync())!;
        check(restored.State.LiveDocument.Version == 2 && restored.State.CaptureDocument!.Version == 2 &&
            restored.State.CaptureOrigin == state.CaptureOrigin && restored.Assets.Get(assetId).Data.Span.SequenceEqual(assets.Get(assetId).Data.Span),
            "legacy workspace upgrades both documents in memory and retains capture origin and shared assets");
        await recovery.SaveAsync(restored.State, restored.Assets);
        using var savedRecovery = ZipFile.OpenRead(recovery.CurrentPath);
        var workspace = await JsonAsync(savedRecovery, "workspace.json");
        check(await VersionAsync(savedRecovery, "manifest.json") == 2 && workspace["LiveDocument"]!["Version"]!.GetValue<int>() == 2 &&
            workspace["CaptureDocument"]!["Version"]!.GetValue<int>() == 2,
            "saving migrated recovery writes v2 for the envelope and both documents");
    }

    // These are the exact two version gates in the published v1 PackageCodec/SceneValidation.
    private static bool LegacyV1ReaderAccepts(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var manifest = JsonAsync(zip, "manifest.json").GetAwaiter().GetResult();
        if (manifest["Version"]!.GetValue<int>() != 1) return false;
        string payload = manifest["Payload"]!.GetValue<string>();
        var content = JsonAsync(zip, payload).GetAwaiter().GetResult();
        return payload == "scene.json" ? content["Version"]!.GetValue<int>() == 1 :
            content["LiveDocument"]!["Version"]!.GetValue<int>() == 1 &&
            (content["CaptureDocument"] is null || content["CaptureDocument"]!["Version"]!.GetValue<int>() == 1);
    }
    private static async Task<int> VersionAsync(ZipArchive zip, string name) => (await JsonAsync(zip, name))["Version"]!.GetValue<int>();
    private static async Task<JsonObject> JsonAsync(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
    }
    private static void RemoveNewFields(JsonObject scene)
    {
        scene["Version"] = 1; scene.Remove("Groups");
        foreach (var element in scene["Elements"]!.AsArray().Append(scene["Background"]).Where(e => e is not null))
        {
            var item = element!.AsObject(); item.Remove("GroupId"); item.Remove("CornerRadius"); item.Remove("Symbol");
            var style = item["Style"]!.AsObject(); style.Remove("StartArrowHead"); style.Remove("EndArrowHead"); style.Remove("StrokePattern");
        }
    }
    private static async Task ConvertToV1Async(string path, string payload, Action<JsonObject> mutate)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        foreach (string name in new[] { "manifest.json", payload })
        {
            var json = await JsonAsync(zip, name);
            if (name == "manifest.json") json["Version"] = 1; else mutate(json);
            zip.GetEntry(name)!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry(name).Open()); await writer.WriteAsync(json.ToJsonString());
        }
    }
}
