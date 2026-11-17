using System.IO;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

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
        check("picker placement remains centered and one undo", capture.Document.Elements.Single().Transform == Affine2D.Translation(640, 460) &&
            capture.UndoCount == 1 && capture.Undo() && before == JsonSerializer.Serialize(capture.Document));
        GuardedCompletion(check);
        GuardedAuthoringContext(check);
        PreserveExistingCapture(check);
        FileDecodingAndRetry(check);
        DropPosition(check);
        GroupViewDropPosition(check);
        EditingHandoff(check);
    }

    private static void EditingHandoff(Action<string, bool> check)
    {
        foreach (string change in new[] { "epoch", "content", "scope", "mapping", "cancel", "entry failure", "later navigation" })
        {
            var target = new EditorSession(new() { Viewport = new(0, 0, 600, 400) });
            object epoch = new(); var mapping = CanvasMapping.Create(target.Document);
            using var request = new ImageDropRequest(target, target.Document.Viewport, CancellationToken.None,
                mapping: () => mapping, viewEpoch: () => epoch);
            bool continued = request.TryEnterEditing(request, target, null, target, () =>
            {
                epoch = new();
                if (change == "content") target.Add(new() { Width = 10, Height = 10 });
                if (change == "scope") target.SetRootCanvas(RootCanvasKind.Foreground);
                if (change == "mapping") mapping = CanvasMapping.CreateViewing(target.Document, new CanvasViewTransform(2, default));
                if (change == "cancel") request.Cancel();
                return change != "entry failure";
            });
            if (change == "later navigation") epoch = new();
            bool committed = continued && request.TryCommit(request, target, null, [new([1, 2, 3], 20, 15)]);
            check("drop handoff only accepts its intentional epoch change: " + change, committed == (change == "epoch"));
        }
    }

    private static void GroupViewDropPosition(Action<string, bool> check)
    {
        var target = new EditorSession(new() { Viewport = new(0, 0, 600, 400) });
        var display = new SceneDocument { Viewport = new(0, 0, 1000, 700) };
        display.Canvas.Projection = CanvasCalibration.CreateInitial(display.Viewport);
        display.Canvas.Projection.Enabled = true;
        display.Canvas.Projection.Matrix = new(1.2, .05, 40, .03, .95, 22, .0002, .0001, 1);
        var placement = new LayerPlacement(Affine2D.Translation(120, 80).Compose(Affine2D.Rotation(18)).Compose(Affine2D.Scale(1.4, .8)), 1);
        var mapping = CanvasMapping.CreateView(display, placement);
        object epoch = new(); var initialEpoch = epoch;
        string camera = JsonSerializer.Serialize(target.Document.Canvas);
        using var request = new ImageDropRequest(target, new(0, 0, 400, 300), CancellationToken.None,
            mapping: () => mapping, viewEpoch: () => epoch);
        var sourcePoint = new PointD(90, 70); var origin = new PointD(-1300, 40);
        var physical = mapping.ToScreen(sourcePoint) + origin;
        check("a root-canvas drop resolves physical coordinates through the entered instance and root perspective",
            request.TrySetDropPoint(physical, origin) && request.DropCenter!.Value.DistanceTo(sourcePoint) < 1e-8);
        check("a root-canvas drop commits into its real source graph without baking the root camera",
            request.TryCommit(request, target, null, [new([7, 8, 9], 40, 30)]) &&
            ElementGeometry.Bounds(target.Document.Elements.Single(), includeStroke: false).Center.DistanceTo(sourcePoint) < 1e-8 &&
            JsonSerializer.Serialize(target.Document.Canvas) == camera && target.UndoCount == 1 && target.Undo());
        using var pending = new ImageDropRequest(target, new(0, 0, 400, 300), CancellationToken.None,
            mapping: () => mapping, viewEpoch: () => epoch);
        epoch = new();
        check("switching the group canvas invalidates an unfinished drop even with identical camera matrices",
            !pending.IsCurrent(pending, target, null));
        epoch = initialEpoch;
        check("a late drop cannot revive after returning to the original group view",
            !pending.TryCommit(pending, target, null, [new([7, 8, 9], 40, 30)]) && target.Document.Elements.Count == 0);
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

    private static void GuardedAuthoringContext(Action<string, bool> check)
    {
        foreach (string change in new[] { "scope", "scope ABA", "author edit", "foreign transaction", "context callback", "context callback ABA" })
        {
            var target = new EditorSession(); Guid group = target.CreatePopulatedGroup("Target");
            using var request = new ImageDropRequest(target, new(0, 0, 500, 400), CancellationToken.None);
            bool context = true;
            request.BindCompletionGuard(() => context);
            switch (change)
            {
                case "scope": target.SetEditScope(group); break;
                case "scope ABA": target.SetEditScope(group); target.SetEditScope(null); break;
                case "author edit": target.Add(new() { Kind = ElementKind.Rectangle, Width = 10, Height = 10 }); break;
                case "foreign transaction": target.BeginTransaction("foreign edit"); break;
                case "context callback": context = false; break;
                case "context callback ABA": context = false; _ = request.IsCurrent(request, target, null); context = true; break;
            }
            string before = JsonSerializer.Serialize(target.Document); int undo = target.UndoCount; long transaction = target.TransactionId;
            check("drop rejects an expired " + change + " context without publishing assets", !request.TryCommit(request, target, null, [new([1], 10, 10)]) &&
                JsonSerializer.Serialize(target.Document) == before && target.Assets.Count == 0 && target.UndoCount == undo);
            if (change == "foreign transaction")
            {
                check("drop rejection preserves the other command's active transaction", target.IsTransactionActive && target.TransactionId == transaction);
                target.Cancel();
            }
        }
        var current = new EditorSession(); current.BeginTransaction("existing edit");
        using var pending = new ImageDropRequest(current, new(0, 0, 500, 400), CancellationToken.None);
        current.Cancel();
        check("a request created while another transaction is active cannot revive after its cancellation", !pending.IsCurrent(pending, current, null) && current.Assets.Count == 0);
        var draft = new EditorSession();
        using var draftRequest = new ImageDropRequest(draft, new(0, 0, 500, 400), CancellationToken.None);
        check("explicit active prefab draft may receive a drop without becoming the main live canvas", draftRequest.TryCommit(draftRequest, new(), null, [new([7], 10, 10)], draft) &&
            draft.Document.Elements.Count == 1 && draft.UndoCount == 1);
    }

    private static void PreserveExistingCapture(Action<string, bool> check)
    {
        var assets = new AssetLibrary(); string original = assets.Add([9, 8, 7]);
        var live = new EditorSession(new(), assets);
        var capture = new EditorSession(new() { Kind = DocumentKind.FrozenCapture, Viewport = new(140, 90, 700, 500),
            Background = new() { Kind = ElementKind.Image, AssetId = original, Width = 1200, Height = 800, Locked = true,
                Effects = [new() { Kind = ImageEffectKind.Blur, Bounds = new(.1, .2, .3, .4) }] } }, assets);
        capture.Add(new() { Kind = ElementKind.Image, AssetId = original, Width = 300, Height = 180,
            ImageCrop = new(.2, .1, .6, .7), Transform = Affine2D.Translation(250, 180).Compose(Affine2D.Rotation(37)),
            Effects = [new() { Kind = ImageEffectKind.Blur, Bounds = new(.25, .3, .4, .2) }] });
        string before = JsonSerializer.Serialize(capture.Document), existing = JsonSerializer.Serialize(capture.Document.Elements[0]);
        var editor = new EditorController(capture) { Tool = EditorTool.Freehand };
        editor.ReplaceSession(live, new(0, 0)); int controllerChanges = 0; editor.Changed += () => controllerChanges++;
        using var request = new ImageDropRequest(capture, new(200, 130, 500, 350), CancellationToken.None);
        check("drop still targets capture after controller returns to live", request.TryCommit(request, live, capture, [new([6, 7, 8], 200, 100)]) &&
            editor.Session == live && editor.Tool == EditorTool.Freehand && live.Document.Elements.Count == 0);
        var stripped = capture.Document.Clone(); new SceneGraph(stripped).RemoveNodes([stripped.Elements[1].Id]);
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

    private static void DropPosition(Action<string, bool> check)
    {
        foreach (var (label, viewport, origin, screenPoint) in new[]
        {
            ("negative monitor", new RectD(-3840, -1080, 3840, 2160), new PointD(0, 0), new PointD(-2700, -520)),
            ("frozen crop with screen origin", new RectD(300, 200, 800, 600), new PointD(1200, 500), new PointD(1750, 900))
        })
        {
            var session = new EditorSession(new() { Viewport = viewport });
            using var request = new ImageDropRequest(session, viewport, CancellationToken.None);
            check(label + " stores the release point with the original mapping", request.TrySetDropPoint(screenPoint, origin) && request.DropCenter == screenPoint - origin);
            check(label + " does not recalculate placement at completion", !request.TrySetDropPoint(new(1, 2), new(3, 4)) &&
                request.TryCommit(request, session, null, [new([1], 1200, 800), new([2], 60, 40)]));
            var elements = session.Document.Elements;
            check(label + " first image center matches the actual release point", ElementGeometry.Bounds(elements[0]).Center.DistanceTo(screenPoint - origin) < 1e-8);
            check(label + " additional images offset from the same release point", ElementGeometry.Bounds(elements[1]).Center == screenPoint - origin + new PointD(20, 20));
            check(label + " imported images fit the original viewport", elements[0].Width <= viewport.Width * .8 && elements[0].Height <= viewport.Height * .8);
            check(label + " multiple images share one undo", session.UndoCount == 1 && session.Undo() && session.Document.Elements.Count == 0 && session.Redo() && session.Document.Elements.Count == 2);
        }
        var invalid = new EditorSession();
        using var bad = new ImageDropRequest(invalid, new(0, 0, 100, 100), CancellationToken.None);
        check("invalid drop coordinates are rejected before asset publication", !bad.TrySetDropPoint(new(double.NaN, 0), new()) &&
            !bad.TrySetDropPoint(new(), new(0, double.PositiveInfinity)) && bad.DropCenter is null && invalid.Assets.Count == 0);
        bad.Cancel();
        check("cancelled request cannot accept a later release point", !bad.TrySetDropPoint(new(10, 10), new()));
    }
}
