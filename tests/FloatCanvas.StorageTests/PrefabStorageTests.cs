using FloatCanvas.Core;
using FloatCanvas.Storage;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class PrefabStorageTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "prefab-storage-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var assets = new AssetLibrary(); string imageId = assets.Add([17, 23, 42]); string draftImageId = assets.Add([56, 77, 89]);
        var source = new CanvasElement { Kind = ElementKind.Image, AssetId = imageId, Width = 30, Height = 50, Locked = true, Visible = false,
            Effects = [new() { Kind = ImageEffectKind.Blur, Bounds = new(.1, .1, .4, .4) }] };
        var definition = new PrefabDefinition { Name = "source", Graph = new() { Elements = [source] } };
        var inner = new PrefabInstance { DefinitionId = definition.Id, Transform = Affine2D.Translation(4, 7), ElevationScale = 2 };
        var entry = new PrefabDefinition { Name = "entry", Graph = new() { Instances = [inner], RootOrder = [inner.Id] } };
        var package = new PrefabPackage { EntryDefinitionId = entry.Id, Definitions = [entry, definition] };
        var packageSnapshot = package.Clone(); var prefabStore = new PrefabStore("0.4.0-m4-test"); var projectStore = new ProjectStore("0.4.0-m4-test");
        string prefabPath = Path.Combine(root, "nested.floatprefab"); string original = JsonSerializer.Serialize(package);
        check(!package.ResourceIdentityPersistent && !packageSnapshot.ResourceIdentityPersistent, "new prefab identity is not advertised as an accepted file identity");
        await prefabStore.SaveAsync(prefabPath, package, assets); var loaded = await prefabStore.LoadAsync(prefabPath);
        check(package.ResourceIdentityPersistent && packageSnapshot.ResourceIdentityPersistent && loaded.Package.ResourceIdentityPersistent &&
            JsonSerializer.Serialize(loaded.Package) == original && loaded.Assets.Resources.Select(resource => resource.Id).SequenceEqual([imageId]), "prefab package roundtrip preserves shared closure, source flags, bytes and stable accepted identity");
        using (var zip = ZipFile.OpenRead(prefabPath))
        {
            var manifest = await Read(zip, "manifest.json");
            check(manifest["Format"]!.GetValue<string>() == "FloatCanvas.prefab" && manifest["Payload"]!.GetValue<string>() == "prefab.json" &&
                zip.Entries.Count == 3 && loaded.Package.Provenance.LastSavedWith == "0.4.0-m4-test", "prefab has an explicit package kind and one asset payload independent of instance count");
        }
        byte[] saved = await File.ReadAllBytesAsync(prefabPath);
        await Reject(() => projectStore.LoadAsync(prefabPath), "prefab cannot be silently opened as a project camera");
        var invalid = package.Clone(); invalid.EntryDefinitionId = Guid.NewGuid();
        await Reject(() => prefabStore.SaveAsync(prefabPath, invalid, assets), "missing prefab entry cannot replace a valid resource");
        check((await File.ReadAllBytesAsync(prefabPath)).AsSpan().SequenceEqual(saved), "rejected prefab save preserves exact accepted bytes");
        var hugeLeaf = new CanvasElement { Kind = ElementKind.Rectangle, Width = 1e150, Height = 1, Transform = Affine2D.Scale(1e150, 1e150) };
        SceneValidation.Validate(new SceneDocument { Elements = [hugeLeaf] });
        var hugeDefinition = new PrefabDefinition { Graph = new() { Elements = [hugeLeaf] } };
        var hugeInstance = new PrefabInstance { DefinitionId = hugeDefinition.Id, Transform = Affine2D.Scale(1e150, 1e150) };
        var hugeEntry = new PrefabDefinition { Graph = new() { Instances = [hugeInstance], RootOrder = [hugeInstance.Id] } };
        await Reject(() => prefabStore.SaveAsync(prefabPath, new() { Definitions = [hugeDefinition, hugeEntry], EntryDefinitionId = hugeEntry.Id }, new()),
            "prefab entry rejects a nonfinite cumulative appearance even when each independent source graph has finite geometry");
        var conflict = package.Clone(); conflict.Definitions[0].Name = "changed";
        await Reject(() => prefabStore.SaveVersionedAsync(prefabPath, conflict, assets, expectedVersion: new ProjectFileVersion(new string('A', 64))), "prefab expected generation prevents overwriting a changed file");
        check((await File.ReadAllBytesAsync(prefabPath)).AsSpan().SequenceEqual(saved), "version conflict does not replace the prefab snapshot");
        var linked = package.Clone(); linked.Definitions[0].Source = new()
        {
            Locator = "../source/source.floatprefab", IsRelative = true, SourceResourceId = Guid.NewGuid(), EntryKind = PrefabSourceKind.PrefabDefinition,
            EntryDefinitionId = entry.Id, LastAcceptedFileSha256 = new string('C', 64), LastAcceptedContentRevision = entry.Revision,
            OriginClosure = [new(Guid.NewGuid(), PrefabSourceKind.ProjectContent)], ImportedDefinitionIds = new() { [entry.Id] = entry.Id, [definition.Id] = definition.Id }
        };
        string linkedPath = Path.Combine(root, "linked.floatprefab"); await prefabStore.SaveAsync(linkedPath, linked, assets);
        var linkedLoaded = (await prefabStore.LoadAsync(linkedPath)).Package; var detached = linkedLoaded.Clone();
        detached.Definitions[0].Source!.ImportedDefinitionIds[entry.Id] = Guid.NewGuid(); detached.Definitions[0].Source!.OriginClosure.Clear();
        check(linkedLoaded.Definitions[0].Source!.IsLinked && linkedLoaded.Definitions[0].Source!.ImportedDefinitionIds[entry.Id] == entry.Id &&
            linkedLoaded.Definitions[0].Source!.OriginClosure.Count == 1, "linked source metadata roundtrips and draft clones do not share mutable mapping or origin containers");
        var host = new SceneDocument { Definitions = package.Definitions.Select(item => item.Clone()).ToList() };
        var group = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, Name = "transformed", Transform = Affine2D.Translation(70, -23).Compose(Affine2D.Rotation(31)),
            ElevationScale = 2.5, Visible = false, Locked = true, ChildOrder = [] };
        new SceneGraph(host).InsertGroup(group, worldCoordinates: false);
        var instance = new SceneGraph(host).InsertInstance(new() { DefinitionId = entry.Id, Transform = Affine2D.Translation(23, 44) }, group.Id, worldCoordinates: false);
        string projectPath = Path.Combine(root, "shared.floatcanvas"); var hostSnapshot = host.Clone(); await projectStore.SaveAsync(projectPath, host, assets);
        var reopened = await projectStore.LoadAsync(projectPath);
        check(JsonSerializer.Serialize(reopened.Document) == JsonSerializer.Serialize(host) && hostSnapshot.ResourceIdentityPersistent && reopened.Document.ResourceIdentityPersistent,
            "project saves and history snapshots share persistent resource metadata without adding it to editable content");
        check(reopened.Document.Instances.Single().Id == instance.Id && reopened.Document.Elements.Count == 0 && reopened.Document.ReferencedAssets.SequenceEqual([imageId]),
            "project keeps instances real and retains hidden dependency assets");
        var runtime = SceneGraphClone.CloneGraph(SceneEvaluation.Build(host).RenderDocument).Graph;
        var runtimePackage = package.Clone(); runtimePackage.Definitions[1].Graph = runtime;
        await Reject(() => prefabStore.SaveAsync(prefabPath, runtimePackage, new()), "runtime base-typed definition is rejected before missing asset access or replacing prefab");
        var runtimeHost = host.Clone(); runtimeHost.Definitions[1].Graph = runtime;
        await Reject(() => projectStore.SaveAsync(projectPath, runtimeHost, new()), "runtime nested graph is rejected at project public save boundary");
        var draftDocument = new SceneDocument { Elements = [new() { Kind = ElementKind.Image, AssetId = draftImageId, Width = 20, Height = 20 }] };
        var parentDraft = new PrefabDraftState { OwnerResourceId = host.ResourceId, DefinitionId = entry.Id, ExpectedRevision = entry.Revision,
            DraftDocument = draftDocument, ReturnScopeId = group.Id, ReturnSelection = [instance.Id], DraftSelection = [draftDocument.Elements[0].Id] };
        var sourceDocument = draftDocument.Clone(); sourceDocument.ResourceId = Guid.NewGuid(); sourceDocument.Canvas.Grid.FlatCellSize = 31;
        var draft = new PrefabDraftState { OwnerResourceId = draftDocument.ResourceId, DefinitionId = entry.Id, ExpectedRevision = entry.Revision,
            DraftDocument = sourceDocument, IsSourceProject = true, SourcePath = "C:/source/original.floatcanvas", SourceFileSha256 = new string('B', 64), ParentEdit = parentDraft };
        var state = new WorkspaceState { LiveDocument = host, PendingPrefabEdit = draft };
        var recovery = new RecoveryStore(Path.Combine(root, "recovery"), "0.4.0-m4-test"); await recovery.SaveAsync(state, assets);
        var recovered = (await recovery.LoadAsync())!;
        check(JsonSerializer.Serialize(recovered.State) == JsonSerializer.Serialize(state) && recovered.Assets.Resources.Select(resource => resource.Id).Order().SequenceEqual(new[] { imageId, draftImageId }.Order()),
            "recovery retains nested edit source contexts and assets referenced only by an unfinished draft");
        check(recovered.State.PendingPrefabEdit!.IsSourceProject && recovered.State.PendingPrefabEdit.ParentEdit!.OwnerResourceId == host.ResourceId &&
            recovered.State.PendingPrefabEdit.DraftDocument.Canvas.Grid.FlatCellSize == 31, "source-project draft recovers its complete camera and parent editing chain");
        await recovery.SaveAsync(state, assets); byte[] recoveryCurrent = await File.ReadAllBytesAsync(recovery.CurrentPath), recoveryBackup = await File.ReadAllBytesAsync(recovery.BackupPath);
        var badState = state.Clone(); badState.PendingPrefabEdit!.ParentEdit!.DraftDocument.Definitions = [new() { Graph = runtime }];
        await Reject(() => recovery.SaveAsync(badState, new()), "runtime graph in an outer unfinished draft is rejected before assets and backup promotion");
        check((await File.ReadAllBytesAsync(recovery.CurrentPath)).AsSpan().SequenceEqual(recoveryCurrent) && (await File.ReadAllBytesAsync(recovery.BackupPath)).AsSpan().SequenceEqual(recoveryBackup),
            "invalid runtime draft leaves both recovery generations intact");
        var loop = state.Clone(); loop.PendingPrefabEdit!.ParentEdit = loop.PendingPrefabEdit;
        await Reject(() => recovery.SaveAsync(loop, assets), "cyclic edit recovery chain is rejected before serialization");
        PrefabDraftState? bounded = null;
        for (int i = 0; i < SceneLimits.MaximumGroupDepth; i++) bounded = new() { OwnerResourceId = host.ResourceId, DefinitionId = entry.Id, ExpectedRevision = entry.Revision, DraftDocument = new(), ParentEdit = bounded };
        var nestedState = new WorkspaceState { PendingPrefabEdit = bounded }; var nestedRecovery = new RecoveryStore(Path.Combine(root, "nested64"));
        await nestedRecovery.SaveAsync(nestedState, new());
        check(PrefabDraftState.Enumerate((await nestedRecovery.LoadAsync())!.State.PendingPrefabEdit).Count == 64, "bounded64 editing contexts serialize and recover within the finite workspace envelope");
        nestedState.PendingPrefabEdit = new() { OwnerResourceId = host.ResourceId, DefinitionId = entry.Id, ExpectedRevision = entry.Revision, ParentEdit = bounded };
        await Reject(() => nestedRecovery.SaveAsync(nestedState, new()), "65 nested editing contexts are rejected before unbounded recursive serialization");
        foreach (var change in new (string Name, Action<JsonObject> Mutate)[]
        {
            ("missing-resource-id", json => json.Remove("ResourceId")),
            ("missing-instances", json => json.Remove("Instances")),
            ("missing-definitions", json => json.Remove("Definitions")),
            ("missing-definition-revision", json => json["Definitions"]![0]!.AsObject().Remove("Revision")),
            ("missing-source-element-id", json => json["Definitions"]![1]!["Graph"]!["Elements"]![0]!.AsObject().Remove("Id")),
            ("missing-instance-definition", json => json["Instances"]![0]!["DefinitionId"] = Guid.NewGuid()),
            ("missing-instance-transform", json => json["Instances"]![0]!.AsObject().Remove("Transform")),
            ("definition-camera", json => json["Definitions"]![0]!["Graph"]!["Canvas"] = JsonSerializer.SerializeToNode(new CanvasSettings())),
            ("future", json => json["Version"] = SceneDocument.CurrentVersion + 1),
            ("invalid-version-type", json => json["Version"] = "eleven")
        })
        {
            string path = Path.Combine(root, change.Name + ".floatcanvas"); File.Copy(projectPath, path);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update)) { var json = await Read(zip, "scene.json"); change.Mutate(json); await Replace(zip, "scene.json", json); }
            await Reject(() => projectStore.LoadAsync(path), $"v11 malformed {change.Name} cannot silently discard source nodes");
        }
        await PublishedGroups(check, projectStore, root);
        var sharedPath = new CanvasElement { Kind = ElementKind.Freehand, Points = new(Enumerable.Range(0, 1000).Select(i => new PointD(i, i % 17))) };
        var sharedDefinition = new PrefabDefinition { Graph = new() { Elements = [sharedPath] } };
        var shared = new SceneDocument { Definitions = [sharedDefinition] };
        shared.Instances = Enumerable.Range(0, 1000).Select(i => new PrefabInstance { DefinitionId = sharedDefinition.Id, Transform = Affine2D.Translation(i, 0) }).ToList();
        shared.RootOrder = shared.Instances.Select(item => item.Id).ToList();
        string scalePath = Path.Combine(root, "thousand-shared.floatcanvas"); var watch = System.Diagnostics.Stopwatch.StartNew();
        await projectStore.SaveAsync(scalePath, shared, new()); var scaleLoaded = (await projectStore.LoadAsync(scalePath)).Document;
        check(scaleLoaded.Elements.Count == 0 && scaleLoaded.Instances.Count == 1000 && scaleLoaded.Definitions.Single().Graph.Elements.Single().Points.Count == 1000 &&
            PrefabClosure.Validate(scaleLoaded).ExpandedPathPoints == 1_000_000, "1000 placements survive a real package save/load with one1000-point source path");
        Console.WriteLine($"PREFAB_PACKAGE instances=1000 source_points=1000 expanded_points=1000000 package_bytes={new FileInfo(scalePath).Length} save_load_ms={watch.Elapsed.TotalMilliseconds:F3}");
        Console.WriteLine($"Prefab storage artifacts: {root}");

        async Task Reject(Func<Task> action, string message)
        {
            bool rejected = false; try { await action(); } catch (Exception ex) when (ex is InvalidDataException or IOException) { rejected = true; }
            check(rejected, message);
        }
    }
    private static async Task PublishedGroups(Action<bool, string> check, ProjectStore store, string root)
    {
        var outer = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, Transform = Affine2D.Translation(33, 44).Compose(Affine2D.Rotation(27)),
            ElevationScale = -2, Visible = false, Locked = true, ChildOrder = [] };
        var empty = new CanvasGroup { ParentId = outer.Id, OrderMode = GroupOrderMode.AtomicBlock, Transform = Affine2D.Scale(2, 3), ChildOrder = [] };
        var leaf = new CanvasElement { Kind = ElementKind.Rectangle, GroupId = outer.Id, Width = 20, Height = 30, Transform = Affine2D.Translation(5, 7) };
        outer.ChildOrder = [empty.Id, leaf.Id];
        var document = new SceneDocument { Groups = [outer, empty], Elements = [leaf], RootOrder = [outer.Id] };
        document.Canvas.Grid.FlatCellSize = 29; document.Canvas.Grid.PlaneCellSize = 17; document.Canvas.Grid.PlaneOrigin = new(3, 7);
        var json = JsonSerializer.SerializeToNode(document)!.AsObject(); json["Version"] = 19; LegacyFixtureSchema.UseForegroundBackground(json);
        string path = Path.Combine(root, "released-v19.floatcanvas");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var manifest = new JsonObject { ["Format"] = "FloatCanvas.project", ["Version"] = 19, ["Payload"] = "scene.json", ["CreatedWith"] = "0.4.0", ["LastSavedWith"] = "0.4.0", ["Assets"] = new JsonArray() };
            using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open())) await writer.WriteAsync(manifest.ToJsonString());
            using (var writer = new StreamWriter(zip.CreateEntry("scene.json").Open())) await writer.WriteAsync(json.ToJsonString());
        }
        byte[] original = await File.ReadAllBytesAsync(path); var loaded = (await store.LoadAsync(path)).Document;
        check(loaded.ResourceIdentityPersistent && loaded.ResourceId == document.ResourceId && loaded.Version == SceneDocument.CurrentVersion && loaded.Groups[0].Transform == outer.Transform &&
            loaded.Groups[0].ElevationScale == -2 && !loaded.Groups[0].Visible && loaded.Groups[0].Locked && loaded.Groups[0].OrderMode == GroupOrderMode.AtomicBlock &&
            loaded.Groups[0].ChildOrder!.SequenceEqual([empty.Id, leaf.Id]) && loaded.RootOrder!.SequenceEqual([outer.Id]) && loaded.Groups[1].ChildOrder is { Count: 0 },
            "released v19 preserves local matrices, flags, empty atomic slots, source identity and order");
        check(JsonSerializer.Serialize(loaded.Canvas) == JsonSerializer.Serialize(document.Canvas) && loaded.Provenance.CreatedWith == "0.4.0" &&
            (await File.ReadAllBytesAsync(path)).AsSpan().SequenceEqual(original), "released v19 retains independent plane/grid, provenance and original bytes intact");
        var snapshot = loaded.Clone(); await store.SaveAsync(path, loaded, new()); var reopened = (await store.LoadAsync(path)).Document;
        check(snapshot.ResourceIdentityPersistent && loaded.ResourceIdentityPersistent && reopened.ResourceIdentityPersistent && reopened.ResourceId == loaded.ResourceId,
            "resave retains the stable identity shared by undo snapshots");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var manifest = await Read(zip, "manifest.json"); manifest["Version"] = 10; await Replace(zip, "manifest.json", manifest);
            var scene = await Read(zip, "scene.json"); scene["Version"] = 10; await Replace(zip, "scene.json", scene);
        }
        bool rejected = false; try { await store.LoadAsync(path); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "legacy envelope carrying v11 prefab fields is explicitly rejected instead of dropping them");
    }
    private static async Task<JsonObject> Read(ZipArchive zip, string entry)
    {
        using var reader = new StreamReader(zip.GetEntry(entry)!.Open()); return JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
    }
    private static async Task Replace(ZipArchive zip, string entry, JsonObject value)
    {
        zip.GetEntry(entry)!.Delete(); using var writer = new StreamWriter(zip.CreateEntry(entry).Open()); await writer.WriteAsync(value.ToJsonString());
    }
}
