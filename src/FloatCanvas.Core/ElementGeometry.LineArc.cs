namespace FloatCanvas.Core;

public static partial class ElementGeometry
{
    public const double DefaultLineArcBulge = .2;
    public const double MaximumLineArcBulge = .5;

    public static PointD? LineArcHandle(CanvasElement element)
    {
        if (element.Style.LineArcBulge is not { } bulge || !TryLineChord(element, out var middle, out var along, out var normal, out double length))
            return null;
        return middle + along * (element.Style.LineArcOffset * length) + normal * (bulge * length);
    }

    public static (double Offset, double Bulge) LineArcCoordinatesAt(CanvasElement element, PointD local, bool centered)
    {
        if (!TryLineChord(element, out var middle, out var along, out var normal, out double length)) return default;
        var offset = (local - middle) * (1 / length);
        return (centered ? 0 : Dot(offset, along), Dot(offset, normal));
    }

    private static IReadOnlyList<PointD> LineCenterline(CanvasElement element)
    {
        if (TryLineEllipse(element, out var arc)) return arc.Sample(element.Points);
        return HasCollinearOverhang(element) && LineArcHandle(element) is { } overhang
            ? [element.Points[0], overhang, element.Points[^1]] : element.Points;
    }

    // P(t) = Center - ChordRadius cos(t) + ConjugateRadius sin(t), 0 <= t <= pi.
    private readonly record struct LineEllipse(PointD Center, PointD ChordRadius, PointD ConjugateRadius,
        PointD Handle, double HandleAngle)
    {
        public RectD Bounds
        {
            get
            {
                var (left, right) = Extent(ChordRadius.X, ConjugateRadius.X);
                var (top, bottom) = Extent(ChordRadius.Y, ConjugateRadius.Y);
                return new(Center.X + left, Center.Y + top, right - left, bottom - top);
            }
        }

        private static (double Min, double Max) Extent(double chord, double conjugate)
        {
            double endpoint = Math.Abs(chord), extreme = double.Hypot(chord, conjugate);
            return (conjugate < 0 ? -extreme : -endpoint, conjugate > 0 ? extreme : endpoint);
        }

        public IReadOnlyList<PointD> Sample(IReadOnlyList<PointD> endpoints)
        {
            // The two radii bound the amplification of a unit-circle chord error.
            double scale = double.Hypot(double.Hypot(ChordRadius.X, ChordRadius.Y),
                double.Hypot(ConjugateRadius.X, ConjugateRadius.Y));
            double maxStep = 4 * Math.Asin(Math.Min(1, Math.Sqrt(.025 / scale)));
            int SegmentCount(double sweep) => (int)Math.Clamp(Math.Ceiling(sweep / maxStep), 1, 512);
            int first = SegmentCount(HandleAngle), last = SegmentCount(Math.PI - HandleAngle);
            var points = new PointD[first + last + 1];
            points[0] = endpoints[0]; points[first] = Handle; points[^1] = endpoints[^1];
            for (int i = 1; i < first; i++) points[i] = At(HandleAngle * i / first);
            for (int i = 1; i < last; i++) points[first + i] = At(HandleAngle + (Math.PI - HandleAngle) * i / last);
            return points;
        }

        private PointD At(double angle)
        {
            var (sin, cos) = Math.SinCos(angle);
            return Center - ChordRadius * cos + ConjugateRadius * sin;
        }
    }

    private static bool TryLineEllipse(CanvasElement element, out LineEllipse arc)
    {
        arc = default;
        if (element.Style.LineArcBulge is not { } bulge || bulge == 0 ||
            !TryLineChord(element, out var center, out var along, out var normal, out double length)) return false;

        // In half-chord units A=(-1,0), B=(1,0), C=(x,h). The cross magnitude
        // and dot product of CA and CB give the opening angle. The unit term
        // also fades the lateral excursion when C approaches either endpoint.
        double x = 2 * element.Style.LineArcOffset, h = 2 * bulge;
        double opening = 2 * Math.Abs(h), dot = x * x + h * h - 1;
        double roundness = opening / double.Hypot(double.Hypot(dot, opening), 1);
        double difference = 1 - x * x, product = x * roundness;
        double span = double.Hypot(difference, 2 * product);
        // At C, the horizontal derivative is positive. Solve shear * advance = product
        // and advance^2 - shear^2 = difference without subtracting nearly equal squares.
        double shear, advance;
        if (difference >= 0)
        {
            advance = Math.Sqrt((span + difference) / 2);
            shear = product / advance;
        }
        else
        {
            shear = Math.CopySign(Math.Sqrt((span - difference) / 2), x);
            advance = product / shear;
        }
        double sinNumerator = x * shear + advance;
        double sinAtHandle = sinNumerator / (1 + shear * shear);
        double lift = h / sinAtHandle;
        double radius = length / 2;
        var conjugate = along * (radius * shear) + normal * (radius * lift);
        if (!double.IsFinite(conjugate.X) || !double.IsFinite(conjugate.Y)) return false;
        var handle = center + along * (element.Style.LineArcOffset * length) + normal * (bulge * length);
        arc = new(center, along * radius, conjugate, handle, Math.Atan2(sinNumerator, x * (roundness - 1)));
        return true;
    }

