using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using FloatCanvas.Core;
using FloatCanvas.Storage;

internal static class EditablePresetStorageTests
{
    public static async Task RunAsync(ProjectStore store, string root, Action<bool, string> check)
    {
        var assets = new AssetLibrary(); string resource = assets.Add([1, 2, 3, 4]);
        var inner = new CanvasGroup { Name = "text and image" };
        var outer = new CanvasGroup { Name = "editable stamp" }; inner.ParentId = outer.Id;
        var document = new SceneDocument
        {
            Groups = [inner, outer],
            Elements =
            [
                new() { Kind = ElementKind.Text, Text = "原始矢量", GroupId = inner.Id, Width = 120, Height = 30 },
                new() { Kind = ElementKind.Image, AssetId = resource, Width = 100, Height = 80, GroupId = inner.Id,
                    Transform = Affine2D.Translation(30, 60).Compose(Affine2D.Rotation(23)), ImageCrop = new(.1, .2, .7, .6),
                    Effects = [new() { Kind = ImageEffectKind.Pixelate, Bounds = new(.1, .1, .2, .2), Amount = 8 }] },
                new() { Kind = ElementKind.Line, GroupId = outer.Id, Points = [new(10, 20), new(140, 40)], Style = new() { StartArrowHead = ArrowHeadStyle.Open, EndArrowHead = ArrowHeadStyle.Diamond, StrokePattern = StrokePattern.Dotted } },
                new() { Kind = ElementKind.Rectangle, Width = 80, Height = 40, CornerRadius = 14, Style = new() { StrokePattern = StrokePattern.Dashed } },
                new() { Kind = ElementKind.Symbol, Symbol = SymbolKind.Heart, Width = 60, Height = 60, Style = new() { Fill = 0xFF22CC66 } }
            ]
        };
        string path = Path.Combine(root, "editable-stamp.floatcanvas");
        await store.SaveAsync(path, document, assets);
        var loaded = await store.LoadAsync(path);
        check(JsonSerializer.Serialize(loaded.Document) == JsonSerializer.Serialize(document) && loaded.Assets.Get(resource).Data.Span.SequenceEqual(assets.Get(resource).Data.Span), "nested groups, styles, symbols, text, crops, effects and original assets roundtrip exactly");
        var receiving = new EditorSession(); receiving.InsertDocument(loaded.Document, loaded.Assets, Affine2D.Scale(2, 2));
        check(receiving.Document.Groups.Count == 2 && receiving.Document.Elements.Single(e => e.Kind == ElementKind.Text).Text == "原始矢量", "loaded preset inserts with independently editable nested vector members");
        string before = JsonSerializer.Serialize(receiving.Document);
        byte[] original = await File.ReadAllBytesAsync(path);
        foreach (var mutation in new (string Name, Action<JsonObject> Apply)[]
        {
            ("group cycle", n => n["Groups"]![1]!["ParentId"] = inner.Id.ToString()),
            ("orphan group parent", n => n["Groups"]![0]!["ParentId"] = Guid.NewGuid().ToString()),
            ("orphan member", n => n["Elements"]![0]!["GroupId"] = Guid.NewGuid().ToString()),
            ("duplicate group identity", n => n["Groups"]![1]!["Id"] = inner.Id.ToString()),
            ("null group list", n => n["Groups"] = null),
            ("unknown symbol", n => n["Elements"]![4]!["Symbol"] = 99),
            ("unknown stroke pattern", n => n["Elements"]![2]!["Style"]!["StrokePattern"] = 99),
            ("unknown start head", n => n["Elements"]![2]!["Style"]!["StartArrowHead"] = 99),
            ("negative radius", n => n["Elements"]![3]!["CornerRadius"] = -1),
            ("unknown future field", n => n["FutureUnrecognizedGeometry"] = true),
            ("unknown version", n => n["Version"] = 99)
        })
        {
            string broken = Path.Combine(root, "invalid-preset-" + mutation.Name.Replace(' ', '-') + ".floatcanvas");
            File.Copy(path, broken, true);
            await MutateAsync(broken, mutation.Apply);
            bool rejected = false;
            try { await store.LoadAsync(broken); } catch (InvalidDataException) { rejected = true; }
            check(rejected && JsonSerializer.Serialize(receiving.Document) == before, mutation.Name + " load rejects before any receiving-document mutation");
        }
        var invalid = document.Clone(); invalid.Groups[0].ParentId = Guid.NewGuid();
        bool rejectedSave = false;
        try { await store.SaveAsync(path, invalid, assets); } catch (InvalidDataException) { rejectedSave = true; }
        byte[] afterRejectedSave = await File.ReadAllBytesAsync(path);
        check(rejectedSave && original.AsSpan().SequenceEqual(afterRejectedSave), "invalid group save preserves existing project bytes");

        string legacyPath = Path.Combine(root, "legacy-before-preset-model.floatcanvas");
        var legacy = new SceneDocument { Elements = [new() { Kind = ElementKind.RoundedRectangle, Width = 100, Height = 60 }, new() { Kind = ElementKind.Arrow, Points = [new(0, 0), new(100, 0)] }] };
        await store.SaveAsync(legacyPath, legacy, new());
        await MutateAsync(legacyPath, n =>
        {
            n.Remove("Groups");
            foreach (var element in n["Elements"]!.AsArray())
            {
                var obj = element!.AsObject(); obj.Remove("GroupId"); obj.Remove("CornerRadius"); obj.Remove("Symbol");
                var style = obj["Style"]!.AsObject(); style.Remove("StartArrowHead"); style.Remove("EndArrowHead"); style.Remove("StrokePattern");
            }
        });
        var old = await store.LoadAsync(legacyPath);
        check(old.Document.Groups.Count == 0 && old.Document.Elements.All(e => e.GroupId is null && e.CornerRadius is null && e.Style.StrokePattern == StrokePattern.Solid) &&
            ElementGeometry.CornerRadius(old.Document.Elements[0]) == 12 && ElementGeometry.GetArrowHeads(old.Document.Elements[1]).Count == 1,
            "old project missing every new field preserves default solid legacy arrow and rounded rectangle");
        var workspace = new WorkspaceState { LiveDocument = document.Clone() };
        var recovery = new RecoveryStore(Path.Combine(root, "preset-recovery"));
        await recovery.SaveAsync(workspace, assets);
        check(JsonSerializer.Serialize((await recovery.LoadAsync())!.State.LiveDocument) == JsonSerializer.Serialize(document), "recovery preserves complete editable preset model too");
    }

    private static async Task MutateAsync(string path, Action<JsonObject> mutation)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = zip.GetEntry("scene.json")!; JsonObject json;
        using (var reader = new StreamReader(entry.Open())) json = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
        mutation(json); entry.Delete();
        using var writer = new StreamWriter(zip.CreateEntry("scene.json").Open()); await writer.WriteAsync(json.ToJsonString());
    }
}
