using System.IO;
using System.IO.Compression;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using SkiaSharp;

/// <summary>Exercises the production snapshot source, insertion and persistence seams without capturing the user's desktop.</summary>
internal static class SnapshotImageTests
{
    internal static void Run(Action<string, bool> check) => RunAsync(check).GetAwaiter().GetResult();

    private static async Task RunAsync(Action<string, bool> check)
    {
        SourcePixels(check);
        LayerOpacityAndLiveEffects(check);
        RequestGuards(check);
        await InsertAndPersistenceAsync(check);
    }

    private static void SourcePixels(Action<string, bool> check)
    {
        var assets = new AssetLibrary();
        string frozen = assets.Add(Png(120, 80, (_, _) => SKColors.Lime));
        var scene = new SceneDocument
        {
            Viewport = new(-80, -40, 120, 80), LayerOpacity = .25,
            Background = new() { Kind = ElementKind.Image, AssetId = frozen, Width = 120, Height = 80, Transform = Affine2D.Translation(-80, -40) },
            Elements = [Box(-60, -25, 20, 20, 0x80FF0000)]
        };
        var selection = Selection(CaptureSource.Background, new(-70, -30, 70, 50));
        var desktop = Desktop(new(-80, -40, 120, 80), (_, _) => SKColors.Blue);
        RectD[] displays = [scene.Viewport];
        string original = JsonSerializer.Serialize(scene);
        var resourceBytes = assets.Resources.ToDictionary(a => a.Id, a => a.Data.ToArray());
        var background = SnapshotImageCommand.Render(scene, assets.Resources, selection, desktop, displays);
        var combined = SnapshotImageCommand.Render(scene, assets.Resources, selection with { Source = CaptureSource.BackgroundAndCanvas }, desktop, displays);
        var canvas = SnapshotImageCommand.Render(scene, assets.Resources, selection with { Source = CaptureSource.CanvasOnly }, null, displays);
        using var bg = Decode(background); using var all = Decode(combined); using var overlay = Decode(canvas);
        check("snapshot background source uses the fresh desktop and excludes original annotations", bg.GetPixel(20, 15) == SKColors.Blue && bg.GetPixel(60, 40) == SKColors.Blue);
        check("snapshot combined source composites the fresh desktop and annotation pixels", all.GetPixel(20, 15).Alpha == 255 && all.GetPixel(20, 15).Red is >= 127 and <= 128 && all.GetPixel(20, 15).Blue is >= 127 and <= 128);
        check("snapshot canvas-only source preserves element alpha and omits both desktop and stored background", overlay.GetPixel(20, 15).Alpha is >= 127 and <= 128 && overlay.GetPixel(20, 15).Red == 255 && overlay.GetPixel(60, 40).Alpha == 0);
        check("all capture sources retain exact original physical coordinates and dimensions", background.Bounds == selection.Bounds && combined.Bounds == selection.Bounds && canvas.Bounds == selection.Bounds && bg.Width == 70 && bg.Height == 50);
        check("source preparation leaves the live document, original background and opacity unchanged", JsonSerializer.Serialize(scene) == original);
        check("source preparation does not append resources or alter original PNG bytes", assets.Count == resourceBytes.Count && assets.Resources.All(r => r.Data.Span.SequenceEqual(resourceBytes[r.Id])));
        foreach (var source in new[] { CaptureSource.Background, CaptureSource.BackgroundAndCanvas })
        {
            bool missing = false;
            try { SnapshotImageCommand.Render(scene, assets.Resources, selection with { Source = source }, null, displays); }
            catch (InvalidOperationException) { missing = true; }
            check($"{source} rejects missing desktop instead of reusing an old frozen background", missing);
        }
        var empty = new SceneDocument { Viewport = scene.Viewport };
        var invisible = SnapshotImageCommand.Render(empty, [], selection with { Source = CaptureSource.CanvasOnly }, null, displays);
        var emptySession = new EditorSession(empty);
        bool refused = false;
        try { SnapshotImageCommand.Insert(emptySession, invisible); }
        catch (InvalidOperationException) { refused = true; }
        check("an empty canvas-only result does not create an invisible image, asset or undo entry", !invisible.HasVisiblePixels && refused && emptySession.Document.Elements.Count == 0 && emptySession.Assets.Count == 0 && emptySession.UndoCount == 0);
        var gap = SnapshotImageCommand.Render(scene, assets.Resources, selection, desktop,
            [new(-80, -40, 45, 80), new(-25, -40, 65, 80)]);
        using var gapPixels = Decode(gap);
        check("production source rendering preserves physical monitor gaps", gapPixels.GetPixel(38, 20).Alpha == 0 && gapPixels.GetPixel(20, 20) == SKColors.Blue && gapPixels.GetPixel(55, 20) == SKColors.Blue);
    }

