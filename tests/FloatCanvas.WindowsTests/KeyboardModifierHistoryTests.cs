using FloatCanvas.Windows;

namespace FloatCanvas.WindowsTests;

internal static class KeyboardModifierHistoryTests
{
    internal static void Run(Action<bool, string> check)
    {
        var state = new KeyboardSuppressionState(256, 2000);
        state.Begin(new byte[256], HotkeyBinding.DefaultToggle, HotkeyBinding.DefaultHide, 100);
        var history = new KeyboardModifierHistory(257);
        history.Reset(100, state.CurrentModifiers);
        void Key(uint key, uint time, bool up = false, uint flags = 0)
        {
            state.Process(key, 0, flags, time, up, up ? 0x101u : 0x100u, 42, true, true, time);
            history.Record(time, time, state.CurrentModifiers);
        }
        Key(0x78, 110);
        check(state.TryDequeue(out var first), "A previous private keyboard packet can remain in flight");
        Key(0xA2, 120);
        check(state.CurrentModifiers == OverlayModifiers.Control && history.At(125, 125) == OverlayModifiers.Control,
            "Pointer modifiers include a held Ctrl even when its private packet is blocked by an older acknowledgement");
        Key(0xA2, 140, up: true);
        check(history.At(125, 145) == OverlayModifiers.Control && history.At(145, 145) == OverlayModifiers.None,
            "A queued mouse press resolves its earlier Ctrl state rather than a newer physical release");
        check(first!.Modifiers == OverlayModifiers.None && !state.TryDequeue(out _),
            "Pointer history neither mutates an older packet-local keyboard snapshot nor advances its acknowledgement");
        state.Complete(first.Sequence);
        check(state.TryDequeue(out var ctrlDown) && ctrlDown!.Modifiers == OverlayModifiers.Control,
            "Delayed keyboard commands still receive their own ordered Ctrl snapshot");
        state.Complete(ctrlDown!.Sequence);

        Key(0xA2, 150); Key(0xA3, 160, flags: 1); Key(0xA2, 170, up: true);
        check(history.At(175, 175) == OverlayModifiers.Control,
            "Releasing left Ctrl does not clear a still-held right Ctrl in pointer history");
        Key(0xA0, 180);
        check(history.At(185, 185) == (OverlayModifiers.Control | OverlayModifiers.Shift),
            "Pointer history preserves Ctrl creation together with independent Shift constraints");
        Key(0xA3, 190, up: true, flags: 1);
        check(history.At(195, 195) == OverlayModifiers.Shift,
            "Ctrl release does not clear an independent held Shift");
        Key(0xA0, 200, up: true);
        check(history.At(205, 205) == OverlayModifiers.None,
            "All modifier releases restore ordinary pointer editing");

        long beforeWrap = uint.MaxValue - 10L, afterWrap = uint.MaxValue + 20L;
        history.Reset(beforeWrap, OverlayModifiers.None);
        history.Record(uint.MaxValue - 5, beforeWrap + 5, OverlayModifiers.Control);
        history.Record(5, afterWrap, OverlayModifiers.None);
        check(history.At(uint.MaxValue - 1, afterWrap) == OverlayModifiers.Control && history.At(10, afterWrap) == OverlayModifiers.None,
            "Native message-time lookup preserves a queued Ctrl gesture across the uint tick wrap");
        long longLease = (1L << 33) + 100;
        history.Record(100, longLease, OverlayModifiers.Shift);
        check(history.At(101, longLease + 1) == OverlayModifiers.Shift,
            "A long-running editing lease resolves current native ticks after multiple wraps");

        var bounded = new KeyboardModifierHistory(3); bounded.Reset(0, OverlayModifiers.None);
        for (uint i = 1; i <= 10; i++) bounded.Record(i, i, i % 2 == 0 ? OverlayModifiers.None : OverlayModifiers.Control);
        check(bounded.At(9, 11) == OverlayModifiers.Control && bounded.At(10, 11) == OverlayModifiers.None,
            "Bounded transition retention preserves the recent input timeline after ring-buffer rollover");
        bounded.Reset(20, OverlayModifiers.Shift);
        check(bounded.At(21, 21) == OverlayModifiers.Shift,
            "A new editing lease resets the previous lease's modifier history");
    }
}
