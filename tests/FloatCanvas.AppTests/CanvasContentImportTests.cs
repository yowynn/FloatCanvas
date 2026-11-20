using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using SkiaSharp;

internal static class CanvasContentImportTests
{
    internal static async Task RunAsync(Action<string, bool> check, string? output = null)
    {
        string root = Path.Combine(output ?? Path.GetTempPath(), "canvas-content-import-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var png = Png(); string imagePath = Path.Combine(root, "picture.png"); await File.WriteAllBytesAsync(imagePath, png);
        var sourceAssets = new AssetLibrary(); string asset = sourceAssets.Add(png);
        var sourceDocument = new SceneDocument
        {
            Elements = [new() { Kind = ElementKind.Rectangle, Name = "project rectangle", Width = 60, Height = 40, Transform = Affine2D.Translation(160, 90) }],
            Background = new() { Kind = ElementKind.Image, AssetId = asset, Width = 80, Height = 60, Transform = Affine2D.Translation(150, 80) }, Viewport = new(0, 0, 640, 480)
        };
        var foreground = new CanvasElement { Kind = ElementKind.Text, Text = "foreground omitted", Width = 90, Height = 30 };
        var backgroundLayer = new CanvasElement { Kind = ElementKind.Ellipse, Name = "background omitted", Width = 80, Height = 40 };
        sourceDocument.Elements.AddRange([foreground, backgroundLayer]);
        sourceDocument.RootCanvasAssignments[foreground.Id] = RootCanvasKind.Foreground;
        sourceDocument.RootCanvasAssignments[backgroundLayer.Id] = RootCanvasKind.Background;
        sourceDocument.Canvas.BackgroundColor = 0xFF228866;
        var projects = new ProjectStore("0.4.0"); string projectPath = Path.Combine(root, "画布A.floatcanvas");
        await projects.SaveAsync(projectPath, sourceDocument, sourceAssets);
        var child = new PrefabDefinition { Name = "nested original", Graph = new() { Elements = [new() { Kind = ElementKind.Ellipse, Width = 30, Height = 20 }] } };
        var nested = new PrefabInstance { DefinitionId = child.Id };
        var entry = new PrefabDefinition { Name = "stored definition name", Graph = new() { Instances = [nested], RootOrder = [nested.Id] } };
        var package = new PrefabPackage { EntryDefinitionId = entry.Id, Definitions = [entry, child] };
        string prefabPath = Path.Combine(root, "预制体A.floatprefab"); var prefabs = new PrefabStore("0.4.0"); await prefabs.SaveAsync(prefabPath, package, sourceAssets);
        var fileHashes = new[] { projectPath, prefabPath, imagePath }.ToDictionary(path => path, Hash);

        var old = new CanvasElement { Kind = ElementKind.Rectangle, Name = "old member", Width = 20, Height = 20 };
        var owner = new EditorSession(new() { Elements = [old], Viewport = new(0, 0, 1000, 800), LayerOpacity = .8, NextNumber = 9 });
        owner.SetSelection([old.Id]); Guid group = owner.GroupSelection("host group")!.Value;
        owner.Document.Groups.Single().Transform = Affine2D.Translation(45, 30).Compose(Affine2D.Rotation(17)); owner.SetEditScope(group);
        string before = JsonSerializer.Serialize(owner.Document); int beforeUndo = owner.UndoCount, beforeAssets = owner.Assets.Count; long beforeRevision = owner.SceneRevision;
        var prepared = await CanvasContentImport.PrepareAsync(owner, [projectPath, imagePath, prefabPath], [AssetImport.Normalize(png)],
            new(0, 0, 1000, 800), new(400, 300), .65, null, CancellationToken.None);
        check("mixed file preparation leaves author document assets history and selection unchanged", JsonSerializer.Serialize(owner.Document) == before &&
            owner.UndoCount == beforeUndo && owner.Assets.Count == beforeAssets && owner.SceneRevision == beforeRevision && owner.Selection.Count == 0);
        check("prepared batch retains both linked project types and ordinary images", prepared.HasProjects && prepared.ProjectCount == 2 && prepared.ImageCount == 2 && prepared.NodeCount == 4 && prepared.IsCurrent(owner));
        check("mixed batch commits successfully once", prepared.TryCommit(owner) && owner.UndoCount == beforeUndo + 1 && owner.Selection.SetEquals(prepared.SelectedIds));
        var graph = new SceneGraph(owner.Document);
        check("every placed root is a direct child of the captured edit scope", prepared.SelectedIds.All(id => graph.ParentId(id) == group) && owner.EditScopeId == group);
        check("file names become instance names without changing shared definition names", owner.Document.Instances.Select(instance => instance.Name).ToHashSet().SetEquals(["画布A", "预制体A"]) &&
            owner.Document.Definitions.Any(definition => definition.Name == "stored definition name"));
        check("project and prefab sources retain explicit disk links and accepted file identities", owner.Document.Definitions.Where(definition => definition.Source?.IsLinked == true).Count() == 2 &&
            owner.Document.Definitions.Where(definition => definition.Source?.IsLinked == true).All(definition => definition.Source!.Locator is not null && !definition.Source.IsRelative && definition.Source.LastAcceptedFileSha256 == Hash(definition.Source.Locator!)));
        var placedProject = owner.Document.Instances.Single(instance => instance.Name == "画布A"); var placedPrefab = owner.Document.Instances.Single(instance => instance.Name == "预制体A");
        var projectLeaves = SceneEvaluation.Build(owner.Document).OccurrencesOfHostNode(placedProject.Id).ToArray();
        check("project placement includes Main author content and ignores foreground background and canvas backdrops", projectLeaves.Length == 1 && projectLeaves.Single().RenderElement.Kind == ElementKind.Rectangle);
        var importedProject = owner.Document.Definitions.Single(definition => definition.Id == placedProject.DefinitionId);
        check("project canvas configuration and backdrop survive solely as independent preview metadata", importedProject.PreviewCanvas?.Canvas.BackgroundColor == 0xFF228866 &&
            importedProject.PreviewCanvas.Background?.AssetId == asset && owner.Document.Canvas.BackgroundColor != 0xFF228866);
        check("nested prefab definitions are preserved and remapped", SceneEvaluation.Build(owner.Document).OccurrencesOfHostNode(placedPrefab.Id).Single().RenderElement.Kind == ElementKind.Ellipse && owner.Document.Definitions.Count == 3);
        check("project roots are centered at their requested plane drop positions", Near(owner.NodeBounds(placedProject.Id)!.Value.Center, new(400, 300)) && Near(owner.NodeBounds(placedPrefab.Id)!.Value.Center, new(440, 340)));
        check("image opacity and authored document settings retain their own contracts", owner.Document.Elements.Where(element => prepared.SelectedIds.Contains(element.Id)).All(element => element.Style.Opacity == .65) && owner.Document.LayerOpacity == .8 && owner.Document.NextNumber == 9);
        check("all source files remain byte-identical after placement", fileHashes.All(pair => Hash(pair.Key) == pair.Value));
        check("committed batch cannot replay or add a second history entry", !prepared.TryCommit(owner) && owner.UndoCount == beforeUndo + 1);
        string after = JsonSerializer.Serialize(owner.Document);
        check("one Undo restores the exact pre-placement author snapshot", owner.Undo() && JsonSerializer.Serialize(owner.Document) == before);
        check("one Redo restores links source closure placement and names", owner.Redo() && JsonSerializer.Serialize(owner.Document) == after);
        string savedPath = Path.Combine(root, "host.floatcanvas"); await projects.SaveAsync(savedPath, owner.Document, owner.Assets);
        var reopened = await projects.LoadAsync(savedPath); check("saved mixed content remains a self-contained offline snapshot", reopened.Document.Instances.Count == 2 && reopened.Assets.TryGet(asset, out _) && reopened.Document.Definitions.Count == 3);

        // Placing the same file reuses and refreshes its accepted shared definition.
        sourceDocument.Elements[0].Width = 110; await projects.SaveAsync(projectPath, sourceDocument, sourceAssets);
        var repeat = await CanvasContentImport.PrepareAsync(owner, [projectPath], [], owner.Document.Viewport, new(600, 400), 1, savedPath, CancellationToken.None);
        check("repeat linked placement is an accepted atomic edit", repeat.TryCommit(owner));
        check("same external file shares one definition and accepts the newly saved source SHA", owner.Document.Instances.Where(instance => instance.Name == "画布A").Select(instance => instance.DefinitionId).Distinct().Count() == 1 &&
            owner.Document.Definitions.Single(definition => definition.Source?.EntryKind == PrefabSourceKind.ProjectContent).Source!.LastAcceptedFileSha256 == Hash(projectPath));
        check("shared source refresh updates every old occurrence", SceneEvaluation.Build(owner.Document).OccurrencesOfHostNode(placedProject.Id).Any(leaf => leaf.RenderElement.Kind == ElementKind.Rectangle && leaf.RenderElement.Width == 110));

        var stale = await CanvasContentImport.PrepareAsync(owner, [prefabPath], [], owner.Document.Viewport, null, 1, savedPath, CancellationToken.None);
        int staleAssets = owner.Assets.Count, staleUndo = owner.UndoCount; owner.SetEditScope(null); owner.SetEditScope(group);
        check("scope A B A rejects a prepared batch without publishing assets or history", !stale.TryCommit(owner) && owner.Assets.Count == staleAssets && owner.UndoCount == staleUndo);
        var foreign = await CanvasContentImport.PrepareAsync(owner, [prefabPath], [], owner.Document.Viewport, null, 1, savedPath, CancellationToken.None);
        owner.BeginTransaction("foreign gesture"); long foreignId = owner.TransactionId;
        check("foreign author transaction is retained when a prepared commit expires", !foreign.TryCommit(owner) && owner.IsTransactionActive && owner.TransactionId == foreignId); owner.Cancel();
        var changed = await CanvasContentImport.PrepareAsync(owner, [prefabPath], [], owner.Document.Viewport, null, 1, savedPath, CancellationToken.None);
        owner.Execute("external change", document => document.NextNumber++);
        check("content changes expire prepared placement", !changed.TryCommit(owner));
        string unchanged = JsonSerializer.Serialize(owner.Document); int unchangedAssets = owner.Assets.Count, unchangedUndo = owner.UndoCount;
        string badPath = Path.Combine(root, "invalid.floatcanvas"); await File.WriteAllTextAsync(badPath, "not a project");
        check("a bad later file prevents every earlier member from being partially placed", await Fails(() => CanvasContentImport.PrepareAsync(owner, [projectPath, badPath], [], owner.Document.Viewport, null, 1, savedPath, CancellationToken.None)) &&
            JsonSerializer.Serialize(owner.Document) == unchanged && owner.Assets.Count == unchangedAssets && owner.UndoCount == unchangedUndo);
        check("self-resource cycle is rejected before publication", await Fails(() => CanvasContentImport.PrepareAsync(owner, [savedPath], [], owner.Document.Viewport, null, 1, null, CancellationToken.None)));
        string wrongFormat = Path.Combine(root, "image.floatprefab"); await File.WriteAllBytesAsync(wrongFormat, png);
        check("an extension cannot disguise non-package content", await Fails(() => CanvasContentImport.PrepareAsync(owner, [wrongFormat], [], owner.Document.Viewport, null, 1, savedPath, CancellationToken.None)));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        check("cancelled preparation cannot publish author state", await Fails(() => CanvasContentImport.PrepareAsync(owner, [prefabPath], [], owner.Document.Viewport, null, 1, savedPath, cancelled.Token)) && JsonSerializer.Serialize(owner.Document) == unchanged);

        string emptyProject = Path.Combine(root, "empty.floatcanvas"); var emptyDocument = new SceneDocument { Viewport = new(0, 0, 720, 480) };
        await projects.SaveAsync(emptyProject, emptyDocument, new());
        var emptyLink = await new PrefabLinkResolver().PrepareAsync(owner, emptyProject, null);
        var emptyDefinition = emptyLink.Package.Definitions.Single();
        check("empty linked projects retain their resource and preview without an ordinary empty wrapper", emptyDefinition.Graph.Groups.Count == 0 && emptyDefinition.Graph.Elements.Count == 0 && emptyDefinition.PreviewCanvas!.Viewport == emptyDocument.Viewport);
        check("preparing an empty linked resource leaves its owner unchanged", JsonSerializer.Serialize(owner.Document) == unchanged && owner.Assets.Count == unchangedAssets);
        string legacy = Path.Combine(root, "legacy-v8.floatcanvas"); await Legacy(legacy); string legacyHash = Hash(legacy); Exception? legacyError = null;
        try { _ = await CanvasContentImport.PrepareAsync(owner, [legacy], [], owner.Document.Viewport, null, 1, null, CancellationToken.None); } catch (Exception error) { legacyError = error; }
        check("legacy files without persistent identity request an explicit save before linking (" + legacyError?.GetType().Name + ": " + legacyError?.Message + ")", legacyError?.Message == FloatCanvas.Localization.AppText.Get("Prefab.LinkNeedsIdentity"));
        check("rejected legacy linking never migrates or rewrites the source file", Hash(legacy) == legacyHash && JsonSerializer.Serialize(owner.Document) == unchanged && owner.Assets.Count == unchangedAssets);
        Console.WriteLine("CANVAS_CONTENT_IMPORT_EVIDENCE " + root);
    }
    internal static async Task Legacy(string path)
    {
        var json = JsonSerializer.SerializeToNode(new SceneDocument())!.AsObject(); json["Version"] = 8;
        json.Remove("ResourceId"); json.Remove("Definitions"); json.Remove("Instances"); json.Remove("RootCanvasAssignments"); json.Remove("RootPreviewCanvases");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var manifest = new JsonObject { ["Format"] = "FloatCanvas.project", ["Version"] = 8, ["Payload"] = "scene.json", ["CreatedWith"] = "0.4.0-m3", ["LastSavedWith"] = "0.4.0-m3", ["Assets"] = new JsonArray() };
        using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open())) await writer.WriteAsync(manifest.ToJsonString());
        using (var writer = new StreamWriter(zip.CreateEntry("scene.json").Open())) await writer.WriteAsync(json.ToJsonString());
    }
    private static byte[] Png() { using var bitmap = new SKBitmap(4, 3); bitmap.Erase(SKColors.MediumPurple); using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray(); }
    private static bool Near(PointD a, PointD b) => a.DistanceTo(b) < 1e-6;
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static async Task<bool> Fails(Func<Task<PreparedCanvasContentBatch>> action) { try { _ = await action(); return false; } catch { return true; } }
}
