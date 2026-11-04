namespace FloatCanvas.Core;

/// <summary>Velocity in physical view pixels/second, using only the outer edges of the available screens.</summary>
public static class CanvasEdgePan
{
    public static PointD Velocity(PointD pointer, IReadOnlyList<RectD> screens, double band = 40, double maximumSpeed = 900)
    {
        if (!double.IsFinite(pointer.X + pointer.Y) || !double.IsFinite(band) || band <= 0 ||
            !double.IsFinite(maximumSpeed) || maximumSpeed <= 0) return default;
        var screen = screens.FirstOrDefault(bounds => bounds.Contains(pointer));
        if (screen.IsEmpty) return default;
        double x = 0, y = 0;
        if (pointer.X - screen.Left < band && !Neighbor(new(screen.Left - 1, pointer.Y))) x -= Speed(pointer.X - screen.Left);
        if (screen.Right - pointer.X < band && !Neighbor(new(screen.Right + 1, pointer.Y))) x += Speed(screen.Right - pointer.X);
        if (pointer.Y - screen.Top < band && !Neighbor(new(pointer.X, screen.Top - 1))) y -= Speed(pointer.Y - screen.Top);
        if (screen.Bottom - pointer.Y < band && !Neighbor(new(pointer.X, screen.Bottom + 1))) y += Speed(screen.Bottom - pointer.Y);
        return new(x, y);

        bool Neighbor(PointD point) => screens.Any(bounds => bounds != screen && bounds.Contains(point));
        double Speed(double distance) => maximumSpeed * Math.Pow(Math.Clamp(1 - distance / band, 0, 1), 2);
    }
}
