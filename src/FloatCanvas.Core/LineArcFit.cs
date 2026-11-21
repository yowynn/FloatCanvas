namespace FloatCanvas.Core;

/// <summary>Fits one editable half ellipse to a drag. All input is relative to the press point.</summary>
public sealed class LineArcFit
{
    private const int DirectionCount = 64;
    private static readonly PointD[] Directions = Enumerable.Range(0, DirectionCount)
        .Select(i => { var (sin, cos) = Math.SinCos(i * Math.Tau / DirectionCount); return new PointD(cos, sin); })
        .ToArray();

    private readonly (double Local, double Screen)[] supports = new (double, double)[DirectionCount];
    private PointD previous, previousScreen, firstMoment;
    private double twiceArea, screenTwiceArea, unsignedArea, pathLength;
    private int segments;

    // Keep only accepted input. A candidate rejected by the canvas projection cannot affect a later fit.
    public void Accept(PointD point) => Accept(point, point);

    public void Accept(PointD point, PointD screenPoint)
    {
        if (point == previous && screenPoint == previousScreen) return;
        double cross = Cross(previous, point);
        twiceArea += cross;
        screenTwiceArea += Cross(previousScreen, screenPoint);
        unsignedArea += Math.Abs(cross);
        firstMoment += (previous + point) * cross;
        pathLength += previous.DistanceTo(point);
        previous = point;
        previousScreen = screenPoint;
        segments = Math.Min(2, segments + 1);
        for (int i = 0; i < Directions.Length; i++)
        {
            supports[i].Local = Math.Max(supports[i].Local, Dot(point, Directions[i]));
            supports[i].Screen = Math.Max(supports[i].Screen, Dot(screenPoint, Directions[i]));
        }
    }

    public (double Offset, double Bulge)? Estimate(PointD end) => Estimate(end, end);

    public (double Offset, double Bulge)? Estimate(PointD end, PointD screenEnd)
    {
        if (segments == 0 || segments == 1 && previous == end) return null;
        double chord = end.DistanceTo(default);
        if (chord < 1e-6) return null;

        double cross = Cross(previous, end), area = twiceArea + cross;
        double length = pathLength + previous.DistanceTo(end);
        if (Math.Abs(area) <= 1e-9 * chord * length) return (0, 0);

        var along = end * (1 / chord);
        var normal = new PointD(-along.Y, along.X);
        double radius = chord / 2;
        // The trace closed by its chord has signed area -pi * radius * lift / 2.
        // Its centroid is 4/(3*pi) of the conjugate radius away from the chord midpoint.
        double lift = -area / (Math.PI * radius);
        double centeredBulge = lift / chord;
        double lateralWeight = LateralFitWeight(screenEnd);
        if (lateralWeight == 0) return (0, centeredBulge);

        var centroid = (firstMoment + (previous + end) * cross) * (1 / (3 * area));
        double coherence = Math.Min(1, 2 * Math.Abs(area) / (unsignedArea + Math.Abs(cross)));
        double shear = (Dot(centroid, along) - radius) * (3 * Math.PI / 4) * coherence * coherence;
        var conjugate = BoundRadius(along * shear + normal * lift, length / 2);

        // Match the distances to supporting lines of the observed trace. An ellipse with radii
        // A and V reaches hypot(n.A, n.V) along n on the chosen half when n.V is positive.
        // Two least-squares updates are bounded regardless of the number of pointer events.
        var chordRadius = end * .5;
        for (int pass = 0; pass < 2; pass++)
        {
            double xx = .25, xy = 0, yy = .25;
            PointD rhs = default;
            for (int i = 0; i < Directions.Length; i++)
            {
                var n = Directions[i];
                double v = Dot(n, conjugate);
                if (v <= 0) continue;
                double a = Dot(n, chordRadius), predicted = double.Hypot(a, v);
                double observed = Math.Max(Math.Abs(a), supports[i].Local - a);
                var gradient = n * (v / predicted);
                xx += gradient.X * gradient.X;
                xy += gradient.X * gradient.Y;
                yy += gradient.Y * gradient.Y;
                rhs += gradient * (observed - predicted);
            }
            double determinant = xx * yy - xy * xy;
            var step = new PointD(yy * rhs.X - xy * rhs.Y, xx * rhs.Y - xy * rhs.X) * (coherence / determinant);
            var next = BoundRadius(conjugate + step, length / 2);
            if (Dot(next, normal) * lift <= 0) break;
            conjugate = next;
        }
        var fitted = ElementGeometry.LineArcHandleForRadius(Dot(conjugate, along) / radius, Dot(conjugate, normal) / radius);
        if (lateralWeight == 1) return fitted;
        // Blend the editable handle itself. At very small lifts even a tiny shear can put it at an endpoint.
        return (fitted.Offset * lateralWeight, centeredBulge + (fitted.Bulge - centeredBulge) * lateralWeight);
    }

    private double LateralFitWeight(PointD screenEnd)
    {
        double chord = screenEnd.DistanceTo(default);
        if (chord < 1e-6) return 0;
        // Screen-space evidence keeps the same hand movement centered under zoom or foreshortening.
        double tolerance = Math.Max(3, .02 * chord);
        double departure = 2 * Math.Abs(screenTwiceArea + Cross(previousScreen, screenEnd)) / (Math.PI * chord);
        // A narrow arc may still have a clear return past an endpoint. Compare the actual
        // trace's support in each direction with that of the finite segment [0, end].
        for (int i = 0; i < Directions.Length && departure < 2 * tolerance; i++)
            departure = Math.Max(departure, supports[i].Screen - Math.Max(0, Dot(screenEnd, Directions[i])));
        double t = Math.Clamp(departure / tolerance - 1, 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static PointD BoundRadius(PointD vector, double limit)
    {
        double size = vector.DistanceTo(default);
        return size > limit ? vector * (limit / size) : vector;
    }

    private static double Cross(PointD a, PointD b) => a.X * b.Y - a.Y * b.X;
    private static double Dot(PointD a, PointD b) => a.X * b.X + a.Y * b.Y;
}
