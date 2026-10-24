using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;

/// <summary>Creation and follow-up adjustments are exercised through the real pointer transaction boundary.</summary>
internal static class CreationSelectionTests
{
    private static readonly EditorTool[] DragTools = [EditorTool.Line, EditorTool.Arrow,
        EditorTool.DoubleArrow, EditorTool.Rectangle, EditorTool.RoundedRectangle, EditorTool.Ellipse, EditorTool.Symbol];

    public static void Run(Action<string, bool> check)
    {
        foreach (var tool in DragTools) DraggedObject(tool, check);
        ClosedStroke(check);
        PointerCancellation(check);
        ExistingSelectionIsTheTarget(check);
        CtrlGestureOwnership(check);
        ClickObjectsAndImage(check);
        ContinuousClickInsertion(check);
    }

    private static void DraggedObject(EditorTool tool, Action<string, bool> check)
    {
        var session = new EditorSession(); var controller = new EditorController(session) { Tool = tool, Symbol = SymbolKind.Heart };
        var a = new PointD(40, 60); var b = new PointD(180, 140);
        Gesture(controller, a, b);
        var element = session.Document.Elements.Single(); var id = element.Id;
        check(tool + " creation keeps its tool and selects the committed object", controller.Tool == tool && session.Selection.SetEquals([id]) && session.UndoCount == 1 && !session.IsTransactionActive);
        string beforeMove = JsonSerializer.Serialize(session.Document); int history = session.UndoCount;
        var center = session.SelectionBounds()!.Value.Center;
        Gesture(controller, center, center + new PointD(25, 15));
        element = session.Document.Elements.Single();
        check(tool + " selected-frame drag moves the new object without creating another", session.Document.Elements.Count == 1 && controller.Tool == tool && session.Selection.SetEquals([id]) &&
            element.Transform.OffsetX == 65 && element.Transform.OffsetY == 75 && session.UndoCount == history + 1);
        check(tool + " movement is one exact undo and redo operation", session.Undo() && JsonSerializer.Serialize(session.Document) == beforeMove && session.Redo());

        history = session.UndoCount; string beforeResize = JsonSerializer.Serialize(session.Document); var bounds = session.SelectionBounds()!.Value;
        PointD handle = ElementGeometry.IsLine(element.Kind) ? element.Transform.TransformPoint(element.Points[^1]) : new(bounds.Right, bounds.Bottom);
        Gesture(controller, handle, handle + new PointD(35, 25));
        check(tool + " selected handle adjusts geometry while preserving creation mode", controller.Tool == tool && session.Document.Elements.Count == 1 && session.UndoCount == history + 1 &&
            JsonSerializer.Serialize(session.Document) != beforeResize && session.Selection.SetEquals([id]));
        check(tool + " handle adjustment undoes to the complete prior geometry", session.Undo() && JsonSerializer.Serialize(session.Document) == beforeResize);
        bounds = session.SelectionBounds()!.Value; center = bounds.Center;
        var rotationHandle = new PointD(center.X, bounds.Top - 28);
        history = session.UndoCount; string beforeRotation = JsonSerializer.Serialize(session.Document);
        Gesture(controller, rotationHandle, new(center.X + center.Y - rotationHandle.Y, center.Y));
        check(tool + " rotation handle works without switching to Select", controller.Tool == tool && session.UndoCount == history + 1 && session.Document.Elements.Count == 1 &&
            Math.Abs(session.Document.Elements.Single().Transform.M12 + 1) < 1e-8 && Math.Abs(session.Document.Elements.Single().Transform.M21 - 1) < 1e-8);
        check(tool + " rotation is exactly reversible", session.Undo() && JsonSerializer.Serialize(session.Document) == beforeRotation);

        var selected = session.Selection.Single(); history = session.UndoCount; string beforeCtrl = JsonSerializer.Serialize(session.Document);
        check(tool + " Ctrl hides the selection controls without changing selection or history", controller.ShouldShowSelection(OverlayModifiers.None) &&
            !controller.ShouldShowSelection(OverlayModifiers.Control) && !controller.ShouldShowSelection(OverlayModifiers.Control | OverlayModifiers.Shift) &&
            session.Selection.SetEquals([selected]) && session.UndoCount == history && JsonSerializer.Serialize(session.Document) == beforeCtrl);
        check(tool + " releasing Ctrl restores the existing controls", controller.ShouldShowSelection(OverlayModifiers.None));
        var previous = session.Document.Elements.Single();
        var overlapStart = previous.Transform.TransformPoint(previous.Points.Count > 0 ? previous.Points[0] : new(0, 0));
        var overlapEnd = previous.Transform.TransformPoint(previous.Points.Count > 0 ? previous.Points[^1] : new(previous.Width, previous.Height));
        Gesture(controller, overlapStart, overlapEnd, OverlayModifiers.Control);
        var duplicate = session.Document.Elements.Last();
        check(tool + " Ctrl draws a fresh overlapping object rather than transforming the selected one", session.Document.Elements.Count == 2 && duplicate.Id != id &&
            session.Selection.SetEquals([duplicate.Id]) && controller.Tool == tool && session.UndoCount == history + 1 &&
            JsonSerializer.Serialize(session.Document.Elements[0]) == JsonSerializer.Serialize(JsonSerializer.Deserialize<SceneDocument>(beforeCtrl)!.Elements[0]));
        check(tool + " overlapping creation undoes once and restores the previous selection", session.Undo() && session.Document.Elements.Count == 1 && session.Selection.SetEquals([id]));
        history = session.UndoCount; int redo = session.RedoCount; string beforeBlank = JsonSerializer.Serialize(session.Document);
        Gesture(controller, new(500, 350), new(500, 350));
        if (EditorController.UsesDefaultSize(tool))
            check(tool + " a blank click inserts its configured default-sized object", session.Document.Elements.Count == 2 && controller.Tool == tool &&
                session.Selection.SetEquals([session.Document.Elements[^1].Id]) && session.UndoCount == history + 1 && !session.IsTransactionActive);
        else check(tool + " a blank click deselects without leaving a tiny object or history entry", session.Selection.Count == 0 && controller.Tool == tool &&
                JsonSerializer.Serialize(session.Document) == beforeBlank && session.UndoCount == history && session.RedoCount == redo && !session.IsTransactionActive);
    }

