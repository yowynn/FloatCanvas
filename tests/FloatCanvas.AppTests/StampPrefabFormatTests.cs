using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using SkiaSharp;

internal static class StampPrefabFormatTests
{
    internal static async Task RunAsync(string profile, Action<string, bool> check)
    {
        foreach (bool perspective in new[] { false, true })
        {
            var reference = new SceneDocument { Viewport = new(0, 0, 1920, 1080), Elements = [new()
                { Kind = ElementKind.Rectangle, Width = 100, Height = 60, Transform = Affine2D.Translation(200, 100) }] };
            reference.Canvas.Grid.Visible = true; reference.Canvas.Projection.Enabled = perspective;
            string before = JsonSerializer.Serialize(reference);
            var original = CanvasMapping.Create(reference); var fitted = CanvasMapping.Create(StampPreviewScene.Prepare(reference));
            check("fitting a stamp preview preserves implicit grid phase spacing and projection",
                original.GridOrigin == fitted.GridOrigin && original.GridCellSize == fitted.GridCellSize &&
                original.ToScreen(new(200, 100)).DistanceTo(fitted.ToScreen(new(200, 100))) < 1e-7);
            check("fitting a stamp preview never materializes defaults in saved author settings", before == JsonSerializer.Serialize(reference));
        }
        var assets = new AssetLibrary();
        using var surface = SKSurface.Create(new SKImageInfo(8, 8)); surface.Canvas.Clear(SKColors.CornflowerBlue);
        using var image = surface.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        string backdrop = assets.Add(data.ToArray());
        var leaf = new CanvasElement { Kind = ElementKind.Rectangle, Width = 100, Height = 60,
            Transform = Affine2D.Translation(200, 100), Style = new() { StrokeWidth = 0, Background = 0xFFCC3344 } };
        var document = new SceneDocument { Elements = [leaf], Viewport = new(0, 0, 640, 480), LayerOpacity = .6,
            Background = new() { Kind = ElementKind.Image, AssetId = backdrop, Width = 640, Height = 480 } };
        document.Canvas.BackgroundColor = 0xFF224466; document.Canvas.Grid.FlatCellSize = 25;
        document.Canvas.Grid.FlatTranslation = new(11, 19);
        var session = new EditorSession(document, assets); session.SetSelection([leaf.Id]);
        var expectedHeader = session.BuildSelectionPreviewSettings(session.CreateSelectionSnapshot());
        var library = new StampLibrary(profile); await library.LoadAsync();
        foreach (var mode in Enum.GetValues<StampSaveMode>())
        {
            var entry = await library.SaveSelectionAsync(session, mode.ToString(), mode);
            string fileBase = Path.Combine(library.DirectoryPath, entry.Id.ToString("N") + "-" + entry.Revision.ToString("N"));
            var saved = await new PrefabStore().LoadAsync(fileBase + ".floatprefab");
            var definition = saved.Package.Definitions.Single(item => item.Id == saved.Package.EntryDefinitionId);
            check($"{mode} uses a prefab package with its authored independent header and preview-only image bytes",
                entry.Format == StampPackageFormat.Prefab && !File.Exists(fileBase + ".floatcanvas") &&
                JsonSerializer.Serialize(definition.PreviewCanvas) == JsonSerializer.Serialize(expectedHeader) &&
                saved.Assets.Get(backdrop).Data.Span.SequenceEqual(data.ToArray()));
            check($"{mode} preview background remains outside the inserted graph", definition.Graph.Elements.All(item => item.AssetId != backdrop));
            var hover = await StampPreviewTooltip.GetPreviewAsync(library, entry);
            byte[] sample = new byte[4]; hover!.CopyPixels(new System.Windows.Int32Rect(hover.PixelWidth / 2, hover.PixelHeight / 2, 1, 1), sample, 4, 0);
            check($"{mode} hover uses the saved background and .6 preview opacity instead of a transparent host-sized crop",
                hover.PixelWidth == 320 && sample[3] == 255 && Math.Abs(sample[2] - 162) <= 2 &&
                Math.Abs(sample[1] - 90) <= 2 && Math.Abs(sample[0] - 136) <= 2);
            if (mode == StampSaveMode.RasterMerge)
            {
                var source = await new PrefabStore().LoadAsync(fileBase + ".source.floatprefab");
                check("raster editable source is also prefab and retains vector content plus preview metadata",
                    source.Package.Definitions.Single(item => item.Id == source.Package.EntryDefinitionId) is { Graph.Elements: [{ Kind: ElementKind.Rectangle }], PreviewCanvas: not null } &&
                    !File.Exists(fileBase + ".source.floatcanvas"));
            }
            var target = new EditorSession(new SceneDocument { Viewport = new(0, 0, 900, 700), LayerOpacity = .8 });
            target.Document.Canvas.BackgroundColor = 0xFFABCDEF;
            string before = JsonSerializer.Serialize(SubprojectCanvasSettings.FromDocument(target.Document));
            await library.PlaceAsync(entry.Id, target, new(450, 350));
            var bounds = SceneEvaluation.Build(target.Document).RenderDocument.Elements.Select(item => ElementGeometry.Bounds(item)).Aggregate(RectD.Union);
            check($"{mode} insertion ignores preview viewport, backdrop, color and opacity while centering content",
                before == JsonSerializer.Serialize(SubprojectCanvasSettings.FromDocument(target.Document)) &&
                target.Document.Background is null && bounds.Center.DistanceTo(new(450, 350)) < 1e-7 && target.UndoCount == 1);
        }
        var catalog = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(library.DirectoryPath, "library.json")))!;
        check("new catalogs explicitly record prefab storage without mode-dependent extension inference", catalog["Version"]!.GetValue<int>() == 3 &&
            catalog["Items"]!.AsArray().All(item => item!["Format"]!.GetValue<int>() == (int)StampPackageFormat.Prefab));
        await LegacyAsync(profile + "-legacy", check);
        await StampLegacyCatalogTests.RunAsync(profile + "-catalog-compat", check);
        var emptyGroup = new CanvasGroup { Name = "empty reusable group", OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] };
        var emptySession = new EditorSession(new SceneDocument { Groups = [emptyGroup], RootOrder = [emptyGroup.Id] }); emptySession.SetSelection([emptyGroup.Id]);
        var emptyEntry = await library.SaveSelectionAsync(emptySession, "empty reusable group", StampSaveMode.SharedPrefab);
        var empty = await library.LoadStampAsync(emptyEntry.Id);
        using var emptyPreview = SKBitmap.Decode(await library.GetThumbnailAsync(emptyEntry.Id));
        check("empty shared groups remain saveable with a bounded neutral preview",
            empty.SharedPrefab is not null && emptyPreview.Width <= 160 && emptyPreview.Height <= 160);
        var dot = new CanvasElement { Kind = ElementKind.Rectangle, Width = 0, Height = 0, Style = new() { StrokeWidth = 0 } };
        var dotSession = new EditorSession(new SceneDocument { Elements = [dot] }); dotSession.SetSelection([dot.Id]);
        var dotEntry = await library.SaveSelectionAsync(dotSession, "degenerate rectangle");
        using var dotPreview = SKBitmap.Decode(await library.GetThumbnailAsync(dotEntry.Id));
        check("zero-area authored geometry cannot allocate an infinite or empty thumbnail", dotPreview.Width is > 0 and <= 160 && dotPreview.Height is > 0 and <= 160);
    }

    private static async Task LegacyAsync(string profile, Action<string, bool> check)
    {
        string root = Path.Combine(profile, "stamps"); Directory.CreateDirectory(root);
        var first = new StampEntry(Guid.NewGuid(), "old editable", StampSaveMode.EditableGroup, 90, 40, DateTime.UtcNow, Guid.NewGuid());
        var second = new StampEntry(Guid.NewGuid(), "old raster", StampSaveMode.RasterMerge, 90, 40, DateTime.UtcNow.AddMinutes(-1), Guid.NewGuid());
        var document = new SceneDocument { Viewport = new(0, 0, 90, 40), Elements = [new() { Kind = ElementKind.Rectangle, Width = 90, Height = 40 }] };
        string Base(StampEntry item) => Path.Combine(root, item.Id.ToString("N") + "-" + item.Revision.ToString("N"));
        await new ProjectStore().SaveAsync(Base(first) + ".floatcanvas", document, new());
        await new ProjectStore().SaveAsync(Base(second) + ".floatcanvas", document, new());
        await new ProjectStore().SaveAsync(Base(second) + ".source.floatcanvas", document, new());
        Guid?[] pins = new Guid?[10]; pins[2] = first.Id; pins[8] = second.Id;
        // Old records have no format field; preserve their original filenames during catalog edits.
        var json = JsonSerializer.SerializeToNode(new { Version = 2, Items = new[] { second, first }, Pins = pins, Order = new[] { second.Id, first.Id } })!;
        foreach (var item in json["Items"]!.AsArray()) item!.AsObject().Remove("Format");
        await File.WriteAllTextAsync(Path.Combine(root, "library.json"), json.ToJsonString());
        byte[] original = await File.ReadAllBytesAsync(Base(first) + ".floatcanvas");
        var library = new StampLibrary(profile); await library.LoadAsync();
        check("legacy project-based stamps and raster sources still load without rewriting their packages",
            (await library.LoadStampAsync(first.Id)).Document.Elements.Count == 1 &&
            (await library.LoadSourceAsync(second.Id)).Document.Elements.Count == 1 &&
            (await File.ReadAllBytesAsync(Base(first) + ".floatcanvas")).SequenceEqual(original));
        await library.RenameAsync(first.Id, "renamed legacy");
        var again = new StampLibrary(profile); await again.LoadAsync();
        check("v2-to-v3 catalog publication preserves custom order, pins and legacy path inference",
            again.Items.Select(item => item.Id).SequenceEqual(new[] { second.Id, first.Id }) && again.Pins[2] == first.Id && again.Pins[8] == second.Id &&
            (await again.LoadStampAsync(first.Id)).Document.Elements.Count == 1);
        var session = new EditorSession(document.Clone()); session.SetSelection(session.Document.Elements.Select(item => item.Id));
        var replacement = await again.SaveSelectionAsync(session, "migrated", replaceId: first.Id);
        check("resaving a legacy stamp publishes prefab while retaining its identity and shortcut",
            replacement.Id == first.Id && replacement.Format == StampPackageFormat.Prefab && again.Pins[2] == first.Id &&
            File.Exists(Base(replacement) + ".floatprefab") && !File.Exists(Base(first) + ".floatcanvas"));
    }
}
