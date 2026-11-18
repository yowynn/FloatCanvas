using FloatCanvas.Core;

namespace FloatCanvas.App;

/// <summary>Refines a bounded tail of the live stroke without drawing beyond the input.</summary>
internal sealed class FreehandPathSmoother(double strokeWidth)
{
    private const int WindowSize = 256;
    private const int StablePointCount = 160;
    private const double ReferenceWidth = 4;
    private readonly List<PointD> tail = new(WindowSize);
    private PointCollection? prefix;
    private PointD continuation;
    private double? widthScale;

    internal void Reset()
    {
        tail.Clear();
        prefix = null;
        continuation = default;
    }

    internal PointCollection Update(PointCollection current, PointD next, Projective2D toScreen, bool finishStroke)
    {
        if (prefix is null)
        {
            prefix = new(current.Take(current.Count - 1));
            tail.Add(current[^1]);
        }

        toScreen = NormalizeByStrokeWidth(toScreen, tail[0], next);
        double spacing = finishStroke && tail.Count > 1 ? 1e-9 : .75;
        if (toScreen.TryTransformPoint(next, out var screenNext) && toScreen.TryTransformPoint(tail[^1], out var screenLast) &&
            screenNext.DistanceTo(screenLast) >= spacing) tail.Add(next);

        if (tail.Count >= WindowSize)
        {
            var settled = Refine(StablePointCount, toScreen, out var heading);
            for (int i = 0; i < settled.Count - 1; i++) prefix.Add(settled[i]);
            tail.RemoveRange(0, StablePointCount - 1);
            continuation = heading;
        }
        var points = prefix.Snapshot();
        points.AddRange(Refine(tail.Count, toScreen, out _));
        return points;
    }

    private Projective2D NormalizeByStrokeWidth(Projective2D toScreen, PointD first, PointD next)
    {
        if (widthScale is null && toScreen.TryTransformPoint(first, out var start) &&
            toScreen.TryTransformPoint(next, out var end) && start.DistanceTo(end) >= .75)
        {
            // Measure the width perpendicular to the projected stroke direction at the start.
            // Keep this scale for the stroke so extended lines and Shift transitions cannot retune older geometry.
            var tangent = next - first;
            var jacobian = toScreen.Jacobian(first);
            double projectedTangent = jacobian.TransformPoint(tangent).DistanceTo(default);
            double determinant = jacobian.M11 * jacobian.M22 - jacobian.M12 * jacobian.M21;
            double normalScale = Math.Abs(determinant) * (tangent.DistanceTo(default) / projectedTangent);
            double projectedWidth = strokeWidth * normalScale;
            widthScale = double.IsFinite(projectedWidth) ? Math.Clamp(projectedWidth / ReferenceWidth, .5, 16) : 1;
        }
        double inverseScale = 1 / (widthScale ?? 1);
        return Math.Abs(inverseScale - 1) < 1e-9 ? toScreen
            : Projective2D.FromAffine(Affine2D.Scale(inverseScale, inverseScale)).Compose(toScreen);
    }

