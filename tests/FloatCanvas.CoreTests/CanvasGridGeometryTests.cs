using FloatCanvas.Core;
using System.Text.Json;

internal static class CanvasGridGeometryTests
{
    public static void Run(Action<bool, string> check)
    {
        void Near(double actual, double expected, string name) => check(Math.Abs(actual - expected) < 1e-7, $"{name}: {actual} / {expected}");
        var viewport = new RectD(-1600, -200, 4000, 1600);
        var primary = new RectD(0, 0, 1920, 1080);
        var scene = new SceneDocument { Viewport = viewport };
        string uninitialized = JsonSerializer.Serialize(scene);
        Near(CanvasMapping.Create(scene).GridCellSize, CanvasCalibration.CreateInitial(viewport).CellSize, "legacy flat scene resolves its original spacing read-only");
        check(JsonSerializer.Serialize(scene) == uninitialized, "reading legacy grid does not initialize document fields");
        scene.Canvas.Grid.FlatOrigin = primary.Center;
        scene.Canvas.Grid.FlatCellSize = 73.125;
        var flat = CanvasMapping.Create(scene);
        check(flat.GridOrigin == primary.Center && flat.PlaneToScreen == Projective2D.Identity && flat.GridCellSize == 73.125,
            "flat grid has an exact independent spacing and primary-screen origin");
        var counts = CanvasGridGeometry.CountsForCellSize(primary, flat.GridCellSize);
        check(counts == (16, 9), "flat size maps to deterministic counts for the primary reference region");
        var reset = CanvasGridGeometry.CreateFlatReference(primary, counts.Columns, counts.Rows, flat.GridCellSize);
        check(reset.CellSize == 73.125 && reset.Matrix == Projective2D.Identity && reset.LockedGridCellSize is null,
            "reset uses exact cell size without quantizing it back through integer counts or locking");
        reset.Enabled = true; scene.Canvas.Projection = reset;
        var perspective = CanvasMapping.Create(scene);
        check(perspective.GridCellSize == flat.GridCellSize && perspective.GridOrigin == flat.GridOrigin && perspective.PlaneToScreen == flat.PlaneToScreen,
            "turning perspective on and resetting produces the same lattice as the flat grid");
        foreach (double size in new[] { 1.0, 73.125, 1_000_000.0 })
        {
            var pair = CanvasGridGeometry.CountsForCellSize(primary, size);
            var settings = CanvasGridGeometry.CreateFlatReference(primary, pair.Columns, pair.Rows, size);
            check(settings.CellSize == size && settings.Matrix == Projective2D.Identity,
                "minimum/maximum count clamping does not round the requested spacing");
        }
        check(CanvasGridGeometry.CountsForCellSize(primary, 1) == (256, 256) &&
            CanvasGridGeometry.CountsForCellSize(primary, 1_000_000) == (1, 1), "derived reference counts stay in the supported range");
        check(CanvasGridGeometry.CountsForCellSize(new(0, 0, 5, 5), 1) == (4, 4), "count derivation preserves the minimum four-pixel reference edges");
        var legacySmall = new SceneDocument { Canvas = new() { Grid = new() { FlatCellSize = .01 } } };
        check(CanvasMapping.Create(legacySmall).GridCellSize == .01, "legacy small flat spacing remains readable without applying the new edit minimum");
        bool tinyReferenceRejected = false;
        try { _ = CanvasGridGeometry.CreateFlatReference(primary, 1, 1, 1); } catch (ArgumentOutOfRangeException) { tinyReferenceRejected = true; }
        check(tinyReferenceRejected, "explicit reset refuses a sub-four-pixel reference instead of constructing invalid calibration");

        PointD[] quad = [new(150, 100), new(750, 80), new(1100, 700), new(100, 650)];
        check(CanvasCalibration.TryCalibrate(reset, quad, viewport, out var calibrated, out _), "asymmetric perspective reference calibrates");
        scene.Canvas.Projection = calibrated;
        var mapping = CanvasMapping.Create(scene);
        var cornerPlane = calibrated.Origin;
        Near((cornerPlane.X - mapping.GridOrigin.X) / mapping.GridCellSize, -calibrated.Columns / 2d,
            "even-count left edge stays on its original grid line after perspective deformation");
        Near((cornerPlane.Y - mapping.GridOrigin.Y) / mapping.GridCellSize, -calibrated.Rows / 2d,
            "even-count top edge stays on its original grid line after perspective deformation");
        var handle = CanvasGridGeometry.ReferenceCenter(calibrated);
        Near(mapping.ToScreen(mapping.GridOrigin).DistanceTo(handle), 0, "perspective grid origin lands at the projective center handle");
        var planeCenter = calibrated.Origin + new PointD(calibrated.Columns * calibrated.CellSize / 2, calibrated.Rows * calibrated.CellSize / 2);
        var arithmeticCenter = new PointD(calibrated.Quad.Average(p => p.X), calibrated.Quad.Average(p => p.Y));
        check(mapping.ToScreen(planeCenter).DistanceTo(arithmeticCenter) > 10, "fixture distinguishes the corrected projective center from the arithmetic screen center");
        var element = new CanvasElement { Kind = ElementKind.Rectangle, Width = 50, Height = 30,
            Transform = Affine2D.Translation(calibrated.Origin.X + 80, calibrated.Origin.Y + 80), Style = new() { StrokeWidth = 2 } };
        scene.Elements.Add(element);
        string layers = JsonSerializer.Serialize(scene.Elements);
        var matrix = calibrated.Matrix; var bounds = mapping.ProjectedBounds(element);
        var origin = calibrated.Origin;
        string calibratedJson = JsonSerializer.Serialize(calibrated);
        for (int iteration = 0; iteration < 100; iteration++)
        {
            scene.Canvas.Projection.Enabled = false; _ = CanvasMapping.Create(scene);
            scene.Canvas.Projection.Enabled = true; _ = CanvasMapping.Create(scene);
        }
        check(JsonSerializer.Serialize(calibrated) == calibratedJson && calibrated.Matrix == matrix && calibrated.Origin == origin,
            "repeated mode toggles never reverse-convert counts or perturb dormant calibration");
        check(JsonSerializer.Serialize(scene.Elements) == layers && CanvasMapping.Create(scene).ProjectedBounds(element) == bounds,
            "grid origins and mode roundtrips leave element placement and the final projection unchanged");
        foreach (var pair in new[] { (8, 6), (12, 10), (256, 256) })
        {
            var input = CanvasCalibration.CreateInitial(viewport, pair.Item1, pair.Item2);
            check(CanvasCalibration.TryCalibrate(input, quad, viewport, out var result, out _), "even-count alignment fixture calibrates");
            scene.Canvas.Projection = result;
            var aligned = CanvasMapping.Create(scene);
            foreach (var corner in result.Quad)
            {
                var plane = aligned.ScreenToPlane.TransformPoint(corner);
                double column = (plane.X - aligned.GridOrigin.X) / aligned.GridCellSize;
                double row = (plane.Y - aligned.GridOrigin.Y) / aligned.GridCellSize;
                Near(column, Math.Round(column), "every even-count corner stays on a column after deformation");
                Near(row, Math.Round(row), "every even-count corner stays on a row after deformation");
            }
        }
        scene.Canvas.Projection = calibrated;
        scene.Canvas.Grid.FlatCellSize = calibrated.GridCellSize;
        var session = new EditorSession(scene);
        string before = JsonSerializer.Serialize(session.Document);
        session.Execute("flat size", document =>
        {
            document.Canvas.Grid.FlatCellSize = 93.75;
            document.Canvas.Grid.FlatOrigin = new(-20, -30);
        });
        string changed = JsonSerializer.Serialize(session.Document);
        check(session.Undo() && JsonSerializer.Serialize(session.Document) == before && session.Redo() && JsonSerializer.Serialize(session.Document) == changed,
            "flat grid parameters clone and undo with the rest of the canvas");
        foreach (double invalid in new[] { 0, -.01, .001, .5, 1_000_001, double.NaN, double.PositiveInfinity })
        {
            bool rejected = false;
            try { _ = CanvasGridGeometry.CountsForCellSize(primary, invalid); } catch (ArgumentOutOfRangeException) { rejected = true; }
            check(rejected, "invalid flat spacing is rejected before producing reference counts");
        }
    }
}
