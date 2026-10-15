using L = FloatCanvas.Localization.AppText;
using System.Text.Json.Serialization;

namespace FloatCanvas.Core;

public readonly record struct PointD(double X, double Y)
{
    public static PointD operator +(PointD a, PointD b) => new(a.X + b.X, a.Y + b.Y);
    public static PointD operator -(PointD a, PointD b) => new(a.X - b.X, a.Y - b.Y);
    public static PointD operator *(PointD a, double scale) => new(a.X * scale, a.Y * scale);
    public double DistanceTo(PointD other) => Math.Sqrt((X - other.X) * (X - other.X) + (Y - other.Y) * (Y - other.Y));
}

public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    [JsonIgnore] public double Left => Math.Min(X, X + Width);
    [JsonIgnore] public double Top => Math.Min(Y, Y + Height);
    [JsonIgnore] public double Right => Math.Max(X, X + Width);
    [JsonIgnore] public double Bottom => Math.Max(Y, Y + Height);
    [JsonIgnore] public PointD Center => new((Left + Right) / 2, (Top + Bottom) / 2);
    [JsonIgnore] public bool IsEmpty => Width == 0 || Height == 0;
    public bool Contains(PointD p) => p.X >= Left && p.X <= Right && p.Y >= Top && p.Y <= Bottom;
    public bool Intersects(RectD other) => Right >= other.Left && Bottom >= other.Top && Left <= other.Right && Top <= other.Bottom;
    public RectD Inflate(double value) => new(Left - value, Top - value, Right - Left + value * 2, Bottom - Top + value * 2);
    public RectD Normalize() => new(Left, Top, Right - Left, Bottom - Top);
    public RectD Intersect(RectD other)
    {
        double x = Math.Max(Left, other.Left), y = Math.Max(Top, other.Top);
        return new(x, y, Math.Max(0, Math.Min(Right, other.Right) - x), Math.Max(0, Math.Min(Bottom, other.Bottom) - y));
    }
    public static RectD FromPoints(PointD a, PointD b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    public static RectD Union(RectD a, RectD b) => new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right) - Math.Min(a.Left, b.Left), Math.Max(a.Bottom, b.Bottom) - Math.Min(a.Top, b.Top));
}

/// <summary>Column-vector affine matrix. Compose(other) applies other first, then this.</summary>
public readonly record struct Affine2D(double M11, double M12, double M21, double M22, double OffsetX, double OffsetY)
{
    public static Affine2D Identity => new(1, 0, 0, 1, 0, 0);
    public static Affine2D Translation(double x, double y) => new(1, 0, 0, 1, x, y);
    public static Affine2D Scale(double x, double y) => new(x, 0, 0, y, 0, 0);
    public static Affine2D Rotation(double degrees)
    {
        double radians = degrees * Math.PI / 180, c = Math.Cos(radians), s = Math.Sin(radians);
        return new(c, -s, s, c, 0, 0);
    }
    public static Affine2D Around(PointD center, Affine2D transform) => Translation(center.X, center.Y).Compose(transform).Compose(Translation(-center.X, -center.Y));
    public PointD TransformPoint(PointD p) => new(M11 * p.X + M12 * p.Y + OffsetX, M21 * p.X + M22 * p.Y + OffsetY);
    public RectD TransformBounds(RectD r)
    {
        PointD[] points = [TransformPoint(new(r.Left, r.Top)), TransformPoint(new(r.Right, r.Top)), TransformPoint(new(r.Right, r.Bottom)), TransformPoint(new(r.Left, r.Bottom))];
        return new(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X) - points.Min(p => p.X), points.Max(p => p.Y) - points.Min(p => p.Y));
    }
    public Affine2D Compose(Affine2D other) => new(
        M11 * other.M11 + M12 * other.M21, M11 * other.M12 + M12 * other.M22,
        M21 * other.M11 + M22 * other.M21, M21 * other.M12 + M22 * other.M22,
        M11 * other.OffsetX + M12 * other.OffsetY + OffsetX, M21 * other.OffsetX + M22 * other.OffsetY + OffsetY);
    public bool TryInverse(out Affine2D inverse)
    {
        double determinant = M11 * M22 - M12 * M21;
        if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-12) { inverse = default; return false; }
        double a = M22 / determinant, b = -M12 / determinant, c = -M21 / determinant, d = M11 / determinant;
        inverse = new(a, b, c, d, -a * OffsetX - b * OffsetY, -c * OffsetX - d * OffsetY);
        return true;
    }
    public Affine2D Inverse() => TryInverse(out var inverse) ? inverse : throw new InvalidOperationException(L.Get("Geometry.NonInvertibleTransform"));
    [JsonIgnore] public double MaximumScale
    {
        get
        {
            double a = M11 * M11 + M21 * M21, b = M12 * M12 + M22 * M22, c = M11 * M12 + M21 * M22;
            return Math.Sqrt(Math.Max(0, (a + b + Math.Sqrt((a - b) * (a - b) + 4 * c * c)) / 2));
        }
    }
}
