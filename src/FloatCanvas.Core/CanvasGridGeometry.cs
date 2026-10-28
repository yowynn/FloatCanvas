namespace FloatCanvas.Core;

/// <summary>Display-grid spacing and reference geometry, independent of element placement coordinates.</summary>
public static class CanvasGridGeometry
{
    public const double MinimumCellSize = .01;
    public const double MinimumEditableCellSize = 1;
    public const double MaximumCellSize = 1_000_000;

    public static double ResolveCellSize(CanvasSettings settings, RectD fallbackViewport)
    {
        if (!settings.Projection.Enabled && settings.Grid.FlatCellSize is { } size) return size;
        if (settings.Projection.Quad.Count != 0) return settings.Projection.GridCellSize;
        var initial = CanvasCalibration.CreateInitial(fallbackViewport, settings.Projection.Columns, settings.Projection.Rows);
        return initial.CellSize;
    }

    public static PointD ReferencePlaneCenter(CanvasProjectionSettings projection) =>
        projection.Origin + new PointD(projection.Columns * projection.CellSize / 2, projection.Rows * projection.CellSize / 2);

    /// <summary>The projective rectangle center, at the intersection of its screen diagonals.</summary>
    public static PointD ReferenceCenter(CanvasProjectionSettings projection) =>
        projection.Matrix.TransformPoint(ReferencePlaneCenter(projection));

    public static (int Columns, int Rows) CountsForCellSize(RectD reference, double cellSize)
    {
        Validate(reference, cellSize);
        if (cellSize < MinimumEditableCellSize) throw new ArgumentOutOfRangeException(nameof(cellSize));
        int Count(double length) => (int)Math.Clamp(Math.Round(length * .6 / cellSize, MidpointRounding.AwayFromZero),
            Math.Max(1, Math.Ceiling(4 / cellSize)), CanvasCalibration.MaximumGridCount);
        return (Count(reference.Width), Count(reference.Height));
    }

    public static CanvasProjectionSettings CreateFlatReference(RectD reference, int columns, int rows, double exactCellSize)
    {
        Validate(reference, exactCellSize);
        if (columns is < 1 or > CanvasCalibration.MaximumGridCount || rows is < 1 or > CanvasCalibration.MaximumGridCount)
            throw new ArgumentOutOfRangeException(nameof(columns));
        if (columns * exactCellSize < 4 || rows * exactCellSize < 4)
            throw new ArgumentOutOfRangeException(nameof(exactCellSize));
        var origin = reference.Center - new PointD(columns * exactCellSize / 2, rows * exactCellSize / 2);
        return new()
        {
            Columns = columns, Rows = rows, Origin = origin, CellSize = exactCellSize, AlgorithmVersion = CanvasCalibration.AlgorithmVersion,
            ReferenceBounds = new(origin.X, origin.Y, columns * exactCellSize, rows * exactCellSize),
            PinPlanePoint = origin + new PointD(0, rows * exactCellSize),
            Quad = [origin, origin + new PointD(columns * exactCellSize, 0),
                origin + new PointD(columns * exactCellSize, rows * exactCellSize), origin + new PointD(0, rows * exactCellSize)]
        };
    }

    private static void Validate(RectD reference, double cellSize)
    {
        if (!double.IsFinite(reference.X) || !double.IsFinite(reference.Y) || !double.IsFinite(reference.Width) ||
            !double.IsFinite(reference.Height) || reference.Width <= 0 || reference.Height <= 0 ||
            !double.IsFinite(reference.Right) || !double.IsFinite(reference.Bottom))
            throw new ArgumentOutOfRangeException(nameof(reference));
        if (!double.IsFinite(cellSize) || cellSize is < MinimumCellSize or > MaximumCellSize)
            throw new ArgumentOutOfRangeException(nameof(cellSize));
    }
}
