using System.Text.Json.Serialization;

namespace FloatCanvas.Core;

public sealed class CanvasSettings
{
    public uint BackgroundColor { get; set; }
    /// <summary>Use canvas-plane depth for compositing while perspective is enabled. Retained when perspective is disabled.</summary>
    public bool DepthPriorityOcclusion { get; set; }
    public CanvasProjectionSettings Projection { get; set; } = new();
    public CanvasGridSettings Grid { get; set; } = new();
    public CanvasSettings Clone() => new() { BackgroundColor = BackgroundColor, DepthPriorityOcclusion = DepthPriorityOcclusion, Projection = Projection.Clone(), Grid = Grid.Clone() };
}

public sealed class CanvasProjectionSettings
{
    public bool Enabled { get; set; }
    public int Columns { get; set; } = 8;
    public int Rows { get; set; } = 6;
    /// <summary>Top-left of the reference grid in plane coordinates.</summary>
    public PointD Origin { get; set; }
    /// <summary>Persisted plane units per square cell; loading never recalibrates this value.</summary>
    public double CellSize { get; set; } = 1;
    /// <summary>Fixed display-grid spacing in plane units; null follows calibration's reference cell size.</summary>
    public double? LockedGridCellSize { get; set; }
    [JsonIgnore]
    public double GridCellSize => LockedGridCellSize is { } size && double.IsFinite(size) && size > 0 ? size : CellSize;
    [JsonIgnore]
    public double GridColumns => Columns * (CellSize / GridCellSize);
    [JsonIgnore]
    public double GridRows => Rows * (CellSize / GridCellSize);
    /// <summary>Screen corners in top-left, top-right, bottom-right, bottom-left order. Empty means not yet initialized.</summary>
    public List<PointD> Quad { get; set; } = [];
    public Projective2D Matrix { get; set; } = Projective2D.Identity;
    public int AlgorithmVersion { get; set; } = 1;
    public CanvasProjectionSettings Clone() => new()
    {
        Enabled = Enabled, Columns = Columns, Rows = Rows, Origin = Origin, CellSize = CellSize, LockedGridCellSize = LockedGridCellSize,
        Quad = [.. Quad], Matrix = Matrix, AlgorithmVersion = AlgorithmVersion
    };
}

public sealed class CanvasGridSettings
{
    public bool Visible { get; set; }
    public uint Color { get; set; } = 0x80FFFFFF;
    public double Width { get; set; } = 1;
    public StrokePattern Pattern { get; set; }
    public CanvasGridSettings Clone() => (CanvasGridSettings)MemberwiseClone();
}

