using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class PresentationChordSequenceTests
{
    private static readonly PresentationRuntimeDemand All = new(true, true, true, true, true, 900, 450, 800);
    private static TimeSpan Ms(long value) => TimeSpan.FromMilliseconds(value);
    private static PresentationObservedInput Key(int vk, bool down = true, long time = 0, long device = 1,
        int scan = 0, uint flags = 0, string? label = null) =>
        new(down ? PresentationObservedKind.KeyDown : PresentationObservedKind.KeyUp, time, device, vk, scan, flags,
            label ?? (PresentationTimeline.IsModifier(vk) ? vk switch
            {
                0x10 or 0xA0 or 0xA1 => "Shift", 0x11 or 0xA2 or 0xA3 => "Ctrl",
                0x12 or 0xA4 or 0xA5 => "Alt", _ => "Win"
            } : vk is >= 0x41 and <= 0x5A ? ((char)vk).ToString() : "Key " + vk));
    private static void Apply(PresentationTimeline timeline, long now, params PresentationObservedInput[] inputs)
        => timeline.Apply(new(1, inputs, null, false), All, Ms(now));
    private static PresentationFrame Frame(PresentationTimeline timeline, long now = 100)
        => timeline.Capture(1, 1, Ms(now));
    private static bool Keys(PresentationChord chord, params int[] expected)
        => chord.Keys.Select(key => key.VirtualKey).SequenceEqual(expected);

    internal static void Run(Action<string, bool> check)
    {
        ShortcutLifecycle(check);
        MainKeySequences(check);
        ModifierFamilies(check);
        DeviceOwnership(check);
        RepeatFilterAndHold(check);
        ResetAndTime(check);
    }

    private static void ShortcutLifecycle(Action<string, bool> check)
    {
        var timeline = new PresentationTimeline();
        Apply(timeline, 10, Key(0x11, time: 10));
        check("modifier down updates raw physical state without a semantic history entry",
            Frame(timeline).PressedKeys.Single().VirtualKey == 0x11 && Frame(timeline).RecentChords.Count == 0);
        Apply(timeline, 20, Key(0x43, time: 20));
        var chord = Frame(timeline).RecentChords.Single();
        check("Ctrl then C produces exactly one held Ctrl+C semantic event", chord.IsHeld && Keys(chord, 0x11, 0x43));
        Apply(timeline, 30, Key(0x43, false, 30));
        chord = Frame(timeline).RecentChords.Single();
        check("C release retains one complete shortcut instead of a new lone Ctrl entry",
            !chord.IsHeld && Keys(chord, 0x11, 0x43) && chord.Timestamp == Ms(30) && Frame(timeline).PressedKeys.Single().VirtualKey == 0x11);
        Apply(timeline, 40, Key(0x11, false, 40));
        check("final modifier release does not create or rewrite a semantic event",
            Frame(timeline).PressedKeys.Count == 0 && Frame(timeline).RecentChords.Count == 1 && Keys(Frame(timeline).RecentChords[0], 0x11, 0x43));

        timeline.Clear();
        Apply(timeline, 10, Key(0x11), Key(0x43));
        Apply(timeline, 20, Key(0x11, false, 20));
        chord = Frame(timeline).RecentChords.Single();
        check("releasing Ctrl before C closes the same shortcut without degrading it to C",
            !chord.IsHeld && Keys(chord, 0x11, 0x43) && Frame(timeline).PressedKeys.Single().VirtualKey == 0x43);
        Apply(timeline, 30, Key(0x43, false, 30));
        check("remaining main-key release does not restart the shortcut fade interval", Frame(timeline).RecentChords.Single().Timestamp == Ms(20));

        timeline.Clear();
        foreach (int vk in new[] { 0x11, 0x12, 0x10, 0x5B })
        { Apply(timeline, 10, Key(vk)); Apply(timeline, 20, Key(vk, false, 20)); }
        check("standalone Ctrl Alt Shift and Win taps stay silent in semantic history",
            Frame(timeline).PressedKeys.Count == 0 && Frame(timeline).RecentChords.Count == 0);
    }

    private static void MainKeySequences(Action<string, bool> check)
    {
        var timeline = new PresentationTimeline();
        Apply(timeline, 10, Key(0x11), Key(0x43), Key(0x56));
        var chords = Frame(timeline).RecentChords;
        check("overlapping Ctrl+C then V creates Ctrl+C and Ctrl+V without a false three-key chord",
            chords.Count == 2 && Keys(chords[0], 0x11, 0x43) && Keys(chords[1], 0x11, 0x56) && chords.All(chord => chord.IsHeld));
        Apply(timeline, 20, Key(0x43, false, 20));
        chords = Frame(timeline).RecentChords;
        check("releasing the first overlapping main key leaves the second shortcut held",
            !chords[0].IsHeld && chords[1].IsHeld && Keys(chords[1], 0x11, 0x56));

        timeline.Clear();
        Apply(timeline, 10, Key(0x43));
        Apply(timeline, 20, Key(0x11, time: 20));
        check("a modifier pressed after C does not retroactively relabel the earlier C event",
            Frame(timeline).RecentChords.Count == 1 && Keys(Frame(timeline).RecentChords[0], 0x43));
        Apply(timeline, 30, Key(0x56, time: 30));
        check("a later new main key uses the current modifiers but not the old held C",
            Keys(Frame(timeline).RecentChords[0], 0x43) && Keys(Frame(timeline).RecentChords[1], 0x11, 0x56));

        timeline.Clear();
        Apply(timeline, 10, Key(0x41), Key(0x42));
        check("overlapping ordinary keys remain separate semantic events in all-keys mode",
            Frame(timeline).RecentChords.Count == 2 && Keys(Frame(timeline).RecentChords[0], 0x41) && Keys(Frame(timeline).RecentChords[1], 0x42));
    }

    private static void ModifierFamilies(Action<string, bool> check)
    {
        var timeline = new PresentationTimeline();
        Apply(timeline, 10, Key(0xA2, scan: 29), Key(0xA3, scan: 29, flags: 2), Key(0xA0, scan: 42),
            Key(0xA1, scan: 54), Key(0xA4, scan: 56), Key(0xA5, scan: 56, flags: 2), Key(0x5B), Key(0x5C), Key(0x43));
        var frame = Frame(timeline); var chord = frame.RecentChords.Single();
        check("left and right modifier families normalize to one Ctrl Alt Shift Win cap per semantic chord",
            Keys(chord, 0x11, 0x12, 0x10, 0x5B, 0x43) && chord.Keys.Take(4).All(key => key.IsModifier));
        check("semantic normalization preserves distinct raw left and right physical keycaps",
            frame.PressedKeys.Count == 9 && frame.PressedKeys.Any(key => key.VirtualKey == 0xA2) && frame.PressedKeys.Any(key => key.VirtualKey == 0xA3));
        Apply(timeline, 20, Key(0xA2, false, 20, scan: 29), Key(0xA0, false, 20, scan: 42),
            Key(0xA4, false, 20, scan: 56), Key(0x5B, false, 20));
        check("releasing one side of every modifier family leaves the complete shortcut held",
            Frame(timeline).RecentChords.Single().IsHeld && Keys(Frame(timeline).RecentChords.Single(), 0x11, 0x12, 0x10, 0x5B, 0x43));
        Apply(timeline, 30, Key(0xA3, false, 30, scan: 29, flags: 2));
        check("releasing the final member of a required modifier family closes the shortcut as one unit",
            !Frame(timeline).RecentChords.Single().IsHeld && Frame(timeline).RecentChords.Single().Timestamp == Ms(30));

        timeline.Clear();
        Apply(timeline, 10, Key(0xA2, label: "Strg"), Key(0x43));
        check("canonical modifier identity preserves the incoming localized key label",
            Keys(Frame(timeline).RecentChords.Single(), 0x11, 0x43) && Frame(timeline).RecentChords.Single().Keys[0].Label == "Strg");
    }

    private static void DeviceOwnership(Action<string, bool> check)
    {
        var timeline = new PresentationTimeline();
        Apply(timeline, 10, Key(0x11, device: 1, scan: 29), Key(0x11, device: 2, scan: 29), Key(0x43, device: 2, scan: 46));
        Apply(timeline, 20, Key(0x11, false, 20, 1, 29));
        check("one keyboard releasing Ctrl cannot release another keyboard's modifier family",
            Frame(timeline).RecentChords.Single().IsHeld && Frame(timeline).PressedKeys.Any(key => key.VirtualKey == 0x11));
        Apply(timeline, 30, Key(0x11, false, 30, 3, 29));
        check("unknown-device key-up cannot release a known held shortcut", Frame(timeline).RecentChords.Single().IsHeld);
        Apply(timeline, 35, Key(0x11, time: 35, device: 1, scan: 29));
        timeline.Apply(new(1, [new(PresentationObservedKind.DeviceRemoved, 40, Device: 1)], null, false), All, Ms(40));
        check("removing an unrelated keyboard preserves the surviving device's shortcut", Frame(timeline).RecentChords.Single().IsHeld);
        timeline.Apply(new(1, [new(PresentationObservedKind.DeviceRemoved, 50, Device: 2)], null, false), All, Ms(50));
        check("removing the last owning device releases the complete shortcut and clears physical state",
            !Frame(timeline).RecentChords.Single().IsHeld && Frame(timeline).RecentChords.Single().Timestamp == Ms(50) && Frame(timeline).PressedKeys.Count == 0);

        timeline.Clear();
        Apply(timeline, 10, Key(0x11, scan: 29), Key(0x11, scan: 29, flags: 2), Key(0x43));
        Apply(timeline, 20, Key(0x11, false, 20, scan: 29));
        check("extended-key identity keeps the other physical modifier held", Frame(timeline).RecentChords.Single().IsHeld);

        timeline.Clear();
        Apply(timeline, 10, Key(0x43, device: 1, scan: 46), Key(0x43, device: 2, scan: 46));
        Apply(timeline, 20, Key(0x43, false, 20, 1, 46));
        check("two keyboards pressing the same main key retain separate semantic event ownership",
            Frame(timeline).RecentChords.Count == 2 && !Frame(timeline).RecentChords[0].IsHeld && Frame(timeline).RecentChords[1].IsHeld && Frame(timeline).PressedKeys.Count == 1);
    }

    private static void RepeatFilterAndHold(Action<string, bool> check)
    {
        var timeline = new PresentationTimeline();
        Apply(timeline, 10, Key(0x11), Key(0x43)); long revision = timeline.Revision;
        for (int index = 0; index < 4; index++)
            Apply(timeline, 20, Enumerable.Repeat(Key(0x43, time: 20), 250).ToArray());
        check("a thousand repeated downs do not grow semantic history or its revision",
            Frame(timeline).RecentChords.Count == 1 && timeline.Revision == revision);
        timeline.Trim(Ms(5000), All);
        check("a continuously held shortcut survives beyond the configured released hold interval",
            Frame(timeline, 5000).RecentChords.Single().IsHeld);
        Apply(timeline, 5000, Key(0x43, false, 5000)); timeline.Trim(Ms(5899), All);
        check("a long-held shortcut receives the full release fade interval", Frame(timeline, 5899).RecentChords.Count == 1);
        timeline.Trim(Ms(5900), All);
        check("the complete released shortcut expires at its release boundary", Frame(timeline, 5900).RecentChords.Count == 0);
        Apply(timeline, 6000, Key(0x43, time: 6000));
        check("repressing C while Ctrl remains held is a new Ctrl+C invocation", Keys(Frame(timeline, 6000).RecentChords.Single(), 0x11, 0x43));

        timeline.Clear(); var shortcuts = All with { AllPhysicalKeys = false };
        timeline.Apply(new(1, [Key(0x41), Key(0xA0), Key(0x42)], null, false), shortcuts, Ms(10));
        check("shortcut filtering still ignores unmodified and Shift-only ordinary letters",
            Frame(timeline).RecentChords.Count == 0 && Frame(timeline).PressedKeys.All(key => key.IsModifier));
        timeline.Apply(new(1, [Key(9, time: 20), Key(0xA2, time: 20), Key(0x43, time: 20)], null, false), shortcuts, Ms(20));
        check("named controls and Ctrl shortcuts retain the original shortcut-only admission rules",
            Frame(timeline).RecentChords.Count == 2 && Keys(Frame(timeline).RecentChords[0], 0x10, 9) && Keys(Frame(timeline).RecentChords[1], 0x11, 0x10, 0x43));
    }

    private static void ResetAndTime(Action<string, bool> check)
    {
        var timeline = new PresentationTimeline();
        Apply(timeline, 100, Key(0x11, time: 100), Key(0x43, time: 100));
        timeline.Apply(new(1, [Key(0x56, time: 90)], null, true), All, Ms(90));
        var frame = Frame(timeline, 0);
        check("overflow clears old physical modifiers and semantic history without inheriting a stale Ctrl",
            frame.PressedKeys.Count == 1 && frame.RecentChords.Count == 1 && Keys(frame.RecentChords[0], 0x56));
        check("overflow and out-of-order input cannot move semantic time backwards",
            frame.Time >= Ms(100) && frame.RecentChords[0].Timestamp >= Ms(100));
        timeline.SetKeyboardSuppressed(true);
        timeline.Apply(new(1, [Key(0x11, time: 110), Key(0x43, time: 110)], new(40, 50, 110), true), All, Ms(110));
        frame = Frame(timeline, 110);
        check("overflow preserves password suppression while continuing independent pointer input",
            frame.KeyboardSuppressed && frame.PressedKeys.Count == 0 && frame.RecentChords.Count == 0 && frame.CursorScreen == new PointD(40, 50));
        timeline.SetKeyboardSuppressed(false);
        Apply(timeline, 120, Key(0x43, false, 120));
        check("resuming suppression does not reconstruct a key from a stale break packet", Frame(timeline, 120).RecentChords.Count == 0);
        timeline.ClearKeyboard();
        Apply(timeline, 150, Key(0x41, time: 200), Key(0x42, time: 10));
        Apply(timeline, 50, Key(0x43, time: 0), Key(0x44, time: -1));
        frame = Frame(timeline, 0);
        check("future old and negative packet times remain bounded by one monotonic frame time",
            frame.Time >= Ms(150) && frame.RecentChords.Count == 3 && frame.RecentChords.All(chord => chord.Timestamp == Ms(150)));

        timeline.Clear();
        for (int index = 0; index < 40; index++)
        { int vk = 0x41 + index % 20; Apply(timeline, index, Key(vk, time: index), Key(vk, false, index)); }
        check("many semantic invocations preserve the bounded chord history and frame payload",
            Frame(timeline).RecentChords.Count == PresentationLimits.MaximumChords && Frame(timeline).PayloadBytes <= PresentationLimits.MaximumFramePayloadBytes);
        timeline.Clear();
        Apply(timeline, 10, Enumerable.Range(0x41, 30).Select(vk => Key(vk)).ToArray());
        check("held-key saturation retains bounded ownership and semantic events",
            Frame(timeline).PressedKeys.Count is > 0 and <= PresentationLimits.MaximumHeldKeys && Frame(timeline).RecentChords.Count <= PresentationLimits.MaximumChords);
    }
}
