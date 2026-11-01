using System.Collections.Specialized;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using SkiaSharp;
using OleDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

/// <summary>Owned OLE file data through production project preparation and guarded commit; no clipboard or native drag loop.</summary>
internal static class ProjectDropIntegrationTests
{
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        string root = Path.Combine(output ?? Path.GetTempPath(), "project-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var assets = new AssetLibrary();
        string project = Path.Combine(root, "Linked canvas.floatcanvas"), prefab = Path.Combine(root, "Preset A.floatprefab"), image = Path.Combine(root, "image.png");
        var leaf = new CanvasElement { Kind = ElementKind.Rectangle, Width = 80, Height = 40, Style = new() { Fill = 0xFF2080E0, StrokeWidth = 0 } };
        var projectDocument = new SceneDocument { Elements = [leaf], Viewport = new(0, 0, 320, 180) };
        new ProjectStore().SaveAsync(project, projectDocument, assets).GetAwaiter().GetResult();
        var definition = new PrefabDefinition { Name = "Preset A", Graph = new() { Elements = [leaf.Clone()] } };
        new PrefabStore().SaveAsync(prefab, new() { EntryDefinitionId = definition.Id, Definitions = [definition] }, assets).GetAwaiter().GetResult();
        using (var pixels = new SKBitmap(18, 12))
        {
            pixels.Erase(SKColors.Crimson); using var encoded = pixels.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(image, encoded.ToArray());
        }
        string projectSha = Hash(project), prefabSha = Hash(prefab);

        var target = new EditorSession(new() { Viewport = new(0, 0, 1000, 700) });
        Guid group = target.CreateEmptyGroup("Drop target");
        target.Document.Groups.Single().Transform = Affine2D.Translation(45, 30).Compose(Affine2D.Rotation(15));
        target.SetEditScope(group);
        var payload = ReadPaths([project, prefab, image], check);
        using var request = new ImageDropRequest(target, target.Document.Viewport, CancellationToken.None, .65);
        check("project OLE release point uses original physical-to-plane origin", request.TrySetDropPoint(new(250, 200), new(-200, -100)) && request.DropCenter == new PointD(450, 300));
        string before = JsonSerializer.Serialize(target.Document); int undo = target.UndoCount; Guid resource = target.Document.ResourceId;
        var prepared = Prepare(target, payload, request);
        check("mixed OLE preparation reads all files without publishing any author resource", prepared.HasProjects && prepared.ProjectCount == 2 && prepared.ImageCount == 1 && prepared.NodeCount == 3 &&
            JsonSerializer.Serialize(target.Document) == before && target.Assets.Count == 0 && target.UndoCount == undo);
        check("mixed image project and prefab drop commits through one guarded request", request.TryCommit(request, target, null, prepared) && request.Completed && target.UndoCount == undo + 1);
        var graph = new SceneGraph(target.Document);
        check("drop inserts selected siblings into the captured current group instead of replacing the canvas", target.Document.ResourceId == resource && target.EditScopeId == group &&
            graph.EnumerateChildren(group).Count == 3 && target.Selection.SetEquals(prepared.SelectedIds) && target.Document.Instances.Count == 2 && target.Document.Elements.Count == 1);
        check("project and prefab file names become the linked instance names", target.Document.Instances.Select(instance => instance.Name).ToHashSet().SetEquals(["Linked canvas", "Preset A"]));
        check("both project formats keep validated external source identity and offline definitions", target.Document.Definitions.Count >= 2 &&
            target.Document.Definitions.Where(item => item.Source?.IsLinked == true).Count() == 2 && target.Document.Definitions.Where(item => item.Source?.IsLinked == true).All(item =>
                !string.IsNullOrEmpty(item.Source!.Locator) && !string.IsNullOrEmpty(item.Source.LastAcceptedFileSha256)));
        check("source project and prefab bytes are never rewritten during the drop", Hash(project) == projectSha && Hash(prefab) == prefabSha);
        for (int i = 0; i < prepared.SelectedIds.Count; i++)
            check("mixed drop item " + i + " remains centered at the release point with its ordered offset", target.NodeBounds(prepared.SelectedIds[i])!.Value.Center.DistanceTo(request.DropCenter!.Value + new PointD(i * 20, i * 20)) < 1e-6);
        check("mixed drop image retains image insertion opacity", Math.Abs(target.Document.Elements.Single().Style.Opacity - .65) < 1e-9);
        string committed = JsonSerializer.Serialize(target.Document);
        check("one undo removes the complete mixed drop and exact redo restores its ids", target.Undo() && JsonSerializer.Serialize(target.Document) == before && target.Redo() && JsonSerializer.Serialize(target.Document) == committed);
        check("completed project request cannot place the same prepared graph twice", !request.TryCommit(request, target, null, prepared) && target.UndoCount == undo + 1);
        using (var renderer = new SceneRenderer())
        using (var raster = SKBitmap.Decode(renderer.ExportPng(target.Document, target.Assets, true)))
            check("linked snapshots and normalized image render from owned author data", raster is { Width: 1000, Height: 700 } && raster.Bytes.Any(value => value != 0));

        foreach (string change in new[] { "scope ABA", "foreign transaction", "cancel", "document replacement", "new owner" })
        {
            var owner = new EditorSession(new() { Viewport = new(0, 0, 600, 400) }); Guid folder = owner.CreateEmptyGroup("Other");
            using var delayed = new ImageDropRequest(owner, owner.Document.Viewport, CancellationToken.None);
            var batch = Prepare(owner, ReadPaths([project], check), delayed); var live = owner;
            switch (change)
            {
                case "scope ABA": owner.SetEditScope(folder); owner.SetEditScope(null); break;
                case "foreign transaction": owner.BeginTransaction("foreign command"); break;
                case "cancel": delayed.Cancel(); break;
                case "document replacement": owner.RestoreDocument("replacement", owner.Document.Clone()); break;
                case "new owner": live = new(); break;
            }
            string snapshot = JsonSerializer.Serialize(owner.Document); int history = owner.UndoCount; long transaction = owner.TransactionId;
            check("prepared project drop after " + change + " cannot publish a late graph or resource", !delayed.TryCommit(delayed, live, null, batch) &&
                JsonSerializer.Serialize(owner.Document) == snapshot && owner.Assets.Count == 0 && owner.UndoCount == history && owner.Document.Instances.Count == 0);
            if (change == "foreign transaction")
            {
                check("failed project drop does not cancel the foreign transaction", owner.IsTransactionActive && owner.TransactionId == transaction);
                owner.Cancel();
            }
        }

        var clean = new EditorSession(); string broken = Path.Combine(root, "broken.floatcanvas"); File.WriteAllBytes(broken, [1, 2, 3]);
        using var failing = new ImageDropRequest(clean, new(0, 0, 500, 400), CancellationToken.None);
        bool rejected = false;
        try { _ = Prepare(clean, ReadPaths([image, broken], check), failing); }
        catch (InvalidDataException) { rejected = true; }
        check("a bad project after a valid image leaves no partial scene assets or undo", rejected && !failing.Completed && clean.Document.Elements.Count == 0 && clean.Document.Instances.Count == 0 && clean.Assets.Count == 0 && clean.UndoCount == 0);

        var draft = new EditorSession();
        using var draftDrop = new ImageDropRequest(draft, new(0, 0, 500, 400), CancellationToken.None);
        var draftBatch = Prepare(draft, ReadPaths([prefab], check), draftDrop);
        check("explicit active prefab draft can receive a linked dropped prefab", draftDrop.TryCommit(draftDrop, new(), null, draftBatch, draft) && draft.Document.Instances.Count == 1 && draft.UndoCount == 1);
        Console.WriteLine("PROJECT_DROP_EVIDENCE " + root);
    }

    private static ImageDropPayload ReadPaths(string[] paths, Action<string, bool> check)
    {
        var source = new DataObject(); var list = new StringCollection(); list.AddRange(paths); source.SetFileDropList(list);
        var ole = (OleDataObject)source;
        check("owned real CF_HDROP negotiates project placement without using the clipboard", ImageDropDataReader.Supports(ole));
        var payload = ImageDropDataReader.Read(ole);
        check("owned CF_HDROP keeps the complete ordered file paths", payload.FilePaths.SequenceEqual(paths));
        return payload;
    }
    private static PreparedCanvasContentBatch Prepare(EditorSession target, ImageDropPayload payload, ImageDropRequest request) =>
        CanvasContentImport.PrepareAsync(target, payload.FilePaths, [], request.Placement, request.DropCenter, request.Opacity, null, request.Token).GetAwaiter().GetResult();
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
