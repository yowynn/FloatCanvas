using FloatCanvas.Core;
using SkiaSharp;

namespace FloatCanvas.Rendering;

/// <summary>Fits closed alpha contours in sample pixels. Smooth spans share a tangent; sharp corners have independent tangents.</summary>
internal static class SymbolCurveFitter
{
    internal static int Append(SKPathBuilder path, PointD[] loop, double requestedError, CancellationToken token)
    {
        int n = loop.Length;
        path.MoveTo((float)loop[0].X, (float)loop[0].Y);
        if (n < 6)
        {
            foreach (var p in loop.AsSpan()[1..]) path.LineTo((float)p.X, (float)p.Y);
            path.Close(); return n;
        }
        var points = new PointD[n + 1]; loop.CopyTo(points, 0); points[n] = loop[0];
        var lengths = new double[n + 1]; int opposite = 0; double farthest = 0;
        double minX = points[0].X, maxX = minX, minY = points[0].Y, maxY = minY;
        for (int i = 1; i <= n; i++)
        {
            if ((i & 2047) == 0) token.ThrowIfCancellationRequested();
            var p = points[i];
            lengths[i] = lengths[i - 1] + p.DistanceTo(points[i - 1]);
            double distance = p.DistanceTo(points[0]);
            if (distance > farthest) { farthest = distance; opposite = i; }
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }
        // Cap error per component: a small hole or a separate dot must not collapse on a large selection.
        double tolerance = Math.Min(requestedError, Math.Max(.025, .085 * Math.Min(maxX - minX, maxY - minY)));
        var keep = new bool[n + 1]; keep[0] = keep[opposite] = keep[n] = true;
        var arcs = new Stack<(int Start, int End)>(); arcs.Push((0, opposite)); arcs.Push((opposite, n));
        int work = 0, anchorCount = 2;
        while (arcs.TryPop(out var arc))
        {
            token.ThrowIfCancellationRequested();
            work += arc.End - arc.Start;
            if (work > 32_000_000 || anchorCount > SymbolShape.MaximumVertices)
                throw new InvalidDataException(FloatCanvas.Localization.AppText.Get("Symbol.ExtractComplex"));
            int split = -1; double error = tolerance;
            for (int i = arc.Start + 1; i < arc.End; i++)
            {
                if ((i & 2047) == 0) token.ThrowIfCancellationRequested();
                double d = ElementGeometry.DistanceToSegment(points[i], points[arc.Start], points[arc.End]);
                if (d > error) { error = d; split = i; }
            }
            if (split < 0) continue;
            keep[split] = true; anchorCount++; arcs.Push((arc.Start, split)); arcs.Push((split, arc.End));
        }
        var anchors = Enumerable.Range(0, n).Where(i => keep[i]).ToArray();
        if (anchors.Length < 3)
        {
            foreach (var p in loop.AsSpan()[1..]) path.LineTo((float)p.X, (float)p.Y);
            path.Close(); return n;
        }
        var incoming = new PointD[anchors.Length]; var outgoing = new PointD[anchors.Length];
        var corners = new bool[anchors.Length];
        for (int k = 0; k < anchors.Length; k++)
        {
            int i = anchors[k], prev = anchors[(k + anchors.Length - 1) % anchors.Length], next = anchors[(k + 1) % anchors.Length];
            double before = lengths[i] - lengths[prev], after = lengths[next] - lengths[i];
            if (before <= 0) before += lengths[n]; if (after <= 0) after += lengths[n];
            double reach = Math.Min(.45 * Math.Min(before, after), Math.Max(2.5, tolerance * 1.7));
            var inward = Unit(points[i] - Sample(lengths[i] - reach));
            var outward = Unit(Sample(lengths[i] + reach) - points[i]);
            double broadTurn = Dot(Unit(points[i] - points[prev]), Unit(points[next] - points[i]));
            corners[k] = Dot(inward, outward) < .55 && broadTurn < .76;
            var tangent = Unit(inward + outward);
            incoming[k] = corners[k] ? inward : tangent;
            outgoing[k] = corners[k] ? outward : tangent;
        }
        int count = 1;
        for (int k = 0; k < anchors.Length; k++)
        {
            int next = (k + 1) % anchors.Length;
            Fit(anchors[k], next == 0 ? n : anchors[next], outgoing[k], incoming[next], corners[k] && corners[next], 0);
        }
        path.Close(); return count;

        PointD Sample(double length)
        {
            if (length < 0) length += lengths[n]; else if (length > lengths[n]) length -= lengths[n];
            int i = Array.BinarySearch(lengths, length);
            if (i >= 0) return points[i];
            i = Math.Clamp(~i, 1, n);
            return points[i - 1] + (points[i] - points[i - 1]) * ((length - lengths[i - 1]) / (lengths[i] - lengths[i - 1]));
        }

        void Fit(int start, int end, PointD left, PointD right, bool cornerSpan, int depth)
        {
            token.ThrowIfCancellationRequested();
            var p = points[start]; var q = points[end]; double chord = p.DistanceTo(q);
            void Line() { path.LineTo((float)q.X, (float)q.Y); count++; }
            if (end <= start + 1 || chord < 1e-8) { Line(); return; }
            double span = lengths[end] - lengths[start];
            // Fit the two handle lengths by least squares. Their directions are fixed by the adjacent contour spans.
            double aa = 0, ab = 0, bb = 0, ax = 0, bx = 0, lineError = 0;
            for (int i = start + 1; i < end; i++)
            {
                if ((i & 2047) == 0) token.ThrowIfCancellationRequested();
                double t = (lengths[i] - lengths[start]) / span, u = 1 - t, b1 = 3 * u * u * t, b2 = 3 * u * t * t;
                var a = left * b1; var b = right * (-b2);
                var residual = points[i] - (p * (u * u * u + b1) + q * (b2 + t * t * t));
                aa += Dot(a, a); ab += Dot(a, b); bb += Dot(b, b); ax += Dot(a, residual); bx += Dot(b, residual);
                lineError = Math.Max(lineError, ElementGeometry.DistanceToSegment(points[i], p, q));
            }
            var direction = Unit(q - p);
            if (lineError <= Math.Min(.65, tolerance) && (cornerSpan || Dot(direction, left) > .985 && Dot(direction, right) > .985))
            { Line(); return; }

            double det = aa * bb - ab * ab, l = chord / 3, r = l;
            if (det > 1e-10)
            {
                l = Math.Clamp((ax * bb - bx * ab) / det, .035 * chord, .85 * chord);
                r = Math.Clamp((bx * aa - ax * ab) / det, .035 * chord, .85 * chord);
            }
            var c1 = p + left * l; var c2 = q - right * r;
            double error = 0; int worst = start + 1;
            for (int i = start + 1; i < end; i++)
            {
                if ((i & 2047) == 0) token.ThrowIfCancellationRequested();
                double t = (lengths[i] - lengths[start]) / span, u = 1 - t;
                var fitted = p * (u * u * u) + c1 * (3 * u * u * t) + c2 * (3 * u * t * t) + q * (t * t * t);
                double distance = points[i].DistanceTo(fitted);
                if (distance > error) { error = distance; worst = i; }
            }
            if (error > tolerance)
            {
                if (depth >= 12) { Line(); return; }
                double reach = Math.Min(Math.Min(lengths[worst] - lengths[start], lengths[end] - lengths[worst]) * .5, Math.Max(2, tolerance));
                var tangent = Unit(Sample(lengths[worst] + reach) - Sample(lengths[worst] - reach));
                Fit(start, worst, left, tangent, false, depth + 1); Fit(worst, end, tangent, right, false, depth + 1); return;
            }
            path.CubicTo((float)c1.X, (float)c1.Y, (float)c2.X, (float)c2.Y, (float)q.X, (float)q.Y); count++;
        }
    }

    private static double Dot(PointD a, PointD b) => a.X * b.X + a.Y * b.Y;
    private static PointD Unit(PointD p)
    {
        double length = Math.Sqrt(Dot(p, p)); return length > 1e-10 ? p * (1 / length) : default;
    }
}
