using FloatCanvas.App;
using FloatCanvas.Core;

internal static class CanvasCalibrationTests
{
    internal static void Run(Action<string, bool> check)
    {
        CanvasSettingsControlTests.Run(check);
        var document = new SceneDocument();
        document.Canvas.Projection = CanvasCalibration.CreateInitial(document.Viewport);
        document.Canvas.Projection.Enabled = true;
        var session = new EditorSession(document); var controller = new CanvasCalibrationController();
        controller.Begin(session);
        check("calibration activation does not mutate history", session.UndoCount == 0 && !session.IsTransactionActive);
        var initial = session.Document.Canvas.Projection.Quad.ToArray();
        check("calibration corner starts drag", controller.Down(initial[0]));
        controller.Move(initial[0] + new PointD(4, 4)); controller.Move(initial[0] + new PointD(8, 8)); controller.Up();
        check("calibration gesture commits one undo", session.UndoCount == 1 && !session.IsTransactionActive);
        session.Undo();
        check("calibration undo restores quad", session.Document.Canvas.Projection.Quad.SequenceEqual(initial));
        var center = new PointD(initial.Average(p => p.X), initial.Average(p => p.Y));
        controller.Down(center); controller.Move(center + new PointD(10, 10)); controller.Escape();
        check("escape cancels drag and preserves mode", controller.IsActive && !controller.IsDragging && session.Document.Canvas.Projection.Quad.SequenceEqual(initial));
        controller.Down(initial[0]);
        string? error = null; controller.Error += value => error = value;
        controller.Move(initial[2]);
        check("invalid quad leaves previous valid preview", error == "Canvas.InvalidQuad" && session.Document.Canvas.Projection.Quad.SequenceEqual(initial));
        controller.Up();
        check("invalid gesture adds no undo", session.UndoCount == 0);
        controller.Down(center); controller.Move(center + new PointD(10, 10));
        controller.Begin(new EditorSession());
        check("changing sessions rolls back drag", !session.IsTransactionActive && session.Document.Canvas.Projection.Quad.SequenceEqual(initial));
        controller.Escape(); check("escape outside drag ends mode", !controller.IsActive);
        controller.Begin(session);
        var edge = (initial[0] + initial[1]) * .5;
        controller.Down(edge); controller.Move(edge + new PointD(0, 6)); controller.Up();
        var moved = session.Document.Canvas.Projection.Quad;
        check("edge drag moves both endpoints", moved[0].DistanceTo(initial[0] + new PointD(0, 6)) < 1e-8 && moved[1].DistanceTo(initial[1] + new PointD(0, 6)) < 1e-8 && moved[2] == initial[2]);
        controller.End();
    }
}
