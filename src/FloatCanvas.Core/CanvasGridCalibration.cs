namespace FloatCanvas.Core;

/// <summary>Fits square cells from a common grid intersection and one known span along each frame axis.</summary>
public static class CanvasGridCalibration
{
    public static bool TryFit(CanvasSettings current, IReadOnlyList<PointD> screenPoints, int columns, int rows,
        out CanvasSettings result, out string? error)
    {
        result = current.Clone(); error = "Canvas.GridCalibrationInvalid";
        if (screenPoints.Count != 3 || columns is < 1 or > 256 || rows is < 1 or > 256 ||
            !CanvasCalibration.TryGridToScreen(1, 1, current.Projection.Quad, out var unit) || !unit.TryInverse(out var inverse)) return false;
        var points = new PointD[3];
        for (int i = 0; i < 3; i++)
            if (!inverse.TryTransformPoint(screenPoints[i], out points[i])) return false;
        var horizontal = points[1] - points[0]; var vertical = points[2] - points[0];
        double dx = Math.Abs(horizontal.X), dy = Math.Abs(vertical.Y);
        // A span must be visibly resolved and follow the calibrated rectangle's axes.
        if (screenPoints[0].DistanceTo(screenPoints[1]) < 8 || screenPoints[0].DistanceTo(screenPoints[2]) < 8 ||
            dx < 1e-5 || dy < 1e-5 || Math.Abs(horizontal.Y) > dx * .08 || Math.Abs(vertical.X) > dy * .08) return false;
        double aspect = columns * dy / (rows * dx);
        CanvasGeometryMigration.UpgradeLegacy(result);
        if (!CanvasProjectionGeometry.TrySetAspect(result.Projection, aspect, out var projection, out error)) return false;
        var plane = CanvasProjectionGeometry.ResolvePlaneBounds(projection);
        double spacing = plane.Width * dx / columns;
        if (!double.IsFinite(spacing) || spacing is < CanvasGridGeometry.MinimumCellSize or > CanvasGridGeometry.MaximumCellSize)
        { error = "Canvas.GridCalibrationInvalid"; return false; }
        result.Projection = projection;
        result.Grid.PlaneCellSize = spacing;
        result.Grid.PlaneOrigin = new(plane.X + points[0].X * plane.Width, plane.Y + points[0].Y * plane.Height);
        error = null; return true;
    }
}
