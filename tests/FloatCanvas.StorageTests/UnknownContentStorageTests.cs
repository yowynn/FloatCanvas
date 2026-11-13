using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using FloatCanvas.Core;
using FloatCanvas.Storage;

internal static class UnknownContentStorageTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "unknown-content", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var assets = new AssetLibrary(); string asset = assets.Add([1, 2, 3, 4]);
        var image = new CanvasElement { Kind = ElementKind.Image, AssetId = asset, Width = 100, Height = 80,
            Effects = [new() { Kind = ImageEffectKind.Blur, Bounds = new(0, 0, .5, .5) }, new() { Kind = ImageEffectKind.Pixelate, Bounds = new(.5, .5, .5, .5) }] };
        var group = new CanvasGroup { Name = "Removed child", OrderMode = GroupOrderMode.AtomicBlock };
        var child = new CanvasElement { Kind = ElementKind.Rectangle, GroupId = group.Id, Width = 10, Height = 10 };
        group.ChildOrder = [child.Id];
        var scene = new SceneDocument { Viewport = new(0, 0, 800, 600), Elements = [image, child], Groups = [group], RootOrder = [image.Id, group.Id] };
        var store = new ProjectStore("0.4.0-test"); string original = Path.Combine(root, "source.floatcanvas");
        await store.SaveAsync(original, scene, assets);
        string compatible = await Copy("compatible", json =>
        {
            json["Elements"]![0]!["Effects"]![0]!["Kind"] = 2;
            json["Elements"]![0]!["Effects"]![0]!["FutureEffectData"] = "ignored";
            json["Elements"]![1]!["Kind"] = 9876;
            json["Elements"]![1]!["FutureLayerData"] = new JsonObject { ["extra"] = true };
        });
        var before = await File.ReadAllBytesAsync(compatible); var loaded = await store.LoadAsync(compatible);
        check(loaded.Document.Elements.Count == 1 && loaded.Document.Elements[0].Id == image.Id &&
            loaded.Document.Elements[0].Effects is [{ Kind: ImageEffectKind.Pixelate }], "unknown node and effect are discarded while the known image and effect survive");
        check(loaded.Document.Groups.Count == 0 && loaded.Document.RootOrder!.SequenceEqual([image.Id]), "discarded child cleans its empty group and draw ledger");
        var after = await File.ReadAllBytesAsync(compatible);
        check(before.SequenceEqual(after), "compatibility load does not overwrite its source");
        string saved = Path.Combine(root, "saved.floatcanvas"); await store.SaveAsync(saved, loaded.Document, loaded.Assets);
        var reopened = await store.LoadAsync(saved);
        check(reopened.Document.Elements.Count == 1 && reopened.Document.Elements[0].Effects.Count == 1, "saving does not recreate unknown records");

        foreach (var (name, edit) in new (string, Action<JsonNode>)[]
        {
            ("missing-kind", j => j["Elements"]![1]!.AsObject().Remove("Kind")),
            ("invalid-kind", j => j["Elements"]![1]!["Kind"] = "unexpected"),
            ("duplicate-id", j => { j["Elements"]![1]!["Kind"] = 9876; j["Elements"]![1]!["Id"] = image.Id; }),
            ("broken-known", j => { j["Elements"]![1]!["Kind"] = 9876; j["Elements"]![0]!["Width"] = -1; }),
            ("duplicate-effect", j => { j["Elements"]![0]!["Effects"]![0]!["Kind"] = 2; j["Elements"]![0]!["Effects"]![0]!["Id"] = image.Effects[1].Id; })
        })
        {
            string path = await Copy(name, edit); bool rejected = false;
            try { await store.LoadAsync(path); } catch (InvalidDataException) { rejected = true; }
            check(rejected, name + " is not excused by unknown-content compatibility");
        }
        string unused = await Copy("unused-asset", j =>
        { j["Elements"]![0]!["Kind"] = 9876; });
        var withoutImage = await store.LoadAsync(unused);
        check(!withoutImage.Document.ReferencedAssets.Any() && withoutImage.Assets.Count == 0, "assets referenced only by discarded nodes are released");

        // Identical local IDs in separate definition graphs must not cross-clean ledgers.
        var definition = new PrefabDefinition { Name = "Known", Graph = new() { Elements = [new() { Id = child.Id, Kind = ElementKind.Rectangle, Width = 20, Height = 20 }], RootOrder = [child.Id] } };
        scene.Definitions.Add(definition);
        var instance = new PrefabInstance { DefinitionId = definition.Id }; scene.Instances.Add(instance); scene.RootOrder.Add(instance.Id);
        string definitions = Path.Combine(root, "definitions.floatcanvas"); await store.SaveAsync(definitions, scene, assets);
        await Edit(definitions, j => j["Elements"]![1]!["Kind"] = 9876);
        var withDefinition = await store.LoadAsync(definitions);
        check(withDefinition.Document.Definitions[0].Graph.RootOrder!.SequenceEqual([child.Id]), "skipping a host node does not remove an equal ID in a definition");

        string corruptedUnused = await Copy("corrupt-unused-asset", j => j["Elements"]![0]!["Kind"] = 9876);
        using (var zip = ZipFile.Open(corruptedUnused, ZipArchiveMode.Update))
        { var entry = zip.GetEntry("assets/" + asset)!; entry.Delete(); using var stream = zip.CreateEntry("assets/" + asset).Open(); stream.Write(new byte[] { 1, 2, 3, 9 }); }
        bool corruptRejected = false; try { await store.LoadAsync(corruptedUnused); } catch (InvalidDataException) { corruptRejected = true; }
        check(corruptRejected, "discarding unknown nodes does not bypass integrity checks on their assets");

        var recovery = new RecoveryStore(Path.Combine(root, "recovery"), "0.4.0-test");
        var workspace = new WorkspaceState { LiveDocument = scene.Clone(), PendingPrefabEdit = new()
        { OwnerResourceId = scene.ResourceId, DefinitionId = definition.Id, ExpectedRevision = definition.Revision, DraftDocument = scene.Clone(),
          ReturnScopeId = group.Id, DraftScopeId = group.Id, ReturnSelection = [child.Id], DraftSelection = [child.Id] } };
        await recovery.SaveAsync(workspace, assets);
        await Edit(recovery.CurrentPath, j =>
        { j["LiveDocument"]!["Elements"]![1]!["Kind"] = 9876; j["PendingPrefabEdit"]!["DraftDocument"]!["Elements"]![1]!["Kind"] = 9876; }, "workspace.json");
        var recovered = await recovery.LoadAsync();
        var draft = recovered!.State.PendingPrefabEdit!;
        check(draft.ReturnScopeId is null && draft.DraftScopeId is null && draft.ReturnSelection.Count == 0 && draft.DraftSelection.Count == 0,
            "unknown-node recovery resolves removed scope chains and selections before publication");

        async Task<string> Copy(string name, Action<JsonNode> edit)
        { string path = Path.Combine(root, name + ".floatcanvas"); File.Copy(original, path); await Edit(path, edit); return path; }
    }

    private static async Task Edit(string path, Action<JsonNode> edit, string payload = "scene.json")
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update); var entry = archive.GetEntry(payload)!;
        JsonNode json; using (var reader = new StreamReader(entry.Open())) json = JsonNode.Parse(await reader.ReadToEndAsync())!;
        edit(json); entry.Delete(); using var writer = new StreamWriter(archive.CreateEntry(payload).Open()); await writer.WriteAsync(json.ToJsonString());
    }
}
