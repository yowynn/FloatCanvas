using System.Text.Json.Serialization;

namespace FloatCanvas.Core;

/// <summary>Column-vector homogeneous matrix. Compose(other) applies other first, then this.</summary>
public readonly record struct Projective2D(double M11, double M12, double M13, double M21, double M22, double M23,
    double M31, double M32, double M33)
{
    public static Projective2D Identity => new(1, 0, 0, 0, 1, 0, 0, 0, 1);
    public static Projective2D FromAffine(Affine2D value) => new(value.M11, value.M12, value.OffsetX, value.M21, value.M22, value.OffsetY, 0, 0, 1);
    [JsonIgnore] public bool IsFinite => double.IsFinite(M11) && double.IsFinite(M12) && double.IsFinite(M13) &&
        double.IsFinite(M21) && double.IsFinite(M22) && double.IsFinite(M23) && double.IsFinite(M31) && double.IsFinite(M32) && double.IsFinite(M33);
    public double Denominator(PointD point) => M31 * point.X + M32 * point.Y + M33;
    public bool TryTransformPoint(PointD point, out PointD result)
    {
        double denominator = Denominator(point);
        double magnitude = Math.Abs(M31 * point.X) + Math.Abs(M32 * point.Y) + Math.Abs(M33);
        result = default;
        if (!double.IsFinite(denominator) || magnitude == 0 || Math.Abs(denominator) <= magnitude * 1e-10) return false;
        result = new((M11 * point.X + M12 * point.Y + M13) / denominator, (M21 * point.X + M22 * point.Y + M23) / denominator);
        return double.IsFinite(result.X) && double.IsFinite(result.Y);
    }
    public PointD TransformPoint(PointD point) => TryTransformPoint(point, out var result) ? result :
        throw new InvalidOperationException("The point lies outside the finite projective plane.");
    public bool TryTransformBounds(RectD bounds, out RectD result)
    {
        result = default;
        PointD[] corners = [new(bounds.Left, bounds.Top), new(bounds.Right, bounds.Top), new(bounds.Right, bounds.Bottom), new(bounds.Left, bounds.Bottom)];
        double first = Denominator(corners[0]);
        var points = new PointD[4];
        for (int i = 0; i < corners.Length; i++)
            if (first * Denominator(corners[i]) <= 0 || !TryTransformPoint(corners[i], out points[i])) return false;
        result = new(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X) - points.Min(p => p.X), points.Max(p => p.Y) - points.Min(p => p.Y));
        return double.IsFinite(result.Width) && double.IsFinite(result.Height);
    }
    public RectD TransformBounds(RectD bounds) => TryTransformBounds(bounds, out var result) ? result :
        throw new InvalidOperationException("The bounds cross the projective horizon.");
    public Projective2D Compose(Projective2D other) => new(
        M11 * other.M11 + M12 * other.M21 + M13 * other.M31, M11 * other.M12 + M12 * other.M22 + M13 * other.M32, M11 * other.M13 + M12 * other.M23 + M13 * other.M33,
        M21 * other.M11 + M22 * other.M21 + M23 * other.M31, M21 * other.M12 + M22 * other.M22 + M23 * other.M32, M21 * other.M13 + M22 * other.M23 + M23 * other.M33,
        M31 * other.M11 + M32 * other.M21 + M33 * other.M31, M31 * other.M12 + M32 * other.M22 + M33 * other.M32, M31 * other.M13 + M32 * other.M23 + M33 * other.M33);
    public Projective2D ScaleHomogeneous(double scale) => new(M11 * scale, M12 * scale, M13 * scale, M21 * scale, M22 * scale, M23 * scale, M31 * scale, M32 * scale, M33 * scale);
    public bool TryInverse(out Projective2D inverse)
    {
        inverse = default;
        if (!IsFinite) return false;
        double a = M22 * M33 - M23 * M32, b = M23 * M31 - M21 * M33, c = M21 * M32 - M22 * M31;
        double determinant = M11 * a + M12 * b + M13 * c;
        double magnitude = Math.Abs(M11 * a) + Math.Abs(M12 * b) + Math.Abs(M13 * c);
        if (!double.IsFinite(determinant) || magnitude == 0 || Math.Abs(determinant) <= magnitude * 1e-12) return false;
        inverse = new(a / determinant, (M13 * M32 - M12 * M33) / determinant, (M12 * M23 - M13 * M22) / determinant,
            b / determinant, (M11 * M33 - M13 * M31) / determinant, (M13 * M21 - M11 * M23) / determinant,
            c / determinant, (M12 * M31 - M11 * M32) / determinant, (M11 * M22 - M12 * M21) / determinant);
        return inverse.IsFinite;
    }
    public Projective2D Inverse() => TryInverse(out var result) ? result : throw new InvalidOperationException("The projective matrix is not invertible.");
    public Affine2D Jacobian(PointD point)
    {
        var projected = TransformPoint(point);
        double denominator = Denominator(point);
        return new((M11 - projected.X * M31) / denominator, (M12 - projected.X * M32) / denominator,
            (M21 - projected.Y * M31) / denominator, (M22 - projected.Y * M32) / denominator, 0, 0);
    }
}
