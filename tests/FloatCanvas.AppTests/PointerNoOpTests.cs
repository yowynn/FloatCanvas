using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class PointerNoOpTests
{
    public static void Run(Action<string, bool> check)
    {
        foreach (var tool in new[] { EditorTool.Freehand, EditorTool.Highlighter, EditorTool.Line, EditorTool.Arrow,
            EditorTool.DoubleArrow, EditorTool.Rectangle, EditorTool.RoundedRectangle, EditorTool.Ellipse, EditorTool.Triangle })
        {
            var session = new EditorSession(new() { Viewport = new(0, 0, 400, 300) });
            var controller = new EditorController(session) { Tool = tool };
            int changes = 0; controller.Changed += () => changes++;
            Send(controller, OverlayPointerKind.Down, new(20, 30));
            changes = 0;
            Send(controller, OverlayPointerKind.Move, new(20, 30));
            check(tool + ": stationary pointer after down requests no redraw", changes == 0);
            Send(controller, OverlayPointerKind.Move, new(120, 90));
            check(tool + ": changed pointer updates preview", changes > 0);
            changes = 0;
            for (int i = 0; i < 100; i++) Send(controller, OverlayPointerKind.Move, new(120, 90));
            check(tool + ": repeated same-position moves request no redraw", changes == 0);
            Send(controller, OverlayPointerKind.Up, new(125, 95));
            check(tool + ": up still consumes its final coordinate and commits", !session.IsTransactionActive && session.UndoCount == 1 &&
                ElementGeometry.Bounds(session.Document.Elements.Single(), false).Right >= 125);
            controller.Cancel();
            Send(controller, OverlayPointerKind.Down, new(120, 90));
            changes = 0;
            Send(controller, OverlayPointerKind.Move, new(130, 100));
            check(tool + ": new gesture resets stationary-input state", changes > 0);
            controller.Cancel();
        }

        var selected = new EditorSession(new() { Viewport = new(0, 0, 400, 300) });
        selected.Add(new() { Kind = ElementKind.Rectangle, Width = 80, Height = 40,
            Transform = Affine2D.Translation(150, 150) });
        var editor = new EditorController(selected) { Tool = EditorTool.Select };
        var bounds = selected.SelectionBounds()!.Value;
        var start = new PointD(bounds.Center.X, bounds.Top - 28);
        var target = Affine2D.Around(bounds.Center, Affine2D.Rotation(22)).TransformPoint(start);
        Send(editor, OverlayPointerKind.Down, start);
        Send(editor, OverlayPointerKind.Move, target);
        int updates = 0; editor.Changed += () => updates++;
        Send(editor, OverlayPointerKind.Move, target, OverlayModifiers.Shift);
        var transform = selected.Document.Elements.Single().Transform;
        var angle = Math.Atan2(transform.M21, transform.M11) * 180 / Math.PI;
        check("stationary rotation responds to a modifier change", updates > 0 && Math.Abs(angle - 15) < 1e-6);
        updates = 0;
        for (int i = 0; i < 100; i++) Send(editor, OverlayPointerKind.Move, target, OverlayModifiers.Shift);
        check("stationary snapped rotation does not keep rendering", updates == 0);
        editor.Cancel();
    }

    private static void Send(EditorController editor, OverlayPointerKind kind, PointD p, OverlayModifiers modifiers = OverlayModifiers.None) =>
        editor.Pointer(new(kind, new(p.X, p.Y), OverlayMouseButton.Left, modifiers));
}
