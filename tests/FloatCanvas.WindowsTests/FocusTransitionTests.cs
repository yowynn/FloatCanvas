using FloatCanvas.Windows;

namespace FloatCanvas.WindowsTests;

internal static class FocusTransitionTests
{
    public static void Run(Action<bool, string> check)
    {
        var state = new FocusTransitionState();
        check(state.Begin(1) == default && !state.CanResume(1, KeyboardLeaseEndReason.ForegroundLost, true),
            "A pass-through panel cannot acquire an editing-resume permit");
        state.BeginSession();
        long firstSession = state.SessionVersion;
        var first = state.Begin(12);
        check(state.IsCurrent(first) && state.CanResume(12, KeyboardLeaseEndReason.ForegroundLost, true),
            "One internal boundary may resume its own foreground-ended lease after returning to an own window");
        check(!state.CanResume(12, KeyboardLeaseEndReason.ForegroundLost, false),
            "An internal boundary never permits a resume with an external foreground");
        check(!state.CanResume(11, KeyboardLeaseEndReason.ForegroundLost, true),
            "A previous lease's notification cannot renew the current lease");
        foreach (var reason in Enum.GetValues<KeyboardLeaseEndReason>().Where(r => r != KeyboardLeaseEndReason.ForegroundLost))
            check(!state.CanResume(12, reason, true), "Internal boundaries cannot recover " + reason);
        check(!state.CanResume(12, null, true), "An absent end reason cannot be treated as foreground loss");

        var nested = state.Begin(12);
        check(!state.Complete(first) && state.IsCurrent(nested),
            "An earlier queued completion cannot revoke the latest nested boundary");
        state.ReplacedLease(13);
        check(!state.CanResume(12, KeyboardLeaseEndReason.ForegroundLost, true) &&
            state.CanResume(13, KeyboardLeaseEndReason.ForegroundLost, true),
            "A successful replacement updates the permit identity without changing the editing session");
        check(state.Complete(nested) && !state.HasPending &&
            !state.CanResume(13, KeyboardLeaseEndReason.ForegroundLost, true),
            "The control-window completion revokes the permit; modal lifetime cannot keep it alive");
        check(!state.Complete(nested), "Disposal and posted completion are idempotent");

        var old = state.Begin(13);
        state.EndSession();
        check(state.SessionVersion > firstSession && !state.HasPending && !state.IsCurrent(old),
            "Exiting editing immediately invalidates pending internal transitions");
        state.BeginSession();
        var current = state.Begin(14);
        check(current.SessionVersion != old.SessionVersion && !state.Complete(old) && state.IsCurrent(current),
            "A delayed close from an old editing session cannot affect a new session");
        state.Cancel();
        check(!state.HasPending && !state.CanResume(14, KeyboardLeaseEndReason.ForegroundLost, true),
            "A fatal input end cancels all internal resume permission");

        // The hook decision must remain independent of the UI's pending boundary.
        var keyboard = new KeyboardSuppressionState(256, 2000);
        keyboard.Begin(new byte[256], HotkeyBinding.DefaultToggle, HotkeyBinding.DefaultHide, 100);
        state.Begin(keyboard.LeaseId);
        bool suppressed = keyboard.Process(0x58, 45, 0, 110, false, 0x100, 42,
            ownsForeground: false, healthy: true, now: 110);
        check(!suppressed && !keyboard.IsActive && keyboard.LastEndReason == KeyboardLeaseEndReason.ForegroundLost,
            "A fresh external key fails open immediately even while the UI owns a transition permit");
        check(!keyboard.CanResumeForegroundLoss,
            "The external key that reveals foreground loss also vetoes renewing that lease");

        keyboard.Begin(new byte[256], HotkeyBinding.DefaultToggle, HotkeyBinding.DefaultHide, 200);
        keyboard.End(KeyboardLeaseEndReason.ForegroundLost, 210);
        check(keyboard.CanResumeForegroundLoss, "A watchdog-only foreground loss can be considered by a short UI transition");
        keyboard.Process(0x59, 21, 0x80, 220, true, 0x101, 42, false, true, 220);
        check(keyboard.CanResumeForegroundLoss, "A passed external keyup alone does not veto a transition return");
        check(!keyboard.Process(0x59, 21, 0, 230, false, 0x100, 42, false, true, 230) && !keyboard.CanResumeForegroundLoss,
            "A fresh external down after foreground loss passes and permanently vetoes that lease's resume");

        keyboard = new KeyboardSuppressionState(256, 2000);
        keyboard.Begin(new byte[256], HotkeyBinding.DefaultToggle, HotkeyBinding.DefaultHide, 300);
        keyboard.Process(0x58, 45, 0, 310, false, 0x100, 42, true, true, 310);
        keyboard.End(KeyboardLeaseEndReason.ForegroundLost, 320);
        check(keyboard.Process(0x58, 45, 0x80, 330, true, 0x101, 42, false, true, 330) && keyboard.CanResumeForegroundLoss,
            "The matching swallowed keyup remains balanced without being mistaken for a fresh external press");
        keyboard.ObserveExternalPointerPress(340);
        check(!keyboard.CanResumeForegroundLoss && keyboard.LastEndReason == KeyboardLeaseEndReason.ForegroundLost,
            "An explicit external mouse press vetoes an ended foreground lease even if foreground returns before queue delivery");

        keyboard.Begin(new byte[256], HotkeyBinding.DefaultToggle, HotkeyBinding.DefaultHide, 400);
        keyboard.Process(0x58, 45, 0, 410, false, 0x100, 42, true, true, 410);
        keyboard.ObserveExternalPointerPress(420);
        check(!keyboard.IsActive && !keyboard.CanResumeForegroundLoss &&
            keyboard.Process(0x58, 45, 0x80, 430, true, 0x101, 42, false, true, 430),
            "An external mouse press ends active isolation immediately while preserving the already swallowed key's tail");
        keyboard.Begin(new byte[256], HotkeyBinding.DefaultToggle, HotkeyBinding.DefaultHide, 500);
        keyboard.End(KeyboardLeaseEndReason.ForegroundLost, 510);
        check(keyboard.CanResumeForegroundLoss, "An explicit fresh editing session clears an earlier external-press veto");
    }
}
