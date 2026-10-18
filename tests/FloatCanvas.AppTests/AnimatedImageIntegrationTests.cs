using System.IO;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using SkiaSharp;
using Wpf = System.Windows;

internal static class AnimatedImageIntegrationTests
{
    // Same 3x1, red/blue, 100ms-per-frame GIF as the native playback fixture.
    private static byte[] Gif() => Convert.FromBase64String("R0lGODlhAwABAPIAAAAAAP8AAAD/AAAA////AP///wD///8A/yH/C05FVFNDQVBFMi4wAwEAAAAh+QQFCgAAACwAAAAAAwABAAADBBgYGAkAIfkEBQoAAAAsAAAAAAMAAQAAAwQ4ODgJADs=");

    public static void Run(Action<string, bool> check)
    {
        byte[] gif = Gif(), webp = Webp(), png = Png(SKColors.Green);
        ClipboardRepresentations(gif, webp, png, check);
        Task.Run(() => PersistenceAndStampsAsync(gif, webp, check)).GetAwaiter().GetResult();
    }

    private static void ClipboardRepresentations(byte[] gif, byte[] webp, byte[] png, Action<string, bool> check)
    {
        foreach (var (format, bytes, mediaType) in new[]
        {
            ("GIF", gif, "image/gif"), ("image/gif", gif, "image/gif"),
            ("WebP", webp, "image/webp"), ("WEBP", webp, "image/webp"), ("image/webp", webp, "image/webp")
        })
        foreach (bool asStream in new[] { false, true })
        {
            // This is only an in-memory IDataObject; never accesses the system clipboard.
            var data = new Wpf.DataObject();
            data.SetData("PNG", png, autoConvert: false);
            using var stream = new MemoryStream(bytes, writable: false); stream.Position = stream.Length;
            data.SetData(format, asStream ? stream : bytes, autoConvert: false);
            byte[] actual = DesktopSession.ReadClipboardImage(data) ?? throw new InvalidOperationException("Missing clipboard representation.");
            check($"in-memory clipboard {format} original takes precedence over PNG ({asStream})", actual.SequenceEqual(bytes));
            var normalized = AssetImport.Normalize(actual);
            check($"in-memory clipboard {format} normalizes without flattening its animation ({asStream})", normalized.MediaType == mediaType && normalized.PngBytes.SequenceEqual(bytes));
        }
        var pngOnly = new Wpf.DataObject(); pngOnly.SetData("PNG", png, autoConvert: false);
        check("in-memory clipboard PNG-only source remains a static fallback", DesktopSession.ReadClipboardImage(pngOnly)!.SequenceEqual(png));
        foreach (var (format, wrongBytes, asStream) in new[] { ("image/gif", png, false), ("image/webp", gif, true) })
        {
            var mismatched = new Wpf.DataObject(); mismatched.SetData("PNG", png, autoConvert: false);
            using var stream = new MemoryStream(wrongBytes, writable: false);
            mismatched.SetData(format, asStream ? stream : wrongBytes, autoConvert: false);
            bool refused = false;
            try { _ = DesktopSession.ReadClipboardImage(mismatched); }
            catch (InvalidDataException) { refused = true; }
            check($"in-memory clipboard {format} signature mismatch is rejected despite a valid PNG fallback", refused);
        }
    }

    private static async Task PersistenceAndStampsAsync(byte[] gif, byte[] webp, Action<string, bool> check)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "animation-integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string gifPath = Path.Combine(directory, "two-color-loop.gif"), webpPath = Path.Combine(directory, "two-color-loop.webp");
        await File.WriteAllBytesAsync(gifPath, gif); await File.WriteAllBytesAsync(webpPath, webp);
        var imported = await ImageImportCommand.NormalizeFilesAsync([gifPath, webpPath], CancellationToken.None);
        check("file import keeps GIF/WebP original bytes and media types", imported.Count == 2 &&
            imported[0].MediaType == "image/gif" && imported[0].PngBytes.SequenceEqual(gif) &&
            imported[1].MediaType == "image/webp" && imported[1].PngBytes.SequenceEqual(webp));
        var drop = await DesktopSession.NormalizeCanvasDropAsync(new([], [gif, webp]), CancellationToken.None);
        check("drop normalization keeps GIF/WebP animation bytes and media types", drop.Count == 2 &&
            drop[0].MediaType == "image/gif" && drop[0].PngBytes.SequenceEqual(gif) &&
            drop[1].MediaType == "image/webp" && drop[1].PngBytes.SequenceEqual(webp));
        var importedSession = new EditorSession();
        ImageImportCommand.Insert(importedSession, imported, new(0, 0, 100, 100));
        check("import insertion keeps two animated sources in one undo transaction", importedSession.Document.Elements.Count == 2 && importedSession.UndoCount == 1 &&
            importedSession.Assets.Resources.Any(asset => asset.MediaType == "image/gif" && asset.Data.Span.SequenceEqual(gif)) &&
            importedSession.Assets.Resources.Any(asset => asset.MediaType == "image/webp" && asset.Data.Span.SequenceEqual(webp)));