    private static void LayerOpacityAndLiveEffects(Action<string, bool> check)
    {
        var scene = new SceneDocument { Viewport = new(0, 0, 100, 80), LayerOpacity = .25, Elements = [Box(10, 10, 30, 30, 0xFFFF0000)] };
        var capture = SnapshotImageCommand.Render(scene, [], Selection(CaptureSource.CanvasOnly, new(5, 5, 50, 50)), null, [scene.Viewport]);
        using var raster = Decode(capture);
        check("snapshot PNG does not bake the live canvas's global layer opacity", raster.GetPixel(15, 15) == SKColors.Red);
        var resultSession = new EditorSession(new() { Viewport = scene.Viewport, LayerOpacity = .25 });
        var element = SnapshotImageCommand.Insert(resultSession, capture);
        using var renderer = new SceneRenderer();
        using var displayed = SKBitmap.Decode(renderer.ExportPng(resultSession.Document, resultSession.Assets, true));
        check("inserted snapshot receives global layer opacity only in the normal image render pass", displayed.GetPixel(20, 20).Alpha is >= 63 and <= 64 && element.Style.Opacity == 1);
        check("source preparation preserves the original canvas opacity", scene.LayerOpacity == .25);

        var viewport = new RectD(-80, -40, 160, 120);
        var live = new SceneDocument { Viewport = viewport, LayerOpacity = .4,
            Elements = [new() { Kind = ElementKind.LivePixelate, Width = 90, Height = 60,
                Transform = Affine2D.Translation(-60, -20), Effects = [new() { Kind = ImageEffectKind.Pixelate, Amount = 17 }], Style = new() { StrokeWidth = 0 } }] };
        var desktop = Desktop(new(-80, -40, 160, 120), (x, y) => x % 13 < 5 ? SKColors.White : y % 9 < 4 ? SKColors.Blue : SKColors.Red);
        var entire = SnapshotImageCommand.Render(live, [], Selection(CaptureSource.CanvasOnly, viewport), desktop, [viewport]);
        var region = new RectD(-47, -11, 51, 40);
        var partial = SnapshotImageCommand.Render(live, [], Selection(CaptureSource.CanvasOnly, region), desktop, [viewport]);
        using var fullPixels = Decode(entire); using var partialPixels = Decode(partial);
        check("canvas-only dynamic mosaic freezes sampled desktop only inside the effect", fullPixels.GetPixel(40, 40).Alpha == 255 && fullPixels.GetPixel(150, 110).Alpha == 0 && entire.HasVisiblePixels);
        bool same = true;
        for (int y = 0; y < partialPixels.Height; y++) for (int x = 0; x < partialPixels.Width; x++)
            same &= partialPixels.GetPixel(x, y) == fullPixels.GetPixel(x + 33, y + 29);
        check("production source preparation preserves the full-scene mosaic sampling context for cropped selections", same);
        check("freezing dynamic pixels does not attach the sampled desktop to the original document", live.Background is null && live.Elements.Single().AssetId is null && live.LayerOpacity == .4);
        var zeroOpacity = live.Clone(); zeroOpacity.LayerOpacity = 0;
        var zeroCapture = SnapshotImageCommand.Render(zeroOpacity, [], Selection(CaptureSource.CanvasOnly, viewport), desktop, [viewport]);
        using var zeroPixels = Decode(zeroCapture);
        check("zero global opacity does not bake fallback pixels into a captured dynamic mosaic", zeroPixels.GetPixel(40, 40) == fullPixels.GetPixel(40, 40) && zeroPixels.GetPixel(150, 110).Alpha == 0 && zeroOpacity.LayerOpacity == 0);
    }