    private static void ClosedStroke(Action<string, bool> check)
    {
        foreach (var tool in new[] { EditorTool.Freehand, EditorTool.Highlighter })
        {
            var session = new EditorSession(); var controller = new EditorController(session) { Tool = tool };
            Gesture(controller, new(30, 30), new(100, 80));
            Send(controller, OverlayPointerKind.Down, new(400, 250));
            Send(controller, OverlayPointerKind.Move, new(440, 250));
            Send(controller, OverlayPointerKind.Move, new(440, 300));
            Send(controller, OverlayPointerKind.Move, new(400, 250));
            Send(controller, OverlayPointerKind.Up, new(400, 250));
            check(tool + " a genuinely dragged closed stroke is not mistaken for a blank click", session.Document.Elements.Count == 2 && session.Document.Elements[^1].Points.Count >= 4 &&
                session.Selection.Count == 0 && session.UndoCount == 2);
        }
    }

    private static void PointerCancellation(Action<string, bool> check)
    {
        var session = new EditorSession(); var controller = new EditorController(session) { Tool = EditorTool.Rectangle };
        Gesture(controller, new(40, 50), new(160, 130));
        string before = JsonSerializer.Serialize(session.Document); var id = session.Selection.Single(); int history = session.UndoCount;
        var center = session.SelectionBounds()!.Value.Center;
        Send(controller, OverlayPointerKind.Down, center); Send(controller, OverlayPointerKind.Move, center + new PointD(50, 20));
        Send(controller, OverlayPointerKind.Cancel, center);
        check("capture cancellation rolls back follow-up adjustment and retains the previous selection", JsonSerializer.Serialize(session.Document) == before &&
            session.Selection.SetEquals([id]) && session.UndoCount == history && !session.IsTransactionActive && controller.Tool == EditorTool.Rectangle);
        Send(controller, OverlayPointerKind.Down, new(400, 300)); Send(controller, OverlayPointerKind.Move, new(480, 350));
        Send(controller, OverlayPointerKind.Cancel, new(480, 350));
        check("capture cancellation removes an unfinished new object and restores the old selection", JsonSerializer.Serialize(session.Document) == before && session.Selection.SetEquals([id]) && session.UndoCount == history);
    }

    private static void ExistingSelectionIsTheTarget(Action<string, bool> check)
    {
        var session = new EditorSession(); var controller = new EditorController(session) { Tool = EditorTool.Rectangle };
        Gesture(controller, new(40, 50), new(180, 140)); var selected = session.Document.Elements.Single();
        var overlapping = selected.Clone(true); session.Add(overlapping); session.SetSelection([selected.Id]);
        string before = JsonSerializer.Serialize(session.Document.Elements.Single(e => e.Id == overlapping.Id));
        var center = session.SelectionBounds()!.Value.Center;
        Gesture(controller, center, center + new PointD(20, 10));
        check("creation mode adjusts the selected object even when another layer covers it", session.Document.Elements.Single(e => e.Id == selected.Id).Transform.OffsetX == 60 &&
            JsonSerializer.Serialize(session.Document.Elements.Single(e => e.Id == overlapping.Id)) == before && session.Selection.SetEquals([selected.Id]));
    }

