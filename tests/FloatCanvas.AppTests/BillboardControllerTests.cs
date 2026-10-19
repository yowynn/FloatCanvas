using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class BillboardControllerTests
{
    private static void Send(EditorController editor, OverlayPointerKind kind, PointD point, OverlayModifiers modifiers = OverlayModifiers.None) =>
        editor.Pointer(new(kind, new(point.X, point.Y), OverlayMouseButton.Left, modifiers));
    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;
    internal static void Run(Action<string, bool> check)
    {
        var session = new EditorSession(new SceneDocument { Viewport = new(0, 0, 800, 600) });
        session.Document.Canvas.Projection.Enabled = true;
        session.Document.Canvas.Projection.Matrix = new(1, 0, 0, 0, 1, 0, 0, .001, 1);
        session.Document.Canvas.Projection.Quad = [new(0,0),new(800,0),new(800,600),new(0,600)];
        var editor = new EditorController(session) { Tool = EditorTool.Rectangle, BillboardMode = true };
        editor.Style.StrokeWidth = 5;
        Send(editor, OverlayPointerKind.Move, new(300, 300));
        check("billboard preview shows only a depth line without adding a layer", editor.VisibleBillboardDepthLine is not null && session.Document.Elements.Count == 0);
        Send(editor, OverlayPointerKind.Down, new(300, 300)); Send(editor, OverlayPointerKind.Up, new(300, 300));
        check("first click locks depth without drawing, history or transaction", editor.ActiveBillboardDepthLine is not null && session.Document.Elements.Count == 0 && !session.IsTransactionActive && session.UndoCount == 0);
        var depth = editor.ActiveBillboardDepthLine!.Value;
        check("fixture exercises actual non-unit perspective depth", Near(depth.Scale, .7));
        Send(editor, OverlayPointerKind.Down, new(100, 100)); Send(editor, OverlayPointerKind.Move, new(200, 180));
        var item = session.Document.Elements.Single();
        var map = CanvasMapping.Create(session.Document);
        var bounds = map.ProjectedBounds(item, false);
        check("billboard drawing preview keeps screen rectangle and logical stroke", item.IsBillboard && Near(item.Width, 100 / depth.Scale) && Near(item.Height, 80 / depth.Scale) && item.Style.StrokeWidth == 5 && Near(bounds.Width, 100) && Near(bounds.Height, 80));
        check("drawing shows fixed depth and a vertical center-anchor guide", editor.VisibleBillboardDepthLine is not null && editor.BillboardDrawingGuide is { } guide && Near(guide.Center.X, guide.Anchor.X));
        Send(editor, OverlayPointerKind.Up, new(200, 180));
        check("completed drawing removes depth and preserves selected guide in one undo", editor.VisibleBillboardDepthLine is null && editor.BillboardSelectionGuide is not null && session.UndoCount == 1 && !session.IsTransactionActive);
        editor.Tool = EditorTool.Select;
        var originalAnchor = item.BillboardAnchor; double originalHeight = item.BillboardHeight, originalWidth = item.Width;
        var center = editor.BillboardSelectionGuide!.Value.Center;
        Send(editor, OverlayPointerKind.Down, center); Send(editor, OverlayPointerKind.Up, center + new PointD(50, 25));
        check("center drag ignores horizontal input and changes only logical height", item.BillboardAnchor == originalAnchor && Near(item.BillboardHeight, originalHeight - 25 / depth.Scale) && item.Width == originalWidth);
        center = editor.BillboardSelectionGuide!.Value.Center;
        Send(editor, OverlayPointerKind.Down, center); Send(editor, OverlayPointerKind.Up, center + new PointD(0, 300));
        check("height drag supports explicitly approved negative heights", item.BillboardHeight < 0 && item.BillboardAnchor == originalAnchor);
        // Restore an above-ground example, grab its interior away from all handles.
        session.Undo(); item = session.Document.Elements.Single();
        map = CanvasMapping.Create(session.Document);
        var grab = new PointD(item.Width * .3, item.Height * .4);
        var screen = map.ElementToScreen(item).TransformPoint(grab); originalAnchor = item.BillboardAnchor; originalHeight = item.BillboardHeight;
        Send(editor, OverlayPointerKind.Down, screen); Send(editor, OverlayPointerKind.Move, screen + new PointD(17, 22));
        check("body drag retains the original grabbed local point", map.ElementToScreen(item).TransformPoint(grab).DistanceTo(screen + new PointD(17, 22)) < 1e-5 && item.BillboardHeight == originalHeight && item.Width == originalWidth && item.BillboardAnchor != originalAnchor);
        editor.ModifiersChanged(OverlayModifiers.Shift, new(screen.X + 17, screen.Y + 22));
        var delta = item.BillboardAnchor - originalAnchor;
        check("late Shift constrains anchor from original gesture origin in canvas axes", Math.Abs(delta.X) < 1e-6 || Math.Abs(delta.Y) < 1e-6);
        Send(editor, OverlayPointerKind.Up, screen + new PointD(17, 22), OverlayModifiers.Shift);
        session.Document.Canvas.Projection.Enabled = false; session.NotifyChanged();
        check("disabling perspective resets only new-creation mode and retains billboard metadata", !editor.BillboardMode && item.IsBillboard && editor.BillboardSelectionGuide is null);
        OrdinaryShift(check); CancelAndInsertion(check);
    }
    private static void OrdinaryShift(Action<string,bool> check)
    {
        var session = new EditorSession(); var item = new CanvasElement { Kind = ElementKind.Rectangle, Width = 200, Height = 120, Transform = Affine2D.Translation(100,100) };
        session.Document.Elements.Add(item); session.SetSelection([item.Id]); var editor = new EditorController(session) { Tool = EditorTool.Select };
        item.Style.Fill = 0xFF224466;
        Send(editor, OverlayPointerKind.Down, new(140, 150)); Send(editor, OverlayPointerKind.Move, new(180, 180));
        editor.ModifiersChanged(OverlayModifiers.Shift, new(180, 180));
        check("ordinary late Shift uses drag origin, not key-down point", item.Transform.OffsetX == 140 && item.Transform.OffsetY == 100);
        editor.ModifiersChanged(OverlayModifiers.None, new(180, 180));
        check("releasing Shift restores the unconstrained current displacement", item.Transform.OffsetX == 140 && item.Transform.OffsetY == 130);
        editor.ModifiersChanged(OverlayModifiers.Shift, new(150, 220));
        check("repeated Shift keeps original origin while switching dominant axis", item.Transform.OffsetX == 100 && item.Transform.OffsetY == 170);
        editor.Cancel(); check("cancel restores entire constrained move", session.Document.Elements.Single().Transform.OffsetX == 100 && session.UndoCount == 0);
    }
    private static void CancelAndInsertion(Action<string,bool> check)
    {
        var session = new EditorSession(); session.Document.Canvas.Projection.Enabled = true;
        var editor = new EditorController(session) { BillboardMode = true }; bool inserted=false;
        editor.BeginBillboardInsertion((line,point)=>inserted=true);
        Send(editor, OverlayPointerKind.Down, new(300,300)); Send(editor, OverlayPointerKind.Up, new(300,300));
        check("queued image insertion does not insert during depth confirmation", !inserted && editor.HasPendingBillboardInsertion);
        Send(editor, OverlayPointerKind.Down, new(200,200));
        check("queued image insertion completes only on the placement click", inserted && !editor.HasPendingBillboardInsertion && editor.ActiveBillboardDepthLine is null);
        editor.BeginBillboardInsertion((line,point)=>throw new Exception("stale callback")); editor.Cancel();
        check("cancel clears pending external insertion and transient depth", !editor.HasPendingBillboardInsertion && editor.VisibleBillboardDepthLine is null);
    }
}
