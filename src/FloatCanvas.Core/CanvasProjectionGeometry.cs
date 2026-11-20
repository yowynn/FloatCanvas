namespace FloatCanvas.Core;

/// <summary>Fixed source geometry. Grid density is deliberately absent from this interface.</summary>
public static class CanvasProjectionGeometry
{
    /// <summary>Establish the independent plane and grid from a newly adopted quad, preserving its projection.</summary>
    public static void InitializePlane(CanvasSettings canvas)
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

    public static RectD ResolvePlaneBounds(CanvasProjectionSettings projection) => projection.PlaneBounds ??
        new(projection.Origin.X, projection.Origin.Y, projection.Columns * projection.CellSize, projection.Rows * projection.CellSize);

    public static PointD PinFraction(CanvasPinLocation location) => new((int)location % 3 * .5, (int)location / 3 * .5);
    public static PointD PinPoint(RectD bounds, CanvasPinLocation location)
    {
        var fraction = PinFraction(location);
        return new(bounds.X + bounds.Width * fraction.X, bounds.Y + bounds.Height * fraction.Y);
    }
    public static bool IsValidSize(double width, double height) => double.IsFinite(width) && double.IsFinite(height) &&
        width is >= .01 and <= 256_000_000 && height is >= .01 and <= 256_000_000;

    public static bool TryCalibrate(CanvasProjectionSettings current, IReadOnlyList<PointD> quad,
        out CanvasProjectionSettings result, out string? error)
    {
        result = current.Clone(); error = "Canvas.InvalidQuad";
        var bounds = ResolvePlaneBounds(current);
        if (!IsValidSize(bounds.Width, bounds.Height) || !Enum.IsDefined(current.PinLocation) ||
            !CanvasCalibration.TryGridToScreen(1, 1, quad, out var normalized)) return false;
        var sourceToUnit = Projective2D.FromAffine(Affine2D.Scale(1 / bounds.Width, 1 / bounds.Height)
            .Compose(Affine2D.Translation(-bounds.X, -bounds.Y)));
        var matrix = normalized.Compose(sourceToUnit);
        PointD[] corners = [new(bounds.Left, bounds.Top), new(bounds.Right, bounds.Top), new(bounds.Right, bounds.Bottom), new(bounds.Left, bounds.Bottom)];
        double minimum = corners.Min(matrix.Denominator);
        if (!(minimum > 0) || !CanvasCalibration.IsValidProjection(matrix)) return false;
        result.Matrix = matrix.ScaleHomogeneous(1 / minimum);
        result.PlaneBounds = bounds; result.Origin = new(bounds.X, bounds.Y);
        result.Quad = [..quad]; result.Enabled = true; result.AlgorithmVersion = CanvasCalibration.AlgorithmVersion;
        error = null; return true;
    }

    public static bool TryResize(CanvasProjectionSettings current, double width, double height,
        out CanvasProjectionSettings result, out string? error)
    {
        result = current.Clone(); error = "Canvas.InvalidPlaneSize";
        if (!IsValidSize(width, height)) return false;
        var anchor = current.PinPoint; var fraction = PinFraction(current.PinLocation);
        result.PlaneBounds = new(anchor.X - width * fraction.X, anchor.Y - height * fraction.Y, width, height);
        result.PinPlanePoint = anchor;
        return TryCalibrate(result, current.Quad, out result, out error);
    }

    /// <summary>Preserves the longer logical edge when applying a paper/shape ratio.</summary>
    public static bool TrySetAspect(CanvasProjectionSettings current, double aspect,
        out CanvasProjectionSettings result, out string? error)
    {
        result = current.Clone(); error = "Canvas.InvalidPlaneSize";
        if (!double.IsFinite(aspect) || aspect <= 0) return false;
        var bounds = ResolvePlaneBounds(current); double edge = Math.Max(bounds.Width, bounds.Height);
        return TryResize(current, aspect >= 1 ? edge : edge * aspect, aspect >= 1 ? edge / aspect : edge, out result, out error);
    }
}
