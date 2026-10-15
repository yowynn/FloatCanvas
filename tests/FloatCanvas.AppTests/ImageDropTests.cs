using System.IO;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;
using L = FloatCanvas.Localization.AppText;
using Wpf = System.Windows;

internal static class ImageDropTests
{
    internal static void Run(Action<string, bool> check)
    {
        var assets = new AssetLibrary();
        var live = new EditorSession(new() { Viewport = new(0, 0, 1920, 1080) }, assets);
        var capture = new EditorSession(new() { Kind = DocumentKind.FrozenCapture, Viewport = new(300, 200, 800, 600) }, assets);
        string before = JsonSerializer.Serialize(capture.Document), liveBefore = JsonSerializer.Serialize(live.Document);
        using var request = new ImageDropRequest(capture, new(400, 300, 600, 400), CancellationToken.None);
        check("drop commits to the original inactive capture", request.TryCommit(request, live, capture, [new([1, 2, 3], 120, 80)]) &&
            capture.Document.Elements.Count == 1 && liveBefore == JsonSerializer.Serialize(live.Document));
        check("drop uses its original explicit placement and one undo", capture.Document.Elements.Single().Transform == Affine2D.Translation(640, 460) &&
            capture.UndoCount == 1 && capture.Undo() && before == JsonSerializer.Serialize(capture.Document));
        GuardedCompletion(check);
        PreserveExistingCapture(check);
        FileDecodingAndRetry(check);
        DropView(check);
    }

    private static void GuardedCompletion(Action<string, bool> check)
    {
        foreach (string change in new[] { "cancel", "lifetime", "new request", "replace session", "replace document", "disposed" })
        {
            var target = new EditorSession(); var live = target;
            using var lifetime = new CancellationTokenSource();
            using var request = new ImageDropRequest(target, new(10, 20, 300, 200), lifetime.Token);
            using var other = new ImageDropRequest(target, request.Placement, lifetime.Token);
            var current = request;
            switch (change)
            {
                case "cancel": request.Cancel(); break;
                case "lifetime": lifetime.Cancel(); break;
                case "new request": current = other; break;
                case "replace session": live = new(); break;
                case "replace document": target.ReplaceDocument(new()); break;
                case "disposed": request.Dispose(); break;
            }
            string before = JsonSerializer.Serialize(target.Document);
            check("late drop after " + change + " leaves document, assets and history unchanged",
                !request.TryCommit(current, live, null, [new([4, 5, 6], 100, 60)]) && before == JsonSerializer.Serialize(target.Document) &&
                target.Assets.Count == 0 && target.UndoCount == 0 && live.Document.Elements.Count == 0);
        }
        var session = new EditorSession();
        using var once = new ImageDropRequest(session, new(0, 0, 500, 400), CancellationToken.None);
        check("empty drop never publishes an asset or history entry", !once.TryCommit(once, session, null, []) && session.Assets.Count == 0 && session.UndoCount == 0);
        check("live drop accepts multiple images atomically and selects both", once.TryCommit(once, session, null,
            [new([1], 100, 60), new([2], 80, 40)]) && session.Document.Elements.Count == 2 && session.Selection.Count == 2 && session.UndoCount == 1);
        string committed = JsonSerializer.Serialize(session.Document);
        check("completed request cannot import a second time", !once.TryCommit(once, session, null, [new([3], 10, 10)]) &&
            committed == JsonSerializer.Serialize(session.Document) && session.Assets.Count == 2 && session.UndoCount == 1);
        check("one undo removes the whole drop", session.Undo() && session.Document.Elements.Count == 0 && session.Selection.Count == 0);
        session.Assets.Prune(session.ReferencedAssetsIncludingHistory());
        check("drop assets remain available through undo and redo", session.Assets.Count == 2 && session.Redo() &&
            committed == JsonSerializer.Serialize(session.Document) && session.Document.ReferencedAssets.All(id => session.Assets.TryGet(id, out _)));
    }

    private static void PreserveExistingCapture(Action<string, bool> check)
    {
        var assets = new AssetLibrary(); string original = assets.Add([9, 8, 7]);
        var live = new EditorSession(new(), assets);
        var capture = new EditorSession(new() { Kind = DocumentKind.FrozenCapture, Viewport = new(140, 90, 700, 500),
            Background = new() { Kind = ElementKind.Image, AssetId = original, Width = 1200, Height = 800, Locked = true,
                Effects = [new() { Kind = ImageEffectKind.Redact, Bounds = new(.1, .2, .3, .4) }] } }, assets);
        capture.Add(new() { Kind = ElementKind.Image, AssetId = original, Width = 300, Height = 180,
            ImageCrop = new(.2, .1, .6, .7), Transform = Affine2D.Translation(250, 180).Compose(Affine2D.Rotation(37)),
            Effects = [new() { Kind = ImageEffectKind.Blur, Bounds = new(.25, .3, .4, .2) }] });
        string before = JsonSerializer.Serialize(capture.Document), existing = JsonSerializer.Serialize(capture.Document.Elements[0]);
        var editor = new EditorController(capture) { Tool = EditorTool.Freehand };
        editor.ReplaceSession(live, new(0, 0)); int controllerChanges = 0; editor.Changed += () => controllerChanges++;
        using var request = new ImageDropRequest(capture, new(200, 130, 500, 350), CancellationToken.None);
        check("drop still targets capture after controller returns to live", request.TryCommit(request, live, capture, [new([6, 7, 8], 200, 100)]) &&
            editor.Session == live && editor.Tool == EditorTool.Freehand && live.Document.Elements.Count == 0);
        var stripped = capture.Document.Clone(); stripped.Elements.RemoveAt(1);
        check("drop preserves original crop, effects, transform, background and viewport", before == JsonSerializer.Serialize(stripped) &&
            existing == JsonSerializer.Serialize(capture.Document.Elements[0]) && assets.Get(original).Data.Span.SequenceEqual(new byte[] { 9, 8, 7 }));
        check("inactive capture change needs explicit recovery notification", controllerChanges == 0 && capture.CreateCommittedSnapshot().Elements.Count == 2);
        check("capture drop has one undo restoring the exact document", capture.Undo() && before == JsonSerializer.Serialize(capture.Document));
        assets.Prune(live.ReferencedAssetsIncludingHistory().Concat(capture.ReferencedAssetsIncludingHistory()));
        check("both original and dropped resources survive history pruning", assets.Count == 2 && capture.Redo() && capture.Document.ReferencedAssets.All(id => assets.TryGet(id, out _)));
    }

