using FloatCanvas.Core;
using FloatCanvas.Storage;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class RootCanvasStorageTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "root-canvases", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var store = new ProjectStore("0.4.0-roots-test");
        var owner = new EditorSession();
        CanvasElement Box(string name) => new() { Kind = ElementKind.Rectangle, Name = name, Width = 60, Height = 30,
            Transform = Affine2D.Translation(55, 65), Style = new() { Fill = 0xFF225588 } };
        owner.Add(Box("main")); owner.SetRootCanvas(RootCanvasKind.Background); owner.Add(Box("background"));
        owner.SetRootCanvas(RootCanvasKind.Foreground); owner.Add(Box("foreground"));
        Guid empty = owner.GroupSelection("foreground group")!.Value;
        var document = owner.Document; string original = JsonSerializer.Serialize(document);
        string path = Path.Combine(root, "three.floatcanvas");
        await store.SaveAsync(path, document, owner.Assets); var loaded = await store.LoadAsync(path);
        check(SceneDocument.CurrentVersion >= 17 && JsonSerializer.Serialize(loaded.Document) == original, "current format retains v17 root assignments including populated groups exactly");
        var reopened = new EditorSession(loaded.Document, loaded.Assets);
        check(reopened.ActiveRootCanvas == RootCanvasKind.Main && reopened.EditScopeId is null, "reopening starts editing Main while preserving every root's contents");
        check(SceneEvaluation.Build(loaded.Document).Leaves.Select(leaf => leaf.RootCanvas).SequenceEqual([
            RootCanvasKind.Background, RootCanvasKind.Main, RootCanvasKind.Foreground]), "saved roots retain fixed composite order after reload");
        var recovery = new RecoveryStore(Path.Combine(root, "recovery"), "0.4.0-roots-test");
        await recovery.SaveAsync(new() { LiveDocument = document.Clone() }, owner.Assets);
        check(JsonSerializer.Serialize((await recovery.LoadAsync())!.State.LiveDocument) == original, "recovery saves root assignments with authored source graphs");

        foreach (int version in Enumerable.Range(1, 15))
        {
            // Explicitly remove future fields and write the era's color contract;
            // a current DTO merely relabelled v1 would be a false legacy fixture.
            var fixture = new SceneDocument { Elements = [Box("legacy")], Viewport = new(0, 0, 600, 400), LayerOpacity = .8, NextNumber = 3 };
            var old = JsonSerializer.SerializeToNode(fixture)!.AsObject(); old["Version"] = version; old.Remove("RootCanvasAssignments"); old.Remove("RootPreviewCanvases");
            if (version < 12) LegacyFixtureSchema.UseLegacyAppearance(old);
            if (version < 11) { old.Remove("ResourceId"); old.Remove("Instances"); old.Remove("Definitions"); }
            if (version < 3) old.Remove("Canvas");
            if (version is 6 or 7) old["Canvas"]!["Projection"]!["LockedGridCellSize"] = null;
            string legacy = Path.Combine(root, "v" + version + ".floatcanvas");
            await WritePackage(legacy, version, old); byte[] before = await File.ReadAllBytesAsync(legacy);
            var migrated = await store.LoadAsync(legacy);
            check(migrated.Document.Version == SceneDocument.CurrentVersion && migrated.Document.RootCanvasAssignments.Count == 0 &&
                SceneCanvasRoots.Resolve(migrated.Document, migrated.Document.Elements.Single().Id) == RootCanvasKind.Main,
                $"historical v{version} defaults to Main without inventing root groups");
            check((await File.ReadAllBytesAsync(legacy)).AsSpan().SequenceEqual(before) &&
                migrated.Document.Elements.Single().Transform == Affine2D.Translation(55, 65) &&
                migrated.Document.Elements.Single().Style.Fill == 0xFF225588u,
                $"historical v{version} load preserves source bytes, placement and visible colors");
            string converted = Path.Combine(root, "v" + version + "-converted.floatcanvas");
            await store.SaveAsync(converted, migrated.Document, migrated.Assets);
            check(JsonSerializer.Serialize((await store.LoadAsync(converted)).Document) == JsonSerializer.Serialize(migrated.Document),
                $"historical v{version} resaves and reloads stably as v16");
        }
        foreach (var item in new (string Name, Action<JsonObject> Edit)[]
        {
            ("unknown-node", json => json["RootCanvasAssignments"]![Guid.NewGuid().ToString()] = (int)RootCanvasKind.Foreground),
            ("main-entry", json => json["RootCanvasAssignments"]![document.Elements[0].Id.ToString()] = 0),
            ("unknown-kind", json => json["RootCanvasAssignments"]![empty.ToString()] = 99),
            ("null-map", json => json["RootCanvasAssignments"] = null)
        })
        {
            var invalid = JsonSerializer.SerializeToNode(document)!.AsObject(); item.Edit(invalid);
            string bad = Path.Combine(root, item.Name + ".floatcanvas"); await WritePackage(bad, 16, invalid);
            bool rejected = false; try { await store.LoadAsync(bad); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "storage rejects invalid root metadata: " + item.Name);
        }
        var falseLegacy = JsonSerializer.SerializeToNode(document)!.AsObject(); falseLegacy["Version"] = 15;
        string falsePath = Path.Combine(root, "false-legacy.floatcanvas"); await WritePackage(falsePath, 15, falseLegacy);
        bool unsupported = false; try { await store.LoadAsync(falsePath); } catch (InvalidDataException) { unsupported = true; }
        check(unsupported, "v15 payloads cannot smuggle non-Main roots into the legacy interpretation");
    }

    private static async Task WritePackage(string path, int version, JsonObject document)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var manifest = new JsonObject { ["Format"] = "FloatCanvas.project", ["Version"] = version,
            ["Payload"] = "scene.json", ["Assets"] = new JsonArray() };
        if (version >= 9) { manifest["CreatedWith"] = "0.4.0-test"; manifest["LastSavedWith"] = "0.4.0-test"; }
        using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open())) await writer.WriteAsync(manifest.ToJsonString());
        using (var writer = new StreamWriter(zip.CreateEntry("scene.json").Open())) await writer.WriteAsync(document.ToJsonString());
    }
}
