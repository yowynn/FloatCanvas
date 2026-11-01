using System.IO;
using System.Text.Json;
using System.Windows;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using SkiaSharp;

internal static class GroupedCreationTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; AppTheme.Apply("light");
        Images(check); InlineText(check); Clipboard(check);
    }
    private static EditorSession Session(out Guid scope)
    {
        var outer = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [], Transform = Affine2D.Translation(80, 40).Compose(Affine2D.Rotation(12)), ElevationScale = 1.25 };
        var inner = new CanvasGroup { ParentId = outer.Id, OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [], Transform = Affine2D.Scale(1.3, .8), ElevationScale = 1.6 };
        outer.ChildOrder = [inner.Id]; scope = inner.Id;
        var document = new SceneDocument { Viewport = new(0, 0, 800, 600), Groups = [outer, inner], RootOrder = [outer.Id] };
        var session = new EditorSession(document); session.EnterScope(inner.Id); return session;
    }
    private static void Images(Action<string, bool> check)
    {
        var session = Session(out Guid scope); string before = JsonSerializer.Serialize(session.Document);
        var normalized = Image(); ImageImportCommand.Insert(session, [normalized], new(200, 100, 300, 240));
        var source = session.Document.Elements.Single(); var world = new SceneGraph(session.Document).ToWorld(source);
        check("image import writes the active scope's child slot and retains its intended world center", source.GroupId == scope &&
            session.Document.Groups.Single(group => group.Id == scope).ChildOrder!.SequenceEqual([source.Id]) &&
            ElementGeometry.Bounds(world).Center.DistanceTo(new(350, 220)) < 1e-6);
        SceneValidation.Validate(session.Document);
        session.SetEditScope(null); session.SetSelection([session.Document.Groups.Single(group => group.ParentId is null).Id]);
        check("a group containing only one image cannot bypass direct-leaf crop selection", !ImageToolEligibility.TryGetCropTarget(session, out _));
        check("image replacement also requires a directly selected image leaf", !ImageToolEligibility.TryGetReplaceTarget(session, out _));
        session.EnterScope(scope); session.SetSelection([source.Id]);
        check("the directly selected nested image remains eligible for cropping", ImageToolEligibility.TryGetCropTarget(session, out var crop) && ReferenceEquals(crop, source));
        session.Document.Groups.Single(group => group.Id == scope).Locked = true;
        check("an ancestor lock disables image-only editing", !ImageToolEligibility.TryGetCropTarget(session, out _));
        session.Document.Groups.Single(group => group.Id == scope).Locked = false;
        check("undoing image import removes its slot and restores the original source graph", session.Undo() && JsonSerializer.Serialize(session.Document) == before);

        session = Session(out scope); session.Document.Canvas.Projection = CanvasCalibration.CreateInitial(session.Document.Viewport); session.Document.Canvas.Projection.Enabled = true;
        var mapping = CanvasMapping.Create(session.Document); mapping.TryGetEqualDepthLine(new(350, 300), out var depth);
        ImageImportCommand.InsertBillboard(session, [normalized], new(0, 0, 800, 600), depth, new(350, 260));
        source = session.Document.Elements.Single(); world = new SceneGraph(session.Document).ToWorld(source);
        var center = mapping.ElementToScreen(world).TransformPoint(ElementGeometry.LocalBounds(world).Center);
        check("billboard import converts world anchors and elevation into scope-local data exactly once", source.GroupId == scope && world.IsBillboard &&
            center.DistanceTo(new(350, 260)) < 1e-6 && Math.Abs(world.BillboardHeight - source.BillboardHeight * 2) < 1e-6);
        SceneValidation.Validate(session.Document);
    }
    private static void InlineText(Action<string, bool> check)
    {
        var session = Session(out Guid scope); using var renderer = new SceneRenderer();
        string original = JsonSerializer.Serialize(session.Document);
        using (var edit = new InlineTextEdit(session, null, new(300, 220), new() { FontSize = 24 }, renderer))
        {
            edit.Input.Text = "Nested text";
            check("new inline text belongs to the edit scope while its frame stays at the requested world location", edit.Target.GroupId == scope &&
                edit.LocalToScreen.TransformPoint(default).DistanceTo(new(300, 220)) < 1e-6 &&
                session.Document.Groups.Single(group => group.Id == scope).ChildOrder!.Contains(edit.Target.Id));
            edit.Finish(true);
        }
        var text = session.Document.Elements.Single(); var local = text.Transform;
        var expectedWorld = new SceneGraph(session.Document).ToWorld(text);
        using (var edit = new InlineTextEdit(session, text, default, new(), renderer))
        {
            edit.Input.Text = "Longer nested text";
            check("existing inline text uses the ancestor placement for screen caret and frame geometry", edit.LocalToScreen.TransformPoint(new(12, 8)).DistanceTo(
                CanvasMapping.Create(session.Document).ElementToScreen(expectedWorld).TransformPoint(new(12, 8))) < 1e-6 && text.Transform == local);
            edit.Finish(false);
        }
        text = session.Document.Elements.Single();
        using (var edit = new InlineTextEdit(session, text, default, new(), renderer)) { edit.Input.Text = ""; edit.Finish(true); }
        check("deleting inline text removes its source slot without deleting the empty scope", session.Document.Elements.Count == 0 &&
            session.Document.Groups.Single(group => group.Id == scope).ChildOrder!.Count == 0);
        SceneValidation.Validate(session.Document);
        check("inline deletion undo restores text and its exact scope slot", session.Undo() && session.Document.Elements.Count == 1 &&
            session.Document.Groups.Single(group => group.Id == scope).ChildOrder!.Single() == session.Document.Elements.Single().Id);
        session.Undo();
        check("cancelling or undoing grouped inline creation preserves the original group graph", JsonSerializer.Serialize(session.Document) == original);
    }
    private static void Clipboard(Action<string, bool> check)
    {
        var session = Session(out Guid scope); var picture = new CanvasElement { Kind = ElementKind.Image, AssetId = session.Assets.Add(Image().PngBytes), Width = 60, Height = 40, Transform = Affine2D.Translation(250, 180) };
        session.Add(picture); session.SetEditScope(null); var root = session.Document.Groups.Single(group => group.ParentId is null);
        session.SetSelection([root.Id]);
        var snapshot = ObjectClipboardCommand.CreateSnapshot(session); var sourceWorld = SceneEvaluation.Build(snapshot).RenderDocument;
        var target = Session(out Guid targetScope); ObjectClipboardCommand.Insert(target, snapshot, session.Assets, new(0, 0, 800, 600));
        var pasted = SceneEvaluation.Build(target.Document).RenderDocument.Elements.Single();
        check("clipboard insertion retains nested hierarchy while adding the standard world offset inside a new scope", target.Document.Groups.Count > 2 &&
            ElementGeometry.Bounds(pasted).Center.DistanceTo(ElementGeometry.Bounds(sourceWorld.Elements.Single()).Center + new PointD(16, 16)) < 1e-6 &&
            target.Document.Groups.Count(group => group.ParentId == targetScope) > 0);
        SceneValidation.Validate(target.Document);
        var empty = new CanvasGroup { Name = "Empty legacy folder" }; session.Document.Groups.Add(empty); session.SetSelection([empty.Id]);
        var emptySnapshot = ObjectClipboardCommand.CreateSnapshot(session);
        check("copying a selected empty legacy group preserves the source node", emptySnapshot.Elements.Count == 0 && emptySnapshot.Groups.Single().Name == empty.Name);
        ObjectClipboardCommand.Insert(target, emptySnapshot, session.Assets, new(0, 0, 800, 600));
        check("pasting an empty group keeps it editable in the active scope", target.Document.Groups.Any(group => group.Name == empty.Name && group.ParentId == targetScope));
        SceneValidation.Validate(target.Document);
    }
    private static NormalizedImage Image()
    {
        using var bitmap = new SKBitmap(20, 12); bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return AssetImport.Normalize(encoded.ToArray());
    }
}
