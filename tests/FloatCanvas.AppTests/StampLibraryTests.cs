using System.IO;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class StampLibraryTests
{
    public static async Task RunAsync(Action<string, bool> assert)
    {
        string profile = Path.Combine(Path.GetTempPath(), "FloatCanvas-StampLibraryTests", Guid.NewGuid().ToString("N"));
        var library = new StampLibrary(profile);
        await library.LoadAsync();
        assert("new stamp library has ten empty shortcut slots and no files until a mutation", library.Items.Count == 0 && library.Pins.Count == 10 && library.Pins.All(id => id is null) && !Directory.Exists(library.DirectoryPath));
        var assets = new AssetLibrary();
        using var surface = SKSurface.Create(new SKImageInfo(32, 32)); surface.Canvas.Clear(SKColors.DeepSkyBlue);
        using var image = surface.Snapshot(); using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        byte[] originalPng = encoded.ToArray(); string assetId = assets.Add(originalPng);
        var group = new CanvasGroup { Name = "existing group" };
        var symbol = new CanvasElement { Kind = ElementKind.Symbol, Symbol = SymbolKind.Heart, Width = 40, Height = 35, GroupId = group.Id,
            Style = new() { Stroke = 0xFF179234, Fill = 0xFF53DDA9 }, Transform = Affine2D.Translation(100, 90) };
        var rectangle = new CanvasElement { Kind = ElementKind.Rectangle, Width = 72, Height = 28, GroupId = group.Id,
            Transform = Affine2D.Translation(130, 130).Compose(Affine2D.Rotation(31)) };
        var picture = new CanvasElement { Kind = ElementKind.Image, AssetId = assetId, Width = 56, Height = 48,
            ImageCrop = new(.25, .125, .5, .625), Transform = Affine2D.Translation(190, 95) };
        var text = new CanvasElement { Kind = ElementKind.Text, Text = "Reusable", Width = 120, Height = 32, Transform = Affine2D.Translation(120, 180) };
        var excluded = new CanvasElement { Kind = ElementKind.Ellipse, Width = 900, Height = 900, Transform = Affine2D.Translation(4000, 4000) };
        var session = new EditorSession(new() { Groups = [group], Elements = [symbol, rectangle, picture, text, excluded] }, assets);
        session.SetSelection([symbol.Id, rectangle.Id, picture.Id, text.Id]);
        string before = JsonSerializer.Serialize(session.Document);
        var vector = await library.SaveSelectionAsync(session, "Mixed editable preset");
        var loaded = await library.LoadStampAsync(vector.Id);
        var normalized = session.CreateSelectionSnapshot();
        assert("saving selected entities leaves the source canvas and its unselected object unchanged", JsonSerializer.Serialize(session.Document) == before && session.Document.Elements.Count == 5);
        assert("editable preset contains only the chosen symbol, rotated rectangle, cropped image and text", loaded.Document.Elements.Count == 4 && loaded.Document.Elements.All(element => element.Id != excluded.Id));
        assert("editable preset retains complete object styles, crop, rotation and group metadata", JsonSerializer.Serialize(loaded.Document) == JsonSerializer.Serialize(normalized));
        assert("self-contained vector preset retains original encoded image bytes", loaded.Assets.Get(assetId).Data.Span.SequenceEqual(originalPng));
        using (var thumbnail = SKBitmap.Decode(await library.GetThumbnailAsync(vector.Id)))
            assert("thumbnail renders selected content within a bounded inventory tile", Math.Max(thumbnail.Width, thumbnail.Height) <= 160 && thumbnail.Width > 0 && thumbnail.Height > 0);

        var target = new EditorSession();
        await library.PlaceAsync(vector.Id, target, new(600, 400));
        var placed = target.Document.Elements.Single(element => element.Kind == ElementKind.Rectangle);
        assert("one click stamps at original dimensions and rotation in one undo step", target.UndoCount == 1 && placed.Width == rectangle.Width && placed.Height == rectangle.Height &&
            placed.Transform.M11 == rectangle.Transform.M11 && placed.Transform.M12 == rectangle.Transform.M12 && placed.Transform.M21 == rectangle.Transform.M21 && placed.Transform.M22 == rectangle.Transform.M22);
        var targetBounds = target.Document.Elements.Select(element => ElementGeometry.Bounds(element)).Aggregate(RectD.Union);
        assert("stamp placement centers the original visual bounds at the click", targetBounds.Center.DistanceTo(new(600, 400)) < 1e-8);
        Guid? firstRoot = target.Document.RootGroupId(target.Document.Elements[0]);
        assert("multi-object stamps form a single logical group while children remain independently editable", firstRoot is not null && target.Document.Elements.All(element => target.Document.RootGroupId(element) == firstRoot) && target.SelectionUnitIds(placed.Id, individual: true).SequenceEqual([placed.Id]));
        await library.PlaceAsync(vector.Id, target, new(850, 400));
        assert("repeated stamps receive independent object/group identities and independent undo steps", target.UndoCount == 2 && target.Document.Elements.Select(element => element.Id).Distinct().Count() == 8 && target.Document.RootGroupId(target.Document.Elements[^1]) != firstRoot);
        assert("stamp undo removes one instance and redo retains editable child objects", target.Undo() && target.Document.Elements.Count == 4 && target.Redo() && target.Document.Elements.Count == 8);

        var raster = await library.SaveSelectionAsync(session, "Merged preset", StampSaveMode.RasterMerge);
        var flattened = await library.LoadStampAsync(raster.Id);
        assert("raster save merges exactly the selection into one image at original canvas dimensions", flattened.Document.Elements is [{ Kind: ElementKind.Image }] &&
            flattened.Document.Elements[0].Width == normalized.Viewport.Width && flattened.Document.Elements[0].Height == normalized.Viewport.Height);
        using (var bitmap = SKBitmap.Decode(flattened.Assets.Get(flattened.Document.Elements[0].AssetId!).Data.ToArray()))
            assert("merged preset uses high resolution with a 4096px edge limit", bitmap.Width >= vector.Width && bitmap.Height >= vector.Height && bitmap.Width <= 4096 && bitmap.Height <= 4096);
        var original = await library.LoadSourceAsync(raster.Id);
        assert("merged presets also preserve the original editable project and original picture bytes", original.Document.Elements.Count == 4 && original.Assets.Get(assetId).Data.Span.SequenceEqual(originalPng));
        assert("raster generation never alters or removes unselected canvas objects", JsonSerializer.Serialize(session.Document) == before);
        var wideLibrary = new StampLibrary(profile + "-wide"); await wideLibrary.LoadAsync();
        var wideSession = new EditorSession(new() { Elements = [new() { Kind = ElementKind.Rectangle, Width = 40_000, Height = 1_000 }] });
        wideSession.SetSelection(wideSession.Document.Elements.Select(element => element.Id));
        var wideEntry = await wideLibrary.SaveSelectionAsync(wideSession, "Very wide preset", StampSaveMode.RasterMerge);
        var wideStamp = await wideLibrary.LoadStampAsync(wideEntry.Id);
        using (var bitmap = SKBitmap.Decode(wideStamp.Assets.Get(wideStamp.Document.Elements[0].AssetId!).Data.ToArray()))
            assert("oversized raster presets respect the 4096px allocation limit without shrinking placement size", bitmap.Width == 4096 && bitmap.Height <= 4096 && wideStamp.Document.Elements[0].Width > 40_000);

        await library.PinAsync(0, vector.Id); await library.PinAsync(9, raster.Id);
        await library.RenameAsync(vector.Id, "Renamed vector");
        var reloaded = new StampLibrary(profile); await reloaded.LoadAsync();
        assert("catalog reload preserves names and the 1..0 shortcut assignments", reloaded.Items.Single(item => item.Id == vector.Id).Name == "Renamed vector" && reloaded.Pins[0] == vector.Id && reloaded.Pins[9] == raster.Id);
        await library.PinAsync(9, null);
        assert("clearing a fixed slot leaves its library preset intact", library.Pins[9] is null && library.Items.Any(item => item.Id == raster.Id));
        var replaced = await library.SaveSelectionAsync(session, "Updated preset", StampSaveMode.EditableGroup, vector.Id);
        assert("overwrite retains preset identity and its fixed slot while publishing a new immutable revision", replaced.Id == vector.Id && replaced.Revision != vector.Revision && library.Pins[0] == vector.Id && library.Items.Count == 2);

        string indexPath = Path.Combine(library.DirectoryPath, "library.json");
        byte[] indexBefore = await File.ReadAllBytesAsync(indexPath);
        File.SetAttributes(indexPath, FileAttributes.ReadOnly);
        bool failed = false;
        try { await library.SaveSelectionAsync(session, "Must not replace", StampSaveMode.RasterMerge, vector.Id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
        finally { File.SetAttributes(indexPath, FileAttributes.Normal); }
        byte[] afterFailure = await File.ReadAllBytesAsync(indexPath);
        assert("a failed catalog publication preserves the prior on-disk manifest and in-memory preset", failed && indexBefore.SequenceEqual(afterFailure) && library.Items.Single(item => item.Id == vector.Id).Revision == replaced.Revision);
        assert("previous preset resources stay usable after failed overwrite", (await library.LoadStampAsync(vector.Id)).Document.Elements.Count == 4);

        await library.PinAsync(1, vector.Id); await library.PinAsync(9, raster.Id);
        string unrelated = Path.Combine(library.DirectoryPath, "unrelated.keep"); await File.WriteAllTextAsync(unrelated, "preserve");
        await library.DeleteAsync(vector.Id);
        assert("deleting a preset clears only its matching fixed slots", library.Pins[0] is null && library.Pins[1] is null && library.Pins[9] == raster.Id);
        assert("delete preserves unrelated presets, original resources and unrelated files", library.Items.Count == 1 && File.Exists(unrelated) && (await library.LoadSourceAsync(raster.Id)).Assets.Get(assetId).Data.Span.SequenceEqual(originalPng));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool cancelledWrite = false;
        try { await library.PinAsync(2, raster.Id, cancelled.Token); } catch (OperationCanceledException) { cancelledWrite = true; }
        assert("cancelled catalog edits cannot change assignments", cancelledWrite && library.Pins[2] is null);

        string package = Path.Combine(library.DirectoryPath, raster.Id.ToString("N") + "-" + raster.Revision.ToString("N") + ".floatcanvas");
        await File.WriteAllBytesAsync(package, [1, 2, 3]);
        var damaged = new StampLibrary(profile); await damaged.LoadAsync();
        bool explained = false;
        try { await damaged.LoadStampAsync(raster.Id); } catch (InvalidDataException ex) { explained = ex.Message.Contains(raster.Name); }
        assert("one corrupt resource is explained by name and never removes its catalog entry", explained && damaged.Items.Single().Id == raster.Id);
        string brokenProfile = profile + "-broken", brokenIndex = Path.Combine(brokenProfile, "stamps", "library.json");
        Directory.CreateDirectory(Path.GetDirectoryName(brokenIndex)!); await File.WriteAllTextAsync(brokenIndex, "{broken catalog");
        var broken = new StampLibrary(brokenProfile); bool refused = false;
        try { await broken.LoadAsync(); } catch (InvalidDataException) { refused = true; }
        bool refusedMutation = false;
        try { await broken.PinAsync(0, null); } catch (InvalidOperationException) { refusedMutation = true; }
        assert("malformed catalog blocks mutation and remains byte-for-byte available for repair", refused && refusedMutation && await File.ReadAllTextAsync(brokenIndex) == "{broken catalog");
    }
}
