using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using SkiaSharp;

internal static class LinkedContentLifecycleTests
{
    internal static async Task RunAsync(Action<string, bool> check, string? output = null)
    {
        string root = Path.Combine(output ?? Path.GetTempPath(), "linked-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await RepeatedMovedSource(check, root);
        await ExportEditOfflineRefreshDetach(check, root);
        Console.WriteLine("LINKED_CONTENT_LIFECYCLE_EVIDENCE " + root);
    }

    private static async Task RepeatedMovedSource(Action<string, bool> check, string root)
    {
        var store = new ProjectStore(); var source = new SceneDocument { Elements = [Box(100, 80, 60)], Viewport = new(0, 0, 800, 600) };
        string path = Path.Combine(root, "Movable source.floatcanvas"); var assets = new AssetLibrary();
        await store.SaveAsync(path, source, assets);
        var owner = new EditorSession(new() { Viewport = new(0, 0, 1000, 700) });
        var first = await Place(owner, path, new(300, 200));
        Guid firstId = first.SelectedIds.Single(); var initialCenter = owner.NodeBounds(firstId)!.Value.Center;
        check("initial linked project is centered before its source origin changes", Near(initialCenter, new(300, 200)));
        source.Elements.Single().Transform = Affine2D.Translation(230, 160); source.Elements.Single().Width = 80;
        await store.SaveAsync(path, source, assets); string sourceHash = Hash(path);
        string before = JsonSerializer.Serialize(owner.Document); int history = owner.UndoCount;
        var next = await Place(owner, path, new(650, 400)); Guid nextId = next.SelectedIds.Single();
        var nextCenter = owner.NodeBounds(nextId)!.Value.Center;
        Console.WriteLine($"MOVED_SOURCE_PLACEMENT actual={nextCenter.X},{nextCenter.Y} expected=650,400 first={owner.NodeBounds(firstId)!.Value.Center}");
        check("replacing a moved linked source centers the new occurrence on the requested drop point", Near(nextCenter, new(650, 400)));
        check("refreshing during placement preserves the older occurrence's authored source offset", Near(owner.NodeBounds(firstId)!.Value.Center, initialCenter + new PointD(140, 80)));
        check("refresh plus new occurrence is a single reversible history entry", owner.UndoCount == history + 1 && owner.Undo() && JsonSerializer.Serialize(owner.Document) == before && owner.Redo());
        check("repeated placement leaves the actual source file unchanged", Hash(path) == sourceHash);
        source.Elements.Single().Transform = Affine2D.Translation(260, 185); await store.SaveAsync(path, source, assets);
        var third = await Place(owner, path, new(800, 500));
        check("a second source origin change does not accumulate placement drift", Near(owner.NodeBounds(third.SelectedIds.Single())!.Value.Center, new(800, 500)) &&
            Near(owner.NodeBounds(nextId)!.Value.Center, new(680, 425)) && owner.Document.Instances.Select(instance => instance.DefinitionId).Distinct().Count() == 1);
        string saved = Path.Combine(root, "Moved placement owner.floatcanvas"); await store.SaveAsync(saved, owner.Document, owner.Assets);
        var reopened = await store.LoadAsync(saved); var reopenedSession = new EditorSession(reopened.Document, reopened.Assets);
        check("reopening retains the compensated linked geometry and all placement anchors", Near(reopenedSession.NodeBounds(nextId)!.Value.Center, new(680, 425)) &&
            Near(reopenedSession.NodeBounds(third.SelectedIds.Single())!.Value.Center, new(800, 500)));

        static async Task<PreparedCanvasContentBatch> Place(EditorSession owner, string path, PointD center)
        {
            var batch = await CanvasContentImport.PrepareAsync(owner, [path], [], owner.Document.Viewport, center, 1, null, CancellationToken.None);
            if (!batch.TryCommit(owner)) throw new InvalidOperationException("Placement expired.");
            return batch;
        }
    }

    private static async Task ExportEditOfflineRefreshDetach(Action<string, bool> check, string root)
    {
        var projects = new ProjectStore(); var prefabs = new PrefabStore(); var assets = new AssetLibrary();
        byte[] png = Png(); string asset = assets.Add(png);
        var creator = new EditorSession(new() { Viewport = new(0, 0, 800, 600) }, assets);
        creator.Add(Box(15, 20, 50)); Guid group = creator.GroupSelection("Lifecycle badge")!.Value; creator.SetEditScope(group);
        creator.Add(new() { Kind = ElementKind.Image, AssetId = asset, Width = 24, Height = 18, Transform = Affine2D.Translation(80, 30) });
        creator.Add(Box(40, 70, 20)); var inner = creator.GroupSelection("Inner")!.Value; creator.SetEditScope(group);
        string creatorBefore = JsonSerializer.Serialize(creator.Document); int creatorUndo = creator.UndoCount;
        string prefabPath = Path.Combine(root, "Lifecycle badge.floatprefab"); var package = creator.ExportGroupPrefab(group);
        await prefabs.SaveAsync(prefabPath, package, assets); string prefabHash = Hash(prefabPath);
        check("ordinary group export preserves editable hierarchy name assets and author history", JsonSerializer.Serialize(creator.Document) == creatorBefore && creator.UndoCount == creatorUndo &&
            package.Definitions.Single(item => item.Id == package.EntryDefinitionId).Name == "Lifecycle badge" && package.ReferencedAssets.Contains(asset));

        string imagePath = Path.Combine(root, "Lifecycle image.png"); await File.WriteAllBytesAsync(imagePath, png);
        string projectPath = Path.Combine(root, "Lifecycle canvas.floatcanvas"); await projects.SaveAsync(projectPath, creator.Document, assets); string projectHash = Hash(projectPath);
        var owner = new EditorSession(new() { Viewport = new(0, 0, 1200, 850) }); Guid ownerScope = owner.CreatePopulatedGroup("Import here"); owner.SetEditScope(ownerScope);
        int history = owner.UndoCount; string before = JsonSerializer.Serialize(owner.Document);
        var mixed = await CanvasContentImport.PrepareAsync(owner, [imagePath, prefabPath, projectPath], [], owner.Document.Viewport, new(400, 300), .75, null, CancellationToken.None);
        check("ordinary exported prefab and project place atomically beside an image", mixed.TryCommit(owner) && mixed.ProjectCount == 2 && mixed.ImageCount == 1 &&
            owner.UndoCount == history + 1 && mixed.SelectedIds.All(id => new SceneGraph(owner.Document).ParentId(id) == ownerScope));
        Guid prefabInstance = owner.Document.Instances.Single(instance => instance.Name == "Lifecycle badge").Id;
        Guid definitionId = owner.Document.Instances.Single(instance => instance.Id == prefabInstance).DefinitionId;
        string placed = JsonSerializer.Serialize(owner.Document);
        check("the complete mixed import undoes and redoes with exact persistent source identities", owner.Undo() && JsonSerializer.Serialize(owner.Document) == before && owner.Redo() && JsonSerializer.Serialize(owner.Document) == placed);
        check("exporting and inserting never rewrites either source file", Hash(prefabPath) == prefabHash && Hash(projectPath) == projectHash);

        string ownerPath = Path.Combine(root, "Lifecycle owner.floatcanvas"); await projects.SaveAsync(ownerPath, owner.Document, owner.Assets);
        string offlinePath = Path.Combine(root, "Lifecycle badge.offline"); File.Move(prefabPath, offlinePath);
        var loaded = await projects.LoadAsync(ownerPath); owner = new EditorSession(loaded.Document, loaded.Assets); owner.SetEditScope(ownerScope);
        var resolver = new PrefabLinkResolver(); var accepted = owner.Document.Definitions.Single(item => item.Id == definitionId).Source!;
        check("missing prefab source reports offline while reopened images and vector contents remain editable", await resolver.CheckAsync(accepted, ownerPath) == PrefabLinkStatus.Missing &&
            SceneEvaluation.Build(owner.Document).OccurrencesOfHostNode(prefabInstance).Count == 3 && owner.Assets.TryGet(asset, out _));
        using (var renderer = new SceneRenderer())
        using (var pixels = SKBitmap.Decode(renderer.ExportPng(owner.Document, owner.Assets, true)))
            check("a reopened owner renders its exported prefab without opening the source file", pixels is { Width: 1200, Height: 850 } && pixels.Bytes.Any(value => value != 0));
        File.Move(offlinePath, prefabPath);

        var sourceFile = await prefabs.LoadAsync(prefabPath); var entry = sourceFile.Package.Definitions.Single(item => item.Id == sourceFile.Package.EntryDefinitionId);
        var graph = SceneGraphClone.CloneGraph(entry.Graph).Graph;
        var sourceDocument = new SceneDocument { ResourceId = sourceFile.Package.ResourceId, Elements = graph.Elements, Groups = graph.Groups, Instances = graph.Instances,
            RootOrder = graph.RootOrder, Definitions = sourceFile.Package.Definitions.Select(item => item.Clone()).ToList(), Viewport = creator.Document.Viewport };
        var sourceSession = new EditorSession(sourceDocument, owner.Assets);
        var context = new PrefabEditContext(owner, sourceSession, definitionId, owner.Document.Definitions.Single(item => item.Id == definitionId).Revision,
            new(-1900, 80), EditorTool.Select, sourcePath: prefabPath, sourceEntryDefinitionId: entry.Id);
        string ownerBeforeDraft = JsonSerializer.Serialize(owner.Document); string diskBeforeDraft = Hash(prefabPath);
        Guid editedLeaf = sourceSession.Document.Elements.First(element => element.Kind == ElementKind.Rectangle).Id;
        sourceSession.BeginTransaction("cancel source sample"); sourceSession.Document.Elements.Single(element => element.Id == editedLeaf).Width = 91; sourceSession.NotifyChanged();
        var preview = new GroupCanvasScene().Capture(sourceSession, context, GroupCanvasMode.RootProject, default, owner.Assets);
        check("root preview composes unsaved external prefab edits and the original monitor origin", preview.Origin == new PointD(-1900, 80) &&
            SceneEvaluation.Build(preview.Document).OccurrencesOfHostNode(prefabInstance).Any(leaf => leaf.RenderElement.Width == 91));
        sourceSession.Cancel();
        check("canceling source edits leaves the owner and actual prefab byte-identical", JsonSerializer.Serialize(owner.Document) == ownerBeforeDraft && Hash(prefabPath) == diskBeforeDraft);
        sourceSession.Execute("saved source edit", document => document.Elements.Single(element => element.Id == editedLeaf).Width = 97);
        entry.Graph = SceneGraphClone.CloneGraph(sourceSession.Document).Graph; entry.Revision = Guid.NewGuid();
        await prefabs.SaveVersionedAsync(prefabPath, sourceFile.Package, owner.Assets, expectedVersion: sourceFile.FileVersion);
        check("a saved source is detected without changing the accepted offline snapshot", await resolver.CheckAsync(accepted, ownerPath) == PrefabLinkStatus.Changed &&
            SceneEvaluation.Build(owner.Document).OccurrencesOfHostNode(prefabInstance).All(leaf => leaf.RenderElement.Width != 97));
        var refresh = await resolver.PrepareAsync(owner, prefabPath, ownerPath, definitionId);
        history = owner.UndoCount; string preRefresh = JsonSerializer.Serialize(owner.Document);
        owner.ApplyPrefabEdit(owner.PreparePrefabRefresh(definitionId, refresh.Package, refresh.Assets, refresh.Source));
        check("refresh accepts the saved revision in one undoable owner edit", owner.UndoCount == history + 1 &&
            SceneEvaluation.Build(owner.Document).OccurrencesOfHostNode(prefabInstance).Any(leaf => leaf.RenderElement.Width == 97) && owner.Undo() && JsonSerializer.Serialize(owner.Document) == preRefresh && owner.Redo());
        Guid duplicate = owner.PlacePrefab(definitionId, new(Affine2D.Translation(800, 500), 1), "Linked copy");
        string preDetach = JsonSerializer.Serialize(owner.Document); var detachedBounds = owner.NodeBounds(prefabInstance); string sourceBeforeDetach = Hash(prefabPath);
        history = owner.UndoCount; owner.DetachPrefab(prefabInstance); owner.SetEditScope(prefabInstance);
        check("detaching one linked occurrence creates an ordinary editable group with identical geometry", new SceneGraph(owner.Document).FindGroup(prefabInstance)?.Name == "Lifecycle badge" &&
            owner.NodeBounds(prefabInstance) == detachedBounds && owner.UndoCount == history + 1 && owner.EditScopeId == prefabInstance &&
            owner.Document.Instances.Any(instance => instance.Id == duplicate && instance.DefinitionId == definitionId));
        check("detach preserves the source bytes and other linked copies and has exact undo", Hash(prefabPath) == sourceBeforeDetach && owner.Undo() && JsonSerializer.Serialize(owner.Document) == preDetach && owner.Redo());
        await projects.SaveAsync(ownerPath, owner.Document, owner.Assets); File.Move(prefabPath, offlinePath);
        var final = await projects.LoadAsync(ownerPath); var finalSession = new EditorSession(final.Document, final.Assets);
        check("detached and shared siblings survive another reopen while the source is offline", new SceneGraph(final.Document).FindGroup(prefabInstance) is not null &&
            final.Document.Instances.Any(instance => instance.Id == duplicate) && finalSession.NodeBounds(prefabInstance) == detachedBounds && final.Assets.TryGet(asset, out _));
        string finalBefore = JsonSerializer.Serialize(final.Document); int finalAssets = final.Assets.Count;
        bool failed = false;
        try { _ = await CanvasContentImport.PrepareAsync(finalSession, [imagePath, prefabPath], [], final.Document.Viewport, null, 1, ownerPath, CancellationToken.None); }
        catch (FileNotFoundException) { failed = true; }
        check("offline member failure in a mixed placement cannot publish its earlier image", failed && JsonSerializer.Serialize(final.Document) == finalBefore && final.Assets.Count == finalAssets && finalSession.UndoCount == 0);
    }

    private static CanvasElement Box(double x, double y, double width) => new()
    { Kind = ElementKind.Rectangle, Width = width, Height = 24, Transform = Affine2D.Translation(x, y), Style = new() { Fill = 0xff4488cc, StrokeWidth = 0 } };
    private static bool Near(PointD a, PointD b) => a.DistanceTo(b) < 1e-6;
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static byte[] Png()
    {
        using var pixels = new SKBitmap(4, 3); pixels.Erase(SKColors.Coral); using var encoded = pixels.Encode(SKEncodedImageFormat.Png, 100); return encoded.ToArray();
    }
}
