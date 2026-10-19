namespace FloatCanvas.Core;

public enum CaptureShape { Rectangle, Ellipse, FreehandLasso, PolygonLasso }
public enum CaptureSource { Background, BackgroundAndCanvas, CanvasOnly }

/// <summary>A transient selection in physical pixel coordinates. Lasso contours close implicitly using the even-odd fill rule.</summary>
public sealed record CaptureSelection(CaptureShape Shape, CaptureSource Source, RectD Bounds,
    IReadOnlyList<PointD> Points, double BorderWidth, uint BorderColor)
{
    /// <summary>Original plane rectangle for a projected rectangular/elliptical capture.</summary>
    public RectD? PlaneBounds { get; init; }
    public Projective2D? PlaneToScreen { get; init; }
    /// <summary>Chosen creation depth, frozen before asynchronous desktop acquisition.</summary>
    public BillboardDepthLine? BillboardDepthLine { get; init; }
    public CaptureSelection Translate(PointD offset) => this with
    {
        Bounds = Bounds with { X = Bounds.X + offset.X, Y = Bounds.Y + offset.Y },
        Points = Array.AsReadOnly(Points.Select(point => point + offset).ToArray()),
        BillboardDepthLine = BillboardDepthLine is { } depth ? depth with { ScreenPoint = depth.ScreenPoint + offset } : null,
        PlaneToScreen = PlaneToScreen is { } projection
            ? Projective2D.FromAffine(Affine2D.Translation(offset.X, offset.Y)).Compose(projection) : null
    };
}