    private static void ClickObjectsAndImage(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer();
        foreach (var tool in new[] { EditorTool.Text, EditorTool.Number, EditorTool.Emoji, EditorTool.Image })
        {
            var session = new EditorSession(); var controller = new EditorController(session) { Tool = tool };
            if (tool == EditorTool.Text)
                controller.EditText += (existing, point) => CommitText(session, existing, point, controller.Style, renderer);
            if (tool == EditorTool.Image)
                session.Add(new() { Kind = ElementKind.Image, AssetId = session.Assets.Add([1, 2, 3]), Width = 120, Height = 80, Transform = Affine2D.Translation(100, 100) });
            else Gesture(controller, new(100, 100), new(100, 100));
            var element = session.Document.Elements.Single(); var center = session.SelectionBounds()!.Value.Center; int history = session.UndoCount;
            check(tool + " successful insertion selects the object and keeps the active tool", session.Selection.SetEquals([element.Id]) && controller.Tool == tool && controller.ShouldShowSelection(OverlayModifiers.None));
            Gesture(controller, center, center + new PointD(15, 20));
            check(tool + (tool==EditorTool.Text ? " clicking inserted text reopens editing without moving it" : " the inserted object can be moved immediately in its creation mode"), session.Document.Elements.Count == 1 && session.UndoCount == history + (tool==EditorTool.Text?0:1) && controller.Tool == tool);
            check(tool + " Ctrl temporarily hides its adjustment controls", !controller.ShouldShowSelection(OverlayModifiers.Control) && controller.ShouldShowSelection(OverlayModifiers.None));
        }
        var nonCreationSession = new EditorSession();
        nonCreationSession.Add(new() { Kind = ElementKind.Rectangle, Width = 100, Height = 60 });
        var plain = new EditorController(nonCreationSession);
        plain.Tool = EditorTool.Stamp;
        check("Stamp displays an existing selection without making new stamps selected", plain.ShouldShowSelection(OverlayModifiers.None));
        foreach (var tool in new[] { EditorTool.Eraser, EditorTool.Pixelate })
        { plain.Tool = tool; check(tool + " is not accidentally promoted to a creation selection mode", !plain.ShouldShowSelection(OverlayModifiers.None)); }
        plain.Tool = EditorTool.CaptureRegion;
        check("capture retains the current image and object adjustment contract", plain.ShouldShowSelection(OverlayModifiers.None));
        plain.Tool = EditorTool.Crop;
        check("crop without an editable image remains in ordinary selection mode", plain.Tool == EditorTool.Select && plain.ShouldShowSelection(OverlayModifiers.None));
    }

    private static void CtrlGestureOwnership(Action<string, bool> check)
    {
        var session = new EditorSession(); var controller = new EditorController(session) { Tool = EditorTool.Rectangle };
        Gesture(controller, new(40, 50), new(180, 140)); var id = session.Selection.Single(); var center = session.SelectionBounds()!.Value.Center;
        Send(controller, OverlayPointerKind.Down, center);
        Send(controller, OverlayPointerKind.Move, center + new PointD(20, 10), OverlayModifiers.Control);
        Send(controller, OverlayPointerKind.Up, center + new PointD(20, 10), OverlayModifiers.Control);
        check("pressing Ctrl during a selected-object drag does not change the gesture into creation", session.Document.Elements.Count == 1 && session.Selection.SetEquals([id]) &&
            session.Document.Elements.Single().Transform.OffsetX == 60 && session.UndoCount == 2);
        center = session.SelectionBounds()!.Value.Center;
        Send(controller, OverlayPointerKind.Down, center, OverlayModifiers.Control);
        Send(controller, OverlayPointerKind.Move, center + new PointD(50, 30));
        Send(controller, OverlayPointerKind.Up, center + new PointD(50, 30));
        check("Ctrl-started creation remains creation if Ctrl is released before mouse-up", session.Document.Elements.Count == 2 && session.Selection.SetEquals([session.Document.Elements[^1].Id]) &&
            session.UndoCount == 3 && controller.ShouldShowSelection(OverlayModifiers.None));
    }