    private List<PointD> Refine(int count, Projective2D toScreen, out PointD terminal)
    {
        terminal = default;
        if (count < 3 || !toScreen.TryInverse(out var toLocal)) return tail.GetRange(0, count);
        Span<PointD> samples = stackalloc PointD[tail.Count];
        Span<double> lengths = stackalloc double[tail.Count];
        lengths[0] = 0;
        var bounds = RectD.FromPoints(tail[0], tail[0]);
        for (int i = 0; i < tail.Count; i++)
        {
            if (!toScreen.TryTransformPoint(tail[i], out samples[i])) return tail.GetRange(0, count);
            if (i < count) bounds = RectD.Union(bounds, RectD.FromPoints(tail[i], tail[i]));
            if (i > 0) lengths[i] = lengths[i - 1] + samples[i].DistanceTo(samples[i - 1]);
        }

        Span<bool> keep = stackalloc bool[count];
        keep.Clear();
        keep[0] = keep[^1] = true;
        RetainRoundedTransitions(samples, lengths, keep);
        int start = 0;
        for (int i = 1; i < count; i++)
        {
            if (!keep[i]) continue;
            SelectKnots(samples, lengths, keep, start, i);
            start = i;
        }

        Span<PointD> knots = stackalloc PointD[count];
        Span<int> indices = stackalloc int[count];
        Span<bool> sharp = stackalloc bool[count];
        int knotCount = 0;
        for (int i = 0; i < count; i++)
        {
            if (!keep[i]) continue;
            knots[knotCount] = samples[i];
            indices[knotCount] = i;
            sharp[knotCount++] = i == 0 || i == count - 1 || IsSharp(samples, lengths, i);
        }
        knots = knots[..knotCount];
        indices = indices[..knotCount];
        sharp = sharp[..knotCount];
        AlignAdjacentRuns(knots, sharp);
        Span<PointD> tangents = stackalloc PointD[knotCount];
        tangents.Clear();
        for (int i = 1; i < knotCount - 1; i++)
        {
            if (sharp[i]) continue;
            var before = Unit(knots[i] - knots[i - 1]);
            var after = Unit(knots[i + 1] - knots[i]);
            var heading = Heading(samples, lengths, indices[i]);
            tangents[i] = Dot(heading, before) > .1 && Dot(heading, after) > .1 ? heading : Unit(before + after);
        }
        if (continuation != default && toScreen.TryTransformPoint(tail[0] + continuation, out var continued))
            tangents[0] = Unit(continued - knots[0]);
        else if (HasBow(samples, indices[0], indices[1]))
            tangents[0] = Heading(samples, lengths, indices[0]);
        bool continuesSmoothly = count < tail.Count && !IsSharp(samples, lengths, indices[^1]);
        if (continuesSmoothly || HasBow(samples, indices[^2], indices[^1]))
            tangents[^1] = Heading(samples, lengths, indices[^1]);

        var result = new List<PointD>(knotCount) { tail[0] };
        bool projective = toScreen.M31 != 0 || toScreen.M32 != 0;
        bounds = bounds.Inflate(1e-7);
        PointD endHeading = default;
        for (int i = 1; i < knotCount; i++)
        {
            var first = knots[i - 1];
            var last = knots[i];
            var chord = last - first;
            double distance = chord.DistanceTo(default);
            var direction = Unit(chord);
            var incoming = tangents[i - 1] != default ? tangents[i - 1] : direction;
            var outgoing = tangents[i] != default ? tangents[i] : direction;
            endHeading = outgoing;
            double startBend = Math.Abs(Cross(direction, incoming)), endBend = Math.Abs(Cross(direction, outgoing));
            if (distance <= 1e-9 || startBend < .005 && endBend < .005 && Dot(incoming, outgoing) > 0)
            {
                if (i < knotCount - 1 && !Append(last)) return tail.GetRange(0, count);
                continue;
            }
            // Both adjacent curves use the same direction at a smooth knot.
            var control1 = first + incoming * Math.Min(distance / 3, 8 / Math.Max(startBend, .001));
            var control2 = last - outgoing * Math.Min(distance / 3, 8 / Math.Max(endBend, .001));
            int steps = (int)Math.Clamp(Math.Ceiling(distance / 2.5), 6, 96);
            for (int step = 1; step < steps; step++)
            {
                double t = (double)step / steps, u = 1 - t;
                var point = first * (u * u * u) + control1 * (3 * u * u * t) + control2 * (3 * u * t * t) + last * (t * t * t);
                if (!Append(point)) return tail.GetRange(0, count);
            }
            if (i < knotCount - 1 && !Append(last)) return tail.GetRange(0, count);
        }
        result.Add(tail[count - 1]);
        if (continuesSmoothly && toLocal.TryTransformPoint(knots[^1] + endHeading, out var next)) terminal = next - tail[count - 1];
        return result;

        bool Append(PointD point)
        {
            if (!toLocal.TryTransformPoint(point, out var local) || projective && !bounds.Contains(local)) return false;
            if (result[^1].DistanceTo(local) > 1e-9) result.Add(local);
            return true;
        }
    }

    private static void SelectKnots(ReadOnlySpan<PointD> samples, ReadOnlySpan<double> lengths, Span<bool> keep, int first, int last)
    {
        if (last - first < 2) return;
        var chord = samples[last] - samples[first];
        double span = chord.DistanceTo(default);
        var direction = span > 0 ? chord * (1 / span) : default;
        double positive = 0, negative = 0, max = 0, sum = 0, lastProgress = 0, reverse = 0;
        int split = first + 1;
        for (int i = first + 1; i < last; i++)
        {
            var offset = samples[i] - samples[first];
            double signed = Cross(direction, offset);
            positive = Math.Max(positive, signed);
            negative = Math.Max(negative, -signed);
            double progress = Dot(direction, offset);
            reverse += Math.Max(0, lastProgress - progress);
            lastProgress = progress;
            double deviation = SegmentDistance(offset, chord, progress, span);
            sum += deviation * deviation;
            if (deviation > max) { max = deviation; split = i; }
        }
        reverse += Math.Max(0, lastProgress - span);
        bool wobble = positive >= 2 && negative >= 2;
        double threshold = wobble ? Math.Clamp(span * .14, 3.5, 18) : Math.Clamp(span * .065, 3, 8);
        if (max <= threshold && Math.Sqrt(sum / (last - first - 1)) <= Math.Max(3, span * .10) &&
            reverse <= Math.Max(1, span * .08) && lengths[last] - lengths[first] <= span * 1.58 + 2) return;
        keep[split] = true;
        SelectKnots(samples, lengths, keep, first, split);
        SelectKnots(samples, lengths, keep, split, last);
    }

    private static bool IsSharp(ReadOnlySpan<PointD> samples, ReadOnlySpan<double> lengths, int index)
    {
        double broad = Math.Abs(Turn(samples, lengths, index, 8));
        double narrow = Math.Abs(Turn(samples, lengths, index, 2.5));
        return broad > 1 && narrow > broad * .7;
    }

