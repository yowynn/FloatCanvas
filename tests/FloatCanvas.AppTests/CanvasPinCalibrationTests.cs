using FloatCanvas.App;
using FloatCanvas.Core;
using System.Text.Json;

internal static class CanvasPinCalibrationTests
{
    internal static void Run(Action<string, bool> check)
    {
        var viewport = new RectD(0, 0, 1000, 800);
        var projection = CanvasCalibration.CreateInitial(viewport, 9, 9);
        var document = new SceneDocument { Viewport = viewport, Canvas = new() { Projection = projection } };
        var element = new CanvasElement { Kind = ElementKind.Rectangle, Width = 100, Height = 100,
            Transform = Affine2D.Translation(projection.PinPoint.X, projection.PinPoint.Y - 100) };
        document.Elements.Add(element);
        var original = JsonSerializer.Serialize(element);
        var session = new EditorSession(document); var controller = new CanvasCalibrationController();
        controller.Begin(session);
        check("corner gesture starts", controller.Down(projection.Quad[0]));
        for (int i = 1; i <= 10; i++)
        {
            controller.Move(projection.Quad[0] + new PointD(i * 5, i * 3));
            var p = session.Document.Canvas.Projection;
            var mapping = CanvasMapping.Create(session.Document);
            check("material pin remains fixed through every drag sample", p.PinPoint == projection.PinPoint);
            check("pinned rectangle corner and marker remain coincident", mapping.ElementToScreen(element).TransformPoint(new(0, 100))
                .DistanceTo(mapping.ToScreen(p.PinPoint)) < 1e-6);
        }
        controller.Up();
        check("one calibration gesture commits one undo step", session.UndoCount == 1 && !session.IsTransactionActive);
        check("calibration changes no layer data", original == JsonSerializer.Serialize(session.Document.Elements[0]));
        check("undo restores entire reference", session.Undo() && session.Document.Canvas.Projection.Quad.SequenceEqual(projection.Quad));
        check("redo restores calibration", session.Redo());
        var before = JsonSerializer.Serialize(session.Document.Canvas);
        check("next corner gesture starts", controller.Down(session.Document.Canvas.Projection.Quad[1]));
        controller.Move(projection.Quad[1] + new PointD(-20, 35)); controller.CancelDrag();
        check("cancel restores pin and mapping", before == JsonSerializer.Serialize(session.Document.Canvas));
        controller.End();
        RunReset(check);
    }

    private static void RunReset(Action<string, bool> check)
    {
        foreach (var reference in new[] { new RectD(0, 0, 1200, 900), new RectD(-1500, 200, 900, 1200) })
        foreach (var (columns, rows) in new[] { (6, 8), (8, 6), (9, 9) })
        foreach (var pin in Enum.GetValues<CanvasPinLocation>())
        foreach (bool locked in new[] { false, true })
        {
            var settings = new CanvasSettings { Projection = CanvasCalibration.CreateInitial(new(0, 0, 1000, 800)) };
            settings.Projection.Columns = columns; settings.Projection.Rows = rows; settings.Projection.PinLocation = pin;
            PointD[] tilted = [new(100, 200), new(750, 80), new(900, 650), new(50, 600)];
            check("reset fixture accepts the current counts and pin", CanvasCalibration.TryCalibrate(settings.Projection, tilted,
                reference, out var projection, out _));
            settings.Projection = projection;
            if (locked) settings.Projection.LockedGridCellSize = projection.CellSize * .75;
            var reset = DesktopSession.CreateResetCanvasProjection(settings, reference);
            var q = reset.Quad;
            double cellWidth = (q[1].X - q[0].X) / columns, cellHeight = (q[3].Y - q[0].Y) / rows;
            string label = $"reset {columns}x{rows} {pin}";
            check(label + " rebuilds square visual cells from the current counts", Math.Abs(cellWidth - cellHeight) < 1e-8);
            check(label + " restores the unwarped document without moving or scaling layers", new[] { new PointD(), new PointD(100, 200), projection.PinPoint }
                .All(p => reset.Matrix.TransformPoint(p).DistanceTo(p) < 1e-6));
            var expectedPin = q[0] + reset.PinGridPoint * cellWidth;
            check(label + " binds the selected point of the new rectangle", reset.PinPoint.DistanceTo(expectedPin) < 1e-6 &&
                reset.Matrix.TransformPoint(reset.PinPoint).DistanceTo(expectedPin) < 1e-6);
            check(label + " establishes the new default reference", reset.ReferenceBounds == new RectD(q[0].X, q[0].Y, columns * cellWidth, rows * cellHeight));
            check(label + " retains automatic fractional coverage and lock state", reset.LockedGridCellSize.HasValue == locked &&
                Math.Abs(reset.GridColumns - projection.GridColumns) < 1e-6 && Math.Abs(reset.GridRows - projection.GridRows) < 1e-6);
            settings.Projection = reset;
            var repeated = DesktopSession.CreateResetCanvasProjection(settings, reference);
            check(label + " repeated reset has no accumulated state", JsonSerializer.Serialize(reset) == JsonSerializer.Serialize(repeated));
            check(label + " can recalibrate its reset rectangle", CanvasCalibration.TryCalibrate(reset, q, reference, out var calibrated, out _));
            check(label + " a later solve preserves the reset display", new[] { new PointD(), new PointD(100, 200), reset.PinPoint }
                .All(p => calibrated.Matrix.TransformPoint(p).DistanceTo(p) < 1e-6));
        }
    }
}
