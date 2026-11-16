using System.Reflection;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class PresentationPhysicalStateTests
{
    internal static void Run(Action<string, bool> check)
    {
        var demand = new PresentationRuntimeDemand(true, false, false, false, true, 900, 450, 800);
        var timeline = new PresentationTimeline(); long now = 1000;
        void Apply(params PresentationObservedInput[] keys) => timeline.Apply(new(1, keys, null, false), demand, TimeSpan.FromMilliseconds(now++));
        PresentationFrame Frame() => timeline.Capture(1, now, TimeSpan.FromMilliseconds(now));
        Apply(Key(0xA2, 29), Key(0xA3, 29, extended: true), Key(13, 28), Key(13, 28, extended: true));
        var held = Frame();
        check("virtual keyboard state preserves both Enter keys and both Control physical identities", held.PressedKeys.Count == 4 &&
            held.PressedKeys.Count(key => key.VirtualKey == 13) == 2 && held.PressedKeys.Any(key => key.VirtualKey == 0xA2 && !key.IsExtended) && held.PressedKeys.Any(key => key.VirtualKey == 0xA3 && key.IsExtended));
        Apply(Key(13, 28, up: true));
        check("releasing main Enter leaves numpad Enter pressed", Frame().PressedKeys.Any(key => key.VirtualKey == 13 && key.IsExtended) && !Frame().PressedKeys.Any(key => key.VirtualKey == 13 && !key.IsExtended));
        Apply(Key(0xA2, 29, up: true));
        check("releasing left Control leaves right Control pressed", Frame().PressedKeys.Any(key => key.VirtualKey == 0xA3) && !Frame().PressedKeys.Any(key => key.VirtualKey == 0xA2));
        timeline.Clear();
        Apply(Key(0xA2, 29));
        var modifier = Frame();
        check("a modifier-only press has live physical state and a held log preview", modifier.PressedKeys.Count == 1 && modifier.RecentChords is [{ IsHeld: true }]);
        check("modifier-only transitions invalidate the keyboard preview", !Same(PresentationFrame.Empty, modifier));
        Apply(Key(0xA2, 29, up: true));
        check("modifier-only release invalidates the keyboard preview", !Same(modifier, Frame()));
        check("equal physical snapshots do not redraw merely because sequence or time changes", Same(Frame(), new(1, now + 1, TimeSpan.FromMilliseconds(now + 1), recentChords: Frame().RecentChords)));
        timeline.Clear();
        Apply(Key(0x41, 30, device: 1), Key(0x41, 30, device: 2));
        check("the same physical position on two devices has one visible key state", Frame().PressedKeys.Count == 1);
        Apply(Key(0x41, 30, device: 1, up: true));
        check("one device release cannot clear another device's held key", Frame().PressedKeys.Count == 1);
        Apply(Key(0x41, 30, device: 2, up: true));
        check("the physical position clears after all devices release", Frame().PressedKeys.Count == 0);
        Apply(Key(0x61, 79));
        Apply(Key(0x23, 79, up: true));
        check("NumLock navigation aliases release the same physical numpad key", Frame().PressedKeys.Count == 0 && Frame().RecentChords.All(chord => !chord.IsHeld));
        Apply(Key(0x51, 30));
        Apply(Key(0x41, 30, up: true));
        check("a keyboard layout change between make and break cannot leave a physical key held", Frame().PressedKeys.Count == 0);
        timeline.Clear();
        var full = PresentationKeyboardLayout.CreateDefault().Keys;
        Apply(full.Select(key => Key(key.VirtualKey, key.ScanCode, extended: key.IsExtended)).ToArray());
        check("a full keyboard snapshot is not truncated at the former sixteen-key limit", Frame().PressedKeys.Count == full.Count && full.Count == 104 && Frame().PayloadBytes <= PresentationLimits.MaximumFramePayloadBytes);
        timeline.SetKeyboardSuppressed(true);
        check("password suppression removes live held state as well as semantic history", Frame().PressedKeys.Count == 0 && Frame().RecentChords.Count == 0 && Frame().KeyboardSuppressed);
        bool invalid = false;
        try { _ = new PresentationFrame(0, 0, TimeSpan.Zero, [new(65, "A", ScanCode: -1)]); }
        catch (ArgumentException) { invalid = true; }
        check("runtime snapshots reject invalid physical scan identity", invalid);
    }
    private static PresentationObservedInput Key(int vk, int scan, bool extended = false, bool up = false, long device = 1) =>
        new(up ? PresentationObservedKind.KeyUp : PresentationObservedKind.KeyDown, 1000, device, vk, scan, extended ? 2u : 0u, "Key " + vk);
    private static bool Same(PresentationFrame first, PresentationFrame second) => (bool)typeof(DesktopSession)
        .GetMethod("SameKeyboardPresentation", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [first, second])!;
}
