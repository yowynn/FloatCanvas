using FloatCanvas.Core;
using System.Text.Json;

internal static class CanvasRectangleCalibrationTests
{
    internal static void Run(Action<bool, string> check)
    {
        var settings = new CanvasSettings { Projection = CanvasCalibration.CreateInitial(new(0, 0, 1200, 900)) };
        CanvasGeometryMigration.UpgradeLegacy(settings); settings.Projection.Enabled = true;
        PointD[] q = [new(100, 80), new(900, 160), new(1050, 800), new(50, 720)];
        check(CanvasProjectionGeometry.TryCalibrate(settings.Projection, q, out var projection, out _), "rectangle fixture has nonaffine projection");
        settings.Projection = projection; settings.Grid.Visible = false; settings.Projection.LockedGridCellSize = 17;
        CanvasCalibration.TryGridToScreen(1, 1, q, out var unit);
        PointD At(double x, double y) => unit.TransformPoint(new(x, y));
        var first = At(.2, .2); var last = At(.6, .7);
        check(CanvasGridCalibration.TrySample(projection, last, first, out var sample, out var corners) && corners.Length == 4 && Math.Abs(sample.Width - .4) < 1e-10,
            "reverse rectangle drag resolves the same normalized region");
        string before = JsonSerializer.Serialize(settings);
        check(CanvasGridCalibration.TryFitRectangle(settings, sample, 4, 5, true, null, null, out var fit, out _) && fit!.Columns == 10 && fit.Rows == 10,
            "four-by-five local sample infers ten-by-ten frame without counting the frame");
        var fitted = fit!.Settings; var bounds = fitted.Projection.PlaneBounds!.Value;
        check(Math.Abs(bounds.Width / fitted.Grid.PlaneCellSize!.Value - 10) < 1e-8 && Math.Abs(bounds.Height / fitted.Grid.PlaneCellSize.Value - 10) < 1e-8 &&
            fitted.Grid.PlaneOrigin == new PointD(bounds.Left, bounds.Top) && fitted.Projection.Quad.SequenceEqual(q), "locked boundary puts all four edges on lattice lines without moving screen corners");
        check(!fitted.Grid.Visible && fitted.Projection.LockedGridCellSize == fitted.Grid.PlaneCellSize && JsonSerializer.Serialize(settings) == before,
            "fit preserves visibility and lock intent without mutating source settings");
        var perturbed = new RectD(sample.X + .0002, sample.Y - .0002, sample.Width + .0005, sample.Height - .0005);
        check(CanvasGridCalibration.TryFitRectangle(settings, perturbed, 4, 5, true, null, null, out var stable, out _) &&
            JsonSerializer.Serialize(stable!.Settings) == JsonSerializer.Serialize(fitted), "small local error leaves integer-constrained geometry exactly unchanged");
        check(CanvasGridCalibration.TryFitRectangle(settings, sample, 4, 5, true, 11, 9, out var manual, out _) && manual!.Columns == 11 && manual.Rows == 9,
            "explicit integer candidates adjust both axes independently");
        check(CanvasGridCalibration.TryFitRectangle(settings, sample, 4, 5, false, null, null, out var free, out _) &&
            free!.Settings.Projection.Matrix.TransformPoint(free.Settings.Grid.PlaneOrigin!.Value).DistanceTo(first) < 1e-6,
            "unconstrained grid is anchored to the local sample, not the outer frame");
        check(!CanvasGridCalibration.TryFitRectangle(settings, new(0, 0, .001, .001), 4, 5, true, null, null, out _, out var range) && range == CanvasGridFitError.CountOutOfRange,
            "oversized estimated integer counts report a range error rather than clamping");
        check(!CanvasGridCalibration.TryFitRectangle(settings, sample, 0, 5, true, null, null, out _, out var count) && count == CanvasGridFitError.InvalidCounts,
            "invalid local counts never produce a candidate");
        CanvasReferenceLine[] lines = [new(At(.2, 0), At(.8, 0)), new(At(.2, 1), At(.8, 1)), new(At(0, .25), At(0, .75)), new(At(1, .25), At(1, .75))];
        check(CanvasLineCalibration.TryGetQuad(lines, 1, out var inferred, out _) && inferred.Zip(q).All(p => p.First.DistanceTo(p.Second) < 1e-6),
            "four partial visible edge segments recover hidden corners exactly");
        foreach (double zoom in new[] { .25, 1, 4 })
            check(CanvasLineCalibration.TryGetQuad(lines, 1 / zoom, out _, out _), "adequate line segments remain usable across view zooms");
        CanvasReferenceLine[] parallel = [new(new(0, 0), new(100, 0)), new(new(0, 100), new(100, 100)), new(new(0, 0), new(0, 100)), new(new(100, 0), new(100, 100))];
        check(CanvasLineCalibration.TryGetQuad(parallel, 1, out _, out _), "parallel pairs are valid without finite vanishing points");
        parallel[1] = parallel[0]; check(!CanvasLineCalibration.TryGetQuad(parallel, 1, out _, out _), "coincident frame edges are rejected");
        lines[0] = new(new(0, 0), new(2, 0));
        check(!CanvasLineCalibration.TryGetQuad(lines, 1, out _, out var shortLine) && shortLine == CanvasLineFitError.ShortLine,
            "screen-DIP minimum rejects unresolved reference lines");
    }
}
