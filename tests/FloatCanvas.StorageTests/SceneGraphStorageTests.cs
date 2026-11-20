using FloatCanvas.Core;
using FloatCanvas.Storage;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class SceneGraphStorageTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "scene-graph-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var assets = new AssetLibrary(); var resource = assets.Add([1, 2, 3, 4]);
        var store = new ProjectStore("0.4.0-test"); var source = Fixture(resource);
        string path = Path.Combine(root, "nested.floatcanvas"); string before = JsonSerializer.Serialize(source);
        await store.SaveAsync(path, source, assets);
        var reopened = await store.LoadAsync(path);
        check(JsonSerializer.Serialize(reopened.Document) == before && JsonSerializer.Serialize(source) == before,
            "nested local graph, empty atomic groups and both paint ledgers roundtrip without source mutation");
        check(reopened.Document.Groups.Any(group => group.Name == "empty" && group.ChildOrder is { Count: 0 }) &&
            reopened.Document.ReferencedAssets.SequenceEqual([resource]) && reopened.Assets.Get(resource).Data.Span.SequenceEqual(new byte[] { 1, 2, 3, 4 }),
            "empty groups remain positioned while nested resources retain their original encoded bytes");
        var frozen = SceneEvaluation.Build(source);
        var currentBytes = await File.ReadAllBytesAsync(path);
        foreach (var runtime in new[] { frozen.RenderDocument, frozen.RenderDocument.Clone(), frozen.RenderDocument.CloneElements(frozen.RenderDocument.Elements.Select(element => element.Id)) })
        {
            bool rejected = false; try { await store.SaveAsync(path, runtime, assets); } catch (InvalidDataException) { rejected = true; }
            check(rejected && (await File.ReadAllBytesAsync(path)).AsSpan().SequenceEqual(currentBytes), "runtime views and their clones cannot replace an editable package");
        }
        var recovery = new RecoveryStore(Path.Combine(root, "recovery"), "0.4.0-test");
        var workspace = new WorkspaceState { LiveDocument = source.Clone(), CaptureDocument = source.Clone(), CaptureOrigin = new(-30, 45) };
        workspace.CaptureDocument.Kind = DocumentKind.FrozenCapture;
        await recovery.SaveAsync(workspace, assets);
        workspace.LiveDocument.NextNumber++;
        await recovery.SaveAsync(workspace, assets);
        var recoveryCurrent = await File.ReadAllBytesAsync(recovery.CurrentPath); var recoveryPrevious = await File.ReadAllBytesAsync(recovery.BackupPath);
        var invalidWorkspace = workspace.Clone(); invalidWorkspace.CaptureDocument = SceneEvaluation.Build(invalidWorkspace.CaptureDocument!).RenderDocument;
        bool runtimeRejected = false; try { await recovery.SaveAsync(invalidWorkspace, assets); } catch (InvalidDataException) { runtimeRejected = true; }
        check(runtimeRejected && (await File.ReadAllBytesAsync(recovery.CurrentPath)).AsSpan().SequenceEqual(recoveryCurrent) &&
            (await File.ReadAllBytesAsync(recovery.BackupPath)).AsSpan().SequenceEqual(recoveryPrevious), "runtime recovery rejection precedes any current-to-backup promotion");
        var restored = (await recovery.LoadAsync())!.State;
        check(JsonSerializer.Serialize(restored) == JsonSerializer.Serialize(workspace), "recovery retains both source graphs, empty groups and capture origin");

        foreach (int version in new[] { 1, 2, 3, 6, 8 })
        {
            var legacy = LegacyFixture(); legacy.Version = version;
            string legacyPath = Path.Combine(root, $"v{version}.floatcanvas");
            var json = JsonSerializer.SerializeToNode(legacy)!.AsObject();
            LegacyFixtureSchema.RemovePrefabFields(json);
            if (version is 6 or 7) json["Canvas"]!["Projection"]!["LockedGridCellSize"] = null;
            foreach (var group in json["Groups"]!.AsArray().OfType<JsonObject>())
                foreach (var property in new[] { "Transform", "ElevationScale", "Visible", "Locked", "OrderMode", "ChildOrder" }) group.Remove(property);
            await WritePackage(legacyPath, version, json);
            byte[] original = await File.ReadAllBytesAsync(legacyPath);
            var loaded = (await store.LoadAsync(legacyPath)).Document;
            var view = SceneEvaluation.Build(loaded);
            check(loaded.Version == SceneDocument.CurrentVersion && loaded.RootOrder is null &&
                loaded.Groups.All(group => group.OrderMode == GroupOrderMode.LegacyPassThrough && group.Transform == Affine2D.Identity && group.ElevationScale == 1),
                $"v{version} migrates to explicit legacy group semantics without gathering draw slots");
            check(view.RenderDocument.Elements.Select(element => element.Id).SequenceEqual(legacy.Elements.Select(element => element.Id)) &&
                JsonSerializer.Serialize(view.RenderDocument.Elements.Select(element => element.Transform)) == JsonSerializer.Serialize(legacy.Elements.Select(element => element.Transform)) &&
                (await File.ReadAllBytesAsync(legacyPath)).AsSpan().SequenceEqual(original), $"v{version} preserves the A/B/C picture order and source file bytes");
            string migrated = Path.Combine(root, $"v{version}-resaved.floatcanvas");
            await store.SaveAsync(migrated, loaded, new AssetLibrary());
            check(JsonSerializer.Serialize((await store.LoadAsync(migrated)).Document) == JsonSerializer.Serialize(loaded), $"v{version} migrated nested ownership has a stable subsequent save");
        }

        foreach (var mutation in new (string Name, Action<JsonObject> Change)[]
        {
            ("duplicate-token", n => n["RootOrder"]!.AsArray().Add(n["RootOrder"]![0]!.DeepClone())),
            ("lost-token", n => n["RootOrder"]!.AsArray().Clear()),
            ("partial-group-placement", n => n["Groups"]![0]!["Transform"]!.AsObject().Remove("OffsetX")),
            ("missing-elevation", n => n["Groups"]![0]!.AsObject().Remove("ElevationScale")),
            ("atomic-without-child-order", n => n["Groups"]![0]!.AsObject().Remove("ChildOrder")),
            ("unknown-parent", n => n["Groups"]![0]!["ParentId"] = Guid.NewGuid()),
            ("cycle", n => n["Groups"]![0]!["ParentId"] = n["Groups"]![0]!["Id"]!.DeepClone()),
            ("future", n => n["Version"] = SceneDocument.CurrentVersion + 1)
        })
        {
            string bad = Path.Combine(root, mutation.Name + ".floatcanvas"); File.Copy(path, bad);
            using (var zip = ZipFile.Open(bad, ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry("scene.json")!; JsonObject json;
                using (var reader = new StreamReader(entry.Open())) json = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
                mutation.Change(json); entry.Delete(); using var writer = new StreamWriter(zip.CreateEntry("scene.json").Open()); await writer.WriteAsync(json.ToJsonString());
            }
            bool rejected = false; try { await store.LoadAsync(bad); } catch (InvalidDataException) { rejected = true; }
            check(rejected, mutation.Name + " cannot enter the editable current document");
        }
        Console.WriteLine("Graph storage artifacts: " + root);
    }

    private static SceneDocument Fixture(string assetId)
    {
        var document = new SceneDocument { Canvas = IndependentCanvas() }; var graph = new SceneGraph(document);
        var group = graph.InsertGroup(new() { OrderMode = GroupOrderMode.AtomicBlock, Name = "outer", Transform = Affine2D.Translation(40, 30).Compose(Affine2D.Rotation(15)), ElevationScale = 2 });
        graph.InsertGroup(new() { OrderMode = GroupOrderMode.AtomicBlock, Name = "empty" }, group.Id);
        graph.InsertElement(new() { Kind = ElementKind.Image, Width = 100, Height = 70, AssetId = assetId,
            ImageCrop = new(.1, .2, .7, .6), Effects = [new() { Kind = ImageEffectKind.Blur, Bounds = new(.2, .2, .3, .3) }] }, group.Id, worldCoordinates: false);
        graph.InsertElement(new() { Kind = ElementKind.Number, Width = 50, Height = 50, Number = 12, Style = new() { Variant = ElementAppearance.NumberOutline } }, group.Id, worldCoordinates: false);
        return document;
    }
    private static SceneDocument LegacyFixture()
    {
        var a = new CanvasElement { Kind = ElementKind.Rectangle, Width = 100, Height = 100, Style = new() { Fill = 0xFFFF0000 } };
        var b = a.Clone(true); b.Style.Fill = 0xFF00FF00; b.Transform = Affine2D.Translation(20, 20);
        var c = a.Clone(true); c.Style.Fill = 0xFF0000FF; c.Transform = Affine2D.Translation(40, 40);
        var group = new CanvasGroup { Name = "noncontiguous A/C" }; a.GroupId = c.GroupId = group.Id;
        return new() { Elements = [a, b, c], Groups = [group] };
    }
    private static CanvasSettings IndependentCanvas()
    {
        var canvas = new CanvasSettings { Projection = CanvasCalibration.CreateInitial(new(0, 0, 1500, 1000)) };
        CanvasProjectionGeometry.InitializePlane(canvas);
        if (!CanvasProjectionGeometry.TryResize(canvas.Projection, 840, 1188, out var projection, out _)) throw new InvalidOperationException();
        canvas.Projection = projection; canvas.Grid.PlaneCellSize = 37.125; canvas.Grid.PlaneOrigin = new(-12.125, 143);
        canvas.Grid.FlatCellSize = 91.456; canvas.Grid.FlatOrigin = new(750, 500); return canvas;
    }
    private static async Task WritePackage(string path, int version, JsonObject scene)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var manifest = new JsonObject { ["Format"] = "FloatCanvas.project", ["Version"] = version, ["Payload"] = "scene.json", ["Assets"] = new JsonArray() };
        if (version >= 9) { manifest["CreatedWith"] = "0.3.9-fixture"; manifest["LastSavedWith"] = "0.3.9-fixture"; }
        using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open())) await writer.WriteAsync(manifest.ToJsonString());
        using (var writer = new StreamWriter(zip.CreateEntry("scene.json").Open())) await writer.WriteAsync(scene.ToJsonString());
    }
}