        var assets = new AssetLibrary(); string gifId = assets.Add(gif, "image/gif"), webpId = assets.Add(webp, "image/webp");
        var document = Scene(gifId);
        document.Elements.Add(new() { Kind = ElementKind.Image, AssetId = webpId, Width = 30, Height = 10, Transform = Affine2D.Translation(2, 14), Style = new() { StrokeWidth = 0 } });
        document.Viewport = new(0, 0, 40, 26);
        var projectStore = new ProjectStore(); string projectPath = Path.Combine(directory, "animated.floatcanvas");
        await projectStore.SaveAsync(projectPath, document, assets);
        var project = await projectStore.LoadAsync(projectPath);
        CheckResources("project roundtrip", project.Assets);
        CheckPlayback("project roundtrip GIF", project.Document, project.Assets, project.Document.Elements[0]);
        CheckPlayback("project roundtrip WebP", project.Document, project.Assets, project.Document.Elements[^1]);
        var recoveryStore = new RecoveryStore(Path.Combine(directory, "recovery"));
        await recoveryStore.SaveAsync(new() { LiveDocument = document.Clone() }, assets);
        var recovered = await recoveryStore.LoadAsync() ?? throw new InvalidOperationException("Missing saved recovery.");
        CheckResources("recovery roundtrip", recovered.Assets);
        CheckPlayback("recovery roundtrip GIF", recovered.State.LiveDocument, recovered.Assets, recovered.State.LiveDocument.Elements[0]);
        CheckPlayback("recovery roundtrip WebP", recovered.State.LiveDocument, recovered.Assets, recovered.State.LiveDocument.Elements[^1]);

        var source = new EditorSession(Scene(gifId), assets); source.SetSelection(source.Document.Elements.Select(element => element.Id));
        var library = new StampLibrary(Path.Combine(directory, "profile")); await library.LoadAsync();
        var editable = await library.SaveSelectionAsync(source, "Animated editable group", StampSaveMode.EditableGroup);
        var reloadedLibrary = new StampLibrary(Path.Combine(directory, "profile")); await reloadedLibrary.LoadAsync();
        var editablePackage = await reloadedLibrary.LoadStampAsync(editable.Id);
        check("reloaded editable stamp preserves its GIF source and vector child", editablePackage.Document.Elements.Count == 2 &&
            editablePackage.Document.Elements.Any(element => element.Kind == ElementKind.Rectangle) &&
            editablePackage.Assets.Get(gifId).MediaType == "image/gif" && editablePackage.Assets.Get(gifId).Data.Span.SequenceEqual(gif));
        var target = new EditorSession(new() { Viewport = new(0, 0, 80, 40) });
        StampLibrary.Place(editablePackage, target, new(40, 20));
        var placedGif = target.Document.Elements.Single(element => element.Kind == ElementKind.Image);
        Guid? group = target.Document.RootGroupId(placedGif);
        check("animated editable stamp places image and vector as one group with one undo", target.UndoCount == 1 && target.Document.Elements.Count == 2 &&
            group is not null && target.Document.Elements.All(element => target.Document.RootGroupId(element) == group));
        CheckPlayback("placed editable stamp", target.Document, target.Assets, placedGif);
        var placedIds = target.Document.Elements.Select(element => element.Id).ToArray();
        check("one undo/redo preserves the animated stamp's group and source bytes", target.Undo() && target.Document.Elements.Count == 0 && target.Redo() &&
            target.Document.Elements.Select(element => element.Id).SequenceEqual(placedIds) && target.Document.RootGroupId(target.Document.Elements[0]) == group && target.Assets.Get(gifId).Data.Span.SequenceEqual(gif));

        var frozen = await reloadedLibrary.SaveSelectionAsync(source, "Frozen blue frame", StampSaveMode.RasterMerge,
            renderContext: new(source.Document.Clone(), assets, TimeSpan.FromMilliseconds(150)));
        var merged = await reloadedLibrary.LoadStampAsync(frozen.Id);
        var mergedImage = merged.Document.Elements.Single();
        var mergedAsset = merged.Assets.Get(mergedImage.AssetId!);
        check("raster stamp stores a static PNG at the requested animation time", mergedImage.Kind == ElementKind.Image && mergedAsset.MediaType == "image/png" &&
            FramePixel(merged.Document, merged.Assets, mergedImage, 0) == SKColors.Blue && FramePixel(merged.Document, merged.Assets, mergedImage, 150) == SKColors.Blue);
        var retainedSource = await reloadedLibrary.LoadSourceAsync(frozen.Id);
        check("raster stamp retains the original GIF in its editable source package", retainedSource.Assets.Get(gifId).MediaType == "image/gif" &&
            retainedSource.Assets.Get(gifId).Data.Span.SequenceEqual(gif) && retainedSource.Document.Elements.Count == 2);
        CheckPlayback("raster stamp editable source", retainedSource.Document, retainedSource.Assets, retainedSource.Document.Elements.Single(element => element.Kind == ElementKind.Image));

