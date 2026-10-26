using FloatCanvas.Windows;

namespace FloatCanvas.WindowsTests;

internal static class KeyboardPassedStateTests
{
    internal static void Run(Action<bool, string> check)
    {
        static KeyboardSuppressionState New() => new(256, 2000);
        static void Begin(KeyboardSuppressionState state, byte[]? snapshot = null, long now = 200) =>
            state.Begin(snapshot ?? new byte[256], HotkeyBinding.DefaultToggle, HotkeyBinding.DefaultHide, now);
        static bool Key(KeyboardSuppressionState state, uint key, bool up = false, bool own = false, long now = 100) =>
            state.Process(key, 0, up ? 0x80u : 0, (uint)now, up, up ? 0x101u : 0x100u,
                42, own, true, now);

        foreach (uint key in new uint[] { 0xA2, 0x32 })
        {
            var stale = New();
            check(!Key(stale, key), "An inactive down passes to the global stream: " + key);
            Begin(stale, now: 10000);
            check((stale.CopyLocalState()[key] & 0x80) == 0 && stale.CurrentModifiers == OverlayModifiers.None,
                "A missed historical up cannot force Ctrl or 2 down over a fresh async-up snapshot: " + key);
            check(!Key(stale, key, own: true, now: 10010) && !Key(stale, key, own: true, now: 20000),
                "Passed-down ownership retains repeats even when the entire hook chain delays async publication: " + key);
            check(!Key(stale, key, up: true, own: true, now: 30000),
                "The release of a potentially still-global passed down is never swallowed: " + key);
            check((stale.CopyLocalState()[key] & 0x80) == 0 && stale.CurrentModifiers == OverlayModifiers.None && stale.QueuedCount == 0,
                "The conservative passed stream ends without a sticky local key or private reconstruction: " + key);
            check(Key(stale, key, own: true, now: 30010) && Key(stale, key, up: true, own: true, now: 30020),
                "After one complete conservative stream, the next down and matching up are isolated again: " + key);
        }

        var released = New();
        Key(released, 0xA2);
        Key(released, 0xA2, up: true, now: 110);
        Begin(released);
        check(Key(released, 0xA2, own: true, now: 210) && Key(released, 0xA2, up: true, own: true, now: 220),
            "An observed passed up clears stream ownership without any completion marker or delay");

        var current = New();
        Key(current, 0xA2, up: true);
        var held = new byte[256]; held[0x11] = held[0xA2] = held[0x32] = 0x80;
        Begin(current, held);
        check(current.CurrentModifiers == OverlayModifiers.Control && (current.CopyLocalState()[0x32] & 0x80) != 0,
            "A fresh async-down snapshot is authoritative even after a historical passed up");
        check(!Key(current, 0xA2, up: true, own: true, now: 210) &&
            !Key(current, 0x32, up: true, own: true, now: 220),
            "Each down inherited from the current snapshot retains its matching global release");

        var independent = New();
        Key(independent, 0xA2);
        Key(independent, 0x32, now: 110);
        Key(independent, 0xA2, up: true, now: 120);
        Begin(independent);
        check(Key(independent, 0xA2, own: true, now: 210) && !Key(independent, 0x32, own: true, now: 220),
            "One key's observed up restores its isolation without retiring another key's passed-down ownership");
        check(Key(independent, 0xA2, up: true, own: true, now: 230) &&
            !Key(independent, 0x32, up: true, own: true, now: 240),
            "Interleaved private and inherited keys each retain the matching release route");

        var reentry = New();
        Key(reentry, 0xA2);
        Begin(reentry);
        reentry.End(KeyboardLeaseEndReason.Explicit, 300);
        Begin(reentry, now: 1000000);
        check(reentry.CurrentModifiers == OverlayModifiers.None &&
            !Key(reentry, 0xA2, up: true, own: true, now: 1000010),
            "Entry replacement and elapsed time cannot retire global release ownership or revive a stale local Ctrl");
        check(Key(reentry, 0xA2, own: true, now: 1000020),
            "An actual matching release restores isolation after a later entry");
    }
}
