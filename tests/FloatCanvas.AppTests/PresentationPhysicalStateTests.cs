using System.Reflection;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;

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
        Apply(Key(0x41, 30, keyFlags: 0), Key(0x41, 30, up: true, keyFlags: 0x100));
        check("an injected key-up cannot release the physically held key", Frame().PressedKeys.Count == 1);
        Apply(Key(0x41, 30, up: true, keyFlags: 0));
        check("the physical release clears after an injected duplicate was ignored", Frame().PressedKeys.Count == 0);
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
        Apply(Lock(0x14, true), Lock(0x90, true), Lock(0x91, true));
        check("password suppression hides lock indicators while retaining their current toggle state",
            Frame() is { KeyboardSuppressed: true, LockState: PresentationLockState.None });
        timeline.SetKeyboardSuppressed(false);
        check("lock-state packets populate persistent locked keys without pretending they are pressed after protection ends",
            Frame() is
            {
                LockState: PresentationLockState.CapsLock | PresentationLockState.NumLock | PresentationLockState.ScrollLock,
                PressedKeys.Count: 0
            });
        Apply(Lock(0x90, false));
        check("lock-state changes are independent from physical key make and break",
            Frame().LockState == (PresentationLockState.CapsLock | PresentationLockState.ScrollLock) && Frame().PressedKeys.Count == 0);
        LockIndicators(check);
        bool invalid = false;
        try { _ = new PresentationFrame(0, 0, TimeSpan.Zero, [new(65, "A", ScanCode: -1)]); }
        catch (ArgumentException) { invalid = true; }
        check("runtime snapshots reject invalid physical scan identity", invalid);
    }
    private static PresentationObservedInput Key(int vk, int scan, bool extended = false, bool up = false, long device = 1,
        uint keyFlags = 0) =>
        new(up ? PresentationObservedKind.KeyUp : PresentationObservedKind.KeyDown, 1000, device, vk, scan,
            (extended ? 2u : 0u) | keyFlags, "Key " + vk);
    private static PresentationObservedInput Lock(int vk, bool locked) =>
        new(PresentationObservedKind.LockStateChanged, 1000, VirtualKey: vk, IsLocked: locked);
    private static void LockIndicators(Action<string, bool> check)
    {
        var layout = PresentationKeyboardLayout.CreateDefault();
        foreach (var key in layout.Keys.Where(key => key.VirtualKey is 0x14 or 0x90 or 0x91))
            key.ShowLockIndicator = true;
        var keyboard = new CanvasElement
        {
            Kind = ElementKind.Presentation, Width = 760, Height = 260, Transform = Affine2D.Translation(20, 20),
            Style = new() { Foreground = 0xFFFFFFFF, Background = 0xFF203040, FontSize = 18 },
            Presentation = new() { DisplayStyle = PresentationDisplayStyle.Keyboard, KeyboardLayout = layout }
        };
        var document = new SceneDocument { Viewport = new(0, 0, 820, 320), Elements = [keyboard] };
        byte[] Pixels(PresentationFrame frame)
        {
            using var renderer = new SceneRenderer { PresentationFrame = frame };
            using var bitmap = SKBitmap.Decode(renderer.ExportPng(document, new AssetLibrary(), true));
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        var plain = new PresentationFrame(1, 1, TimeSpan.FromSeconds(1));
        var locks = new PresentationFrame(1, 1, TimeSpan.FromSeconds(1),
            lockState: PresentationLockState.CapsLock | PresentationLockState.NumLock | PresentationLockState.ScrollLock);
        var pressed = new PresentationFrame(1, 1, TimeSpan.FromSeconds(1),
            pressedKeys: [new(0x14, "Caps"), new(0x90, "Num"), new(0x91, "Scroll")]);
        var hidden = new PresentationFrame(1, 1, TimeSpan.FromSeconds(1), keyboardSuppressed: true,
            lockState: PresentationLockState.CapsLock | PresentationLockState.NumLock | PresentationLockState.ScrollLock);
        byte[] plainPixels = Pixels(plain), lockedPixels = Pixels(locks), pressedPixels = Pixels(pressed), hiddenPixels = Pixels(hidden);
        check("live keyboard rendering gives persistent lock indicators distinct pixels",
            !plainPixels.SequenceEqual(lockedPixels) && !pressedPixels.SequenceEqual(lockedPixels));
        check("password suppression removes the filled lock state while retaining the authored indicator", plainPixels.SequenceEqual(hiddenPixels));
        check("lock state remains transient and never enters authored presentation settings",
            !System.Text.Json.JsonSerializer.Serialize(keyboard.Presentation).Contains("LockState", StringComparison.Ordinal));
    }
    private static bool Same(PresentationFrame first, PresentationFrame second) => (bool)typeof(DesktopSession)
        .GetMethod("SameKeyboardPresentation", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [first, second])!;
}
