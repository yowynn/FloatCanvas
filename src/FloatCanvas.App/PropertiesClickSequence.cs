using FloatCanvas.Windows;

namespace FloatCanvas.App;

/// <summary>Property requests pair completed canvas clicks, independently of other double-click actions.</summary>
internal sealed class PropertiesClickSequence
{
    private readonly uint maximumMilliseconds;
    private readonly double horizontalRadius, verticalRadius;
    private bool pendingPress, completedClick;
    private OverlayPoint firstPoint;
    private uint firstTime;

    internal PropertiesClickSequence(uint maximumMilliseconds, int clickWidth, int clickHeight)
    {
        if (maximumMilliseconds == 0 || clickWidth <= 0 || clickHeight <= 0) throw new ArgumentOutOfRangeException();
        this.maximumMilliseconds = maximumMilliseconds;
        horizontalRadius = clickWidth / 2d; verticalRadius = clickHeight / 2d;
    }

    internal bool Observe(OverlayPointerEvent pointer, bool dismissedProperties = false)
    {
        if (pointer.Kind is OverlayPointerKind.Cancel or OverlayPointerKind.Wheel) { Reset(); return false; }
        if (pointer.Button != OverlayMouseButton.Left)
        {
            if (pointer.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick) Reset();
            return false;
        }
        // Untimed synthetic/controller callers explicitly supply semantic kinds. Real
        // native and Popup-forwarded events always carry the original message time.
        if (pointer.Time is not { } time)
        { Reset(); return !dismissedProperties && pointer.Kind == OverlayPointerKind.DoubleClick; }
        if (pointer.Kind == OverlayPointerKind.Move)
        {
            if (pendingPress && !NearFirst(pointer.Position)) Reset();
            return false;
        }
        if (pointer.Kind == OverlayPointerKind.Up)
        {
            completedClick = pendingPress && unchecked((int)(time - firstTime)) >= 0 && NearFirst(pointer.Position);
            pendingPress = false;
            return false;
        }
        if (pointer.Kind is not (OverlayPointerKind.Down or OverlayPointerKind.DoubleClick)) return false;
        // Dismissing Inspector still performs the normal canvas press, but it is not
        // the first half of another properties gesture. A successful pair is consumed.
        if (dismissedProperties) { Reset(); return false; }
        int elapsed = unchecked((int)(time - firstTime));
        bool paired = completedClick && elapsed >= 0 && (uint)elapsed <= maximumMilliseconds && NearFirst(pointer.Position);
        Reset();
        if (paired) return true;
        firstTime = time; firstPoint = pointer.Position; pendingPress = true;
        return false;
    }

    internal void Reset() => pendingPress = completedClick = false;
    private bool NearFirst(OverlayPoint point) => Math.Abs(point.X - firstPoint.X) <= horizontalRadius &&
        Math.Abs(point.Y - firstPoint.Y) <= verticalRadius;
}
