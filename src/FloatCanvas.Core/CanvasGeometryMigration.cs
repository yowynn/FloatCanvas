namespace FloatCanvas.Core;

/// <summary>Adopts existing geometry verbatim. Never recalibrates loaded content or changes its matrix.</summary>
public static class CanvasGeometryMigration
{
    public static void UpgradeLegacy(CanvasSettings canvas)
    {
        var projection = canvas.Projection;
        if (projection.PlaneBounds is not null || projection.Quad.Count != 4) return;
        var pin = projection.PinPoint;
        double spacing = projection.GridCellSize;
        projection.PlaneBounds = CanvasProjectionGeometry.ResolvePlaneBounds(projection);
        projection.PinPlanePoint = pin;
        canvas.Grid.PlaneCellSize ??= spacing;
        canvas.Grid.PlaneOrigin ??= pin;
        projection.AlgorithmVersion = CanvasCalibration.AlgorithmVersion;
    }
}
