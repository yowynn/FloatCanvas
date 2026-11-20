using System.Text.Json;
using FloatCanvas.Core;

internal static class CanvasIndependentGeometryTests
{
    internal static void Run(Action<bool, string> check)
    {
        CanvasRectangleCalibrationTests.Run(check);
        void Near(PointD actual, PointD expected, string text) => check(actual.DistanceTo(expected) < 1e-6, text);
        var viewport = new RectD(-200, -100, 1800, 1200);
        var original = CanvasCalibration.CreateInitial(viewport, 13, 9);
        PointD[] quad = [new(120, 150), new(1000, 70), new(1300, 850), new(40, 900)];
        check(CanvasCalibration.TryCalibrate(original, quad, viewport, out original, out _), "legacy nonaffine fixture calibrates");
        original.LockedGridCellSize = 37.125;
        var scene = new SceneDocument { Viewport = viewport, Canvas = new() { Projection = original } };
        var billboard = new CanvasElement { Kind = ElementKind.Rectangle, Width = 80, Height = 120, IsBillboard = true,
            BillboardAnchor = original.PinPoint + new PointD(100, -100), BillboardHeight = 60 };
        scene.Elements.Add(billboard);
        var oldMatrix = original.Matrix; var oldPin = original.PinPoint; var oldBounds = CanvasMapping.Create(scene).ProjectedBounds(billboard);
        var oldGrid = CanvasMapping.Create(scene); string elements = JsonSerializer.Serialize(scene.Elements);
        CanvasProjectionGeometry.InitializePlane(scene.Canvas);
        check(scene.Canvas.Projection.Matrix == oldMatrix && scene.Canvas.Projection.PinPoint == oldPin, "adoption preserves exact matrix and material pin");
        check(CanvasMapping.Create(scene).ProjectedBounds(billboard) == oldBounds && JsonSerializer.Serialize(scene.Elements) == elements,
            "legacy adoption preserves billboard envelope and all element data");
        check(CanvasMapping.Create(scene).GridOrigin == oldGrid.GridOrigin && CanvasMapping.Create(scene).GridCellSize == oldGrid.GridCellSize,
            "legacy adoption preserves locked lattice phase and spacing");
        string adopted = JsonSerializer.Serialize(scene.Canvas); CanvasProjectionGeometry.InitializePlane(scene.Canvas);
        check(JsonSerializer.Serialize(scene.Canvas) == adopted, "geometry adoption is idempotent");
        foreach (double spacing in new[] { .01, 1.0, 32.125, 1_000_000.0 })
        {
            scene.Canvas.Grid.PlaneCellSize = spacing; scene.Canvas.Grid.PlaneOrigin = new(-30, 77);
            var mapping = CanvasMapping.Create(scene);
            check(mapping.GridCellSize == spacing && mapping.GridOrigin == new PointD(-30, 77), "independent lattice uses edited spacing and phase");
            check(mapping.PlaneToScreen == oldMatrix && mapping.ProjectedBounds(billboard) == oldBounds && JsonSerializer.Serialize(scene.Elements) == elements,
                "grid edits cannot alter projection or billboard scale");
        }
        var projection = scene.Canvas.Projection;
        foreach (var pin in Enum.GetValues<CanvasPinLocation>())
        {
            check(CanvasCalibration.TryChangePin(projection, pin, viewport, out var pinned, out _), "each new pin can be selected");
            check(pinned.Matrix == oldMatrix, "changing the anchor does not move any content");
            var fixedPoint = pinned.PinPoint;
            check(CanvasProjectionGeometry.TryResize(pinned, 840, 1188, out var resized, out _), "paper dimensions are independent of counts");
            check(resized.PinPoint == fixedPoint && resized.PlaneBounds!.Value.Width == 840 && resized.PlaneBounds.Value.Height == 1188,
                "resizing preserves the selected logical anchor");
            Near(resized.Matrix.TransformPoint(fixedPoint), pinned.Matrix.TransformPoint(fixedPoint), "resizing preserves the selected screen anchor");
            var bounds = resized.PlaneBounds!.Value;
            PointD[] corners = [new(bounds.Left, bounds.Top), new(bounds.Right, bounds.Top), new(bounds.Right, bounds.Bottom), new(bounds.Left, bounds.Bottom)];
            for (int i = 0; i < 4; i++) Near(resized.Matrix.TransformPoint(corners[i]), quad[i], "independent rectangle maps exactly to the four screen corners");
        }
        check(CanvasProjectionGeometry.TrySetAspect(projection, 210.0 / 297, out var a4, out _), "A4 ratio can be applied without counting cells");
        var paper = a4.PlaneBounds!.Value;
        check(Math.Abs(paper.Width / paper.Height - 210.0 / 297) < 1e-12 && scene.Canvas.Grid.PlaneCellSize == 1_000_000,
            "paper aspect does not overwrite grid spacing");
        var doubledQuad = quad.Select(p => p * 1.4).ToArray();
        check(CanvasCalibration.TryCalibrate(a4, doubledQuad, viewport, out var enlarged, out _) && enlarged.PlaneBounds == a4.PlaneBounds,
            "corner editing retains fixed logical plane dimensions");
        var originalScale = BillboardGeometry.TryDepthScale(a4.Matrix, a4.PinPoint, out double firstScale);
        var enlargedScale = BillboardGeometry.TryDepthScale(enlarged.Matrix, enlarged.PinPoint, out double secondScale);
        check(originalScale && enlargedScale && Math.Abs(secondScale / firstScale - 1.4) < 1e-8, "fixed-plane corner enlargement has an explicit predictable billboard scale");
        var known = scene.Canvas.Clone();
        known.Projection = a4;
        var b = a4.PlaneBounds.Value;
        double cell = Math.Min(b.Width, b.Height) / 12;
        var common = new PointD(b.Left + cell * 2, b.Top + cell * 3);
        foreach (var spans in new[] { (1, 1), (2, 5), (5, 2) })
        {
            PointD first = a4.Matrix.TransformPoint(common);
            PointD last = a4.Matrix.TransformPoint(common + new PointD(spans.Item1 * cell, spans.Item2 * cell));
            check(CanvasGridCalibration.TrySample(a4, first, last, out var sample, out _), "rectangle samples resolve through perspective");
            check(CanvasGridCalibration.TryFitRectangle(known, sample, spans.Item1, spans.Item2, false, null, null, out var fit, out _), "local rectangle spans fit across perspective");
            var fitted = fit!.Settings.Projection.PlaneBounds!.Value;
            check(Math.Abs(fitted.Width / fitted.Height - b.Width / b.Height) < 1e-9 && Math.Abs(fit.Settings.Grid.PlaneCellSize!.Value - cell) < 1e-6,
                "local rectangle recovers target aspect and independent spacing");
            Near(fit.Settings.Projection.Matrix.TransformPoint(fit.Settings.Grid.PlaneOrigin!.Value), first, "unconstrained lattice anchors to sample corner");
        }
        check(!CanvasGridCalibration.TrySample(a4, new(1, 1), new(1, 1), out _, out _), "zero-area samples are rejected");
        check(!CanvasProjectionGeometry.TryResize(a4, double.NaN, 100, out _, out _) && !CanvasProjectionGeometry.TrySetAspect(a4, -1, out _, out _),
            "invalid plane dimensions cannot enter calibration");
        var session = new EditorSession(scene); string before = JsonSerializer.Serialize(scene.Canvas);
        session.ExecuteSettings("grid", d => d.Canvas.Grid.PlaneCellSize = 56.7);
        check(session.Undo() && JsonSerializer.Serialize(session.Document.Canvas) == before && session.Redo() && session.Document.Canvas.Grid.PlaneCellSize == 56.7,
            "independent geometry and lattice participate in undo snapshots");
    }
}
