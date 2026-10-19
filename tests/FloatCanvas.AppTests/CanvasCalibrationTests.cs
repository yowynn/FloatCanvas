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
        controller.Move(initial[0] + new PointD(4, 4)); controller.Move(initial[0] + new PointD(12, 12)); controller.Up();
        check("calibration gesture commits one undo", session.UndoCount == 1 && !session.IsTransactionActive);
        session.Undo();
        check("calibration undo restores quad", session.Document.Canvas.Projection.Quad.SequenceEqual(initial));
        var center = new PointD(initial.Average(p => p.X), initial.Average(p => p.Y));
        controller.Down(center); controller.Move(center + new PointD(10, 10)); controller.Escape();
        check("escape cancels drag and preserves mode", controller.IsActive && !controller.IsDragging && session.Document.Canvas.Projection.Quad.SequenceEqual(initial));
        controller.Down(initial[0]);
        string? error = null; controller.Error += value => error = value;
        controller.Move(initial[2]);
        check("invalid target stops at a valid projection boundary", error == "Canvas.InvalidQuad" && controller.IsDragConstrained &&
            CanvasCalibration.IsStableViewport(session.Document.Canvas.Projection.Matrix, document.Viewport) &&
            CanvasCalibration.TryGridToScreen(8, 6, session.Document.Canvas.Projection.Quad, out _));
        controller.CancelDrag();
        check("cancelled constrained gesture adds no undo", session.UndoCount == 0 && !controller.IsDragConstrained && session.Document.Canvas.Projection.Quad.SequenceEqual(initial));
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
        RunCenterHandle(check);
        RunBoundaryDragging(check);
        CanvasGridLockCalibrationTests.Run(check);
    }

    private static void RunCenterHandle(Action<string, bool> check)
    {
        var document = new SceneDocument();
        var reference = CanvasCalibration.CreateInitial(document.Viewport);
        PointD[] thin = [new(100, 100), new(600, 100), new(600, 112), new(100, 112)];
        check("thin center-handle fixture is a stable valid calibration", CanvasCalibration.TryCalibrate(reference, thin, document.Viewport, out var projection, out _));
        document.Canvas.Projection = projection;
        var session = new EditorSession(document); var controller = new CanvasCalibrationController(); controller.Begin(session);
        var center = new PointD(350, 106); controller.Down(center); controller.Move(center + new PointD(0, 100)); controller.Up();
        check("thin frame center handle moves all four corners instead of the nearby edge", session.Document.Canvas.Projection.Quad.Select((p, i) => p.DistanceTo(thin[i] + new PointD(0, 100))).Max() < 1e-7);
        session.Undo();
        check("thin frame center gesture is one reversible transaction", session.Document.Canvas.Projection.Quad.SequenceEqual(thin) && session.UndoCount == 0);
        var handleEdge = center + new PointD(16, 16);
        bool started = controller.Down(handleEdge); controller.Move(handleEdge + new PointD(10, 20)); controller.Up();
        check("center handle square remains hittable where it extends beyond a thin frame", started && session.Document.Canvas.Projection.Quad.Select((p, i) => p.DistanceTo(thin[i] + new PointD(10, 20))).Max() < 1e-7);
        session.Undo();
        // A compact frame puts its corner inside the painted center handle; corners keep priority.
        PointD[] compact = [new(100, 100), new(120, 100), new(120, 120), new(100, 120)];
        CanvasCalibration.TryCalibrate(reference, compact, document.Viewport, out projection, out _);
        session.Document.Canvas.Projection = projection;
        controller.Down(compact[0]); controller.Move(compact[0] + new PointD(-2, -2), FloatCanvas.Windows.OverlayModifiers.Control); controller.Up();
        check("corner handles retain priority when a compact frame overlaps the center handle", session.Document.Canvas.Projection.Quad[0].DistanceTo(compact[0]) > .001 && session.Document.Canvas.Projection.Quad.Skip(1).SequenceEqual(compact.Skip(1)));
        session.Document.Canvas.Projection = reference;
        var initial = reference.Quad.ToArray(); var edge = (initial[1] + initial[2]) * .5;
        controller.Down(edge); controller.Move(edge + new PointD(20, 0)); controller.Up();
        check("normal edge handles still move only their two endpoints", session.Document.Canvas.Projection.Quad[0] == initial[0] && session.Document.Canvas.Projection.Quad[3] == initial[3] &&
            session.Document.Canvas.Projection.Quad[1] == initial[1] + new PointD(20, 0) && session.Document.Canvas.Projection.Quad[2] == initial[2] + new PointD(20, 0));
        controller.End();
    }

    internal static void RunBoundaryDragging(Action<string, bool> check)
    {
        var cases = new (string Name, RectD Screen, RectD Desktop, PointD Translation)[]
        {
            ("single screen", new(0, 0, 2560, 1440), new(0, 0, 2560, 1440), new(0, 0)),
            ("Pad below mixed-size main", new(0, 0, 3840, 2160), new(0, 0, 3840, 3600), new(-533, 1800)),
            ("negative left screen", new(0, 0, 2560, 1440), new(-1600, 0, 4160, 1440), new(-2080, -120)),
            ("negative upper screen", new(0, 0, 2560, 1440), new(0, -1200, 2560, 2640), new(0, -1320)),
            ("right mixed-size screen", new(-2560, -200, 2560, 1440), new(-2560, -200, 4480, 1440), new(3520, 20))
        };
        foreach (var test in cases)
        {
            SceneDocument CreateDocument()
            {
                var value = new SceneDocument { Viewport = test.Desktop };
                value.Canvas.Projection = CanvasCalibration.CreateInitial(test.Screen);
                value.Canvas.Projection.Enabled = true;
                return value;
            }
            var document = CreateDocument();
            var session = new EditorSession(document);
            var controller = new CanvasCalibrationController(); controller.Begin(session);
            var initial = document.Canvas.Projection.Quad.ToArray();
            var start = initial[0];
            var delta = new PointD(0, (initial[3].Y - start.Y) * .9);
            var target = initial.ToArray(); target[0] += delta;
            check($"{test.Name}: boundary fixture remains convex", CanvasCalibration.TryGridToScreen(8, 6, target, out _));
            check($"{test.Name}: boundary fixture reaches horizon guard", !CanvasCalibration.TryCalibrate(document.Canvas.Projection, target, test.Desktop, out _, out var error) && error == "Canvas.UnstableProjection");
            int errors = 0; controller.Error += _ => errors++;
            controller.Down(start);
            bool advanced = controller.Move(start + delta);
            var clipped = document.Canvas.Projection.Quad[0];
            check($"{test.Name}: coarse drag advances continuously to safe boundary", advanced && clipped.Y > start.Y + 4 && clipped.Y < start.Y + delta.Y && controller.IsDragConstrained);
            check($"{test.Name}: clipped preview remains convex and stable", CanvasCalibration.TryGridToScreen(8, 6, document.Canvas.Projection.Quad, out _) && CanvasCalibration.IsStableViewport(document.Canvas.Projection.Matrix, test.Desktop));
            var mapping = CanvasMapping.Create(document);
            PointD[] samples = [new(test.Desktop.Left, test.Desktop.Top), new(test.Desktop.Right, test.Desktop.Bottom), test.Desktop.Center];
            check($"{test.Name}: constrained preview retains finite viewport roundtrips", samples.All(p => mapping.TryToPlane(p, out var plane) && mapping.ToScreen(plane).DistanceTo(p) < .001));
            controller.Move(start + delta);
            check($"{test.Name}: repeated blocked samples do not repeat errors", errors == 1);
            controller.Up();
            check($"{test.Name}: constrained gesture commits one undo", session.UndoCount == 1 && !controller.IsDragConstrained);
            session.Undo();
            check($"{test.Name}: constrained gesture undo restores exact corners", session.Document.Canvas.Projection.Quad.SequenceEqual(initial));
            document = session.Document;
            controller.Down(start);
            for (int step = 1; step <= 120; step++) controller.Move(start + delta * (step / 120.0));
            check($"{test.Name}: safe boundary is independent of coarse versus fine input", document.Canvas.Projection.Quad[0].DistanceTo(clipped) < .02);
            controller.Move(start + delta * .05);
            check($"{test.Name}: returning from boundary tracks original drag coordinates", !controller.IsDragConstrained && document.Canvas.Projection.Quad[0].DistanceTo(start + delta * .05) < 1e-7);
            controller.Escape();
            check($"{test.Name}: cancelling restores original corners and constraint state", !controller.IsDragConstrained && session.Document.Canvas.Projection.Quad.SequenceEqual(initial));
            document = session.Document;
            var center = new PointD(initial.Average(p => p.X), initial.Average(p => p.Y));
            controller.Down(center); bool translated = controller.Move(center + test.Translation); controller.Up();
            check($"{test.Name}: whole frame crosses screens without corner constraints", translated && !controller.IsDragConstrained && document.Canvas.Projection.Quad.Select((p, i) => p.DistanceTo(initial[i] + test.Translation)).Max() < 1e-7);
            controller.End();
        }
        var extreme = new SceneDocument(); extreme.Canvas.Projection = CanvasCalibration.CreateInitial(extreme.Viewport);
        var extremeSession = new EditorSession(extreme); var extremeController = new CanvasCalibrationController(); extremeController.Begin(extremeSession);
        var before = extreme.Canvas.Projection.Quad.ToArray(); extremeController.Down(before[0]);
        extremeController.Move(new(double.NaN, double.PositiveInfinity));
        check("nonfinite pointer samples preserve finite projection", extremeController.IsDragConstrained && extreme.Canvas.Projection.Quad.SequenceEqual(before) && extreme.Canvas.Projection.Matrix.IsFinite);
        extremeController.Move(before[2]);
        check("crossing a corner remains constrained and never inverts the projection", extremeController.IsDragConstrained && CanvasCalibration.TryGridToScreen(8, 6, extreme.Canvas.Projection.Quad, out _) && CanvasCalibration.IsStableViewport(extreme.Canvas.Projection.Matrix, extreme.Viewport));
        var lastValid = extreme.Canvas.Projection.Quad.ToArray();
        extremeController.Move(new(1e300, -1e300));
        check("extreme finite pointer preserves last stable preview", extreme.Canvas.Projection.Quad.SequenceEqual(lastValid) && extreme.Canvas.Projection.Matrix.IsFinite);
        extremeController.CancelDrag();
        check("extreme drag cancellation restores finite initial projection", !extremeController.IsDragConstrained && extremeSession.Document.Canvas.Projection.Quad.SequenceEqual(before) && extremeSession.UndoCount == 0);
        for (int corner = 0; corner < 4; corner++)
        {
            extremeController.Down(before[corner]); extremeController.Move(before[(corner + 2) % 4]);
            check($"corner {corner}: coarse crossing sample approaches valid boundary", extremeController.IsDragConstrained &&
                extremeSession.Document.Canvas.Projection.Quad[corner].DistanceTo(before[corner]) > 4 &&
                CanvasCalibration.TryGridToScreen(8, 6, extremeSession.Document.Canvas.Projection.Quad, out _) &&
                CanvasCalibration.IsStableViewport(extremeSession.Document.Canvas.Projection.Matrix, extremeSession.Document.Viewport));
            extremeController.CancelDrag();
        }
        var midpoint = (before[1] + before[2]) * .5;
        extremeController.Down(midpoint); extremeController.Move(midpoint + new PointD(-2000, 0));
        var edgePreview = extremeSession.Document.Canvas.Projection.Quad;
        check("constrained edge drag keeps both endpoints together and the opposite edge fixed", extremeController.IsDragConstrained &&
            edgePreview[0] == before[0] && edgePreview[3] == before[3] &&
            (edgePreview[1] - before[1]).DistanceTo(edgePreview[2] - before[2]) < 1e-7 &&
            edgePreview[1].X < before[1].X - 4 && CanvasCalibration.IsStableViewport(extremeSession.Document.Canvas.Projection.Matrix, extremeSession.Document.Viewport));
        extremeController.Move(midpoint + new PointD(-10, 0));
        check("edge drag recovers original pointer tracking after constraint", !extremeController.IsDragConstrained && extremeSession.Document.Canvas.Projection.Quad[1].DistanceTo(before[1] + new PointD(-10, 0)) < 1e-7);
        extremeController.CancelDrag();
    }
}
