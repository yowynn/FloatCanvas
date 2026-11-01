using FloatCanvas.Windows;

namespace FloatCanvas.WindowsTests;

internal static class PresentationInputTapTests
{
    internal static void Run(Action<bool, string> check)
    {
        var tap = new PresentationInputTap();
        check(tap.Keyboard(0x43, 46, 0, false, 1, true) == 0 && tap.Pointer(0x201, 10, 20, 1, true) == 0,
            "Editing presentation tap collects nothing before configuration");
        tap.Configure(true, true, 91); uint generation = tap.Generation;
        uint first = tap.Keyboard(0x11, 29, 0, false, 10, true);
        uint next = tap.Keyboard(0x43, 46, 0, false, 11, true);
        tap.Keyboard(0x43, 46, 0x80, true, 12, true); tap.Keyboard(0x11, 29, 0x80, true, 13, true);
        check(first == generation && next == 0, "Editing physical transitions coalesce to one UI notification");
        check(tap.TryDrain(generation, out var chord) && chord!.Epoch == 91 && chord.Changes.Count == 4,
            "The editing tap carries every physical Ctrl+C edge with the exact route token");
        check(chord!.Changes.Select(key => key.Kind).SequenceEqual(new[] { PresentationObservedKind.KeyDown,
            PresentationObservedKind.KeyDown, PresentationObservedKind.KeyUp, PresentationObservedKind.KeyUp }),
            "The editing tap does not create a synthetic chord or lose matching releases");
        check(chord.Changes[0].VirtualKey == 0xA2 && chord.Changes[0].KeyLabel == "Ctrl" && chord.Changes[1].KeyLabel == "C" &&
            chord.Changes.All(key => key.Device == 0), "Editing keys preserve canonical side and scan identity with metadata labels");
        check(!tap.TryDrain(generation, out _), "An editing input batch is not replayed after drain");

        tap.Keyboard(0x11, 29, 1, false, 20, true); tap.Keyboard(0x11, 29, 0x81, true, 21, true);
        tap.Keyboard(0x10, 54, 0, false, 22, true); tap.Keyboard(0x10, 54, 0x80, true, 23, true);
        tap.TryDrain(generation, out var sides);
        check(sides!.Changes.Take(2).All(key => key.VirtualKey == 0xA3 && key.KeyFlags == 2 && key.ScanCode == 29),
            "Extended right Control make and break keep one physical identity");
        check(sides.Changes.Skip(2).All(key => key.VirtualKey == 0xA1 && key.KeyFlags == 0 && key.ScanCode == 54),
            "Right Shift comes from its physical scan code in both directions");
        foreach (uint message in new uint[] { 0x201, 0x202, 0x204, 0x205, 0x207, 0x208 })
            tap.Pointer(message, -120, 480, 30, true);
        tap.TryDrain(generation, out var mouse);
        check(mouse!.Changes.Count == 6 && mouse.Changes.Select(input => input.Button).SequenceEqual(new[] { 0, 0, 1, 1, 2, 2 }),
            "Editing mouse tap keeps separate left right and middle button pairs");
        check(mouse.Changes.All(input => input.HasPosition && input.X == -120 && input.Y == 480) &&
            mouse.Changes.Where((_, index) => index % 2 == 0).All(input => input.Kind == PresentationObservedKind.ButtonDown) &&
            mouse.Changes.Where((_, index) => index % 2 == 1).All(input => input.Kind == PresentationObservedKind.ButtonUp),
            "Editing mouse transitions retain physical screen positions and matching releases");
        check(tap.Keyboard(0x43, 46, 0, false, 40, false) == 0 && tap.Pointer(0x201, 1, 2, 40, false) == 0 &&
            !tap.TryDrain(generation, out _), "An inactive editing lease cannot collect keyboard or pointer data");
        check(tap.Keyboard(255, 46, 0, false, 40, true) == 0 && tap.Keyboard(0x43, 255, 0, false, 40, true) == 0 &&
            tap.Pointer(0x20A, 1, 2, 40, true) == 0, "The tap rejects unmapped keyboard packets and unrelated mouse messages");

        tap.Keyboard(0x43, 46, 0, false, 50, true);
        tap.Configure(true, true, 92); uint nextGeneration = tap.Generation;
        tap.Keyboard(0x56, 47, 0, false, 51, true);
        check(generation != nextGeneration && !tap.TryDrain(generation, out _),
            "A stale posted notification cannot drain data from a new route generation");
        check(tap.TryDrain(nextGeneration, out var current) && current!.Epoch == 92 && current.Changes is [{ VirtualKey: 0x56 }],
            "Route changes discard pending old keys while preserving the new batch token");
        tap.Keyboard(0x43, 46, 0, false, 60, true); tap.Configure(true, true, 92);
        check(tap.Generation == nextGeneration && tap.TryDrain(nextGeneration, out _),
            "Repeated identical configuration does not clear in-flight editing data");
        tap.Configure(false, true, 92); generation = tap.Generation;
        check(tap.Keyboard(0x43, 46, 0, false, 70, true) == 0 && tap.Pointer(0x201, 1, 2, 70, true) == generation,
            "Keyboard and pointer tap demand are independently configurable");
        tap.Configure(false, false, 92);
        check(!tap.TryDrain(generation, out _) && !tap.TryDrain(tap.Generation, out _),
            "Disabling the tap discards pending data and invalidates its notification");
        tap.Configure(true, false, long.MaxValue); generation = tap.Generation;
        for (int index = 0; index < 513; index++) tap.Keyboard(0x43, 46, 0, false, 80 + index, true);
        check(tap.TryDrain(generation, out var overflow) && overflow!.Epoch == long.MaxValue && overflow.Overflowed && overflow.Changes.Count == 1,
            "Editing input uses the existing bounded overflow contract and retains a full-width route token");
        tap.Keyboard(0x43, 46, 0, false, 1000, true); tap.Clear();
        check(!tap.TryDrain(generation, out _) && !tap.TryDrain(tap.Generation, out _),
            "Lease reset clears pending input and invalidates earlier posted notifications");
        bool invalid = false; try { tap.Configure(true, true, -1); } catch (ArgumentOutOfRangeException) { invalid = true; }
        check(invalid, "Editing tap rejects negative route tokens");
    }
}
