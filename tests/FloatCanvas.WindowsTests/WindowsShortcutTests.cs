using FloatCanvas.Windows;

namespace FloatCanvas.WindowsTests;

internal static class WindowsShortcutTests
{
    internal static void Run(Action<bool, string> check)
    {
        KeyboardSuppressionState New(byte[]? initial = null)
        {
            var value = new KeyboardSuppressionState(256, 2000);
            value.Begin(initial ?? new byte[256], HotkeyBinding.DefaultToggle, HotkeyBinding.DefaultHide, 100);
            return value;
        }
        bool Key(KeyboardSuppressionState state, uint key, bool up = false, bool own = true, long now = 110) =>
            state.Process(key, 0, up ? 0x80u : 0, (uint)now, up, up ? 0x101u : 0x100u, 42, own, true, now);
        foreach (uint win in new uint[] { 0x5B, 0x5C })
        {
            var state = New();
            check(!Key(state, win) && state.IsActive && state.LastEndReason is null,
                "A Windows modifier passes to the OS without ending editing before an actual foreground choice");
            check(!Key(state, 0x20) && !Key(state, 0x20) && !Key(state, 0x20, true) && !Key(state, win, true),
                "The complete Win+Space repeat/release stream passes unchanged to the OS");
            check(state.IsActive && state.QueuedCount == 0,
                "Win+Space emits no private key or lease-ended packet that could finish a text/save dialog");
            check(Key(state, 0x58), "Ordinary typing remains isolated after the Windows modifier is released");
            check(!Key(state, win) && !Key(state, win, true) && state.IsActive,
                "A later plain Windows press also waits for real foreground intent");
            check(!Key(state, 0x59, own: false) && !state.IsActive &&
                state.LastEndReason == KeyboardLeaseEndReason.ForegroundLost,
                "Actual external typing after Windows still immediately fails open and ends the lease");
        }
        var inherited = new byte[256]; inherited[0x5B] = 0x80;
        var withInheritedWin = New(inherited);
        check(!Key(withInheritedWin, 0x20) && !Key(withInheritedWin, 0x20, true) &&
            !Key(withInheritedWin, 0x5B, true) && withInheritedWin.IsActive,
            "A Windows modifier inherited at entry can switch IME without private reconstruction");
        var held = New();
        check(Key(held, 0x58) && !Key(held, 0x5B) && Key(held, 0x58, true) && held.IsActive,
            "A key swallowed before Windows retains its swallowed release rather than leaking a global keyup");
        var desktop = New();
        check(!Key(desktop, 0x5B) && !Key(desktop, 0x44) && desktop.IsActive,
            "A fresh Win+D chord is passed to the Shell without prematurely ending on Win down");
        desktop.End(KeyboardLeaseEndReason.ForegroundLost, 120);
        check(!desktop.IsActive && desktop.LastEndReason == KeyboardLeaseEndReason.ForegroundLost,
            "The real desktop/Start foreground transition still terminates editing");

        KeyboardSuppressionState Switching()
        {
            var value = New();
            Key(value, 0x5B); Key(value, 0x20);
            return value;
        }
        var switching = Switching();
        long originalLease = switching.LeaseId;
        check(switching.TryGetInputMethodSwitch(originalLease, 120, out var origin) && origin == 42,
            "Only observed own-window Win+Space records the exact return HWND and current lease");
        switching.End(KeyboardLeaseEndReason.ForegroundLost, 130);
        check(!switching.IsActive && switching.CanResumeForegroundLoss &&
            switching.TryGetInputMethodSwitch(originalLease, 140, out _),
            "A transient flyout fails open immediately while retaining only a bounded return candidate");
        check(!Key(switching, 0x20, own: false, now: 150) && switching.CanResumeForegroundLoss,
            "Continuing Win+Space in the flyout passes without being mistaken for typing in another application");
        check(!Key(switching, 0x20, true, own: false, now: 160) && !Key(switching, 0x5B, true, own: false, now: 170),
            "Flyout navigation releases are passed, never reconstructed or swallowed");
        check(!switching.TryGetInputMethodSwitch(originalLease + 1, 180, out _) &&
            !switching.TryGetInputMethodSwitch(originalLease, 2200, out _),
            "Old-lease identities and an expired two-second return window cannot resume editing");
        check(!Key(switching, 0x59, own: false, now: 180) &&
            !switching.CanResumeForegroundLoss && !switching.TryGetInputMethodSwitch(originalLease, 180, out _),
            "Any new external typing immediately vetoes the IME candidate without suppressing that key");
        switching = Switching(); switching.ObserveExternalPointerPress(130);
        check(!switching.IsActive && !switching.TryGetInputMethodSwitch(switching.LeaseId, 140, out _),
            "An actual external pointer choice immediately cancels the IME return candidate");
        switching = Switching(); switching.End(KeyboardLeaseEndReason.ForegroundLost, 130);
        Key(switching, 0x20, true, false, 140); Key(switching, 0x5B, true, false, 150);
        check(!Key(switching, 0x5B, own: false, now: 160) &&
            !switching.TryGetInputMethodSwitch(switching.LeaseId, 170, out _),
            "A fresh Windows gesture after release denotes new Shell intent, not the previous IME navigation");
        switching = Switching(); Key(switching, 0x44);
        check(!switching.TryGetInputMethodSwitch(switching.LeaseId, 140, out _),
            "Win+D or any other Windows chord cancels the narrower Win+Space candidate");
        foreach (var reason in Enum.GetValues<KeyboardLeaseEndReason>().Where(reason => reason != KeyboardLeaseEndReason.ForegroundLost))
        {
            switching = Switching(); switching.End(reason, 130);
            check(!switching.TryGetInputMethodSwitch(switching.LeaseId, 140, out _),
                "IME context cannot recover an explicit or unhealthy lease end: " + reason);
        }
    }
}
