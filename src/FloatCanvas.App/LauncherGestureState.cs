using System;
using System.Collections.Generic;

namespace FloatCanvas.App;

public enum LauncherGestureAction
{
    None,
    OpenMenu,
    ToggleVisibility,
    BeginDrag
}

/// <summary>Resolves left-button launcher clicks and drags without owning input or a clock.</summary>
public sealed class LauncherGestureState
{
    private readonly long doubleClickTimeMs;
    private readonly double horizontalDragDistance;
    private readonly double verticalDragDistance;
    private readonly double doubleClickHalfWidth;
    private readonly double doubleClickHalfHeight;
    private readonly List<Click> pendingClicks = [];
    private Click press;
    private bool isSecondClick;

    private readonly record struct Click(double X, double Y, long DownTimeMs);

    // Distances and pointer coordinates must use the same coordinate system.
    // Double-click width/height are the full system rectangle centered on the first down.
    public LauncherGestureState(long doubleClickTimeMs, double horizontalDragDistance,
        double verticalDragDistance, double doubleClickWidth, double doubleClickHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(doubleClickTimeMs);
        CheckDistance(horizontalDragDistance, nameof(horizontalDragDistance));
        CheckDistance(verticalDragDistance, nameof(verticalDragDistance));
        CheckDistance(doubleClickWidth, nameof(doubleClickWidth));
        CheckDistance(doubleClickHeight, nameof(doubleClickHeight));
        this.doubleClickTimeMs = doubleClickTimeMs;
        this.horizontalDragDistance = horizontalDragDistance;
        this.verticalDragDistance = verticalDragDistance;
        doubleClickHalfWidth = doubleClickWidth / 2;
        doubleClickHalfHeight = doubleClickHeight / 2;
    }

    public bool IsPointerDown { get; private set; }
    public bool IsDragging { get; private set; }
    public bool HasPendingClick => pendingClicks.Count != 0;

    // Do not arm the single-click timer while a press can still become a drag.
    public long? PendingClickDeadlineMs => !IsPointerDown && HasPendingClick
        ? pendingClicks[0].DownTimeMs + doubleClickTimeMs : null;

    public LauncherGestureAction PointerDown(double x, double y, long nowMs)
    {
        if (IsPointerDown) return LauncherGestureAction.None;
        isSecondClick = HasPendingClick && IsDoubleClick(pendingClicks[^1], x, y, nowMs);
        LauncherGestureAction action = LauncherGestureAction.None;
        if (isSecondClick) pendingClicks.RemoveAt(pendingClicks.Count - 1);
        else action = AdvanceTime(nowMs);
        press = new(x, y, nowMs);
        IsPointerDown = true;
        return action;
    }

    public LauncherGestureAction PointerMove(double x, double y, long nowMs)
    {
        if (!IsPointerDown || IsDragging || !CrossesDragThreshold(x, y)) return LauncherGestureAction.None;
        pendingClicks.Clear();
        isSecondClick = false;
        IsDragging = true;
        return LauncherGestureAction.BeginDrag;
    }

    public LauncherGestureAction PointerUp(double x, double y, long nowMs)
    {
        if (!IsPointerDown) return LauncherGestureAction.None;
        bool wasDrag = IsDragging || CrossesDragThreshold(x, y);
        bool wasSecondClick = isSecondClick;
        IsPointerDown = IsDragging = isSecondClick = false;
        if (wasDrag)
        {
            pendingClicks.Clear();
            return LauncherGestureAction.None;
        }
        if (wasSecondClick) return LauncherGestureAction.ToggleVisibility;
        pendingClicks.Add(press);
        return AdvanceTime(nowMs);
    }

    public LauncherGestureAction AdvanceTime(long nowMs)
    {
        if (PendingClickDeadlineMs is not long deadline || nowMs < deadline) return LauncherGestureAction.None;
        pendingClicks.RemoveAt(0);
        return LauncherGestureAction.OpenMenu;
    }

    public LauncherGestureAction Cancel()
    {
        pendingClicks.Clear();
        press = default;
        IsPointerDown = IsDragging = isSecondClick = false;
        return LauncherGestureAction.None;
    }

    private bool IsDoubleClick(Click first, double x, double y, long nowMs) =>
        nowMs >= first.DownTimeMs && nowMs - first.DownTimeMs <= doubleClickTimeMs &&
        Math.Abs(x - first.X) <= doubleClickHalfWidth && Math.Abs(y - first.Y) <= doubleClickHalfHeight;

    private bool CrossesDragThreshold(double x, double y) =>
        Math.Abs(x - press.X) > horizontalDragDistance || Math.Abs(y - press.Y) > verticalDragDistance;

    private static void CheckDistance(double distance, string name)
    {
        if (!double.IsFinite(distance) || distance < 0) throw new ArgumentOutOfRangeException(name);
    }
}
