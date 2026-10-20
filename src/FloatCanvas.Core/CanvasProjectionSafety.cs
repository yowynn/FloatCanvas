namespace FloatCanvas.Core;

/// <summary>The finite, positive ground side of a projection. The horizon margin is measured in screen pixels.</summary>
public sealed class CanvasProjectionSafety
{
    public const double HorizonMargin = 10;
    public const double MaximumCoordinate = 100_000_000;
    public const double MaximumScale = 100_000_000;
    private readonly Projective2D forward;
    private readonly Projective2D inverse;
    private readonly double horizonNormal;
    private readonly double maximumInverseDepth;

    public CanvasProjectionSafety(Projective2D planeToScreen)
    {
        forward = planeToScreen;
        inverse = planeToScreen.Inverse();
        horizonNormal = Math.Sqrt(inverse.M31 * inverse.M31 + inverse.M32 * inverse.M32);
        // Each Jacobian numerator is linear in one plane coordinate. Bound its Frobenius norm
        // over the finite plane box, then require w² >= numeratorBound / MaximumScale.
        // In screen coordinates 1/w is linear, so rendering and input share one convex guard.
        double crossX = forward.M11 * forward.M32 - forward.M12 * forward.M31;
        double crossY = forward.M21 * forward.M32 - forward.M22 * forward.M31;
        double[] numerators = [Math.Abs(crossX) * MaximumCoordinate + Math.Abs(forward.M11 * forward.M33 - forward.M13 * forward.M31),
            Math.Abs(crossX) * MaximumCoordinate + Math.Abs(forward.M12 * forward.M33 - forward.M13 * forward.M32),
            Math.Abs(crossY) * MaximumCoordinate + Math.Abs(forward.M21 * forward.M33 - forward.M23 * forward.M31),
            Math.Abs(crossY) * MaximumCoordinate + Math.Abs(forward.M22 * forward.M33 - forward.M23 * forward.M32)];
        double largest = numerators.Max();
        double bound = largest * Math.Sqrt(numerators.Sum(n => (n / largest) * (n / largest)));
        maximumInverseDepth = Math.Sqrt(MaximumScale / bound);
    }

    public bool IsScreenSafe(PointD screen) => TryToPlane(screen, out _);

    public bool TryToPlane(PointD screen, out PointD plane)
    {
        plane = default;
        double depth = inverse.Denominator(screen);
        if (!FinitePoint(screen) || depth <= 0 || depth < horizonNormal * HorizonMargin * (1 - 1e-9) || depth > maximumInverseDepth * (1 + 1e-9) ||
            !inverse.TryTransformPoint(screen, out var candidate) || !FinitePoint(candidate) || forward.Denominator(candidate) <= 0 ||
            !forward.TryTransformPoint(candidate, out _))
            return false;
        var scale = forward.Jacobian(candidate).MaximumScale;
        if (!double.IsFinite(scale) || scale > MaximumScale) return false;
        plane = candidate;
        return true;
    }

    public bool IsPlaneSafe(PointD plane) => FinitePoint(plane) && forward.Denominator(plane) > 0 &&
        forward.TryTransformPoint(plane, out var screen) && IsScreenSafe(screen);

    /// <summary>Clips before inverse projection, so a viewport may safely contain the horizon.</summary>
    public IReadOnlyList<PointD> ClipScreen(RectD bounds)
    {
        List<PointD> polygon = [new(bounds.Left, bounds.Top), new(bounds.Right, bounds.Top), new(bounds.Right, bounds.Bottom), new(bounds.Left, bounds.Bottom)];
        polygon = Clip(polygon, p => MaximumCoordinate - p.X);
        polygon = Clip(polygon, p => MaximumCoordinate + p.X);
        polygon = Clip(polygon, p => MaximumCoordinate - p.Y);
        polygon = Clip(polygon, p => MaximumCoordinate + p.Y);
        polygon = Clip(polygon, p => inverse.Denominator(p) - horizonNormal * HorizonMargin);
        polygon = Clip(polygon, p => maximumInverseDepth - inverse.Denominator(p));
        // These are linear screen half-planes because the inverse denominator is positive here.
        // Clip the coordinate guards as well, rather than inverse-projecting a huge corner and discarding it.
        polygon = Clip(polygon, p => MaximumCoordinate * inverse.Denominator(p) - (inverse.M11 * p.X + inverse.M12 * p.Y + inverse.M13));
        polygon = Clip(polygon, p => MaximumCoordinate * inverse.Denominator(p) + (inverse.M11 * p.X + inverse.M12 * p.Y + inverse.M13));
        polygon = Clip(polygon, p => MaximumCoordinate * inverse.Denominator(p) - (inverse.M21 * p.X + inverse.M22 * p.Y + inverse.M23));
        polygon = Clip(polygon, p => MaximumCoordinate * inverse.Denominator(p) + (inverse.M21 * p.X + inverse.M22 * p.Y + inverse.M23));
        return polygon;
    }

    private static bool FinitePoint(PointD point) => double.IsFinite(point.X) && double.IsFinite(point.Y) &&
        Math.Abs(point.X) <= MaximumCoordinate * (1 + 1e-9) && Math.Abs(point.Y) <= MaximumCoordinate * (1 + 1e-9);

    private static List<PointD> Clip(List<PointD> input, Func<PointD, double> side)
    {
        var output = new List<PointD>();
        if (input.Count == 0) return output;
        var previous = input[^1]; double before = side(previous);
        foreach (var current in input)
        {
            double after = side(current);
            if ((before >= 0) != (after >= 0)) output.Add(previous + (current - previous) * (before / (before - after)));
            if (after >= 0) output.Add(current);
            previous = current; before = after;
        }
        return output;
    }
}