    private static void RetainRoundedTransitions(ReadOnlySpan<PointD> samples, ReadOnlySpan<double> lengths, Span<bool> keep)
    {
        int first = -1, last = -1;
        double direction = 0;
        for (int i = 1; i < keep.Length - 1; i++)
        {
            double broad = Turn(samples, lengths, i, 8);
            double narrow = Turn(samples, lengths, i, 2.5);
            bool rounded = Math.Abs(broad) > .3 && broad * narrow > 0 &&
                Math.Abs(narrow) >= .06 && Math.Abs(narrow) < Math.Abs(broad) * .65;
            if (!rounded || broad * direction < 0)
            {
                MarkRoundedRun(lengths, keep, first, last);
                first = -1;
            }
            if (!rounded) continue;
            if (first < 0) { first = i; direction = broad; }
            last = i;
        }
        MarkRoundedRun(lengths, keep, first, last);
    }

    private static void MarkRoundedRun(ReadOnlySpan<double> lengths, Span<bool> keep, int first, int last)
    {
        if (first >= 0 && lengths[last] - lengths[first] >= 3)
            keep[first] = keep[last] = true;
    }

    private static bool HasBow(ReadOnlySpan<PointD> samples, int first, int last)
    {
        if (last - first < 3) return false;
        var direction = Unit(samples[last] - samples[first]);
        double positive = 0, negative = 0;
        for (int i = first + 1; i < last; i++)
        {
            double offset = Cross(direction, samples[i] - samples[first]);
            positive = Math.Max(positive, offset);
            negative = Math.Max(negative, -offset);
        }
        return Math.Max(positive, negative) > 1.25 && Math.Min(positive, negative) < Math.Max(positive, negative) * .30;
    }

    private static PointD Heading(ReadOnlySpan<PointD> samples, ReadOnlySpan<double> lengths, int index)
    {
        double at = lengths[index];
        return Unit(AtDistance(samples, lengths, Math.Min(lengths[^1], at + 8)) -
            AtDistance(samples, lengths, Math.Max(0, at - 8)));
    }

    private static double Turn(ReadOnlySpan<PointD> samples, ReadOnlySpan<double> lengths, int index, double reach)
    {
        double at = lengths[index];
        var corner = samples[index];
        var incoming = corner - AtDistance(samples, lengths, Math.Max(0, at - reach));
        var outgoing = AtDistance(samples, lengths, Math.Min(lengths[^1], at + reach)) - corner;
        return Math.Atan2(Cross(incoming, outgoing), Dot(incoming, outgoing));
    }

    private static void AlignAdjacentRuns(Span<PointD> knots, ReadOnlySpan<bool> sharp)
    {
        // Only near-axis straight runs that meet at a sharp corner are aligned.
        Span<int> axes = stackalloc int[knots.Length - 1];
        for (int i = 0; i < axes.Length; i++)
            axes[i] = sharp[i] && sharp[i + 1] ? NearAxis(knots[i + 1] - knots[i]) : 0;
        for (int i = 0; i < axes.Length; i++)
        {
            int axis = axes[i];
            if (axis == 0 || !(i > 0 && axes[i - 1] == -axis || i + 1 < axes.Length && axes[i + 1] == -axis)) continue;
            var a = knots[i]; var b = knots[i + 1];
            var guide = i == 0 ? a : i == axes.Length - 1 ? b : (a + b) * .5;
            if (i > 0) knots[i] = axis > 0 ? a with { Y = guide.Y } : a with { X = guide.X };
            if (i + 1 < axes.Length) knots[i + 1] = axis > 0 ? b with { Y = guide.Y } : b with { X = guide.X };
        }
    }

    private static int NearAxis(PointD chord)
    {
        double x = Math.Abs(chord.X), y = Math.Abs(chord.Y);
        if (x >= 18 && y <= Math.Min(8, x * .14)) return 1;
        if (y >= 18 && x <= Math.Min(8, y * .14)) return -1;
        return 0;
    }

    private static PointD AtDistance(ReadOnlySpan<PointD> samples, ReadOnlySpan<double> lengths, double distance)
    {
        if (distance <= 0) return samples[0];
        int index = lengths.BinarySearch(distance);
        if (index >= 0) return samples[index];
        index = ~index;
        double amount = (distance - lengths[index - 1]) / (lengths[index] - lengths[index - 1]);
        return samples[index - 1] + (samples[index] - samples[index - 1]) * amount;
    }

    private static double SegmentDistance(PointD offset, PointD chord, double progress, double span) =>
        span == 0 ? offset.DistanceTo(default) : (offset - chord * Math.Clamp(progress / span, 0, 1)).DistanceTo(default);
    private static double Dot(PointD a, PointD b) => a.X * b.X + a.Y * b.Y;
    private static double Cross(PointD a, PointD b) => a.X * b.Y - a.Y * b.X;
    private static PointD Unit(PointD vector)
    {
        double length = vector.DistanceTo(default);
        return length > 1e-9 ? vector * (1 / length) : default;
    }
}