        var selection = new CaptureSelection(CaptureShape.Rectangle, CaptureSource.CanvasOnly, source.Document.Viewport, [], 0, 0);
        var snapshotRed = SnapshotImageCommand.Render(source.Document, assets.Resources, selection, null, [source.Document.Viewport], TimeSpan.Zero);
        var snapshotBlue = SnapshotImageCommand.Render(source.Document, assets.Resources, selection, null, [source.Document.Viewport], TimeSpan.FromMilliseconds(150));
        check("snapshot command freezes the caller's actual current animation frame", Pixel(snapshotRed.PngBytes, 17, 7) == SKColors.Red && Pixel(snapshotBlue.PngBytes, 17, 7) == SKColors.Blue);
        var snapshotTarget = new EditorSession(new() { Viewport = source.Document.Viewport });
        var snapshotImage = SnapshotImageCommand.Insert(snapshotTarget, snapshotBlue);
        check("snapshot insertion stays a static PNG at later playback times", snapshotTarget.UndoCount == 1 &&
            snapshotTarget.Assets.Get(snapshotImage.AssetId!).MediaType == "image/png" && FramePixel(snapshotTarget.Document, snapshotTarget.Assets, snapshotImage, 0) == SKColors.Blue &&
            FramePixel(snapshotTarget.Document, snapshotTarget.Assets, snapshotImage, 500) == SKColors.Blue);
        byte[] historyPreview = WorkspacePreview.Render(source.Document, assets, TimeSpan.FromMilliseconds(150)) ?? throw new InvalidOperationException("Missing history preview.");
        check("workspace history preview freezes its requested animation time at the blue frame", Pixel(historyPreview, 17, 7) == SKColors.Blue);

        void CheckResources(string label, AssetLibrary actual) => check(label + " preserves exact GIF/WebP source bytes and media types", actual.Get(gifId).MediaType == "image/gif" &&
            actual.Get(gifId).Data.Span.SequenceEqual(gif) && actual.Get(webpId).MediaType == "image/webp" && actual.Get(webpId).Data.Span.SequenceEqual(webp));
        void CheckPlayback(string label, SceneDocument actual, AssetLibrary actualAssets, CanvasElement image) =>
            check(label + " renders real red at 0ms and blue at 150ms", FramePixel(actual, actualAssets, image, 0) == SKColors.Red && FramePixel(actual, actualAssets, image, 150) == SKColors.Blue);
    }

    private static SceneDocument Scene(string id) => new()
    {
        Viewport = new(0, 0, 40, 16),
        Elements =
        [
            new() { Kind = ElementKind.Image, AssetId = id, Width = 30, Height = 10, Transform = Affine2D.Translation(2, 2), Style = new() { StrokeWidth = 0 } },
            new() { Kind = ElementKind.Rectangle, Width = 3, Height = 8, Transform = Affine2D.Translation(35, 2), Style = new() { StrokeWidth = 0, Fill = 0xFF00FF00 } }
        ]
    };

    private static SKColor FramePixel(SceneDocument document, AssetLibrary assets, CanvasElement element, int milliseconds)
    {
        using var renderer = new SceneRenderer { AnimationTime = TimeSpan.FromMilliseconds(milliseconds) };
        byte[] png = renderer.ExportPng(document, assets, true);
        var center = element.Transform.TransformPoint(new(element.Width / 2, element.Height / 2));
        return Pixel(png, (int)(center.X - document.Viewport.X), (int)(center.Y - document.Viewport.Y));
    }
    private static SKColor Pixel(byte[] png, int x, int y) { using var bitmap = SKBitmap.Decode(png); return bitmap.GetPixel(x, y); }
    private static byte[] Png(SKColor color) { using var bitmap = new SKBitmap(3, 1); bitmap.Erase(color); using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray(); }
    private static byte[] Webp()
    {
        using var red = new SKBitmap(3, 1); red.Erase(SKColors.Red);
        using var blue = new SKBitmap(3, 1); blue.Erase(SKColors.Blue);
        using var data = SKWebpEncoder.EncodeAnimated([new(red, TimeSpan.FromMilliseconds(100)), new(blue, TimeSpan.FromMilliseconds(100))],
            new(SKWebpEncoderCompression.Lossless, 100)) ?? throw new InvalidOperationException("Animated WebP encoding failed.");
        return data.ToArray();
    }
}
