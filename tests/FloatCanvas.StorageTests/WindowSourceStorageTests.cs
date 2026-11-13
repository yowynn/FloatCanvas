using FloatCanvas.Core;
using FloatCanvas.Storage;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class WindowSourceStorageTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "window-source-storage", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var store = new ProjectStore("0.4.0-m7-test"); var prefabStore = new PrefabStore("0.4.0-m7-test");
        var assets = new AssetLibrary(); string fallback = assets.Add(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg=="));
        var leaf = Live(fallback); leaf.WindowSource!.Fit = WindowSourceFit.Stretch; leaf.ImageCrop = new(.1, .2, .7, .6); leaf.ImageFrame = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        leaf.Effects = [new() { Kind = ImageEffectKind.Blur, Bounds = new(.1, .1, .2, .2) }];
        var group = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [leaf.Id], Transform = Affine2D.Translation(17, 29), ElevationScale = -2 };
        leaf.GroupId = group.Id;
        var definition = new PrefabDefinition { Graph = new() { Elements = [leaf], Groups = [group], RootOrder = [group.Id] } };
        var instance = new PrefabInstance { DefinitionId = definition.Id, Transform = Affine2D.Rotation(17) };
        var source = Live();
        var document = new SceneDocument { Elements = [source], Definitions = [definition], Instances = [instance], RootOrder = [source.Id, instance.Id] };
        var presentation = new CanvasElement { Kind = ElementKind.Presentation, Width = 360, Height = 180, Presentation = new() { Enabled = false, Modules = PresentationModules.None } };
        document.Elements.Add(presentation); document.RootOrder.Add(presentation.Id);
        document.Canvas.Grid.PlaneCellSize = 17; document.Canvas.Grid.PlaneOrigin = new(3, 7);
        document.Canvas.Projection.PlaneBounds = new(5, 8, 1200, 800);
        string before = JsonSerializer.Serialize(document), path = Path.Combine(root, "live.floatcanvas");
        await store.SaveAsync(path, document, assets); var loaded = await store.LoadAsync(path);
        check(SceneDocument.CurrentVersion >= 17 && JsonSerializer.Serialize(loaded.Document) == before && loaded.Assets.Count == 1,
            "format14 preserves host/definition window descriptors, nullable first fallback, crop/effects/frame/group and presentation source values");
        check(document.Elements[0].AssetId is null && document.Version == SceneDocument.CurrentVersion && loaded.Document.ResourceIdentityPersistent,
            "saving offline live images never inserts an active fallback and retains persistent owner identity");
        var package = new PrefabPackage { Definitions = [definition.Clone()], EntryDefinitionId = definition.Id };
        string prefab = Path.Combine(root, "live.floatprefab"); await prefabStore.SaveAsync(prefab, package, assets);
        check(JsonSerializer.Serialize((await prefabStore.LoadAsync(prefab)).Package) == JsonSerializer.Serialize(package),
            "format14 prefab roundtrip retains the same authored binding intent and static source-frame fallback");
        var capture = document.Clone(); capture.Kind = DocumentKind.FrozenCapture;
        var pendingParent = new PrefabDraftState { OwnerResourceId = document.ResourceId, DefinitionId = definition.Id, ExpectedRevision = definition.Revision, DraftDocument = document.Clone() };
        var workspace = new WorkspaceState { LiveDocument = document, CaptureDocument = capture,
            PendingPrefabEdit = new() { OwnerResourceId = document.ResourceId, DefinitionId = definition.Id, ExpectedRevision = definition.Revision, DraftDocument = document.Clone(), ParentEdit = pendingParent } };
        var recovery = new RecoveryStore(Path.Combine(root, "recovery")); await recovery.SaveAsync(workspace, assets);
        check(JsonSerializer.Serialize((await recovery.LoadAsync())!.State) == JsonSerializer.Serialize(workspace),
            "format14 recovery includes live/capture and every nested definition draft descriptor without runtime state");

        foreach (var mutation in new (string Name, Action<JsonObject> Edit)[] {
            ("intent-empty", n => Source(n)["BindingIntent"] = Guid.Empty.ToString()), ("intent-missing", n => Source(n).Remove("BindingIntent")),
            ("intent-invalid", n => Source(n)["BindingIntent"] = "not-a-guid"), ("fit-unknown", n => Source(n)["Fit"] = 2),
            ("fit-string", n => Source(n)["Fit"] = "Contain"), ("null-title", n => Source(n)["TitleHint"] = null),
            ("title-over", n => Source(n)["TitleHint"] = new string('t', 513)), ("process-over", n => Source(n)["ProcessNameHint"] = new string('p', 261)),
            ("class-over", n => Source(n)["WindowClassHint"] = new string('c', 257)), ("control-hint", n => Source(n)["TitleHint"] = "Title\tName"),
            ("hwnd-injection", n => Source(n)["Hwnd"] = 123), ("runtime-injection", n => Source(n)["ConnectionVersion"] = 12),
            ("wrong-kind", n => Leaf(n)["Kind"] = (int)ElementKind.Rectangle), ("zero-width", n => Leaf(n)["Width"] = 0),
            ("negative-height", n => Leaf(n)["Height"] = -3), ("invalid-asset", n => Leaf(n)["AssetId"] = "bad"),
            ("invalid-crop", n => Leaf(n)["ImageCrop"]!["Width"] = 0), ("invalid-effect", n => Leaf(n)["Effects"]![0]!["Amount"] = 0),
            ("background-source", n => n["Background"] = Leaf(n).DeepClone()) })
        {
            string invalid = Path.Combine(root, mutation.Name + ".floatcanvas"); File.Copy(path, invalid); await Edit(invalid, "scene.json", mutation.Edit);
            await Reject(() => store.LoadAsync(invalid), check, "format14 rejects invalid nested live-image content: " + mutation.Name);
        }
        foreach (string field in new[] { "BindingIntent", "TitleHint", "ProcessNameHint", "WindowClassHint", "Fit" })
        {
            string invalid = Path.Combine(root, "missing-" + field + ".floatcanvas"); File.Copy(path, invalid); await Edit(invalid, "scene.json", n => Source(n).Remove(field));
            await Reject(() => store.LoadAsync(invalid), check, "format14 descriptor fields are required rather than inferred: " + field);
        }
        var plain = new SceneDocument { Elements = [new() { Kind = ElementKind.Image, Width = 20, Height = 30 }] };
        await Reject(() => store.SaveAsync(Path.Combine(root, "plain-missing.floatcanvas"), plain, assets), check, "ordinary images still require an asset reference");
        var badBackground = document.Clone(); badBackground.Background = Live(fallback);
        await Reject(() => store.SaveAsync(Path.Combine(root, "typed-background.floatcanvas"), badBackground, assets), check, "typed save refuses live background sources before writing a package");

        // A valid older envelope differs only in fields already documented by that schema.
        // Assert the exact new-field guard so an unrelated old-schema rejection cannot make this pass.
        var legacyRoot = new SceneDocument { Elements = [Live(fallback)] };
        string legacyTemplate = Path.Combine(root, "legacy-template.floatcanvas"); await store.SaveAsync(legacyTemplate, legacyRoot, assets);
        for (int version = 1; version <= 13; version++)
        {
            string invalid = Path.Combine(root, "old-" + version + ".floatcanvas"); File.Copy(legacyTemplate, invalid);
            await Edit(invalid, "manifest.json", n => n["Version"] = version);
            await Edit(invalid, "scene.json", n => { n["Version"] = version; if (version < 12) LegacyFixtureSchema.UseLegacyAppearance(n);
                if (version < 11) { n.Remove("ResourceId"); n.Remove("Definitions"); n.Remove("Instances"); }
                if (version < 10) { n.Remove("RootOrder"); n["Groups"] = new JsonArray(); }
                if (version is 6 or 7) n["Canvas"]!["Projection"]!["LockedGridCellSize"] = null; });
            bool exactGuard = false; try { await store.LoadAsync(invalid); } catch (InvalidDataException e) { exactGuard = e.Message.Contains("Window sources require scene format 14.", StringComparison.Ordinal); }
            check(exactGuard, "legacy source field is explicitly rejected before migration in version " + version);
        }
        string oldNull = Path.Combine(root, "old13-null-source.floatcanvas"); File.Copy(legacyTemplate, oldNull);
        await Edit(oldNull, "manifest.json", n => n["Version"] = 13);
        await Edit(oldNull, "scene.json", n => { n["Version"] = 13; n["Elements"]![0]!["WindowSource"] = null; });
        await Reject(() => store.LoadAsync(oldNull), check, "old schemas reject an explicit null future source field too");
        for (int version = 11; version <= 13; version++)
        {
            string invalid = Path.Combine(root, "old-prefab-" + version + ".floatprefab"); File.Copy(prefab, invalid);
            await Edit(invalid, "manifest.json", n => n["Version"] = version);
            await Edit(invalid, "prefab.json", n => { n["Version"] = version; if (version < 12) foreach (var def in n["Definitions"]!.AsArray()) LegacyFixtureSchema.UseLegacyAppearance(def!["Graph"]!.AsObject()); });
            await Reject(() => prefabStore.LoadAsync(invalid), check, "old prefab envelopes cannot hide live source content in version " + version);
        }
        foreach (string target in new[] { "LiveDocument", "CaptureDocument", "DraftDocument", "ParentDraftDocument" })
        {
            string directory = Path.Combine(root, "legacy-recovery-" + target); Directory.CreateDirectory(directory);
            string invalid = Path.Combine(directory, "workspace.floatcanvas"); File.Copy(recovery.CurrentPath, invalid);
            await Edit(invalid, "manifest.json", n => n["Version"] = 13);
            await Edit(invalid, "workspace.json", n => {
                var docs = new[] { n["LiveDocument"]!.AsObject(), n["CaptureDocument"]!.AsObject(), n["PendingPrefabEdit"]!["DraftDocument"]!.AsObject(), n["PendingPrefabEdit"]!["ParentEdit"]!["DraftDocument"]!.AsObject() };
                foreach (var doc in docs) { doc["Version"] = 13; ClearSources(doc); }
                int index = Array.IndexOf(new[] { "LiveDocument", "CaptureDocument", "DraftDocument", "ParentDraftDocument" }, target);
                Leaf(docs[index])["WindowSource"] = JsonSerializer.SerializeToNode(leaf.WindowSource);
            });
            await Reject(() => new RecoveryStore(directory).LoadAsync(), check, "recovery schema guards every document and parent draft: " + target);
        }
        foreach (int version in new[] { 1, 9, 10, 11, 12, 13 })
        {
            var old = legacyRoot.Clone(); old.Version = version;
            await Reject(() => store.SaveAsync(Path.Combine(root, "typed-old-" + version + ".floatcanvas"), old, assets), check, "typed legacy save cannot silently upgrade hidden live content version " + version);
        }

        var oldDocument = document.Clone(); ClearTypedSources(oldDocument);
        string true13 = Path.Combine(root, "true-v13.floatcanvas"); await store.SaveAsync(true13, oldDocument, assets);
        await Edit(true13, "manifest.json", n => n["Version"] = 13); await Edit(true13, "scene.json", n => n["Version"] = 13);
        byte[] sourceBytes = await File.ReadAllBytesAsync(true13); var migrated = (await store.LoadAsync(true13)).Document;
        byte[] sourceBytesAfter = await File.ReadAllBytesAsync(true13);
        check(migrated.Version == SceneDocument.CurrentVersion && migrated.ResourceId == oldDocument.ResourceId && migrated.ResourceIdentityPersistent &&
            JsonSerializer.Serialize(migrated) == JsonSerializer.Serialize(oldDocument) && sourceBytes.AsSpan().SequenceEqual(sourceBytesAfter),
            "explicit 13-to14 migration preserves presentation, canonical palette, local transforms, elevation, grid, identity and source file bytes");
        var oldPackage = new PrefabPackage { Definitions = oldDocument.Definitions.Select(d => d.Clone()).ToList(), EntryDefinitionId = definition.Id };
        string truePrefab13 = Path.Combine(root, "true-v13.floatprefab"); await prefabStore.SaveAsync(truePrefab13, oldPackage, assets);
        await Edit(truePrefab13, "manifest.json", n => n["Version"] = 13); await Edit(truePrefab13, "prefab.json", n => n["Version"] = 13);
        check(JsonSerializer.Serialize((await prefabStore.LoadAsync(truePrefab13)).Package) == JsonSerializer.Serialize(oldPackage), "explicit v13 prefab migration preserves complete definition metadata and graph");

        var runtime = SceneEvaluation.Build(document).RenderDocument;
        await Reject(() => store.SaveAsync(Path.Combine(root, "runtime.floatcanvas"), runtime, assets), check, "project runtime marker remains an early save rejection");
        var runtimePackage = package.Clone(); runtimePackage.Definitions[0].Graph = runtime;
        await Reject(() => prefabStore.SaveAsync(Path.Combine(root, "runtime.floatprefab"), runtimePackage, assets), check, "prefab runtime graph marker cannot be washed into a source definition");
        string packageBefore = JsonSerializer.Serialize(document); var frames = new AssetLibrary(); string newFrame = frames.Add([31, 32, 33]);
        var plan = WindowSourceFallbackPlanner.Prepare(document, new Dictionary<SourceNodeAddress, AssetResource> { [new(null, source.Id)] = frames.Get(newFrame) }, assets.Resources);
        var local = new AssetLibrary(); foreach (var resource in plan.Resources) local.Add(resource);
        string planned = Path.Combine(root, "planned.floatcanvas"); await store.SaveAsync(planned, plan.Document, local);
        var plannedLoad = await store.LoadAsync(planned);
        check(plannedLoad.Document.Elements[0].AssetId == newFrame && JsonSerializer.Serialize(document) == packageBefore && assets.Count == 1 &&
            AuthoredSceneContent.Fingerprint(document) == AuthoredSceneContent.Fingerprint(plannedLoad.Document), "package-only frame snapshot survives strict package validation without active writes or authored hash changes");
        Console.WriteLine("Window source storage artifacts: " + root);
    }

    private static CanvasElement Live(string? asset = null) => new() { Kind = ElementKind.Image, Width = 320, Height = 180, AssetId = asset,
        WindowSource = new() { BindingIntent = Guid.NewGuid(), TitleHint = "Window", ProcessNameHint = "program", WindowClassHint = "Class" } };
    private static JsonObject Leaf(JsonObject json) => json["Definitions"]![0]!["Graph"]!["Elements"]![0]!.AsObject();
    private static JsonObject Source(JsonObject json) => Leaf(json)["WindowSource"]!.AsObject();
    private static void ClearSources(JsonObject document)
    {
        foreach (var element in document["Elements"]!.AsArray()) { element!.AsObject().Remove("WindowSource"); if (element["Kind"]!.GetValue<int>() == (int)ElementKind.Image) element["AssetId"] ??= new string('a', 64); }
        foreach (var definition in document["Definitions"]!.AsArray()) foreach (var element in definition!["Graph"]!["Elements"]!.AsArray()) element!.AsObject().Remove("WindowSource");
    }
    private static void ClearTypedSources(SceneDocument document)
    {
        foreach (var element in document.Elements.Concat(document.Definitions.SelectMany(d => d.Graph.Elements)))
            if (element.WindowSource is not null) { element.WindowSource = null; element.AssetId ??= document.Definitions[0].Graph.Elements[0].AssetId; }
    }
    private static async Task Reject(Func<Task> action, Action<bool, string> check, string message)
    {
        bool rejected = false; try { await action(); } catch (InvalidDataException) { rejected = true; } check(rejected, message);
    }
    private static async Task Edit(string path, string entryName, Action<JsonObject> edit)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update); var entry = zip.GetEntry(entryName)!; JsonObject json;
        using (var reader = new StreamReader(entry.Open())) json = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
        edit(json); LegacyFixtureSchema.RemoveFutureRootFields(json); entry.Delete(); using var writer = new StreamWriter(zip.CreateEntry(entryName).Open()); await writer.WriteAsync(json.ToJsonString());
    }
}