/// <summary>Four-point alignment of a square-cell plane. This does not recover a physical game camera.</summary>
public static class CanvasCalibration
{
    public const int MaximumGridCount = 256;
    public const int AlgorithmVersion = 1;
    public static bool TryOrderCorners(IReadOnlyList<PointD> points,out PointD[] quad)
    {
        quad=[];
        if(points.Count!=4||points.Any(p=>!double.IsFinite(p.X)||!double.IsFinite(p.Y)))return false;
        var center=new PointD(points.Sum(p=>p.X/4),points.Sum(p=>p.Y/4));
        var ordered=points.OrderBy(p=>Math.Atan2(p.Y-center.Y,p.X-center.X)).ToArray();
        int first=Enumerable.Range(0,4).OrderBy(i=>ordered[i].X+ordered[i].Y).ThenBy(i=>ordered[i].Y).ThenBy(i=>ordered[i].X).First();
        var candidate=Enumerable.Range(0,4).Select(i=>ordered[(first+i)%4]).ToArray();
        if(!TryGridToScreen(1,1,candidate,out _))return false;
        quad=candidate;return true;
    }
    public static CanvasProjectionSettings CreateInitial(RectD viewport, int columns = 8, int rows = 6)
    {
        if (columns is < 1 or > MaximumGridCount || rows is < 1 or > MaximumGridCount) throw new ArgumentOutOfRangeException(nameof(columns));
        double size = Math.Min(viewport.Width * .6 / columns, viewport.Height * .6 / rows);
        if (!double.IsFinite(size) || size <= 0) throw new ArgumentOutOfRangeException(nameof(viewport));
        var origin = new PointD(viewport.Center.X - columns * size / 2, viewport.Center.Y - rows * size / 2);
        return new() { Columns = columns, Rows = rows, Origin = origin, CellSize = size,
            Quad = [origin, origin + new PointD(columns * size, 0), origin + new PointD(columns * size, rows * size), origin + new PointD(0, rows * size)] };
    }
    public static bool TryCalibrate(CanvasProjectionSettings current, IReadOnlyList<PointD> quad, RectD viewport,
        out CanvasProjectionSettings settings, out string? error)
    {
        settings = current.Clone(); error = null;
        if (current.Columns is < 1 or > MaximumGridCount || current.Rows is < 1 or > MaximumGridCount)
        { error = "Canvas.InvalidGrid"; return false; }
        if (!TryGridToScreen(current.Columns, current.Rows, quad, out var gridToScreen))
        { error = "Canvas.InvalidQuad"; return false; }
        PointD[] corners = [new(0, 0), new(current.Columns, 0), new(current.Columns, current.Rows), new(0, current.Rows)];
        var depths = corners.Select(gridToScreen.Denominator).ToArray();
        double minimum = depths.Min();
        if (minimum <= 0) { error = "Canvas.InvalidQuad"; return false; }
        gridToScreen = gridToScreen.ScaleHomogeneous(1 / minimum);
        double spread = depths.Max() / minimum - 1;
        double affineSize = gridToScreen.Jacobian(new(current.Columns / 2.0, current.Rows / 2.0)).MaximumScale;
        double cellSize = affineSize;
        if (spread > .005)
        {
            double length = Math.Sqrt(gridToScreen.M31 * gridToScreen.M31 + gridToScreen.M32 * gridToScreen.M32);
            var tangent = new PointD(-gridToScreen.M32 / length, gridToScreen.M31 / length);
            int nearest = Array.IndexOf(depths, minimum);
            double perspectiveSize = gridToScreen.Jacobian(corners[nearest]).TransformPoint(tangent).DistanceTo(default);
            double blend = Math.Clamp((spread - .005) / .015, 0, 1);
            blend = blend * blend * (3 - 2 * blend);
            cellSize = affineSize + (perspectiveSize - affineSize) * blend;
        }
        if (!double.IsFinite(cellSize) || cellSize < .01 || cellSize > 1_000_000)
        { error = "Canvas.UnstableProjection"; return false; }
        var origin = current.Quad.Count == 0 ? CreateInitial(viewport, current.Columns, current.Rows).Origin : current.Origin;
        var planeToGrid = Projective2D.FromAffine(Affine2D.Scale(1 / cellSize, 1 / cellSize).Compose(Affine2D.Translation(-origin.X, -origin.Y)));
        var matrix = gridToScreen.Compose(planeToGrid);
        if (!IsStableViewport(matrix, viewport)) { error = "Canvas.UnstableProjection"; return false; }
        settings = new() { Enabled = true, Columns = current.Columns, Rows = current.Rows, Origin = origin, CellSize = cellSize, LockedGridCellSize = current.LockedGridCellSize,
            Quad = [.. quad], Matrix = matrix, AlgorithmVersion = AlgorithmVersion };
        return true;
    }
    public static bool IsStableViewport(Projective2D matrix, RectD viewport)
    {
        if (!matrix.TryInverse(out var inverse)) return false;
        var guard = viewport.Inflate(Math.Max(viewport.Width, viewport.Height) * .05 + 16);
        PointD[] corners = [new(guard.Left, guard.Top), new(guard.Right, guard.Top), new(guard.Right, guard.Bottom), new(guard.Left, guard.Bottom)];
        foreach (var point in corners)
        {
            if (!inverse.TryTransformPoint(point, out var plane) || matrix.Denominator(plane) <= 1e-6 ||
                Math.Abs(plane.X) > 100_000_000 || Math.Abs(plane.Y) > 100_000_000) return false;
        }
        // A linear denominator has no interior extremum: same signs at all corners exclude a horizon through the viewport.
        return corners.All(p => inverse.Denominator(p) > 0) || corners.All(p => inverse.Denominator(p) < 0);
    }
    public static bool TryGridToScreen(int columns, int rows, IReadOnlyList<PointD> quad, out Projective2D matrix)
    {
        matrix = default;
        if (columns < 1 || rows < 1 || quad.Count != 4 || quad.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y))) return false;
        double sign = 0;
        for (int i = 0; i < 4; i++)
        {
            var a = quad[(i + 1) % 4] - quad[i]; var b = quad[(i + 2) % 4] - quad[(i + 1) % 4];
            double cross = a.X * b.Y - a.Y * b.X;
            if (a.DistanceTo(default) < 4 || Math.Abs(cross) < 16 || i > 0 && cross * sign <= 0) return false;
            sign = cross;
        }
        // Clockwise screen corners preserve the orientation of the source plane.
        if (sign <= 0) return false;
        // Solve in centered, scaled screen coordinates to retain accuracy on negative/mixed-monitor desktops.
        var center = new PointD(quad.Average(p => p.X), quad.Average(p => p.Y));
        double scale = quad.Max(p => p.DistanceTo(center));
        var q = quad.Select(p => (p - center) * (1 / scale)).ToArray();
        PointD[] source = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        var equations = new double[8, 9];
        for (int i = 0; i < 4; i++)
        {
            double x = source[i].X, y = source[i].Y, u = q[i].X, v = q[i].Y;
            double[] first = [x, y, 1, 0, 0, 0, -u * x, -u * y, u];
            double[] second = [0, 0, 0, x, y, 1, -v * x, -v * y, v];
            for (int j = 0; j < 9; j++) { equations[i * 2, j] = first[j]; equations[i * 2 + 1, j] = second[j]; }
        }
        for (int column = 0; column < 8; column++)
        {
            int pivot = column;
            for (int row = column + 1; row < 8; row++) if (Math.Abs(equations[row, column]) > Math.Abs(equations[pivot, column])) pivot = row;
            if (Math.Abs(equations[pivot, column]) < 1e-10) return false;
            for (int j = column; j < 9; j++) (equations[pivot, j], equations[column, j]) = (equations[column, j], equations[pivot, j]);
            double divisor = equations[column, column];
            for (int j = column; j < 9; j++) equations[column, j] /= divisor;
            for (int row = 0; row < 8; row++)
            {
                if (row == column) continue;
                double factor = equations[row, column];
                for (int j = column; j < 9; j++) equations[row, j] -= factor * equations[column, j];
            }
        }
        var normalized = new Projective2D(equations[0, 8], equations[1, 8], equations[2, 8], equations[3, 8], equations[4, 8], equations[5, 8], equations[6, 8], equations[7, 8], 1);
        matrix = Projective2D.FromAffine(Affine2D.Translation(center.X, center.Y).Compose(Affine2D.Scale(scale, scale)))
            .Compose(normalized).Compose(Projective2D.FromAffine(Affine2D.Scale(1.0 / columns, 1.0 / rows)));
        return matrix.IsFinite && matrix.TryInverse(out _);
    }
}

