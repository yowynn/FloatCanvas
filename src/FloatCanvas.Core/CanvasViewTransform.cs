namespace FloatCanvas.Core;

/// <summary>Local viewing: optional plane translation before perspective, then output zoom/offset.
/// Coordinates are relative to the canvas output,
/// never absolute desktop coordinates. Applying this value to authored data is a separate command.</summary>
public readonly record struct CanvasViewTransform(double Zoom, PointD Offset, PointD PlaneOffset = default)
{
    public const double MinimumZoom = .01;
    public const double MaximumZoom = 64;
    public static CanvasViewTransform Identity => new(1, default);
    public bool IsValid => double.IsFinite(Zoom) && Zoom >= MinimumZoom && Zoom <= MaximumZoom &&
        double.IsFinite(Offset.X) && double.IsFinite(Offset.Y) && double.IsFinite(PlaneOffset.X) && double.IsFinite(PlaneOffset.Y);
    public Affine2D Matrix
    {
        get { RequireValid(); return new(Zoom, 0, 0, Zoom, Offset.X, Offset.Y); }
    }

    public PointD ToView(PointD canvasOutput) => Matrix.TransformPoint(canvasOutput);
    public PointD ToCanvas(PointD viewPoint)
    {
        RequireValid(); return (viewPoint - Offset) * (1 / Zoom);
    }

    public CanvasViewTransform Pan(PointD viewDelta)
    {
        RequireValid(); return Checked(this with { Offset = Offset + viewDelta });
    }

    /// <summary>Mouse axes drive the authored plane axes before perspective projection.</summary>
    public CanvasViewTransform PanPlane(PointD viewDelta)
    {
        RequireValid(); return Checked(this with { PlaneOffset = PlaneOffset + viewDelta * (1 / Zoom) });
    }

    public Projective2D ProjectPlane(Projective2D projection) => projection.Compose(
        Projective2D.FromAffine(Affine2D.Translation(PlaneOffset.X, PlaneOffset.Y)));

    public CanvasSettings ViewingSettings(CanvasSettings settings, RectD reference)
    {
        RequireValid();
        var result = settings.ResolveReference(reference);
        if (result.Projection.Enabled) result.Projection.Matrix = ProjectPlane(result.Projection.Matrix);
        return result;
    }

    /// <summary>Retains the canvas point underneath the given viewport point.</summary>
    public CanvasViewTransform ZoomAt(double requestedZoom, PointD viewAnchor)
    {
        RequireValid();
        if (!double.IsFinite(requestedZoom) || requestedZoom <= 0) throw new ArgumentOutOfRangeException(nameof(requestedZoom));
        double zoom = Math.Clamp(requestedZoom, MinimumZoom, MaximumZoom);
        return Checked(new(zoom, viewAnchor - ToCanvas(viewAnchor) * zoom, PlaneOffset));
    }

    public CanvasViewTransform Center(PointD canvasOutputCenter, RectD viewport)
    {
        RequireValid(); RequireViewport(viewport);
        return Checked(new(Zoom, viewport.Center - canvasOutputCenter * Zoom, PlaneOffset));
    }

    /// <summary>First-entry framing uses allowEnlarge=false. An explicit Fit command may enlarge.</summary>
    public static CanvasViewTransform Fit(RectD content, RectD viewport, bool allowEnlarge = false, double padding = 32)
    {
        RequireViewport(viewport);
        if (!Finite(content) || content.Width < 0 || content.Height < 0) throw new ArgumentOutOfRangeException(nameof(content));
        if (!double.IsFinite(padding) || padding < 0) throw new ArgumentOutOfRangeException(nameof(padding));
        double availableWidth = Math.Max(1, viewport.Width - 2 * padding);
        double availableHeight = Math.Max(1, viewport.Height - 2 * padding);
        double zoom = Math.Min(availableWidth / Math.Max(1, content.Width), availableHeight / Math.Max(1, content.Height));
        zoom = Math.Clamp(allowEnlarge ? zoom : Math.Min(1, zoom), MinimumZoom, MaximumZoom);
        return Identity.ZoomAt(zoom, default).Center(content.Center, viewport);
    }

    /// <summary>Restores a locally remembered observation center on a different viewport.</summary>
    public static CanvasViewTransform Restore(PointD observationCenter, double zoom, RectD viewport) =>
        Identity.ZoomAt(zoom, default).Center(observationCenter, viewport);

    private void RequireValid()
    {
        if (!IsValid) throw new InvalidOperationException("Invalid canvas viewing transform.");
    }
    private static CanvasViewTransform Checked(CanvasViewTransform value)
    {
        if (!value.IsValid) throw new ArgumentOutOfRangeException(nameof(value));
        return value;
    }
    private static bool Finite(RectD value) => double.IsFinite(value.X) && double.IsFinite(value.Y) &&
        double.IsFinite(value.Width) && double.IsFinite(value.Height) && double.IsFinite(value.Right) && double.IsFinite(value.Bottom);
    private static void RequireViewport(RectD viewport)
    {
        if (!Finite(viewport) || viewport.Width <= 0 || viewport.Height <= 0) throw new ArgumentOutOfRangeException(nameof(viewport));
    }
}