    private static void FileDecodingAndRetry(Action<string, bool> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "FloatCanvas-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var bitmap = new SKBitmap(24, 16); bitmap.Erase(SKColors.Transparent); bitmap.SetPixel(12, 8, SKColors.Red);
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            string first = Path.Combine(directory, "first.png"), second = Path.Combine(directory, "second.png");
            File.WriteAllBytes(first, encoded.ToArray()); File.WriteAllBytes(second, [0, 1, 2]);
            var session = new EditorSession();
            using var request = new ImageDropRequest(session, new(0, 0, 500, 400), CancellationToken.None);
            bool failed = false;
            try { ImageImportCommand.NormalizeFilesAsync([first, second], request.Token).GetAwaiter().GetResult(); }
            catch (InvalidDataException) { failed = true; }
            check("a failed second decode publishes no partial images, assets or history", failed && session.Assets.Count == 0 && session.UndoCount == 0 && session.Document.Elements.Count == 0 && !request.Completed);
            File.WriteAllBytes(second, encoded.ToArray());
            var normalized = ImageImportCommand.NormalizeFilesAsync([first, second], request.Token).GetAwaiter().GetResult();
            check("same request retries after decoding failure as one import", request.TryCommit(request, session, null, normalized) && session.Document.Elements.Count == 2 && session.UndoCount == 1);
            using var actual = SKBitmap.Decode(session.Assets.Get(session.Document.Elements[0].AssetId!).Data.Span);
            check("shared normalization keeps real transparent alpha and dimensions", actual.Width == 24 && actual.Height == 16 && actual.GetPixel(0, 0).Alpha == 0 && actual.GetPixel(12, 8) == SKColors.Red);
            using var cancelled = new ImageDropRequest(session, new(0, 0, 500, 400), CancellationToken.None);
            cancelled.Cancel(); bool observedCancel = false;
            try { ImageImportCommand.NormalizeFilesAsync([first], cancelled.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { observedCancel = true; }
            check("file decoding honors the request cancellation token", observedCancel && session.Document.Elements.Count == 2 && session.UndoCount == 1);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void DropView(Action<string, bool> check)
    {
        string preference = L.Preference;
        try
        {
            foreach (string language in new[] { "zh-CN", "en" })
            {
                L.Initialize(language); var window = new DropImportWindow(L.Get("ImageDrop.Capture"));
                int drops = 0; string[]? accepted = null;
                window.FilesDropped += paths => { drops++; accepted = paths; window.SetBusy(); };
                Wpf.DragDropEffects Handle(string[] paths, Wpf.DragDropEffects allowed, bool drop = false)
                {
                    var data = new Wpf.DataObject(Wpf.DataFormats.FileDrop, paths);
                    return window.HandleDrag(data, allowed, drop);
                }
                check(language + " drop pad is topmost, modeless and not owned by the hidden toolbar", window.Topmost && window.Owner is null && !window.ShowActivated && !window.ShowInTaskbar && window.Title == L.Get("ImageDrop.Title"));
                check(language + " production drag handler admits supported multi-file Copy", Handle(["one.PNG", "two.webp"], Wpf.DragDropEffects.Copy) == Wpf.DragDropEffects.Copy);
                check(language + " production drag handler rejects a source that disallows Copy", Handle(["one.png"], Wpf.DragDropEffects.Move) == Wpf.DragDropEffects.None);
                check(language + " production drop rejects mixed unsupported files without a callback", Handle(["one.png", "notes.txt"], Wpf.DragDropEffects.Copy, true) == Wpf.DragDropEffects.None && drops == 0);
                check(language + " production drop forwards accepted paths once", Handle(["one.png"], Wpf.DragDropEffects.Copy, true) == Wpf.DragDropEffects.Copy && drops == 1 && accepted!.SequenceEqual(["one.png"]));
                check(language + " busy pad rejects further drops", Handle(["two.png"], Wpf.DragDropEffects.Copy, true) == Wpf.DragDropEffects.None && drops == 1 && !window.AllowDrop);
                window.ShowFailure(L.Format("ImageDrop.Failed", "fixture decode error"));
                check(language + " decoding failure reopens drop for retry", window.AllowDrop && Handle(["retry.bmp"], Wpf.DragDropEffects.Copy, true) == Wpf.DragDropEffects.Copy && drops == 2);
                window.Complete(1);
                check(language + " completed pad rejects a second import", !window.AllowDrop && Handle(["late.jpg"], Wpf.DragDropEffects.Copy, true) == Wpf.DragDropEffects.None && drops == 2);
                check(language + " detached drop test never creates a native window", new System.Windows.Interop.WindowInteropHelper(window).Handle == 0 && !window.IsVisible);
                window.Close();
            }
            check("file feedback rejects empty and blank path lists", !DropImportWindow.SupportsFiles([]) && !DropImportWindow.SupportsFiles([""]));
        }
        finally { L.Initialize(preference); }
    }
}