public sealed class CanvasMapping
{
    private readonly RectD viewport;
    public Projective2D PlaneToScreen { get; }
    public Projective2D ScreenToPlane { get; }
    public PointD GridOrigin { get; }
    public double GridCellSize { get; }
    public IReadOnlyList<PointD> VisiblePlanePolygon { get; }
    private CanvasMapping(SceneDocument document)
    {
        viewport = document.Viewport;
        var settings = document.Canvas.Projection;
        var reference = settings;
        if (settings.Quad.Count == 0)
        {
            reference = CanvasCalibration.CreateInitial(viewport, settings.Columns, settings.Rows);
            reference.LockedGridCellSize = settings.LockedGridCellSize;
        }
        GridOrigin = reference.Origin; GridCellSize = reference.GridCellSize;
        PlaneToScreen = settings.Enabled ? reference.Matrix : Projective2D.Identity;
        ScreenToPlane = PlaneToScreen.Inverse();
        VisiblePlanePolygon = new PointD[] { new(viewport.Left, viewport.Top), new(viewport.Right, viewport.Top), new(viewport.Right, viewport.Bottom), new(viewport.Left, viewport.Bottom) }
            .Select(ScreenToPlane.TransformPoint).ToArray();
    }
    public static CanvasMapping Create(SceneDocument document) => new(document);
    public PointD ToScreen(PointD point) => PlaneToScreen.TransformPoint(point);
    public bool IsVisible(PointD plane) => PlaneToScreen.Denominator(plane) > 1e-8 && PlaneToScreen.TryTransformPoint(plane, out _);
    public bool TryToPlane(PointD point, out PointD plane) => ScreenToPlane.TryTransformPoint(point, out plane) && IsVisible(plane);
    public Projective2D ElementToScreen(CanvasElement element) => BillboardGeometry.ElementToTarget(element, PlaneToScreen);
    public bool TryGetDepthScale(PointD anchor, out double scale) => BillboardGeometry.TryDepthScale(PlaneToScreen, anchor, out scale);
    public bool TryGetEqualDepthLine(PointD screenPoint, out BillboardDepthLine line) => BillboardGeometry.TryEqualDepthLine(this, screenPoint, out line);
    public bool TryGetBillboardAnchor(BillboardDepthLine line, PointD screenCenter, out PointD anchor, out double height)
        => BillboardGeometry.TryAnchor(this, line, screenCenter, out anchor, out height);
    public RectD ProjectedBounds(CanvasElement element, bool includeStroke = true)
    {
        if (element.IsBillboard)
        {
            if (!TryGetDepthScale(element.BillboardAnchor, out _)) return default;
            return ElementToScreen(element).TryTransformBounds(ElementGeometry.StrokedLocalBounds(element, includeStroke), out var billboardBounds) ? billboardBounds : default;
        }
        var planeBounds = ElementGeometry.Bounds(element, includeStroke);
        PointD[] corners = [new(planeBounds.Left, planeBounds.Top), new(planeBounds.Right, planeBounds.Top), new(planeBounds.Right, planeBounds.Bottom), new(planeBounds.Left, planeBounds.Bottom)];
        if (corners.All(p => PlaneToScreen.Denominator(p) <= 0)) return default;
        return ElementToScreen(element).TryTransformBounds(ElementGeometry.StrokedLocalBounds(element, includeStroke), out var bounds) ? bounds : viewport;
    }
}