    private static bool HasCollinearOverhang(CanvasElement element) =>
        element.Style.LineArcBulge == 0 && Math.Abs(element.Style.LineArcOffset) > .5;

    private static PointD LineArrowDirection(CanvasElement element, bool reverse, ArrowHeadStyle style, double size, PointD fallback)
    {
        bool curved = TryLineEllipse(element, out var arc);
        if (curved && style == ArrowHeadStyle.Open)
        {
            // Antipodal endpoints have the same outward tangent along the selected half.
            var v = arc.ConjugateRadius;
            double length = double.Hypot(v.X, v.Y);
            return length > 0 ? new(-v.X / length, -v.Y / length) : fallback;
        }
        if (!curved && !HasCollinearOverhang(element)) return fallback;
        var points = curved ? arc.Sample(element.Points) : LineCenterline(element);
        return FitSampledLineHead(points, reverse, style, size, element.Style.StrokeWidth / 2).Direction ?? fallback;
    }

    private static bool TryLineArcHeadTrim(CanvasElement element, IReadOnlyList<PointD> centerline, bool reverse, ArrowHeadStyle style, double headLength,
        double shaftRadius, out double trim)
    {
        trim = 0;
        if (!IsLine(element.Kind) || element.Style.LineArcBulge is null || centerline.Count < 3) return false;
        trim = FitSampledLineHead(centerline, reverse, style, headLength, shaftRadius).Trim;
        return true;
    }
    private static (PointD? Direction, double Trim) FitSampledLineHead(IReadOnlyList<PointD> points, bool reverse,
        ArrowHeadStyle style, double size, double radius)
    {
        int tipIndex = reverse ? 0 : points.Count - 1, step = reverse ? 1 : -1;
        var tip = points[tipIndex];
        var current = tip;
        double travelled = 0;
        int index = tipIndex + step;
        // Walk inward to where the centerline crosses the circle at the requested head length.
        for (; index >= 0 && index < points.Count; index += step)
        {
            var next = points[index];
            double distance = next.DistanceTo(tip);
            if (style == ArrowHeadStyle.Open && distance > 1e-9)
                return ((tip - next) * (1 / distance), 0);
            var delta = next - current;
            if (distance >= size)
            {
                var start = current - tip;
                double projection = Dot(delta, start), squaredLength = Dot(delta, delta);
                double fraction = (Math.Sqrt(projection * projection + squaredLength * (size * size - Dot(start, start)))
                    - projection) / squaredLength;
                delta *= fraction;
                travelled += delta.DistanceTo(default);
                current += delta;
                break;
            }
            travelled += delta.DistanceTo(default);
            current = next;
        }
        double span = current.DistanceTo(tip);
        if (span < 1e-9) return (null, travelled);
        var direction = (tip - current) * (1 / span);
        if (style != ArrowHeadStyle.Diamond) return (direction, Math.Max(radius, travelled));

        // From the rear vertex, move toward the tip until the diamond contains the round cap.
        double slope = 2 * ArrowHeadHalfWidthRatio;
        double clearance = radius * double.Hypot(1, slope);
        var normal = Perpendicular(direction);
        double Available(PointD point)
        {
            var delta = tip - point;
            double axial = Dot(delta, direction);
            return slope * Math.Min(axial, size - axial) - Math.Abs(Dot(delta, normal));
        }
        double initialTravel = travelled;
        double available = Available(current);
        if (available >= clearance) return (direction, Math.Max(radius, travelled));
        for (index -= step; index != tipIndex; index -= step)
        {
            var next = points[index];
            double distance = current.DistanceTo(next);
            double nextAvailable = Available(next);
            if (nextAvailable >= clearance)
            {
                double fraction = (clearance - available) / (nextAvailable - available);
                return (direction, Math.Max(radius, travelled - fraction * distance));
            }
            travelled -= distance;
            current = next;
            available = nextAvailable;
        }
        return (direction, Math.Max(radius, initialTravel / 2));
    }

    private static double Dot(PointD a, PointD b) => a.X * b.X + a.Y * b.Y;
    private static PointD Perpendicular(PointD vector) => new(-vector.Y, vector.X);

    private static bool TryLineChord(CanvasElement element, out PointD middle, out PointD along, out PointD normal, out double length)
    {
        middle = along = normal = default;
        length = 0;
        if (!IsLine(element.Kind) || element.Points.Count != 2) return false;
        var start = element.Points[0];
        var delta = element.Points[1] - start;
        length = double.Hypot(delta.X, delta.Y);
        if (length < 1e-6 || !double.IsFinite(length)) return false;
        middle = start + delta * .5;
        along = delta * (1 / length);
        normal = new(-along.Y, along.X);
        return true;
    }
}
