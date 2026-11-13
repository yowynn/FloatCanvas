using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;
using System.Text.Json;

internal static class BillboardScreenCreationTests
{
    private static EditorSession Session()
    {
        var document = new SceneDocument { Viewport = new(0, 0, 800, 700) };
        document.Canvas.Projection.Enabled = true;
        document.Canvas.Projection.Matrix = new(1, 0, 0, 0, -.4, 700, 0, -.002, 1);
        document.Canvas.Projection.Quad = [new(0,0), new(800,0), new(800,700), new(0,700)];
        document.Canvas.Projection.PlaneBounds = new(0, 0, 800, 700);
        return new(document);
    }
    private static void Send(EditorController editor, OverlayPointerKind kind, double x, double y, OverlayModifiers modifiers = OverlayModifiers.None) =>
        editor.Pointer(new(kind, new(x, y), OverlayMouseButton.Left, modifiers));
    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;
    internal static void Run(Action<string, bool> check)
    {
        var session = Session(); var editor = new EditorController(session) { Tool = EditorTool.Rectangle, BillboardMode = true };
        check("screen billboard is the default workflow", editor.BillboardWorkflow == BillboardCreationWorkflow.ScreenFirst);
        Send(editor, OverlayPointerKind.Down, 100, 100);
        check("sky down retains a private draft without authored geometry", session.IsTransactionActive && session.Document.Elements.Count == 0 && editor.ScreenBillboardDraft is not null);
        Send(editor, OverlayPointerKind.Move, 300, 400);
        check("valid draft stays private until release", session.Document.Elements.Count == 0 && session.UndoCount == 0 && editor.VisibleBillboardDepthLine is null);
        Send(editor, OverlayPointerKind.Up, 300, 400);
        var item = session.Document.Elements.Single(); var bounds = CanvasMapping.Create(session.Document).ProjectedBounds(item, false);
        check("screen-first publishes exact intended rectangle once", item.IsBillboard && Near(bounds.X, 100) && Near(bounds.Y, 100) && Near(bounds.Width, 200) && Near(bounds.Height, 300) && session.UndoCount == 1 && !session.IsTransactionActive);
        check("default ground uses frame bottom center", Near(item.BillboardAnchor.X, 500) && Near(item.BillboardAnchor.Y, -750) && Near(item.BillboardHeight, 375));
        session.Undo(); check("screen-first creation has one coherent undo", session.Document.Elements.Count == 0);

        session = Session(); editor = new(session) { Tool = EditorTool.Line, BillboardMode = true };
        Send(editor, OverlayPointerKind.Down, 100, 100); Send(editor, OverlayPointerKind.Move, 300, 400);
        Send(editor, OverlayPointerKind.Up, 200, 150);
        check("unsafe latest ground cancels instead of committing older valid sample", session.Document.Elements.Count == 0 && session.UndoCount == 0 && !session.IsTransactionActive);

        session = Session(); editor = new(session) { Tool = EditorTool.Freehand, BillboardMode = true };
        Send(editor, OverlayPointerKind.Down, 100, 100); Send(editor, OverlayPointerKind.Move, 130, 250);
        Send(editor, OverlayPointerKind.Move, 160, 120); Send(editor, OverlayPointerKind.Up, 180, 400);
        item = session.Document.Elements.Single();
        check("screen stroke crosses horizon without clipping or splitting", item.IsBillboard && item.Points.Count == 4 &&
            CanvasMapping.Create(session.Document).ElementToScreen(item).TransformPoint(item.Points[2]).DistanceTo(new(160, 120)) < .01 && session.UndoCount == 1);

        session = Session(); editor = new(session) { Tool = EditorTool.Rectangle, BillboardMode = true };
        Send(editor, OverlayPointerKind.Down, 100, 100); session.Cancel();
        session.BeginTransaction("foreign"); session.Document.NextNumber = 17;
        Send(editor, OverlayPointerKind.Up, 300, 400);
        check("late screen draft release cannot cancel foreign transaction", session.IsTransactionActive && session.Document.NextNumber == 17 && session.Document.Elements.Count == 0);
        session.Cancel();

        session = Session(); editor = new(session) { Tool = EditorTool.CaptureRegion, BillboardMode = true };
        CaptureSelection? capture = null; editor.CaptureSelectionCompleted += value => capture = value;
        Send(editor, OverlayPointerKind.Down, 100, 100); Send(editor, OverlayPointerKind.Up, 300, 400);
        check("screen-first capture freezes independent placement intent", capture is { BillboardScreenFirst: true, BillboardDepthLine: null, PlaneBounds: null } && capture.Bounds == new RectD(100, 100, 200, 300));
        capture = null; Send(editor, OverlayPointerKind.Down, 100, 100); Send(editor, OverlayPointerKind.Up, 200, 150);
        check("capture with unsafe final ground does not start acquisition", capture is null && !session.IsTransactionActive && session.UndoCount == 0);
        Send(editor, OverlayPointerKind.Down, 100, 100);
        session.Document.Canvas.Projection.Enabled = false; session.NotifyChanged();
        check("disabling perspective cancels the private capture region", !editor.HasCaptureGesture && !editor.BillboardMode && session.Document.Elements.Count == 0);
        session.Document.Canvas.Projection.Enabled = true; session.NotifyChanged();
        check("reenabling perspective cannot revive billboard creation mode", !editor.BillboardMode && editor.CapturePreview is null && session.UndoCount == 0);

        using var renderer = new SceneRenderer();
        session = Session();
        var edit = new InlineTextEdit(session, null, new(100, 350), new ElementStyle { FontSize = 24 }, renderer, billboardScreenFirst: true);
        edit.Input.Text = string.Join('\n', Enumerable.Repeat("screen text", 8));
        check("screen text stays detached while measuring logical lines", session.Document.Elements.Count == 0 && edit.ScreenDraft is not null && edit.ResolvedScreenDraft is not null);
        var screenBox = edit.LocalToScreen.TransformPoint(new(0, 0)); edit.Finish(true);
        item = session.Document.Elements.Single(); var actualTop = CanvasMapping.Create(session.Document).ElementToScreen(item).TransformPoint(new(0, 0));
        check("text publishes measured screen placement in one undo", item.IsBillboard && Near(screenBox.X, actualTop.X) && Near(screenBox.Y, actualTop.Y) && session.UndoCount == 1);

        session = Session();
        edit = new(session, null, new(100, 100), new ElementStyle { FontSize = 24 }, renderer, billboardScreenFirst: true);
        edit.Input.Text = "too high"; edit.Finish(true);
        check("unsafe text rejects without source residue", session.Document.Elements.Count == 0 && session.UndoCount == 0 && !session.IsTransactionActive);

        session = Session();
        var group = new SceneGraph(session.Document).InsertGroup(new CanvasGroup { Name = "local", OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [], Transform = Affine2D.Translation(20, 50).Compose(Affine2D.Scale(2, 3)), ElevationScale = 2 });
        session.EnterScope(group.Id); editor = new(session) { Tool = EditorTool.Rectangle, BillboardMode = true };
        Send(editor, OverlayPointerKind.Down, 100, 100); Send(editor, OverlayPointerKind.Up, 300, 400);
        item = session.Document.Elements.Single(); var world = new SceneGraph(session.Document).ToWorld(item);
        bounds = CanvasMapping.Create(session.Document).ProjectedBounds(world, false);
        check("screen-first writes nested placement into local coordinates once", item.GroupId == group.Id && Near(bounds.X, 100) && Near(bounds.Y, 100) && Near(bounds.Width, 200) && Near(bounds.Height, 300));

        session = Session(); group = new SceneGraph(session.Document).InsertGroup(new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] }); session.EnterScope(group.Id);
        editor = new(session) { Tool = EditorTool.CaptureRegion, BillboardMode = true }; capture = null;
        editor.CaptureSelectionCompleted += value => capture = value;
        Send(editor, OverlayPointerKind.Down, 100, 100); session.LeaveScope(); session.EnterScope(group.Id);
        Send(editor, OverlayPointerKind.Up, 300, 400);
        check("capture scope out and back cannot revive a private region", capture is null && editor.CapturePreview is null && session.Document.Elements.Count == 0);

        session = Session(); editor = new(session) { Tool = EditorTool.Image, BillboardMode = true };
        bool inserted = false; BillboardDepthLine? chosen = new(); PointD clicked = default;
        editor.BeginBillboardPlacement((depth, point) => { inserted = true; chosen = depth; clicked = point; });
        Send(editor, OverlayPointerKind.Down, 100, 100);
        check("screen-first external placement takes a single center click", inserted && chosen is null && clicked == new PointD(100, 100) && !editor.HasPendingBillboardInsertion);

        static NormalizedImage Pixels(int width, int height)
        {
            using var surface = SKSurface.Create(new SKImageInfo(width, height)); surface.Canvas.Clear(SKColors.Red);
            using var image = surface.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return new(data.ToArray(), width, height);
        }
        session = Session(); var tall = Pixels(100, 400); var small = Pixels(16, 16); bool rejected = false;
        try { ImageImportCommand.InsertBillboard(session, [tall, small], session.Document.Viewport, null, new(200, 100)); }
        catch (InvalidOperationException) { rejected = true; }
        check("batch screen insertion validates every ground before publishing any asset", rejected && !session.Assets.Resources.Any() && session.Document.Elements.Count == 0 && session.UndoCount == 0);
        ImageImportCommand.InsertBillboard(session, [small], session.Document.Viewport, null, new(200, 400), .7);
        item = session.Document.Elements.Single(); bounds = CanvasMapping.Create(session.Document).ProjectedBounds(item, false);
        check("image center logical size opacity and asset survive screen-first insertion", item.IsBillboard && bounds.Center.DistanceTo(new(200, 400)) < .01 &&
            item.Width == 16 && item.Transform == Affine2D.Identity && item.Style.Opacity == .7 && session.Assets.Resources.Count() == 1);

        session = Session(); session.BeginTransaction("foreign"); rejected = false;
        try { SnapshotImageCommand.Insert(session, new(small.PngBytes, new RectD(100, 300, 16, 16), true), billboardScreenFirst: true); }
        catch (InvalidOperationException) { rejected = true; }
        check("foreign snapshot insertion leaves transaction and asset set untouched", rejected && session.IsTransactionActive && !session.Assets.Resources.Any() && session.Document.Elements.Count == 0);
        session.Cancel();

        var mapping = CanvasMapping.Create(Session().Document);
        var planeElement = new CanvasElement { Kind = ElementKind.Rectangle, Width = 100, Height = 50, Transform = Affine2D.Translation(100, -600) };
        string original = JsonSerializer.Serialize(planeElement);
        check("appearance-preserving conversion rejects genuine projective shape without mutation", !BillboardPlacementConversion.TryPreserveScreen(planeElement, true, mapping) && JsonSerializer.Serialize(planeElement) == original);
        var screenPlacement = Projective2D.FromAffine(Affine2D.Translation(100, 100));
        planeElement.ProjectivePlacement = mapping.ScreenToPlane.Compose(screenPlacement); planeElement.Transform = Affine2D.Identity;
        planeElement.Height = 300;
        check("screen-affine result accepts inverse-projected image-style placement", BillboardPlacementConversion.TryPreserveScreen(planeElement, true, mapping) && mapping.ElementToScreen(planeElement).TransformPoint(new(0, 0)).DistanceTo(new(100, 100)) < 1e-6);
        original = JsonSerializer.Serialize(planeElement);
        check("billboard sky body cannot become a safe surface while preserving appearance", !BillboardPlacementConversion.TryPreserveScreen(planeElement, false, mapping) && JsonSerializer.Serialize(planeElement) == original);
    }
}