    private static void RequestGuards(Action<string, bool> check)
    {
        var target = new EditorSession(new() { Elements = [Box(10, 10, 20, 20, 0xFFFF0000)] });
        var request = new SnapshotCaptureRequest(target, 7);
        check("snapshot request initially accepts only its exact target and committed document", request.MatchesContent(target) && !request.MatchesContent(new(target.Document)) && request.EditingVersion == 7);
        target.SetSelection([target.Document.Elements.Single().Id]);
        check("selection-only changes do not invalidate a committed snapshot source", request.MatchesContent(target));
        target.BeginTransaction("intervening gesture");
        check("an unfinished transaction blocks snapshot insertion", !request.MatchesContent(target));
        target.Cancel();
        check("a cancelled intervening transaction cannot revive the previous snapshot request", !request.MatchesContent(target));
        request = new(target, 9);
        target.Execute("change", document => document.NextNumber++);
        check("committed changes invalidate snapshot insertion", !request.MatchesContent(target));
        target.Undo();
        check("undoing the intervening change does not revive a stale snapshot request", !request.MatchesContent(target));
        request = new(target, 11);
        target.ReplaceDocument(target.Document.Clone());
        check("a replacement document cannot receive a previous document's snapshot", !request.MatchesContent(target));
        request = new(target, 13);
        request.AcceptRestoredVersion(15);
        check("a validated capture continuation advances only the request's recorded editing version", request.EditingVersion == 15 && request.MatchesContent(target));
        request.Cancel();
        request.AcceptRestoredVersion(17);
        check("cancelled capture permission remains revoked after an obsolete continuation arrives", request.Cancelled && !request.MatchesContent(target));
    }

    private static async Task InsertAndPersistenceAsync(Action<string, bool> check)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "snapshot-image-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var assets = new AssetLibrary();
        var original = Box(-60, -30, 20, 25, 0xFFFF0000);
        var live = new SceneDocument { Viewport = new(-100, -60, 180, 120), Elements = [original], LayerOpacity = .65 };
        var session = new EditorSession(live, assets); session.SetSelection([original.Id]);
        string originalElement = JsonSerializer.Serialize(original);
        var defaults = new ToolParameters { Tool = EditorTool.CaptureRegion, CaptureShape = CaptureShape.Ellipse, CaptureSource = CaptureSource.CanvasOnly, CaptureBorderWidth = 4 };
        string defaultState = JsonSerializer.Serialize(defaults);
        var selection = new CaptureSelection(defaults.CaptureShape, defaults.CaptureSource, new(-70, -40, 80, 60), [], defaults.CaptureBorderWidth, defaults.CaptureBorderColor);
        var raster = SnapshotImageCommand.Render(live.Clone(), assets.Resources, selection, null, [live.Viewport]);
        var image = SnapshotImageCommand.Insert(session, raster);
        check("snapshot inserts one ordinary editable PNG image without removing original annotations", session.Document.Kind == DocumentKind.LiveOverlay && session.Document.Elements.Count == 2 && session.Document.Elements.Last().Kind == ElementKind.Image && session.Document.Background is null);
        check("snapshot insertion uses the output's original position and size at 1:1", image.Transform == Affine2D.Translation(raster.Bounds.X, raster.Bounds.Y) && image.Width == raster.Bounds.Width && image.Height == raster.Bounds.Height);
        check("new snapshot is selected and insertion adds exactly one undo entry", session.Selection.SetEquals([image.Id]) && session.UndoCount == 1 && session.RedoCount == 0);
        check("snapshot does not alter original marks or current screenshot parameters", JsonSerializer.Serialize(session.Document.Elements[0]) == originalElement && JsonSerializer.Serialize(defaults) == defaultState);
        check("snapshot asset stores the exact generated PNG bytes", assets.Count == 1 && assets.Get(image.AssetId!).Data.Span.SequenceEqual(raster.PngBytes));
        check("one undo removes only the snapshot and restores the previous selection", session.Undo() && session.Document.Elements.Count == 1 && session.Document.Elements[0].Id == original.Id && session.Selection.SetEquals([original.Id]));
        assets.Prune(session.ReferencedAssetsIncludingHistory());
        check("undo retains PNG resources needed by redo", assets.TryGet(image.AssetId!, out var retained) && retained.Data.Span.SequenceEqual(raster.PngBytes));
        check("one redo restores the same image, resource and selection", session.Redo() && session.Document.Elements.Last().Id == image.Id && session.Document.Elements.Last().AssetId == image.AssetId && session.Selection.SetEquals([image.Id]));

