namespace FloatCanvas.Core;

public enum CanvasGridFitError { None, InvalidProjection, InvalidSample, InvalidCounts, CountOutOfRange, UnsafeResult }
public sealed record CanvasGridFitResult(CanvasSettings Settings, double EstimatedColumns, double EstimatedRows,
    int Columns, int Rows, double SampleDeviation);

/// <summary>Fits a square lattice from a rectangle in a fixed normalized perspective plane.</summary>
public static class CanvasGridCalibration
{
    public static bool TrySample(CanvasProjectionSettings projection, PointD first, PointD opposite,
        out RectD sample, out PointD[] corners)
    {
        sample = default; corners = [];
        if (!CanvasCalibration.TryGridToScreen(1, 1, projection.Quad, out var unit) || !unit.TryInverse(out var inverse) ||
            !inverse.TryTransformPoint(first, out var a) || !inverse.TryTransformPoint(opposite, out var b)) return false;
        sample = new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        if (sample.Width < 1e-8 || sample.Height < 1e-8) return false;
        PointD[] normalized = [new(sample.Left, sample.Top), new(sample.Right, sample.Top), new(sample.Right, sample.Bottom), new(sample.Left, sample.Bottom)];
        if (normalized.Any(p => unit.Denominator(p) <= 0)) return false;
        corners = new PointD[4];
        for (int i = 0; i < 4; i++) if (!unit.TryTransformPoint(normalized[i], out corners[i])) return false;
        return true;
    }
    public static bool TryFitRectangle(CanvasSettings current, RectD sample, int columns, int rows, bool alignBoundary,
        int? candidateColumns, int? candidateRows, out CanvasGridFitResult? fit, out CanvasGridFitError error)
    {
        fit = null; error = CanvasGridFitError.InvalidSample;
        if (!double.IsFinite(sample.X + sample.Y + sample.Width + sample.Height) || sample.Width < 1e-8 || sample.Height < 1e-8) return false;
        error = CanvasGridFitError.InvalidCounts;
        if (columns is < 1 or > CanvasCalibration.MaximumGridCount || rows is < 1 or > CanvasCalibration.MaximumGridCount) return false;
        double nx = columns / sample.Width, ny = rows / sample.Height;
        double cx = candidateColumns ?? Math.Max(1, Math.Round(nx, MidpointRounding.AwayFromZero));
        double cy = candidateRows ?? Math.Max(1, Math.Round(ny, MidpointRounding.AwayFromZero));
        error = CanvasGridFitError.CountOutOfRange;
        if (alignBoundary && (cx is < 1 or > CanvasCalibration.MaximumGridCount || cy is < 1 or > CanvasCalibration.MaximumGridCount)) return false;
        var result = current.Clone(); CanvasGeometryMigration.UpgradeLegacy(result);
        error = CanvasGridFitError.UnsafeResult;
        if (!CanvasProjectionGeometry.TrySetAspect(result.Projection, alignBoundary ? cx / cy : nx / ny, out var projection, out _)) return false;
        var plane = CanvasProjectionGeometry.ResolvePlaneBounds(projection);
        double spacing = plane.Width / (alignBoundary ? cx : nx);
        if (!double.IsFinite(spacing) || spacing is < CanvasGridGeometry.MinimumCellSize or > CanvasGridGeometry.MaximumCellSize) return false;
        result.Projection = projection; result.Grid.PlaneCellSize = spacing;
        if (projection.LockedGridCellSize.HasValue) projection.LockedGridCellSize = spacing;
        result.Grid.PlaneOrigin = alignBoundary ? new(plane.Left, plane.Top) : new(plane.Left + sample.Left * plane.Width, plane.Top + sample.Top * plane.Height);
        error = CanvasGridFitError.InvalidProjection;
        if (!CanvasCalibration.TryGridToScreen(1, 1, projection.Quad, out var unit)) return false;
        double deviation = 0;
        if (alignBoundary)
            foreach (var p in new PointD[] { new(sample.Left, sample.Top), new(sample.Right, sample.Top), new(sample.Right, sample.Bottom), new(sample.Left, sample.Bottom) })
            {
                var snapped = new PointD(Math.Round(p.X * cx) / cx, Math.Round(p.Y * cy) / cy);
                if (!unit.TryTransformPoint(p, out var screen) || !unit.TryTransformPoint(snapped, out var match)) return false;
                deviation = Math.Max(deviation, screen.DistanceTo(match));
            }
        fit = new(result, nx, ny, alignBoundary ? (int)cx : 0, alignBoundary ? (int)cy : 0, deviation);
        error = CanvasGridFitError.None; return true;
    }
}
