using FloatCanvas.Windows;

namespace FloatCanvas.App;

/// <summary>Fits a physical window rectangle inside the union of the active screens.</summary>
internal static class ToolbarScreenBounds
{
    internal static bool TryConstrain(PixelBounds requested, IReadOnlyList<PixelBounds> allowed, out PixelBounds constrained)
    {
        constrained = requested;
        if (!Valid(requested) || allowed is null || allowed.Count == 0 || allowed.Any(screen => !Valid(screen))) return false;
        if (allowed.Count == 1)
        {
            var screen = allowed[0];
            if (requested.Width > screen.Width || requested.Height > screen.Height) return false;
            constrained = requested with
            {
                Left = Math.Clamp(requested.Left, screen.Left, screen.Right - requested.Width),
                Top = Math.Clamp(requested.Top, screen.Top, screen.Bottom - requested.Height)
            };
            return true;
        }
        if (Covered(requested, allowed)) return true;

        double bestDistance = double.PositiveInfinity;
        foreach (var screen in allowed)
        {
            if (requested.Width > screen.Width || requested.Height > screen.Height) continue;
            int x = Math.Clamp(requested.Left, screen.Left, screen.Right - requested.Width);
            int y = Math.Clamp(requested.Top, screen.Top, screen.Bottom - requested.Height);
            bestDistance = Math.Min(bestDistance, Distance(x, y, requested));
        }
        var xs = new HashSet<int> { requested.Left };
        var ys = new HashSet<int> { requested.Top };
        foreach (var screen in allowed)
        {
            foreach (int edge in new[] { screen.Left, screen.Right }) { xs.Add(edge); Add(xs, (long)edge - requested.Width); }
            foreach (int edge in new[] { screen.Top, screen.Bottom }) { ys.Add(edge); Add(ys, (long)edge - requested.Height); }
        }
        // The feasible origin region has boundaries only where a window edge
        // meets a screen edge. Include the requested axes to retain the nearest
        // point along an edge, rather than snapping to its corners.
        var candidates = new List<(double Distance, int X, int Y)>();
        foreach (int x in xs)
        foreach (int y in ys)
        {
            double distance = Distance(x, y, requested);
            if (distance <= bestDistance) candidates.Add((distance, x, y));
        }
        candidates.Sort();
        foreach (var candidate in candidates)
        {
            var rectangle = requested with { Left = candidate.X, Top = candidate.Y };
            if (!Valid(rectangle) || !Covered(rectangle, allowed)) continue;
            constrained = rectangle; return true;
        }
        return false;
    }

    private static void Add(HashSet<int> coordinates, long value)
    { if (value is >= int.MinValue and <= int.MaxValue) coordinates.Add((int)value); }

    private static double Distance(int x, int y, PixelBounds requested)
    {
        double dx = (double)x - requested.Left, dy = (double)y - requested.Top;
        return dx * dx + dy * dy;
    }

    private static bool Valid(PixelBounds rectangle) => rectangle.Width > 0 && rectangle.Height > 0 &&
        (long)rectangle.Left + rectangle.Width <= int.MaxValue && (long)rectangle.Top + rectangle.Height <= int.MaxValue;

    private static bool Covered(PixelBounds rectangle, IReadOnlyList<PixelBounds> screens)
    {
        if (screens.Any(screen => rectangle.Left >= screen.Left && rectangle.Top >= screen.Top && rectangle.Right <= screen.Right && rectangle.Bottom <= screen.Bottom)) return true;
        var remaining = new List<PixelBounds> { rectangle };
        foreach (var screen in screens)
        {
            var next = new List<PixelBounds>();
            foreach (var part in remaining)
            {
                int left = Math.Max(part.Left, screen.Left), top = Math.Max(part.Top, screen.Top);
                int right = Math.Min(part.Right, screen.Right), bottom = Math.Min(part.Bottom, screen.Bottom);
                if (right <= left || bottom <= top) { next.Add(part); continue; }
                Piece(part.Left, part.Top, part.Right, top);
                Piece(part.Left, bottom, part.Right, part.Bottom);
                Piece(part.Left, top, left, bottom);
                Piece(right, top, part.Right, bottom);
            }
            if (next.Count == 0) return true;
            remaining = next;
            void Piece(int left, int top, int right, int bottom)
            { if (right > left && bottom > top) next.Add(new(left, top, right - left, bottom - top)); }
        }
        return false;
    }
}
