using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class TemporaryMoveControllerTests
{
    internal static void Run(Action<string, bool> check)
    {
        ControlRetainsSelection(check);
        foreach (var tool in new[] { EditorTool.Freehand, EditorTool.Highlighter, EditorTool.Line, EditorTool.Rectangle,
            EditorTool.Text, EditorTool.Number, EditorTool.Emoji, EditorTool.Image, EditorTool.Stamp, EditorTool.CaptureRegion,
            EditorTool.Eraser, EditorTool.Blur, EditorTool.Pixelate, EditorTool.Crop }) MiddleMoves(check, tool);
        Cancellation(check);
        GroupAndMarquee(check);
        ExplicitToolChange(check);
        ModifierAndSessionBoundaries(check);
        BillboardInsertionDoesNotOwnMiddle(check);
    }

    private static void ControlRetainsSelection(Action<string, bool> check)
    {
        var a = Box(40, 60); var b = Box(300, 60);
        var session = new EditorSession(new() { Elements = [a, b] }); session.SetSelection([a.Id, b.Id]);
        var editor = new EditorController(session);
        string before = JsonSerializer.Serialize(session.Document);
        editor.ModifiersChanged(OverlayModifiers.Control);
        check("Ctrl hides only selection adorners and preserves actual selection, document and history",
            !editor.ShouldShowSelection(OverlayModifiers.Control) && session.Selection.SetEquals([a.Id, b.Id]) &&
            session.UndoCount == 0 && JsonSerializer.Serialize(session.Document) == before);
        editor.Cancel(); var group = session.GroupSelection();
        check("Ctrl+G can group the selection while its adorners are hidden", group is not null &&
            session.Selection.SetEquals([group.Value]) && session.Document.Groups.Count == 1 && session.UndoCount == 1);
        editor.ModifiersChanged(OverlayModifiers.Control | OverlayModifiers.Shift); editor.Cancel(); session.UngroupSelection();
        check("Ctrl+Shift+G can ungroup the retained selection", session.Document.Groups.Count == 0 &&
            session.Selection.SetEquals([a.Id, b.Id]) && session.UndoCount == 2);
        editor.ModifiersChanged(OverlayModifiers.None);
        check("Releasing Ctrl restores adorners for the same selection", editor.ShouldShowSelection(OverlayModifiers.None) &&
            session.Selection.SetEquals([a.Id, b.Id]));
    }

    private static void MiddleMoves(Action<string, bool> check, EditorTool tool)
    {
        var item = Box(80, 70);
        if (tool == EditorTool.Crop) { item.Kind = ElementKind.Image; item.AssetId = new string('a', 64); }
        var session = new EditorSession(new() { Elements = [item] }); session.SetSelection([item.Id]);
        if (tool == EditorTool.Crop)
        {
            using var image=SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(2,2));
            image.Canvas.Clear(SkiaSharp.SKColors.Red);
            using var snapshot=image.Snapshot(); using var data=snapshot.Encode();
            item.AssetId=session.Assets.Add(data.ToArray());
        }
        var editor = new EditorController(session) { ScreenOrigin = new(-500, 120) };
        editor.ApplyParameters(new() { Tool = tool, Style = new() { Stroke = 0xAA123456, Fill = 0x66445566,
            StrokeWidth = 7.5, Opacity = .65, FontSize = 37, FontFamily = "Arial" }, CornerRadius = 13,
            Symbol = SymbolKind.Heart, ConstrainAspect = true, EffectStrength = 29, Emoji = "🎨",
            DefaultWidth = 281, DefaultHeight = 173, NumberStart = 9, NumberStep = 3,
            CaptureShape = CaptureShape.PolygonLasso, CaptureSource = CaptureSource.CanvasOnly, CaptureBorderWidth = 6, CaptureBorderColor = 0xFFABCDEF });
        string parameters = JsonSerializer.Serialize(editor.Parameters); var style = editor.Style;
        int text = 0, properties = 0, capture = 0, effect = 0;
        editor.EditText += (_, _) => text++; editor.PropertiesRequested += _ => properties++;
        editor.CaptureSelectionCompleted += _ => capture++; editor.DesktopEffectRequested += (_, _, _, _) => effect++;
        var from = item.Transform.TransformPoint(new(item.Width / 2, item.Height / 2));
        var to = from + new PointD(33, 17);
        Send(editor, OverlayPointerKind.Down, from, OverlayMouseButton.Middle);
        Send(editor, OverlayPointerKind.Move, to, OverlayMouseButton.Middle);
        Send(editor, OverlayPointerKind.Up, to, OverlayMouseButton.Middle);
        check(tool + " borrowed selection drag performs the Select/Move transform in one undo operation", session.Document.Elements.Count == 1 &&
            session.Document.Elements.Single().Transform == Affine2D.Translation(113, 87) && session.Selection.SetEquals([item.Id]) &&
            session.UndoCount == 1 && !session.IsTransactionActive);
        check(tool + " borrowed selection release retains every original tool parameter without replacing its style", editor.Tool == tool &&
            JsonSerializer.Serialize(editor.Parameters) == parameters && ReferenceEquals(style, editor.Style));
        check(tool + " borrowed selection drag never inserts text, stamps, capture or effects", text == 0 && properties == 0 && capture == 0 && effect == 0 &&
            editor.CapturePreview is null && editor.Marquee is null);
        check(tool + " borrowed selection move undo and redo preserve the same selected object", session.Undo() &&
            session.Document.Elements.Single().Transform == Affine2D.Translation(80, 70) && session.Selection.SetEquals([item.Id]) &&
            session.Redo() && session.Document.Elements.Single().Transform == Affine2D.Translation(113, 87));
        if (tool == EditorTool.Freehand)
        {
            Send(editor, OverlayPointerKind.Down, new(500, 300), OverlayMouseButton.Left);
            Send(editor, OverlayPointerKind.Move, new(550, 340), OverlayMouseButton.Left);
            Send(editor, OverlayPointerKind.Up, new(550, 340), OverlayMouseButton.Left);
            check("A left stroke after borrowed selection release immediately uses the original drawing tool and style", session.Document.Elements.Count == 2 &&
                session.Document.Elements[^1] is { Kind: ElementKind.Freehand, Style.Stroke: 0xAA123456, Style.StrokeWidth: 7.5 } && session.UndoCount == 2);
        }
    }

    private static void Cancellation(Action<string, bool> check)
    {
        var item = Box(80, 70); var session = new EditorSession(new() { Elements = [item] }); session.SetSelection([item.Id]);
        var editor = new EditorController(session) { Tool = EditorTool.Stamp };
        string before = JsonSerializer.Serialize(session.Document), parameters = JsonSerializer.Serialize(editor.Parameters);
        Send(editor, OverlayPointerKind.Down, new(180, 145), OverlayMouseButton.Middle);
        Send(editor, OverlayPointerKind.Move, new(220, 170), OverlayMouseButton.Middle);
        check("Borrowed selection drag previews a real transform before cancellation", session.IsTransactionActive &&
            session.Document.Elements.Single().Transform == Affine2D.Translation(120, 95));
        editor.Pointer(new(OverlayPointerKind.Cancel, new(220, 170), OverlayMouseButton.None, OverlayModifiers.None));
        check("Capture loss or Escape cancels the borrowed selection transform and retains original selection and tool state", session.UndoCount == 0 &&
            !session.IsTransactionActive && JsonSerializer.Serialize(session.Document) == before && session.Selection.SetEquals([item.Id]) &&
            JsonSerializer.Serialize(editor.Parameters) == parameters);
        Send(editor, OverlayPointerKind.Up, new(220, 170), OverlayMouseButton.Middle);
        check("A stale borrowed selection release after cancellation cannot commit or insert content", session.UndoCount == 0 &&
            JsonSerializer.Serialize(session.Document) == before && editor.Tool == EditorTool.Stamp);
    }

    private static void GroupAndMarquee(Action<string, bool> check)
    {
        var a = Box(80, 70); var b = Box(330, 70); var session = new EditorSession(new() { Elements = [a, b] });
        session.SetSelection([a.Id, b.Id]); var group = session.GroupSelection(); session.SetSelection([]);
        var editor = new EditorController(session) { Tool = EditorTool.Stamp };
        Send(editor, OverlayPointerKind.Down, new(180, 145), OverlayMouseButton.Middle);
        Send(editor, OverlayPointerKind.Move, new(210, 160), OverlayMouseButton.Middle);
        Send(editor, OverlayPointerKind.Up, new(210, 160), OverlayMouseButton.Middle);
        var world = SceneEvaluation.Build(session.Document).RenderDocument;
        check("Borrowed selection dragging an unselected group member selects and moves the full group like Move", group is { } id && session.Selection.SetEquals([id]) &&
            world.Elements.Select(item => item.Transform).SequenceEqual([Affine2D.Translation(110, 85), Affine2D.Translation(360, 85)]) &&
            session.Document.Elements.Select(item => item.Transform).SequenceEqual([Affine2D.Translation(80, 70), Affine2D.Translation(330, 70)]) &&
            session.Document.Groups.Single().Id == group && session.Document.Groups.Single().Transform == Affine2D.Translation(30, 15) && session.UndoCount == 2);
        session.SetSelection([]);
        Send(editor, OverlayPointerKind.Down, new(60, 40), OverlayMouseButton.Middle);
        Send(editor, OverlayPointerKind.Move, new(600, 300), OverlayMouseButton.Middle);
        Send(editor, OverlayPointerKind.Up, new(600, 300), OverlayMouseButton.Middle);
        check("Borrowed selection blank-area drag uses Move marquee selection without creating a stamp or history entry", group is { } selectedGroup && session.Selection.SetEquals([selectedGroup]) &&
            session.Document.Elements.Count == 2 && session.UndoCount == 2 && editor.Tool == EditorTool.Stamp);
    }

    private static void ExplicitToolChange(Action<string, bool> check)
    {
        var item = Box(80, 70); var session = new EditorSession(new() { Elements = [item] }); session.SetSelection([item.Id]);
        var editor = new EditorController(session) { Tool = EditorTool.Rectangle };
        Send(editor, OverlayPointerKind.Down, new(180, 145), OverlayMouseButton.Middle);
        Send(editor, OverlayPointerKind.Move, new(210, 160), OverlayMouseButton.Middle);
        editor.Tool = EditorTool.Eraser;
        Send(editor, OverlayPointerKind.Up, new(210, 160), OverlayMouseButton.Middle);
        check("Explicit tool change during borrowed selection movement cancels it without restoring a stale former tool", editor.Tool == EditorTool.Eraser &&
            session.Document.Elements.Single().Transform == Affine2D.Translation(80, 70) && !session.IsTransactionActive && session.UndoCount == 0);
    }

    private static void ModifierAndSessionBoundaries(Action<string, bool> check)
    {
        var item = Box(80, 70); var session = new EditorSession(new() { Elements = [item] }); session.SetSelection([item.Id]);
        var editor = new EditorController(session) { Tool = EditorTool.Eraser };
        Send(editor, OverlayPointerKind.Down, new(180, 145), OverlayMouseButton.Middle);
        check("Borrowed selection movement exposes Move adorners without replacing the eraser's real tool", editor.IsTemporaryMove &&
            editor.InteractionTool == EditorTool.Select && editor.Tool == EditorTool.Eraser && editor.ShouldShowSelection(OverlayModifiers.None) &&
            !editor.ShouldShowSelection(OverlayModifiers.Control) && session.Selection.SetEquals([item.Id]));
        Send(editor, OverlayPointerKind.Move, new(220, 170), OverlayMouseButton.Middle);
        editor.ModifiersChanged(OverlayModifiers.Shift, new(220, 170), time: 100, pointerLeftDown: true);
        check("Stationary Shift constrains a borrowed selection drag even though the physical left button is held", session.Document.Elements.Single().Transform == Affine2D.Translation(120, 70));
        editor.ModifiersChanged(OverlayModifiers.None, new(220, 170), time: 101, pointerLeftDown: true);
        check("Stationary Shift release restores the unconstrained borrowed selection displacement", session.Document.Elements.Single().Transform == Affine2D.Translation(120, 95));
        Send(editor, OverlayPointerKind.Up, new(220, 170), OverlayMouseButton.Middle);
        check("Borrowed selection release ends temporary Move and restores the original adornment policy", !editor.IsTemporaryMove && editor.InteractionTool == EditorTool.Eraser &&
            !editor.ShouldShowSelection(OverlayModifiers.None) && session.UndoCount == 1);
        Send(editor, OverlayPointerKind.Down, new(220, 170), OverlayMouseButton.Middle);
        Send(editor, OverlayPointerKind.Up, new(220, 170), OverlayMouseButton.Middle);
        check("A borrowed selection click with no displacement creates no history operation", session.UndoCount == 1 && !session.IsTransactionActive && !editor.IsTemporaryMove);
        Send(editor, OverlayPointerKind.Down, new(220, 170), OverlayMouseButton.Middle);
        Send(editor, OverlayPointerKind.Move, new(240, 190), OverlayMouseButton.Middle);
        var replacement = new EditorSession(); editor.ReplaceSession(replacement, new(-300, 100));
        Send(editor, OverlayPointerKind.Up, new(240, 190), OverlayMouseButton.Middle);
        check("Replacing the document cancels borrowed selection movement before a stale release can touch the new session", !editor.IsTemporaryMove &&
            ReferenceEquals(editor.Session, replacement) && replacement.Document.Elements.Count == 0 && replacement.UndoCount == 0 &&
            session.Document.Elements.Single().Transform == Affine2D.Translation(120, 95) && editor.Tool == EditorTool.Eraser);
    }

    private static void BillboardInsertionDoesNotOwnMiddle(Action<string,bool> check)
    {
        var item = Box(80,70);
        var document = new SceneDocument { Elements = [item], Viewport = new(0,0,1000,800) };
        document.Canvas.Projection = CanvasCalibration.CreateInitial(document.Viewport,10,8);
        document.Canvas.Projection.Enabled = true;
        document.Canvas.Projection.Matrix = Projective2D.Identity;
        var session = new EditorSession(document); session.SetSelection([item.Id]);
        var editor=new EditorController(session){Tool=EditorTool.Stamp,BillboardMode=true};
        int inserted=0;editor.BeginBillboardPlacement((_,_)=>inserted++);
        editor.BeginTemporarySelection();
        check("temporary selection does not silently discard pending billboard placement",!editor.IsBorrowingSelection && editor.HasPendingBillboardOperation && inserted==0);
        editor.Cancel();
    }

    private static CanvasElement Box(double x, double y) => new() { Kind = ElementKind.Rectangle, Width = 200, Height = 150,
        Transform = Affine2D.Translation(x, y), Style = new() { Fill = 0xFF336699, Stroke = 0xFF336699 } };
    private static void Send(EditorController editor, OverlayPointerKind kind, PointD point, OverlayMouseButton button, OverlayModifiers modifiers = OverlayModifiers.None)
    {
        // Former borrowed selection-move coverage now exercises the explicit V borrowing entry.
        bool borrowed=button==OverlayMouseButton.Middle;
        if(borrowed && kind==OverlayPointerKind.Down) editor.BeginTemporarySelection();
        editor.Pointer(new(kind,new(point.X+editor.ScreenOrigin.X,point.Y+editor.ScreenOrigin.Y),borrowed?OverlayMouseButton.Left:button,modifiers));
        if(borrowed && kind==OverlayPointerKind.Up) editor.EndTemporarySelection();
    }
}