        var project = new ProjectStore(); string projectPath = Path.Combine(directory, "snapshot.floatcanvas");
        await project.SaveAsync(projectPath, session.Document.Clone(), assets);
        var loaded = await project.LoadAsync(projectPath);
        var loadedImage = loaded.Document.Elements.Last();
        check("project roundtrip keeps a normal PNG image with exact alpha pixels and placement", loadedImage.Kind == ElementKind.Image && loadedImage.Transform == image.Transform && loadedImage.Width == image.Width && loadedImage.Height == image.Height && loaded.Assets.Get(loadedImage.AssetId!).Data.Span.SequenceEqual(raster.PngBytes));
        using (var archive = ZipFile.OpenRead(projectPath))
        using (var reader = new StreamReader(archive.GetEntry("scene.json")!.Open()))
            check("project snapshot adds no persistent screenshot geometry or source type", !reader.ReadToEnd().Contains("CaptureShape", StringComparison.Ordinal));
        var reopened = new EditorSession(loaded.Document, loaded.Assets);
        reopened.SetSelection([loadedImage.Id]);
        var before = loadedImage.Transform;
        reopened.UpdateSelection(element => { element.Transform = Affine2D.Translation(20, -10).Compose(element.Transform).Compose(Affine2D.Rotation(13)); element.ImageCrop = new(.1, .1, .8, .8); });
        check("reopened snapshot supports normal image transform and crop editing", reopened.Document.Elements.Last().Transform != before && reopened.Document.Elements.Last().ImageCrop is { Width: .8, Height: .8 });
        check("ordinary image edits remain reversible after project loading", reopened.Undo() && reopened.Document.Elements.Last().Transform == before && reopened.Document.Elements.Last().ImageCrop is null);
        using (var bitmap = Decode(raster))
            check("saved snapshot alpha remains transparent outside its elliptical selection", bitmap.GetPixel(0, 0).Alpha == 0);

        var oldDraft = live.CreateCapture(new(-90, -50, 120, 80), assets.Add(Png(120, 80, (_, _) => SKColors.Blue)));
        string oldState = JsonSerializer.Serialize(oldDraft);
        var workspace = new WorkspaceState { LiveDocument = session.Document.Clone(), CaptureDocument = oldDraft, CaptureOrigin = new(-90, -50) };
        var store = new RecoveryStore(Path.Combine(directory, "recovery"));
        await store.SaveAsync(workspace, assets);
        var restored = await store.LoadAsync();
        check("recovery roundtrip preserves the new live snapshot as a normal image", restored is not null && restored.State.LiveDocument.Elements.Last().Kind == ElementKind.Image && restored.Assets.Get(image.AssetId!).Data.Span.SequenceEqual(raster.PngBytes));
        check("recovery retains the old FrozenCapture draft and original screen origin unchanged", restored?.State.CaptureDocument?.Kind == DocumentKind.FrozenCapture && JsonSerializer.Serialize(restored.State.CaptureDocument) == oldState && restored.State.CaptureOrigin == new PointD(-90, -50));
        using var legacyRenderer = new SceneRenderer();
        using var legacyPixels = SKBitmap.Decode(legacyRenderer.ExportPng(restored!.State.CaptureDocument!, restored.Assets));
        check("retained legacy draft remains renderable and exportable", legacyPixels.Width == 120 && legacyPixels.Height == 80 && legacyPixels.GetPixel(115, 75) == SKColors.Blue);
        Console.WriteLine($"Snapshot image artifacts: {directory}");
    }

    private static CaptureSelection Selection(CaptureSource source, RectD bounds) => new(CaptureShape.Rectangle, source, bounds, [], 0, ColorLibrary.ForegroundRed);
    private static CanvasElement Box(double x, double y, double width, double height, uint fill) => new()
    { Kind = ElementKind.Rectangle, Width = width, Height = height, Transform = Affine2D.Translation(x, y), Style = new() { Fill = fill, StrokeWidth = 0 } };
    private static SKBitmap Decode(CaptureRasterResult result) => SKBitmap.Decode(result.PngBytes) ?? throw new InvalidDataException("Invalid snapshot PNG.");
    private static CapturedDesktop Desktop(PixelBounds bounds, Func<int, int, SKColor> color) => new(bounds, Png(bounds.Width, bounds.Height, color));
    private static byte[] Png(int width, int height, Func<int, int, SKColor> color)
    {
        using var bitmap = new SKBitmap(width, height);
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) bitmap.SetPixel(x, y, color(x, y));
        using var image = SKImage.FromBitmap(bitmap); using var bytes = image.Encode(SKEncodedImageFormat.Png, 100); return bytes.ToArray();
    }
}
