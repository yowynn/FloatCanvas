using FloatCanvas.App;

/// <summary>Deterministic launcher gestures; no HWND, timers, mouse capture, or injected input.</summary>
internal static class LauncherGestureStateTests
{
    public static void Run(Action<string, bool> assert)
    {
        var state = NewState();
        assert("idle launcher has no timer or gesture", Idle(state));
        assert("unpaired launcher move and up do nothing", state.PointerMove(100, 100, 0) == LauncherGestureAction.None &&
            state.PointerUp(100, 100, 0) == LauncherGestureAction.None && state.AdvanceTime(10000) == LauncherGestureAction.None);
        assert("first launcher down captures a possible gesture without an action", state.PointerDown(10, 20, 100) == LauncherGestureAction.None &&
            state.IsPointerDown && !state.IsDragging && state.PendingClickDeadlineMs is null);
        assert("holding a launcher press does not start its menu timer", state.AdvanceTime(110) == LauncherGestureAction.None);
        assert("first launcher up waits for the supplied double-click interval", state.PointerUp(10, 20, 120) == LauncherGestureAction.None &&
            state.HasPendingClick && state.PendingClickDeadlineMs == 750);
        assert("single launcher click does not fire early", state.AdvanceTime(749) == LauncherGestureAction.None);
        assert("single launcher click opens the menu exactly once at its deadline", state.AdvanceTime(750) == LauncherGestureAction.OpenMenu &&
            state.AdvanceTime(751) == LauncherGestureAction.None && Idle(state));

        Click(state, 10, 20, 1000);
        assert("second launcher down consumes the pending first click", state.PointerDown(13, 24, 1500) == LauncherGestureAction.None &&
            !state.HasPendingClick && state.PendingClickDeadlineMs is null);
        assert("double-click decision waits for release so a drag can win", state.AdvanceTime(2000) == LauncherGestureAction.None);
        assert("second launcher up toggles visibility once with no later menu", state.PointerUp(13, 24, 2010) == LauncherGestureAction.ToggleVisibility &&
            state.PointerUp(13, 24, 2020) == LauncherGestureAction.None && state.AdvanceTime(3000) == LauncherGestureAction.None && Idle(state));

        Click(state, 0, 0, 4000);
        state.PointerDown(6, 10, 4650);
        assert("double-click accepts the supplied time and rectangle boundaries", state.PointerUp(6, 10, 4670) == LauncherGestureAction.ToggleVisibility && Idle(state));

        Click(state, 0, 0, 5000);
        assert("a late second press completes the earlier single click", state.PointerDown(0, 0, 5651) == LauncherGestureAction.OpenMenu);
        state.PointerUp(0, 0, 5660);
        assert("a late second press starts an independent single click", state.AdvanceTime(6301) == LauncherGestureAction.OpenMenu && Idle(state));

        Click(state, 0, 0, 7000);
        state.PointerDown(7, 0, 7100);
        state.PointerUp(7, 0, 7120);
        assert("click outside system double-click width does not toggle or discard first click", state.AdvanceTime(7650) == LauncherGestureAction.OpenMenu &&
            state.HasPendingClick && state.PendingClickDeadlineMs == 7750);
        assert("separate nearby-time clicks each retain their own deadline", state.AdvanceTime(7749) == LauncherGestureAction.None &&
            state.AdvanceTime(7750) == LauncherGestureAction.OpenMenu && Idle(state));

        Click(state, 0, 0, 8000);
        state.PointerDown(0, 11, 8100);
        state.PointerUp(0, 11, 8120);
        assert("click outside system double-click height stays independent", state.AdvanceTime(8650) == LauncherGestureAction.OpenMenu &&
            state.AdvanceTime(8750) == LauncherGestureAction.OpenMenu && Idle(state));

        state.PointerDown(100, 200, 9000);
        assert("motion at the supplied drag thresholds is still a click candidate", state.PointerMove(107, 211, 9010) == LauncherGestureAction.None && !state.IsDragging);
        assert("crossing horizontal drag threshold starts one drag immediately", state.PointerMove(107.1, 200, 9020) == LauncherGestureAction.BeginDrag && state.IsDragging);
        assert("continued launcher dragging cannot restart a drag or open a menu", state.PointerMove(130, 230, 9030) == LauncherGestureAction.None &&
            state.AdvanceTime(10000) == LauncherGestureAction.None);
        assert("drag release clears the entire launcher gesture", state.PointerUp(130, 230, 10010) == LauncherGestureAction.None &&
            state.AdvanceTime(11000) == LauncherGestureAction.None && Idle(state));

        state.PointerDown(0, 0, 12000);
        assert("crossing vertical drag threshold independently starts a drag", state.PointerMove(0, -11.1, 12010) == LauncherGestureAction.BeginDrag);
        state.PointerUp(0, -11.1, 12020);
        assert("vertical drag release also clears state", Idle(state));

        Click(state, 10, 20, 13000);
        state.PointerDown(10, 20, 13500);
        assert("second press drag takes priority over double-click and pending menu", state.PointerMove(18, 20, 13510) == LauncherGestureAction.BeginDrag &&
            state.AdvanceTime(14000) == LauncherGestureAction.None);
        assert("second press drag release cannot toggle or open menu", state.PointerUp(18, 20, 14010) == LauncherGestureAction.None &&
            state.AdvanceTime(15000) == LauncherGestureAction.None && Idle(state));

        Click(state, 0, 0, 16000);
        state.PointerDown(20, 20, 16100);
        assert("an unmatched second press postpones pending single while held", state.HasPendingClick && state.PendingClickDeadlineMs is null &&
            state.AdvanceTime(17000) == LauncherGestureAction.None);
        state.PointerMove(28, 20, 17010);
        assert("an unmatched second press drag also cancels earlier pending single", state.PointerUp(28, 20, 17020) == LauncherGestureAction.None &&
            state.AdvanceTime(18000) == LauncherGestureAction.None && Idle(state));

        state.PointerDown(0, 0, 19000);
        assert("release beyond drag threshold suppresses a click without starting a released drag", state.PointerUp(8, 0, 19020) == LauncherGestureAction.None &&
            state.AdvanceTime(20000) == LauncherGestureAction.None && Idle(state));
        state.PointerDown(0, 0, 21000);
        assert("holding a first press beyond the interval cannot open a menu", state.AdvanceTime(22000) == LauncherGestureAction.None);
        assert("a held first press opens its menu on release after the interval", state.PointerUp(0, 0, 22000) == LauncherGestureAction.OpenMenu && Idle(state));

        Click(state, 0, 0, 23000);
        assert("cancel clears a delayed launcher menu", state.Cancel() == LauncherGestureAction.None && state.AdvanceTime(24000) == LauncherGestureAction.None && Idle(state));
        Click(state, 0, 0, 25000);
        state.PointerDown(0, 0, 25100);
        state.Cancel();
        assert("cancel clears a possible double-click and its release", state.PointerUp(0, 0, 25120) == LauncherGestureAction.None &&
            state.AdvanceTime(26000) == LauncherGestureAction.None && Idle(state));
        state.PointerDown(0, 0, 27000);
        state.PointerMove(8, 0, 27010);
        state.Cancel();
        assert("cancel clears active dragging", state.PointerUp(8, 0, 27020) == LauncherGestureAction.None && Idle(state));
        Click(state, 50, 50, 28000);
        assert("a fresh click after cancellation uses its own position and deadline", state.PendingClickDeadlineMs == 28650 &&
            state.AdvanceTime(28650) == LauncherGestureAction.OpenMenu && Idle(state));

        Click(state, 0, 0, 29000);
        state.PointerDown(0, 0, 29100);
        state.PointerUp(0, 0, 29120);
        Click(state, 0, 0, 29200);
        assert("third click after a completed double-click starts a new single", state.AdvanceTime(29850) == LauncherGestureAction.OpenMenu && Idle(state));

        var alternate = new LauncherGestureState(1400, 30, 40, 60, 80);
        alternate.PointerDown(0, 0, 100);
        assert("launcher gesture honors larger supplied drag distances", alternate.PointerMove(29, 39, 200) == LauncherGestureAction.None);
        alternate.PointerUp(29, 39, 210);
        assert("launcher gesture honors a different supplied double-click interval", alternate.PendingClickDeadlineMs == 1500 &&
            alternate.AdvanceTime(1499) == LauncherGestureAction.None);
        alternate.PointerDown(29, 39, 1499);
        assert("launcher gesture honors a different supplied double-click rectangle", alternate.PointerUp(29, 39, 1510) == LauncherGestureAction.ToggleVisibility && Idle(alternate));
    }

    private static LauncherGestureState NewState() => new(650, 7, 11, 12, 20);
    private static bool Idle(LauncherGestureState state) => !state.IsPointerDown && !state.IsDragging && !state.HasPendingClick && state.PendingClickDeadlineMs is null;
    private static void Click(LauncherGestureState state, double x, double y, long downMs)
    {
        state.PointerDown(x, y, downMs);
        state.PointerUp(x, y, downMs + 20);
    }
}