    private static void ContinuousClickInsertion(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer();
        foreach (var tool in new[] { EditorTool.Text, EditorTool.Number, EditorTool.Emoji })
        {
            var session = new EditorSession(); var controller = new EditorController(session) { Tool = tool };
            if (tool == EditorTool.Text)
                controller.EditText += (existing, point) => CommitText(session, existing, point, controller.Style, renderer);
            Gesture(controller, new(100, 100), new(100, 100));
            var first = session.Selection.Single(); string initial = JsonSerializer.Serialize(session.Document); int history = session.UndoCount;
            Gesture(controller, new(450, 300), new(450, 300));
            var second = session.Document.Elements[^1].Id; string afterSecond = JsonSerializer.Serialize(session.Document);
            check(tool + " a blank click directly inserts and selects another object while retaining the tool", session.Document.Elements.Count == 2 &&
                second != first && session.Selection.SetEquals([second]) && session.UndoCount == history + 1 && controller.Tool == tool);
            Gesture(controller, new(800, 500), new(800, 500));
            var third = session.Document.Elements[^1].Id; string afterThird = JsonSerializer.Serialize(session.Document);
            check(tool + " the next blank click continues insertion without an extra deselect click", session.Document.Elements.Count == 3 && third != second &&
                session.Selection.SetEquals([third]) && session.UndoCount == history + 2 && controller.Tool == tool &&
                (tool != EditorTool.Number || session.Document.Elements.Select(e => e.Number).SequenceEqual([1, 2, 3])));
            check(tool + " undo removes only the latest continuous insertion and restores the previous selection", session.Undo() &&
                JsonSerializer.Serialize(session.Document) == afterSecond && session.Selection.SetEquals([second]));
            check(tool + " the next undo removes the preceding insertion independently", session.Undo() &&
                JsonSerializer.Serialize(session.Document) == initial && session.Selection.SetEquals([first]));
            check(tool + " redo restores both insertions and the newest selected object", session.Redo() && session.Redo() &&
                JsonSerializer.Serialize(session.Document) == afterThird && session.Selection.SetEquals([third]) && controller.Tool == tool);
            var center = session.SelectionBounds()!.Value.Center;
            Gesture(controller, center, center + new PointD(20, 15));
            check(tool + (tool==EditorTool.Text?" inside clicks reopen editing instead of inserting another text layer":" dragging inside the newest selection still adjusts it instead of inserting"), session.Document.Elements.Count == 3 &&
                session.Selection.SetEquals([third]) && session.UndoCount == history + (tool==EditorTool.Text?2:3) && controller.Tool == tool &&
                (tool==EditorTool.Text || JsonSerializer.Serialize(session.Document.Elements[^1]) != JsonSerializer.Serialize(JsonSerializer.Deserialize<SceneDocument>(afterThird)!.Elements[^1])));
            if(tool!=EditorTool.Text)session.Undo();
            center = session.SelectionBounds()!.Value.Center;
            Gesture(controller, center, center, OverlayModifiers.Control);
            check(tool + " Ctrl starts an overlapping insertion rather than adjusting the selected object", session.Document.Elements.Count == 4 &&
                session.Selection.SetEquals([session.Document.Elements[^1].Id]) && session.Document.Elements[^1].Id != third && session.UndoCount == history + 3 &&
                session.Document.Elements.Take(3).Select(e => JsonSerializer.Serialize(e)).SequenceEqual(JsonSerializer.Deserialize<SceneDocument>(afterThird)!.Elements.Select(e => JsonSerializer.Serialize(e))));
        }
        var images = new EditorSession(); images.Add(new() { Kind = ElementKind.Image, AssetId = images.Assets.Add([1, 2, 3]), Width = 120, Height = 80 });
        var imageTool = new EditorController(images) { Tool = EditorTool.Image }; int hints = 0; imageTool.Message += _ => hints++;
        string beforeImageClick = JsonSerializer.Serialize(images.Document); int imageHistory = images.UndoCount;
        Gesture(imageTool, new(500, 350), new(500, 350));
        check("image guidance tool retains blank-click deselection without inserting or opening an import hint", images.Selection.Count == 0 && hints == 0 &&
            JsonSerializer.Serialize(images.Document) == beforeImageClick && images.UndoCount == imageHistory && imageTool.Tool == EditorTool.Image);
    }

    private static void Gesture(EditorController controller, PointD from, PointD to, OverlayModifiers modifiers = OverlayModifiers.None)
    { Send(controller, OverlayPointerKind.Down, from, modifiers); Send(controller, OverlayPointerKind.Move, to, modifiers); Send(controller, OverlayPointerKind.Up, to, modifiers); }
    private static void CommitText(EditorSession session,CanvasElement? existing,PointD point,ElementStyle style,SceneRenderer renderer)
    {using var edit=new InlineTextEdit(session,existing,point,style,renderer);edit.Input.Text="Editable";edit.Finish(true);}
    private static void Send(EditorController controller, OverlayPointerKind kind, PointD point, OverlayModifiers modifiers = OverlayModifiers.None) =>
        controller.Pointer(new(kind, new(point.X, point.Y), OverlayMouseButton.Left, modifiers));
}
