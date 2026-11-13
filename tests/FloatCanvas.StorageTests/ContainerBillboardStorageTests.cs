using FloatCanvas.Core;
using FloatCanvas.Storage;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class ContainerBillboardStorageTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "container-billboard", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var leaf = new CanvasElement { Width = 100, Height = 60, IsBillboard = true, BillboardHeight = -8, BillboardAnchor = new(20, 40) };
        var group = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [leaf.Id] }; leaf.GroupId = group.Id;
        var session = new EditorSession(new SceneDocument { Elements = [leaf], Groups = [group], RootOrder = [group.Id] });
        session.SetContainerBillboard(group.Id, true);
        var definition = new PrefabDefinition { Graph = new() { Elements = [leaf.Clone()], Groups = [group.Clone()], RootOrder = [group.Id] } };
        var instance = new PrefabInstance { DefinitionId = definition.Id, Billboard = new(new(14, 25), -18), Transform = Affine2D.Translation(250, 100) };
        session.Document.Definitions.Add(definition); session.Document.Instances.Add(instance); session.Document.RootOrder!.Add(instance.Id);
        string expected = JsonSerializer.Serialize(session.Document); var store = new ProjectStore();
        string file = Path.Combine(root, "card.floatcanvas"); await store.SaveAsync(file, session.Document, session.Assets);
        var loaded = await store.LoadAsync(file);
        check(JsonSerializer.Serialize(loaded.Document) == expected, "format 19 project retains nested cards and stable references exactly");
        var recovery = new RecoveryStore(Path.Combine(root, "recovery"));
        await recovery.SaveAsync(new() { LiveDocument = session.Document }, session.Assets);
        check(JsonSerializer.Serialize((await recovery.LoadAsync())!.State.LiveDocument) == expected, "recovery retains whole card and child placement");
        var package = new PrefabPackage { EntryDefinitionId = definition.Id, Definitions = [definition.Clone()] };
        var prefabs = new PrefabStore(); string prefab = Path.Combine(root, "card.floatprefab");
        await prefabs.SaveAsync(prefab, package, session.Assets);
        check((await prefabs.LoadAsync(prefab)).Package.Definitions[0].Graph.Groups[0].Billboard == group.Billboard, "shared resource retains internal container placement");
        foreach (string field in new[] { "Height", "ReferencePoint" })
        {
            string broken = Path.Combine(root, "missing-" + field + ".floatcanvas"); File.Copy(file, broken);
            using (var zip = ZipFile.Open(broken, ZipArchiveMode.Update))
            {
                JsonObject json;
                using (var reader = new StreamReader(zip.GetEntry("scene.json")!.Open())) json = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
                json["Groups"]![0]!["Billboard"]!.AsObject().Remove(field); zip.GetEntry("scene.json")!.Delete();
                using var writer = new StreamWriter(zip.CreateEntry("scene.json").Open()); await writer.WriteAsync(json.ToJsonString());
            }
            bool rejected = false; try { await store.LoadAsync(broken); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "container rejects missing " + field);
        }
        var old = new SceneDocument { Elements = [new() { Width = 50, Height = 60, Transform = Affine2D.Scale(.4, .4), IsBillboard = true, BillboardHeight = 30 }] };
        string legacy = Path.Combine(root, "v18.floatcanvas"); await store.SaveAsync(legacy, old, new());
        using (var zip = ZipFile.Open(legacy, ZipArchiveMode.Update))
            foreach (string name in new[] { "manifest.json", "scene.json" })
            {
                JsonObject json; using (var reader = new StreamReader(zip.GetEntry(name)!.Open())) json = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
                json["Version"] = 18; zip.GetEntry(name)!.Delete();
                using var writer = new StreamWriter(zip.CreateEntry(name).Open()); await writer.WriteAsync(json.ToJsonString());
            }
        var legacyLoaded = (await store.LoadAsync(legacy)).Document;
        check(legacyLoaded.Version == 19 && JsonSerializer.Serialize(legacyLoaded.Elements) == JsonSerializer.Serialize(old.Elements), "format 18 preserves historical billboard compensation without rewriting");
        var invalidVersion = session.Document.Clone(); invalidVersion.Version = 18;
        bool versionRejected = false; try { SceneValidation.Validate(invalidVersion); } catch (InvalidDataException) { versionRejected = true; }
        check(versionRejected, "old format cannot carry new container semantics");
    }
}
