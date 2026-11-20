using FloatCanvas.Core;
using FloatCanvas.Storage;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class SubprojectCanvasStorageTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "subproject-canvases", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var assets = new AssetLibrary(); string backdrop = assets.Add([53, 17, 91]);
        var preview = new SubprojectCanvasSettings
        {
            Origin = new(31, -17), Viewport = new(-80, 25, 960, 540), LayerOpacity = .63,
            Background = new() { Kind = ElementKind.Image, AssetId = backdrop, Width = 80, Height = 60 }
        };
        preview.Canvas.BackgroundColor = 0xFF224466; preview.Canvas.Grid.Visible = true;
        preview.Canvas.Grid.FlatCellSize = 37; preview.Canvas.Projection.PlaneBounds = new(-120, 80, 600, 400);
        var leaf = new CanvasElement { Kind = ElementKind.Rectangle, Width = 70, Height = 30 };
        var group = new CanvasGroup { Name = "local group", OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [leaf.Id], PreviewCanvas = preview.Clone() };
        leaf.GroupId = group.Id;
        var definition = new PrefabDefinition { Name = "preview", PreviewCanvas = preview.Clone(),
            Graph = new() { Elements = [leaf], Groups = [group], RootOrder = [group.Id] } };
        var package = new PrefabPackage { EntryDefinitionId = definition.Id, Definitions = [definition] };
        var prefabs = new PrefabStore(); string path = Path.Combine(root, "preview.floatprefab");
        await prefabs.SaveAsync(path, package, assets); var loaded = await prefabs.LoadAsync(path);
        check(JsonSerializer.Serialize(loaded.Package) == JsonSerializer.Serialize(package) && loaded.Assets.Count == 1 && loaded.Assets.TryGet(backdrop, out _),
            "v17 prefab roundtrips its independent canvas and assets referenced only by preview backgrounds");
        var copy = definition.Clone(new Dictionary<string, string> { [backdrop] = new string('A', 64) });
        check(copy.PreviewCanvas!.Background!.AssetId == new string('A', 64) &&
            copy.Graph.Groups[0].PreviewCanvas!.Background!.AssetId == new string('A', 64) && definition.PreviewCanvas!.Background!.AssetId == backdrop,
            "preview asset references are remapped in both definition and nested group clones without mutating source");
        var document = new SceneDocument { RootPreviewCanvases = new() { [RootCanvasKind.Foreground] = preview.Clone(), [RootCanvasKind.Background] = preview.Clone() } };
        var projects = new ProjectStore(); string projectPath = Path.Combine(root, "roots.floatcanvas");
        await projects.SaveAsync(projectPath, document, assets); var project = await projects.LoadAsync(projectPath);
        check(JsonSerializer.Serialize(project.Document) == JsonSerializer.Serialize(document) && project.Assets.TryGet(backdrop, out _),
            "v17 full project stores sparse foreground/background preview headers and their backdrop resources");
        var workspace = new WorkspaceState { LiveDocument = document.Clone() };
        workspace.PendingPrefabEdit = new() { OwnerResourceId = document.ResourceId, DefinitionId = definition.Id, ExpectedRevision = definition.Revision,
            DraftDocument = new(), PreviewOrigin = new(71, -29) };
        var recovery = new RecoveryStore(Path.Combine(root, "recovery")); await recovery.SaveAsync(workspace, assets);
        check(JsonSerializer.Serialize((await recovery.LoadAsync())!.State) == JsonSerializer.Serialize(workspace), "recovery retains independent root preview headers");
        check(workspace.Clone().PendingPrefabEdit!.PreviewOrigin == new PointD(71, -29), "detached entry origin survives its recovery envelope and clone");
        var mirrored = preview.Rebase(new(Affine2D.Scale(-1, 1), 1), new(0, 0, 80, 60));
        var mirroredPackage = package.Clone(); mirroredPackage.Definitions.Single().PreviewCanvas = mirrored;
        string mirroredPath = Path.Combine(root, "mirrored.floatprefab"); await prefabs.SaveAsync(mirroredPath, mirroredPackage, assets);
        check(JsonSerializer.Serialize((await prefabs.LoadAsync(mirroredPath)).Package) == JsonSerializer.Serialize(mirroredPackage),
            "v17 independent previews retain a mirrored author basis without altering the reusable graph");
        var mirroredDraft = new SceneDocument(); mirrored.ApplyTo(mirroredDraft);
        var mirroredRecovery = workspace.Clone(); mirroredRecovery.PendingPrefabEdit!.DraftDocument = mirroredDraft;
        await recovery.SaveAsync(mirroredRecovery, assets);
        check(JsonSerializer.Serialize((await recovery.LoadAsync())!.State) == JsonSerializer.Serialize(mirroredRecovery),
            "a detached mirrored preview survives recovery using its draft envelope");
        Reject(() => SceneValidation.Validate(mirroredDraft), "preview mirror support does not relax the regular project calibration contract");

        var invalid = document.Clone(); invalid.RootPreviewCanvases[RootCanvasKind.Main] = preview.Clone();
        Reject(() => SceneValidation.Validate(invalid), "Main cannot have a second conflicting preview authority");
        invalid = document.Clone(); invalid.RootPreviewCanvases[RootCanvasKind.Foreground].Origin = new(double.NaN, 0);
        Reject(() => SceneValidation.Validate(invalid), "preview root origins must be finite");
        invalid = document.Clone(); invalid.RootPreviewCanvases[RootCanvasKind.Foreground].Canvas.Grid.Width = -1;
        Reject(() => SceneValidation.Validate(invalid), "preview canvases use the same validated canvas contract as root projects");
        invalid = document.Clone(); invalid.RootPreviewCanvases[RootCanvasKind.Foreground].Background!.WindowSource = new();
        Reject(() => SceneValidation.Validate(invalid), "preview backgrounds cannot silently bind live windows");
        var invalidPackage = package.Clone(); invalidPackage.Definitions[0].Graph.Groups[0].PreviewCanvas!.Viewport = new(0, 0, 0, 20);
        Reject(() => SceneValidation.Validate(invalidPackage), "nested group preview headers receive complete geometry validation");

        var historicalDefinition = new PrefabDefinition { Graph = new() { Elements = [new() { Kind = ElementKind.Rectangle, Width = 15, Height = 20 }] } };
        var historicalPackage = new PrefabPackage { EntryDefinitionId = historicalDefinition.Id, Definitions = [historicalDefinition] };
        var oldPrefab = JsonSerializer.SerializeToNode(historicalPackage)!.AsObject(); oldPrefab["Version"] = 19;
        LegacyFixtureSchema.UseForegroundBackground(oldPrefab);
        string historicalPrefab = Path.Combine(root, "v19.floatprefab"); await Write(historicalPrefab, "prefab", "prefab.json", oldPrefab);
        byte[] original = await File.ReadAllBytesAsync(historicalPrefab);
        var migratedPrefab = await prefabs.LoadAsync(historicalPrefab);
        check(migratedPrefab.Package.Version == SceneDocument.CurrentVersion && migratedPrefab.Package.Definitions.Single().PreviewCanvas is null &&
            (await File.ReadAllBytesAsync(historicalPrefab)).SequenceEqual(original),
            "published prefab import preserves its neutral preview and source bytes");

        void Reject(Action action, string message) { bool rejected = false; try { action(); } catch (InvalidDataException) { rejected = true; } check(rejected, message); }
    }
    private static async Task Write(string path, string kind, string payload, JsonObject value)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var manifest = new JsonObject { ["Format"] = "FloatCanvas." + kind, ["Version"] = value["Version"]!.DeepClone(), ["Payload"] = payload,
            ["CreatedWith"] = "0.4.0", ["LastSavedWith"] = "0.4.0", ["Assets"] = new JsonArray() };
        using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open())) await writer.WriteAsync(manifest.ToJsonString());
        using (var writer = new StreamWriter(zip.CreateEntry(payload).Open())) await writer.WriteAsync(value.ToJsonString());
    }
}
