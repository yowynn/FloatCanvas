using FloatCanvas.Windows;

namespace FloatCanvas.WindowsTests;

internal static class IsolatedPointerPressesTests
{
    internal static void Run(Action<bool, string> check)
    {
        var presses = new IsolatedPointerPresses(3);
        var point = new OverlayPoint(-120, 45);
        var timeline = new KeyboardModifierHistory(4);
        timeline.Reset(99, OverlayModifiers.None);
        timeline.Record(100, 100, OverlayModifiers.Control);
        presses.Record(0x201, 100, point, OverlayModifiers.Control);
        timeline.Record(100, 100, OverlayModifiers.None);
        check(timeline.At(100, 100) == OverlayModifiers.None,
            "Millisecond keyboard history alone cannot distinguish Ctrl-down and Ctrl-up around a same-tick mouse press");
        check(presses.TryTake(100, point, OverlayMouseButton.Left, out var exact) && exact == OverlayModifiers.Control,
            "The same-thread hook press snapshot preserves Ctrl-down ownership even if release has the exact same native timestamp");
        check(!presses.TryTake(100, point, OverlayMouseButton.Left, out _),
            "An exact press snapshot is consumed once rather than reused by later gestures");

        presses.Record(0x201, 200, point, OverlayModifiers.Control);
        presses.Record(0x201, 200, point, OverlayModifiers.Shift);
        check(presses.TryTake(200, point, OverlayMouseButton.Left, out var first) && first == OverlayModifiers.Control &&
            presses.TryTake(200, point, OverlayMouseButton.Left, out var second) && second == OverlayModifiers.Shift,
            "Identical timestamp/point presses retain hook order for the native first-click and double-click sequence");
        presses.Record(0x204, 300, point, OverlayModifiers.Shift);
        check(!presses.TryTake(300, point, OverlayMouseButton.Left, out _) &&
            !presses.TryTake(300, new(point.X + 1, point.Y), OverlayMouseButton.Right, out _) &&
            presses.TryTake(300, point, OverlayMouseButton.Right, out var right) && right == OverlayModifiers.Shift,
            "Press matching includes the physical screen point and button, including negative monitor coordinates");
        presses.Record(0x207, 400, point, OverlayModifiers.None);
        check(presses.TryTake(400, point, OverlayMouseButton.Middle, out _), "Middle presses preserve their own input identity");
        presses.Record(0x200, 500, point, OverlayModifiers.Control);
        presses.Record(0x20A, 500, point, OverlayModifiers.Control);
        check(!presses.TryTake(500, point, OverlayMouseButton.Left, out _), "Move and wheel observations never consume press storage");
        for (uint time = 600; time <= 603; time++) presses.Record(0x201, time, point, OverlayModifiers.Control);
        check(!presses.TryTake(600, point, OverlayMouseButton.Left, out _) && presses.TryTake(601, point, OverlayMouseButton.Left, out _),
            "Press retention remains bounded while preserving recent unmatched input");
        presses.Clear();
        check(!presses.TryTake(602, point, OverlayMouseButton.Left, out _), "Ending or replacing a lease clears all old press identities");
    }
}
