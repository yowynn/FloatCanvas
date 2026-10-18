using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;

internal static class DragSizedObjectsTests
{
    public static void Run(Action<string, bool> check)
    {
        foreach (var tool in new[] { EditorTool.Number, EditorTool.Emoji })
        {
            var (session, editor) = Create(tool);
            var origin = new PointD(300, 240);
            Send(editor, OverlayPointerKind.Down, origin);
            var first = session.Document.Elements.Single();
            var defaultBounds = ElementGeometry.Bounds(first, false);
            check(tool + " starts one reversible preview without committing content or a number", session.IsTransactionActive &&
                session.UndoCount == 0 && session.CreateCommittedSnapshot().Elements.Count == 0 && session.Document.NextNumber == 23);
            check(tool + " advertises exactly its preview span to the renderer", editor.RasterPreview == (0, 0, false));
            Send(editor, OverlayPointerKind.Up, origin);
            check(tool + " a click keeps the configured size and placement", !session.IsTransactionActive && session.UndoCount == 1 &&
                ElementGeometry.Bounds(first, false) == defaultBounds && Near(defaultBounds.Center.X, origin.X) && Near(defaultBounds.Center.Y, origin.Y));
            check(tool + " a click selects the object without changing tools", session.Selection.SetEquals([first.Id]) && editor.Tool == tool);
            check(tool + " counts a committed number only once", session.Document.NextNumber == (tool == EditorTool.Number ? 26 : 23));
            check(tool + " one undo restores the empty document and starting number", session.Undo() && session.Document.Elements.Count == 0 && session.Document.NextNumber == 23);
            check(tool + " redo restores the inserted object and number", session.Redo() && session.Document.Elements.Count == 1 && session.Selection.SetEquals([first.Id]) &&
                session.Document.NextNumber == (tool == EditorTool.Number ? 26 : 23));

            foreach (var delta in new[] { new PointD(180, 100), new(-180, 100), new(180, -100), new(-180, -100), new(0, 150), new(150, 0), new(5, 4) })
            {
                var (drawingSession, drawingEditor) = Create(tool);
                string defaults = JsonSerializer.Serialize(drawingEditor.Parameters);
                drawingEditor.ScreenOrigin = new(-1920, -300);
                var start = new PointD(-1700, -100);
                Send(drawingEditor, OverlayPointerKind.Down, start);
                var drawn = drawingSession.Document.Elements.Single();
                double factor = Math.Max(1 / Math.Min(drawn.Width, drawn.Height), Math.Max(Math.Abs(delta.X) / drawn.Width, Math.Abs(delta.Y) / drawn.Height));
                var actualStart = start - drawingEditor.ScreenOrigin;
                Send(drawingEditor, OverlayPointerKind.Move, start + delta);
                var bounds = ElementGeometry.Bounds(drawn, false);
                check(tool + " previews an undistorted drag " + delta, drawingSession.IsTransactionActive && drawingSession.UndoCount == 0 &&
                    Near(bounds.Width, drawn.Width * factor) && Near(bounds.Height, drawn.Height * factor) &&
                    Near(bounds.X, delta.X < 0 ? actualStart.X - bounds.Width : actualStart.X) &&
                    Near(bounds.Y, delta.Y < 0 ? actualStart.Y - bounds.Height : actualStart.Y));
                Send(drawingEditor, OverlayPointerKind.Up, start + delta);
                check(tool + " commits that drag once with unchanged defaults " + delta, drawingSession.UndoCount == 1 && !drawingSession.IsTransactionActive &&
                    drawingSession.Selection.SetEquals([drawn.Id]) && JsonSerializer.Serialize(drawingEditor.Parameters) == defaults && ElementGeometry.Bounds(drawn, false) == bounds);
            }

            var (jitterSession, jitterEditor) = Create(tool);
            Send(jitterEditor, OverlayPointerKind.Down, origin);
            var configured = ElementGeometry.Bounds(jitterSession.Document.Elements.Single(), false);
            Send(jitterEditor, OverlayPointerKind.Move, origin + new PointD(1, 1));
            Send(jitterEditor, OverlayPointerKind.Up, origin + new PointD(1, 1));
            check(tool + " subthreshold hand jitter remains a configured-size click", ElementGeometry.Bounds(jitterSession.Document.Elements.Single(), false) == configured);

            var (releaseSession, releaseEditor) = Create(tool);
            Send(releaseEditor, OverlayPointerKind.Down, origin);
            Send(releaseEditor, OverlayPointerKind.Up, origin + new PointD(140, 90));
            check(tool + " the final release position sizes a drag even with no move packets", releaseSession.UndoCount == 1 &&
                ElementGeometry.Bounds(releaseSession.Document.Elements.Single(), false).Width > 100);

            var (cancelSession, cancelEditor) = Create(tool);
            cancelSession.Add(new() { Kind = ElementKind.Rectangle, Width = 60, Height = 40 });
            string before = JsonSerializer.Serialize(cancelSession.Document); var selection = cancelSession.Selection.ToArray(); int undo = cancelSession.UndoCount;
            Send(cancelEditor, OverlayPointerKind.Down, origin);
            Send(cancelEditor, OverlayPointerKind.Move, origin + new PointD(150, 120));
            Send(cancelEditor, OverlayPointerKind.Cancel, origin + new PointD(150, 120));
            check(tool + " cancelling restores content selection number and history", JsonSerializer.Serialize(cancelSession.Document) == before &&
                cancelSession.Selection.SetEquals(selection) && cancelSession.UndoCount == undo && !cancelSession.IsTransactionActive);
            Send(cancelEditor, OverlayPointerKind.Down, origin);
            Send(cancelEditor, OverlayPointerKind.Move, origin + new PointD(80, 60));
            cancelEditor.Tool = EditorTool.Rectangle;
            check(tool + " changing tools cancels the unfinished insertion", JsonSerializer.Serialize(cancelSession.Document) == before &&
                cancelSession.Selection.SetEquals(selection) && cancelSession.UndoCount == undo && !cancelSession.IsTransactionActive);

            var (overlapSession, overlapEditor) = Create(tool);
            Send(overlapEditor, OverlayPointerKind.Down, origin); Send(overlapEditor, OverlayPointerKind.Up, origin);
            var original = overlapSession.Document.Elements.Single(); string originalData = JsonSerializer.Serialize(original);
            Send(overlapEditor, OverlayPointerKind.Down, origin, OverlayModifiers.Control);
            Send(overlapEditor, OverlayPointerKind.Move, origin + new PointD(100, 80));
            Send(overlapEditor, OverlayPointerKind.Up, origin + new PointD(100, 80));
            check(tool + " Ctrl starts a distinct overlapping drag even when released midway", overlapSession.Document.Elements.Count == 2 &&
                JsonSerializer.Serialize(original) == originalData && overlapSession.Selection.SetEquals([overlapSession.Document.Elements[^1].Id]) && overlapSession.UndoCount == 2);
            var selected = overlapSession.Document.Elements[^1]; var center = ElementGeometry.Bounds(selected, false).Center;
            var oldBounds = ElementGeometry.Bounds(selected, false);
            Send(overlapEditor, OverlayPointerKind.Down, center);
            Send(overlapEditor, OverlayPointerKind.Move, center + new PointD(25, 20));
            Send(overlapEditor, OverlayPointerKind.Up, center + new PointD(25, 20));
            var moved = ElementGeometry.Bounds(selected, false);
            check(tool + " starting inside the selected object still moves it", overlapSession.Document.Elements.Count == 2 &&
                Near(moved.X, oldBounds.X + 25) && Near(moved.Y, oldBounds.Y + 20) && overlapSession.UndoCount == 3);
        }
    }

    private static (EditorSession, EditorController) Create(EditorTool tool)
    {
        var session = new EditorSession(new SceneDocument { NextNumber = 23 });
        var editor = new EditorController(session) { Tool = tool, NumberStep = 3, Emoji = "❤️" };
        editor.Style.FontSize = 36;
        return (session, editor);
    }
    private static bool Near(double first, double second) => Math.Abs(first - second) < .00001;
    private static void Send(EditorController editor, OverlayPointerKind kind, PointD point, OverlayModifiers modifiers = OverlayModifiers.None) =>
        editor.Pointer(new(kind, new(point.X, point.Y), OverlayMouseButton.Left, modifiers));
}
