using System.IO;
using System.Reflection;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using Wpf = System.Windows;

/// <summary>Exercises the PNG clipboard extraction and file-import call sites without accessing the system clipboard.</summary>
internal static class EncodedPngImportTests
{
    // A 3x2 straight-alpha PNG with alpha 0/1/127/128/210/255 and nonzero RGB under alpha 0.
    // Encoded directly as PNG scanlines, so creating the fixture has no premultiply round trip.
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAMAAAACCAYAAACddGYaAAAAI0lEQVR4nGNQbn5wSTD6NKPt/O/1DIbTXja4b7/NIN317D8AmI0MdpKmXpIAAAAASUVORK5CYII=";

    internal static void Run(Action<string, bool> check)
    {
        ImagePresetOpacityTests.Run(check);
        byte[] original = Convert.FromBase64String(Png);
        var readPng = typeof(DesktopSession).GetMethod("ReadClipboardPng", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (bool asStream in new[] { false, true })
        {
            string branch = asStream ? "PNG stream" : "PNG bytes";
            using var stream = new MemoryStream(original, writable: false);
            stream.Position = stream.Length; // The production reader must rewind seekable clipboard streams.
            var data = new Wpf.DataObject();
            data.SetData("PNG", asStream ? stream : original.ToArray(), autoConvert: false);
            var extracted = (byte[]?)readPng.Invoke(null, [data]);
            check(branch + " extraction preserves the exact externally supplied bytes", extracted is not null && extracted.AsSpan().SequenceEqual(original));
            var normalized = AssetImport.Normalize(extracted!);
            VerifyStoredOriginal(branch, normalized, original, check);
        }

        string directory = Path.Combine(Path.GetTempPath(), "FloatCanvas-png-original-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string imagePath = Path.Combine(directory, "transparent.png");
            File.WriteAllBytes(imagePath, original);
            var images = ImageImportCommand.NormalizeFilesAsync([imagePath], CancellationToken.None).GetAwaiter().GetResult();
            check("file import normalizes one image at its original dimensions", images is [{ Width: 3, Height: 2 }]);
            VerifyStoredOriginal("PNG file", images.Single(), original, check);

            var target = new EditorSession();
            using var drop = new ImageDropRequest(target, new(0, 0, 80, 60), CancellationToken.None);
            check("drop request commits the decoded file to its intended document", drop.TryCommit(drop, target, null, images));
            check("drop request retains the exact encoded original", target.Assets.Get(target.Document.Elements.Single().AssetId!).Data.Span.SequenceEqual(original));

            string projectPath = Path.Combine(directory, "original.floatcanvas");
            var store = new ProjectStore();
            store.SaveAsync(projectPath, target.Document.Clone(), target.Assets).GetAwaiter().GetResult();
            File.Delete(imagePath);
            var loaded = store.LoadAsync(projectPath).GetAwaiter().GetResult();
            var loadedImage = loaded.Document.Elements.Single();
            check("project reopened after source deletion retains the original PNG bytes", loadedImage.Kind == ElementKind.Image &&
                loaded.Assets.Get(loadedImage.AssetId!).Data.Span.SequenceEqual(original));
            var session = new EditorSession(loaded.Document, loaded.Assets);
            session.Selection.Add(loadedImage.Id);
            var before = loadedImage.Transform;
            session.UpdateSelection(e => e.Transform = Affine2D.Translation(30, 15).Compose(e.Transform));
            check("reopened image remains editable and one undo restores its transform", session.Undo() && session.Document.Elements.Single().Transform == before);
            check("editing after reopening does not re-encode the retained PNG", session.Assets.Get(loadedImage.AssetId!).Data.Span.SequenceEqual(original));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void VerifyStoredOriginal(string branch, NormalizedImage normalized, byte[] original, Action<string, bool> check)
    {
        check(branch + " normalization retains the original encoded image", normalized.PngBytes.AsSpan().SequenceEqual(original));
        var session = new EditorSession();
        ImageImportCommand.Insert(session, [normalized], new(0, 0, 80, 60));
        var image = session.Document.Elements.Single();
        string assetId = image.AssetId!;
        check(branch + " insertion stores the original as an editable image resource", image.Kind == ElementKind.Image &&
            session.Assets.Get(assetId).Data.Span.SequenceEqual(original) && session.Selection.SetEquals([image.Id]));
        check(branch + " undo and redo retain the original resource", session.Undo() && session.Document.Elements.Count == 0 &&
            session.Redo() && session.Document.Elements.Single().AssetId == assetId && session.Assets.Get(assetId).Data.Span.SequenceEqual(original));
    }
}
