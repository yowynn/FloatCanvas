namespace FloatCanvas.Core;

public enum CaptureShape { Rectangle, Ellipse, FreehandLasso, PolygonLasso }
public enum CaptureSource { Background, BackgroundAndCanvas, CanvasOnly }

/// <summary>A transient selection in physical pixel coordinates. Lasso contours close implicitly using the even-odd fill rule.</summary>
public sealed record CaptureSelection(CaptureShape Shape, CaptureSource Source, RectD Bounds,
    IReadOnlyList<PointD> Points, double BorderWidth, uint BorderColor)
{
    public CaptureSelection Translate(PointD offset) => this with
    {
        Bounds = Bounds with { X = Bounds.X + offset.X, Y = Bounds.Y + offset.Y },
        Points = Array.AsReadOnly(Points.Select(point => point + offset).ToArray())
    };
}
