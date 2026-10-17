using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using SkiaSharp;

internal static class ObjectClipboardTests
{
    internal static void Run(Action<string, bool> check)
    {
        var assets = new AssetLibrary();
        using var image = SKImage.FromBitmap(new SKBitmap(12, 8));
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        byte[] png = encoded.ToArray();
        string asset = assets.Add(png);
        var outer = new CanvasGroup { Name = "outer" };
        var inner = new CanvasGroup { ParentId = outer.Id, Name = "inner" };
        var unused = new CanvasGroup { Name = "unselected" };
        var rectangle = new CanvasElement { Kind = ElementKind.Rectangle, GroupId = outer.Id, Width = 40, Height = 30,
            Transform = Affine2D.Translation(100, 100).Compose(Affine2D.Rotation(20)), Style = new() { Fill = 0xFF0088FF } };
        var picture = new CanvasElement { Kind = ElementKind.Image, GroupId = inner.Id, Width = 80, Height = 60,
            Transform = Affine2D.Translation(180, 120), AssetId = asset, Locked = true, ImageCrop = new(.1, .1, .8, .8),
            Effects = [new() { Kind = ImageEffectKind.Pixelate, Bounds = new(.2, .3, .4, .5), Amount = 7 }] };
        var number = new CanvasElement { Kind = ElementKind.Number, GroupId = outer.Id, Number = 42, Width = 32, Height = 32,
            Transform = Affine2D.Translation(140, 160) };
        var hidden = new CanvasElement { Kind = ElementKind.Text, GroupId = inner.Id, Text = "hidden", Visible = false, Width = 90, Height = 30 };
        var outside = new CanvasElement { Kind = ElementKind.Text, GroupId = unused.Id, Text = "unselected", Width = 80, Height = 20,
            Transform = Affine2D.Translation(1000, 1000) };
        var source = new EditorSession(new() { Groups = [outer, inner, unused], Elements = [rectangle, picture, number, hidden, outside], LayerOpacity = .6 }, assets);
        source.SetSelection([rectangle.Id, picture.Id, number.Id, hidden.Id]);
        string sourceBefore = JsonSerializer.Serialize(source.Document);

        var copy = ObjectClipboardCommand.CreateSnapshot(source);
        SceneValidation.Validate(copy);
        check("clipboard snapshot contains visible selected members without unselected or hidden content", copy.Elements.Count == 3 &&
            copy.Elements.All(element => element.Id != hidden.Id && element.Id != outside.Id));
        check("clipboard snapshot preserves nested group ancestry and removes unused groups", copy.Groups.Count == 2 &&
            copy.Groups.Single(group => group.Id == inner.Id).ParentId == outer.Id && copy.RootGroupId(copy.Elements[0]) == outer.Id);
        check("clipboard snapshot preserves original desktop coordinates and layer opacity", copy.LayerOpacity == .6 &&
            copy.Elements.Single(element => element.Id == rectangle.Id).Transform == rectangle.Transform &&
            copy.Viewport.Contains(ElementGeometry.Bounds(picture).Center));
        check("clipboard copy keeps cropped image effects and immutable asset references", copy.ReferencedAssets.SequenceEqual([asset]) &&
            copy.Elements.Single(element => element.Id == picture.Id) is { ImageCrop: { } crop, Effects: [{ Amount: 7 }] } && crop == picture.ImageCrop);
        check("clipboard copy never changes the source document, selection or history", JsonSerializer.Serialize(source.Document) == sourceBefore &&
            source.Selection.SetEquals([rectangle.Id, picture.Id, number.Id, hidden.Id]) && source.UndoCount == 0);

        // A receiving document may already contain the same source group IDs (paste in the same canvas).
        var existing = new CanvasElement { Kind = ElementKind.Rectangle, GroupId = outer.Id, Width = 10, Height = 10 };
        var target = new EditorSession(new() { Groups = [outer.Clone()], Elements = [existing] });
        target.SetSelection([existing.Id]);
        string targetBefore = JsonSerializer.Serialize(target.Document);
        ObjectClipboardCommand.Insert(target, copy, assets, new(0, 0, 1920, 1080));
        var inserted = target.SelectedElements().ToArray();
        SceneValidation.Validate(target.Document);
        check("paste imports the selection and group hierarchy in one transaction", inserted.Length == 3 && target.Document.Groups.Count == 3 &&
            target.UndoCount == 1 && target.UndoLabel == FloatCanvas.Localization.AppText.Get("History.Paste"));
        check("paste assigns fresh object, image effect and group identities", !inserted.Select(element => element.Id).Intersect(copy.Elements.Select(element => element.Id)).Any() &&
            !inserted.Select(element => element.GroupId).Intersect(copy.Elements.Select(element => element.GroupId)).Any() &&
            inserted.Single(element => element.Kind == ElementKind.Image).Effects.Single().Id != picture.Effects.Single().Id);
        var pastedRectangle = inserted.Single(element => element.Kind == ElementKind.Rectangle);
        check("paste uses the original 16px offset and preserves rotation and size", pastedRectangle.Width == rectangle.Width && pastedRectangle.Height == rectangle.Height &&
            pastedRectangle.Transform == Affine2D.Translation(16, 16).Compose(rectangle.Transform));
        check("pasted nested members remain one group and can also be selected individually", target.SelectionUnitIds(pastedRectangle.Id).Count() == 3 &&
            target.SelectionUnitIds(pastedRectangle.Id, individual: true).SequenceEqual([pastedRectangle.Id]));
        check("pasted content is unlocked and numbering advances monotonically", inserted.All(element => !element.Locked) && target.Document.NextNumber == 43);
        check("paste transfers the original image bytes into the receiving resource library", target.Assets.Get(asset).Data.Span.SequenceEqual(png));
        check("undo removes exactly the pasted objects and group metadata", target.Undo() && JsonSerializer.Serialize(target.Document) == targetBefore && target.Selection.SetEquals([existing.Id]));
        check("redo restores the full editable group hierarchy", target.Redo() && target.Document.Elements.Count == 4 && target.Document.Groups.Count == 3 &&
            target.SelectionUnitIds(target.SelectedElements().First().Id).Count() == 3);
        var firstIds = target.Selection.ToHashSet();
        ObjectClipboardCommand.Insert(target, copy, assets, new(0, 0, 1920, 1080));
        check("repeated paste creates independent groups rather than joining earlier copies", !target.Selection.Overlaps(firstIds) &&
            target.SelectionUnitIds(target.SelectedElements().First().Id).Count() == 3 && target.UndoCount == 2);
        check("paste never alters the clipboard document or its lock and transform data", copy.Elements.Single(element => element.Id == picture.Id).Locked &&
            copy.Elements.Single(element => element.Id == rectangle.Id).Transform == rectangle.Transform);

        var offscreen = new EditorSession();
        var visible = new RectD(-1000, -500, 600, 400);
        ObjectClipboardCommand.Insert(offscreen, copy, assets, visible);
        var pastedBounds = offscreen.SelectionBounds()!.Value;
        check("offscreen paste recenters the whole group into a negative-coordinate screen", pastedBounds.Center.DistanceTo(visible.Center) < 1e-8 && offscreen.UndoCount == 1);
        check("offscreen paste preserves member spacing and rotation without scaling", offscreen.Document.Elements[0].Transform.M11 == rectangle.Transform.M11 &&
            offscreen.Document.Elements[1].Transform.OffsetX - offscreen.Document.Elements[2].Transform.OffsetX == picture.Transform.OffsetX - number.Transform.OffsetX);
        var overlapTarget = new EditorSession();
        ObjectClipboardCommand.Insert(overlapTarget, copy, assets, new(120, 100, 10, 150));
        check("partially visible paste keeps its usual offset instead of unexpectedly recentering", overlapTarget.Document.Elements[0].Transform == Affine2D.Translation(16, 16).Compose(rectangle.Transform));

        source.SetSelection([]);
        var empty = ObjectClipboardCommand.CreateSnapshot(source);
        int history = target.UndoCount;
        ObjectClipboardCommand.Insert(target, empty, assets, visible);
        check("an empty object selection carries no orphan groups and pastes without a history step", empty.Elements.Count == 0 && empty.Groups.Count == 0 && target.UndoCount == history);
        var line = new CanvasElement { Kind = ElementKind.Line, Points = [new(10, 10), new(80, 10)], Style = new() { StrokeWidth = 0 } };
        var lineSession = new EditorSession(new() { Elements = [line] }); lineSession.SetSelection([line.Id]);
        var lineCopy = ObjectClipboardCommand.CreateSnapshot(lineSession);
        check("a horizontal line receives a nonzero raster clipboard viewport", lineCopy.Viewport.Width > 0 && lineCopy.Viewport.Height > 0);

        var capture = source.Document.CreateCapture(new(90, 80, 220, 170), asset);
        SceneValidation.Validate(capture);
        check("capture cloning retains only intersecting visible elements and their group ancestors", capture.Elements.Count == 3 && capture.Groups.Count == 2 &&
            capture.Groups.Any(group => group.Name == "inner" && group.ParentId is not null) && capture.Background?.GroupId is null);
        check("capture cloning remaps group identities independently from the live document", !capture.Groups.Select(group => group.Id).Intersect(source.Document.Groups.Select(group => group.Id)).Any() &&
            capture.Elements.All(element => element.GroupId is { } id && capture.Groups.Any(group => group.Id == id)));
        var captureSession = new EditorSession(capture, assets);
        captureSession.Execute("crop", document => document.Elements.RemoveAll(element => element.Kind == ElementKind.Image));
        SceneValidation.Validate(captureSession.Document);
        check("capture crop commits prune empty nested groups while preserving remaining ancestors", captureSession.Document.Groups.Count == 1 &&
            captureSession.Document.Groups[0].ParentId is null && captureSession.Undo() && captureSession.Document.Groups.Count == 2);
        check("clipboard and capture cloning leave the live document unchanged", JsonSerializer.Serialize(source.Document) == sourceBefore);
    }
}
