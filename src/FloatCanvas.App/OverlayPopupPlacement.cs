using System.Windows;

namespace FloatCanvas.App;

/// <summary>Chooses and constrains the final physical popup rectangle on its anchor's monitor.</summary>
internal static class OverlayPopupPlacement
{
    internal static Point Place(IReadOnlyList<Point> candidates, Size size, Rect workArea, Vector offset)
    {
        if (candidates.Count == 0) throw new ArgumentException("A popup needs an anchor position.", nameof(candidates));
        Point best = candidates[0] + offset;
        double mostVisible = -1;
        foreach (var candidate in candidates)
        {
            var position = candidate + offset;
            var rectangle = new Rect(position, size);
            var visible = Rect.Intersect(rectangle, workArea);
            double area = visible.IsEmpty ? 0 : visible.Width * visible.Height;
            if (area > mostVisible) { best = position; mostVisible = area; }
            // Preserve callback preference when several placements fit equally well.
            if (workArea.Contains(rectangle)) { best = position; break; }
        }
        return new(Math.Clamp(best.X, workArea.Left, Math.Max(workArea.Left, workArea.Right - size.Width)),
            Math.Clamp(best.Y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - size.Height)));
    }
}
