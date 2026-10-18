using FloatCanvas.Windows;

namespace FloatCanvas.WindowsTests;

internal static class CaptureContinuationTests
{
    internal static void Run(Action<bool, string> check)
    {
        var owner = new object();
        CaptureContinuationState New(bool editing = true) { var s = new CaptureContinuationState(owner, 9, 20, editing, true, 3, 4, 5); s.Begin(42, 100); return s; }
        var state = New();
        check(!state.CanRestore(20, 3, 4, 5), "A capture cannot continue before its own complete visibility boundary");
        state.Ready(20, 42, 101, CaptureVisibilityStrategy.ExcludeVisible, 0);
        check(state.CanRestore(20, 3, 4, 5), "A no-action clean capture retains its original editing epoch");
        check(!state.CanRestore(21, 3, 4, 5), "A capture cannot adopt any ended/re-entered editing epoch");
        check(!state.CanRestore(20, 4, 4, 5), "An idempotent explicit exit or hide vetoes capture continuation");
        check(!state.CanRestore(20, 3, 5, 5), "A foreground-loss generation invalidates capture ownership");
        check(!state.CanRestore(20, 3, 4, 6), "New external input invalidates capture even without foreground change");
        var receipt = state.Complete(20);
        check(receipt.OriginEditingSessionVersion == 20 && receipt.RestoredEditingSessionVersion == 20 && receipt.WasEditing,
            "A no-handoff receipt retains the same epoch instead of authorizing End/Begin lease cycles");
        check(!receipt.TryConsume(new object(), 9, 20, 20, true, 3, 4, 5), "A receipt cannot cross native hosts");
        check(!receipt.TryConsume(owner, 10, 20, 20, true, 3, 4, 5), "A newer capture invalidates an old nonce");
        check(!receipt.TryConsume(owner, 9, 19, 20, true, 3, 4, 5), "A receipt cannot renew a different original request");
        check(!receipt.TryConsume(owner, 9, 20, 22, true, 3, 4, 5), "A receipt cannot renew an ended/re-entered session");
        check(!receipt.TryConsume(owner, 9, 20, 20, false, 3, 4, 5), "A receipt never changes editing mode");
        check(!receipt.TryConsume(owner, 9, 20, 20, true, 4, 4, 5), "A later explicit action invalidates an unconsumed receipt");
        check(!receipt.TryConsume(owner, 9, 20, 20, true, 3, 5, 5), "A later focus loss invalidates a receipt");
        check(!receipt.TryConsume(owner, 9, 20, 20, true, 3, 4, 6), "A later external input invalidates a receipt");
        check(receipt.TryConsume(owner, 9, 20, 20, true, 3, 4, 5), "Only the exact request may consume its unchanged capture receipt");
        check(!receipt.TryConsume(owner, 9, 20, 20, true, 3, 4, 5), "The permit remains strictly single-use without an epoch jump");
        state = New(); state.Ready(21, 42, 101, CaptureVisibilityStrategy.ExcludeVisible, 0);
        check(state.Status == CaptureRestoreStatus.Cancelled && !state.CanRestore(21, 3, 4, 5), "Any capture-owned epoch change is now an invariant violation");
        bool denied = false; try { state.Complete(21); } catch (InvalidOperationException) { denied = true; }
        check(denied, "A mechanical End/Begin transition cannot issue a new capture receipt");
        foreach (var reason in new[] { CaptureRestoreStatus.Cancelled, CaptureRestoreStatus.ForegroundChanged, CaptureRestoreStatus.GuardRejected, CaptureRestoreStatus.Failed })
        {
            state = New(); state.Ready(20, 42, 101, CaptureVisibilityStrategy.ExcludeVisible, 0); state.Cancel(reason); state.Cancel();
            check(state.Status == reason && !state.CanRestore(20, 3, 4, 5), "The first capture veto is permanent: " + reason);
            denied = false; try { state.Complete(20); } catch (InvalidOperationException) { denied = true; }
            check(denied, "An invalid capture never issues a permit: " + reason);
        }
        foreach (var strategy in Enum.GetValues<CaptureVisibilityStrategy>())
        {
            state = New(); state.Ready(20, 42, 101, strategy, strategy == CaptureVisibilityStrategy.LegacyInputOffscreen ? 42 : 0);
            check(state.Strategy == strategy && state.CanRestore(20, 3, 4, 5), "Every capture strategy preserves the original transaction: " + strategy);
        }
        state = New(false); state.Ready(20, 42, 101, CaptureVisibilityStrategy.ExcludeVisible, 0);
        var passive = state.Complete(20);
        check(!passive.WasEditing && state.Status == CaptureRestoreStatus.PassThrough && passive.TryConsume(owner, 9, 20, 20, false, 3, 4, 5),
            "A passive capture does not invent an editing session");
        foreach (nuint message in new nuint[] { 0x201, 0x204, 0x207, 0x20A, 0x20B, 0x20D, 0x20E })
            check(KeyboardInputIsolation.IsCapturePointerInteraction(message), "New pointer interaction is observable: " + message.ToString("X"));
        foreach (nuint message in new nuint[] { 0x200, 0x202, 0x205, 0x208, 0x20C })
            check(!KeyboardInputIsolation.IsCapturePointerInteraction(message), "Motion and balancing releases are not new user actions: " + message.ToString("X"));
        foreach (nint external in new nint[] { 65912, 65872, 4266392, 228399310 })
        {
            state = New(); state.Ready(20, 42, 101, CaptureVisibilityStrategy.ExcludeVisible, 0);
            state.ObserveForeground(external, 101, false);
            check(state.Status == CaptureRestoreStatus.ForegroundChanged && state.Reason == "ForegroundEventMismatch",
                "Every actual external switch vetoes without desktop/class exceptions: " + external);
            state.ObserveForeground(42, 102, true);
            check(!state.CanRestore(20, 3, 4, 5), "Returning from an external switch never renews the transaction");
        }
        state = New(); state.ObserveForeground(43, 99, false); state.Ready(20, 42, 101, CaptureVisibilityStrategy.ExcludeVisible, 0);
        check(state.CanRestore(20, 3, 4, 5), "An event before this capture start cannot cancel it");
        state.ObserveForeground(90, 101, true);
        check(state.CanRestore(20, 3, 4, 5), "An own-window focus transfer does not need an external window whitelist");
        state = New(); state.Begin(42, uint.MaxValue - 1); state.Ready(20, 42, uint.MaxValue, CaptureVisibilityStrategy.ExcludeVisible, 0);
        state.ObserveForeground(43, 0, false);
        check(state.Status == CaptureRestoreStatus.ForegroundChanged, "Capture event ordering handles the 32-bit tick wrap");
        check(WindowsCapabilities.FromVersion(new(10, 0, 14393)).IsSupported &&
            !WindowsCapabilities.FromVersion(new(10, 0, 14393)).SupportsCaptureExclusion, "The legacy input carrier does not raise the Windows 1607 baseline");
        state = New(); state.Ready(20, 42, 101, CaptureVisibilityStrategy.LegacyInputOffscreen, 42);
        state.InputRestoreFailed();
        check(state.UnsafeInputGeometry && state.Status == CaptureRestoreStatus.Failed && state.Reason == "InputGeometryRestoreFailed" &&
            !state.CanRestore(20, 3, 4, 5), "An offscreen carrier restore failure is an explicit infrastructure veto");
        state = New(); state.Ready(20, 42, 101, CaptureVisibilityStrategy.LegacyInputOffscreen, 42);
        state.Cancel(); state.InputRestoreFailed();
        check(state.UnsafeInputGeometry && state.Status == CaptureRestoreStatus.Cancelled,
            "An earlier cancel cannot erase the independent obligation to fail open after carrier restore failure");
    }
}
