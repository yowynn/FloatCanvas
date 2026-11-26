using System.Text.Json;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class PresentationKeyboardStylesTests
{
    internal static void Run(Action<string, bool> check)
    {
        check("recording keycaps use square letters, physical modifier proportions and compact space",
            PresentationLayout.KeycapWidthUnits(0x41) == 1 && PresentationLayout.KeycapWidthUnits(0x11) == 1.25 &&
            PresentationLayout.KeycapWidthUnits(0x10) == 2.25 && PresentationLayout.KeycapWidthUnits(0x20) == 2.25 &&
            PresentationLayout.KeycapWidthUnits(0x20) < PresentationKeyboardLayout.CreateDefault().Keys.Single(key => key.VirtualKey == 0x20).Width);
        var layer = Layer(); var scene = Scene(layer); using var renderer = new SceneRenderer();
        using var idleLog = Render(renderer, scene);
        layer.Presentation!.DisplayStyle = PresentationDisplayStyle.Keycaps; using var idleCaps = Render(renderer, scene);
        check("both log styles remain transparent without explicitly supplied transient input", Empty(idleLog) && Empty(idleCaps));
        renderer.PresentationFrame = Chord(); using var capLog = Render(renderer, scene);
        layer.Presentation.DisplayStyle = PresentationDisplayStyle.Log; using var log = Render(renderer, scene);
        check("individual realistic keycaps have distinct pixels from the preserved text log", !Empty(capLog) && !Empty(log) && !capLog.Pixels.SequenceEqual(log.Pixels));
        layer.Presentation.DisplayStyle = PresentationDisplayStyle.Keycaps;
        renderer.PresentationFrame = new(1, 2, TimeSpan.FromSeconds(8), recentChords: [new([new(0x11, "Ctrl", true), new(0x43, "C")], TimeSpan.FromSeconds(1), true)]);
        using var heldCaps = Render(renderer, scene); check("keycap log chords remain visible for the full physical hold", !Empty(heldCaps));
        renderer.PresentationFrame = new(1, 3, TimeSpan.FromSeconds(8), recentChords: [new([new(0x11, "Ctrl", true)], TimeSpan.FromSeconds(8), true)]);
        using var modifierOnly = Render(renderer, scene); check("keycap logs display modifier-only preview rows", !Empty(modifierOnly));
        foreach (var style in new[] { PresentationDisplayStyle.Log, PresentationDisplayStyle.Keycaps })
        foreach (var display in Enum.GetValues<PresentationKeyDisplayMode>())
        {
            layer.Presentation.DisplayStyle = style; layer.Presentation.KeyDisplay = display;
            renderer.PresentationFrame = new(1, 3, TimeSpan.FromSeconds(8), recentChords: [new([new(0x5B, "Win", true)], TimeSpan.FromSeconds(8), true)]);
            using var win = Render(renderer, scene);
            check(style + " " + display + " renders a standalone held Windows key", !Empty(win));
        }
        layer.Presentation.KeyDisplay = PresentationKeyDisplayMode.AllPhysicalKeys;
        DualLabels(check);
        KeyAppearance(check);
        OverlapOrder(check);
        DeviceButtons(check);
        var unitKey = new PresentationKeyboardKey();
        var wideKey = new PresentationKeyboardKey { Width = 4 };
        var largeKey = new PresentationKeyboardKey { Width = 4, Height = 4 };
        check("legend sizes follow the short edge of each key",
            PresentationLayout.KeyFontSize(unitKey, 10) == 10 && PresentationLayout.KeyFontSize(wideKey, 10) == 10 &&
            PresentationLayout.KeyFontSize(largeKey, 10) == 40);
        check("large and normal keys share the same rim profile",
            PresentationLayout.KeyEdges(new(0, 0, 91, 91), 100) ==
            PresentationLayout.KeyEdges(new(0, 0, 391, 391), 100));

        layer.Presentation.DisplayStyle = PresentationDisplayStyle.Keyboard; renderer.PresentationFrame = null;
        using var idle = Render(renderer, scene); string authored = JsonSerializer.Serialize(scene);
        check("live keyboard draws the normal full layout with no input and no serialized 104-key default", !Empty(idle) && !authored.Contains("KeyboardLayout", StringComparison.Ordinal));
        layer.Presentation.KeyPanelAlignment = PresentationPanelAlignment.TopRight;
        using var alternateAlignment = Render(renderer, scene);
        check("physical keyboard does not depend on log panel alignment", alternateAlignment.Pixels.SequenceEqual(idle.Pixels));
        layer.Presentation.KeyPanelAlignment = PresentationPanelAlignment.BottomLeft;
        renderer.PresentationFrame = new(1, 4, TimeSpan.FromSeconds(1), pressedKeys: [new(0xA2, "Ctrl", true, 0x1D, false)]);
        using var leftCtrl = Render(renderer, scene);
        renderer.PresentationFrame = new(1, 5, TimeSpan.FromSeconds(1), pressedKeys: [new(0xA3, "Ctrl", true, 0x1D, true)]);
        using var rightCtrl = Render(renderer, scene);
        check("live modifier-only input lights the correct physical side with no log history", !leftCtrl.Pixels.SequenceEqual(idle.Pixels) && !rightCtrl.Pixels.SequenceEqual(idle.Pixels) && !leftCtrl.Pixels.SequenceEqual(rightCtrl.Pixels));
        var relatedLayout = new PresentationKeyboardLayout
        {
            Keys = [PresentationKeyboardLayout.CreateDefault().Keys.Single(key => key.VirtualKey == 0xA0).Clone()]
        };
        layer.Presentation.KeyboardLayout = relatedLayout;
        renderer.PresentationFrame = null; using var exactShiftIdle = Render(renderer, scene);
        renderer.PresentationFrame = new(1, 6, TimeSpan.FromSeconds(1), pressedKeys: [new(0xA1, "Shift", true, 0x36, false)]);
        using var exactRightShift = Render(renderer, scene);
        check("an authored left Shift stays unmatched by right Shift in exact mode",
            exactRightShift.Pixels.SequenceEqual(exactShiftIdle.Pixels));
        relatedLayout.Keys[0].MatchRelatedKeys = true;
        renderer.PresentationFrame = null; using var eitherShiftIdle = Render(renderer, scene);
        renderer.PresentationFrame = new(1, 7, TimeSpan.FromSeconds(1), pressedKeys: [new(0xA1, "Shift", true, 0x36, false)]);
        using var eitherRightShift = Render(renderer, scene);
        check("related-key matching lights an authored left Shift from the right Shift",
            !eitherRightShift.Pixels.SequenceEqual(eitherShiftIdle.Pixels));
        var digitLayout = new PresentationKeyboardLayout
        {
            Keys = [PresentationKeyboardLayout.CreateDefault().Keys.Single(key => key.VirtualKey == 0x31).Clone()]
        };
        layer.Presentation.KeyboardLayout = digitLayout;
        renderer.PresentationFrame = null; using var exactDigitIdle = Render(renderer, scene);
        renderer.PresentationFrame = new(1, 8, TimeSpan.FromSeconds(1), pressedKeys: [new(0x61, "1", false, 0x4F, false)]);
        using var exactNumpadOne = Render(renderer, scene);
        digitLayout.Keys[0].MatchRelatedKeys = true;
        renderer.PresentationFrame = null; using var relatedDigitIdle = Render(renderer, scene);
        renderer.PresentationFrame = new(1, 9, TimeSpan.FromSeconds(1), pressedKeys: [new(0x61, "1", false, 0x4F, false)]);
        using var relatedNumpadOne = Render(renderer, scene);
        check("top-row and numpad digits stay separate in exact mode and match when related",
            exactNumpadOne.Pixels.SequenceEqual(exactDigitIdle.Pixels) &&
            !relatedNumpadOne.Pixels.SequenceEqual(relatedDigitIdle.Pixels));
        var lockLayout = new PresentationKeyboardLayout
        {
            Keys = [PresentationKeyboardLayout.CreateDefault().Keys.Single(key => key.VirtualKey == 0x14).Clone()]
        };
        layer.Presentation.KeyboardLayout = lockLayout;
        renderer.PresentationFrame = null; using var lockIdle = Render(renderer, scene);
        renderer.PresentationFrame = new(1, 10, TimeSpan.FromSeconds(1), lockState: PresentationLockState.CapsLock);
        using var capsLockedByDefault = Render(renderer, scene);
        lockLayout.Keys[0].ShowLockIndicator = true;
        renderer.PresentationFrame = null; using var visibleLockIdle = Render(renderer, scene);
        renderer.PresentationFrame = new(1, 11, TimeSpan.FromSeconds(1), lockState: PresentationLockState.CapsLock);
        using var capsLocked = Render(renderer, scene);
        lockLayout.Keys[0].ShowLockIndicator = false;
        renderer.PresentationFrame = new(1, 12, TimeSpan.FromSeconds(1), lockState: PresentationLockState.CapsLock);
        using var capsLockHidden = Render(renderer, scene);
        check("lock indicators default off and enabled indicators have hollow and filled states",
            capsLockedByDefault.Pixels.SequenceEqual(lockIdle.Pixels) &&
            !visibleLockIdle.Pixels.SequenceEqual(lockIdle.Pixels) &&
            !capsLocked.Pixels.SequenceEqual(visibleLockIdle.Pixels) &&
            capsLockHidden.Pixels.SequenceEqual(lockIdle.Pixels));
        layer.Presentation.KeyboardLayout = null;
        renderer.PresentationFrame = new(1, 6, TimeSpan.FromSeconds(1), pressedKeys: [new(0x0D, "Enter", false, 0x1C, false)]);
        using var mainEnter = Render(renderer, scene);
        renderer.PresentationFrame = new(1, 7, TimeSpan.FromSeconds(1), pressedKeys: [new(0x0D, "Enter", false, 0x1C, true)]);
        using var numEnter = Render(renderer, scene);
        check("main Enter and extended numpad Enter produce independent live key states", !mainEnter.Pixels.SequenceEqual(numEnter.Pixels) && !mainEnter.Pixels.SequenceEqual(idle.Pixels) && !numEnter.Pixels.SequenceEqual(idle.Pixels));
        foreach (var pair in new[] { (Numeric: 0x61, Navigation: 0x23, Scan: 0x4F), (Numeric: 0x6E, Navigation: 0x2E, Scan: 0x53) })
        {
            renderer.PresentationFrame = new(1, 7, TimeSpan.FromSeconds(1), pressedKeys: [new(pair.Numeric, "Num", false, pair.Scan)]);
            using var number = Render(renderer, scene);
            renderer.PresentationFrame = new(1, 7, TimeSpan.FromSeconds(1), pressedKeys: [new(pair.Navigation, "Nav", false, pair.Scan)]);
            using var numLockOff = Render(renderer, scene);
            renderer.PresentationFrame = new(1, 7, TimeSpan.FromSeconds(1), pressedKeys: [new(pair.Navigation, "Nav", false, pair.Scan, true)]);
            using var navigation = Render(renderer, scene);
            check("NumLock aliases light only their physical keypad key, independently of the extended navigation cluster: " + pair.Navigation,
                number.Pixels.SequenceEqual(numLockOff.Pixels) && !numLockOff.Pixels.SequenceEqual(navigation.Pixels) && !numLockOff.Pixels.SequenceEqual(idle.Pixels));
        }
        renderer.PresentationFrame = new(1, 8, TimeSpan.FromSeconds(1), pressedKeys: [new(0x41, "A")]);
        using var legacyHeld = Render(renderer, scene); check("legacy held metadata without scan identity still matches by virtual key", !legacyHeld.Pixels.SequenceEqual(idle.Pixels));
        renderer.PresentationFrame = new(1, 8, TimeSpan.FromSeconds(1), pressedKeys: [new(0x51, "Q", false, 0x1E)]);
        using var azerty = Render(renderer, scene);
        check("language-layout virtual-key changes keep the same authored physical key position", azerty.Pixels.SequenceEqual(legacyHeld.Pixels));
        renderer.PresentationFrame = new(1, 8, TimeSpan.FromSeconds(1), pressedKeys: [new(0x13, "Pause", false, 0x45)]);
        using var pause = Render(renderer, scene);
        renderer.PresentationFrame = new(1, 8, TimeSpan.FromSeconds(1), pressedKeys: [new(0x90, "Num", false, 0x45)]);
        using var numLock = Render(renderer, scene);
        check("Pause and NumLock disambiguate the shared low scan byte without lighting both", !pause.Pixels.SequenceEqual(numLock.Pixels) && !pause.Pixels.SequenceEqual(idle.Pixels) && !numLock.Pixels.SequenceEqual(idle.Pixels));
        renderer.PresentationFrame = new(1, 9, TimeSpan.FromSeconds(1), pressedKeys: [new(0x41, "A", false, 0x1E)], keyboardSuppressed: true);
        using var protectedKeyboard = Render(renderer, scene); check("privacy suppression restores every live key to normal without hiding the authored keyboard", protectedKeyboard.Pixels.SequenceEqual(idle.Pixels));
        renderer.PresentationFrame = new(1, 10, TimeSpan.FromSeconds(1));
        using var released = Render(renderer, scene); check("key release with no recent chord restores exactly the idle physical keyboard", released.Pixels.SequenceEqual(idle.Pixels));
        check("live keyboard rendering never mutates author geometry or persists its pressed state", JsonSerializer.Serialize(scene) == authored);

        var layout = PresentationKeyboardLayout.CreateDefault();
        foreach (var key in layout.Keys) key.Enabled = key.VirtualKey is 0x57 or 0x41 or 0x53 or 0x44;
        var w = layout.Keys.Single(key => key.VirtualKey == 0x57); w.X = 7; w.Y = 1; w.Width = 2; w.Height = 1.5;
        layer.Presentation.KeyboardLayout = layout; using var game = Render(renderer, scene);
        check("custom two-dimensional gaming layout renders enabled keys with custom geometry", !Empty(game) && !game.Pixels.SequenceEqual(idle.Pixels));
        foreach (var key in layout.Keys) key.Enabled = false; using var disabled = Render(renderer, scene);
        check("a layout with every key hidden draws no keyboard ink", Empty(disabled));
        layer.Presentation.KeyboardLayout = null; layer.Transform = Affine2D.Translation(180, 50).Compose(Affine2D.Rotation(8));
        using var transformed = Render(renderer, scene); check("keyboard keycaps retain their authored layer transform", !Empty(transformed) && !transformed.Pixels.SequenceEqual(idle.Pixels));
        Perspective(scene); using var perspective = Render(renderer, scene);
        check("the live physical keyboard follows the actual canvas projective mapping", !Empty(perspective) && !perspective.Pixels.SequenceEqual(transformed.Pixels));
        Budget(check);
        Overflow(check);
        TwoColorCoverage(check);
    }

    private static void DeviceButtons(Action<string, bool> check)
    {
        var layer = Layer();
        layer.Presentation!.DisplayStyle = PresentationDisplayStyle.Keyboard;
        layer.Presentation.KeyboardLayout = new()
        {
            Keys = [
                new() { VirtualKey = 0x41, Label = "A" },
                new() { DeviceType = PresentationKeyDevice.Mouse, VirtualKey = 1, Label = "Mouse", X = 1 },
                new() { DeviceType = PresentationKeyDevice.Gamepad, VirtualKey = (int)PresentationGamepadButton.A, Label = "A", X = 2 }
            ]
        };
        var scene = Scene(layer);
        using var renderer = new SceneRenderer();
        using var idle = Render(renderer, scene);
        using var key = RenderHeld(new(0x41, "A"));
        using var mouse = RenderHeld(new(1, "Mouse", DeviceType: PresentationKeyDevice.Mouse));
        using var controller = RenderHeld(new((int)PresentationGamepadButton.A, "A", DeviceType: PresentationKeyDevice.Gamepad));
        check("keyboard, mouse and controller input light different cells", !key.Pixels.SequenceEqual(idle.Pixels) &&
            !mouse.Pixels.SequenceEqual(idle.Pixels) && !controller.Pixels.SequenceEqual(idle.Pixels) &&
            !key.Pixels.SequenceEqual(controller.Pixels) && !mouse.Pixels.SequenceEqual(controller.Pixels) &&
            !mouse.Pixels.SequenceEqual(key.Pixels));

        SKBitmap RenderHeld(PresentationKeyCap cap)
        {
            renderer.PresentationFrame = new(1, 1, TimeSpan.Zero, pressedKeys: [cap]);
            return Render(renderer, scene);
        }
    }

    private static void DualLabels(Action<string, bool> check)
    {
        var layer = Layer(); layer.Width = 260; layer.Height = 260; layer.Style.FontSize = 48;
        layer.Presentation!.DisplayStyle = PresentationDisplayStyle.Keyboard;
        layer.Presentation.KeyboardLayout = new()
        {
            Keys = [new() { VirtualKey = 0x41, Label = "A", SecondaryLabel = "Shift", X = 0, Y = 0 }]
        };
        var scene = Scene(layer); using var renderer = new SceneRenderer { PresentationFrame = null };
        using var dual = Render(renderer, scene);
        layer.Presentation.KeyboardLayout.Keys[0].SecondaryLabel = null;
        using var primary = Render(renderer, scene);
        check("live keyboard canvas renders the authored secondary key label", !Empty(dual) &&
            !dual.Pixels.SequenceEqual(primary.Pixels));
    }

    private static void KeyAppearance(Action<string, bool> check)
    {
        var layer = Layer();
        layer.Width = layer.Height = 240;
        layer.Style.FontSize = 36;
        var key = new PresentationKeyboardKey { VirtualKey = 0x41, Label = "Ag" };
        layer.Presentation!.DisplayStyle = PresentationDisplayStyle.Keyboard;
        layer.Presentation.KeyboardLayout = new() { Keys = [key] };
        var scene = Scene(layer);
        using var renderer = new SceneRenderer();
        using var inherited = Render(renderer, scene);
        key.CustomStyle = new();
        using var neutral = Render(renderer, scene);
        check("an enabled neutral key style inherits the layer's original pixels", neutral.Pixels.SequenceEqual(inherited.Pixels));
        var custom = new PresentationKeyboardKeyStyle { RelativeSize = 2, FontFamily = "Consolas", Foreground = 0xFF01AB02, Background = 0xFF650243 };
        key.CustomStyle = custom;
        using var styled = Render(renderer, scene);
        check("the canvas renders both authored key colors", styled.Pixels.Contains(new SKColor(0xFF01AB02)) &&
            styled.Pixels.Contains(new SKColor(0xFF650243)));
        key.CustomStyle = custom with { RelativeSize = -2 };
        using var smaller = Render(renderer, scene);
        check("relative key font size changes the rendered text within the same layout", !smaller.Pixels.SequenceEqual(styled.Pixels));
        layer.Style.Foreground = 0xFF321DEF;
        layer.Style.Background = 0xFF990024;
        layer.Style.FontFamily = "Arial";
        key.CustomStyle = custom;
        using var overrideStillApplies = Render(renderer, scene);
        check("explicit key colors and font take precedence over layer edits", overrideStillApplies.Pixels.SequenceEqual(styled.Pixels));
        layer.Width *= 1.5;
        layer.Height *= 1.5;
        layer.Style.FontSize *= 1.5;
        using var scaled = Render(renderer, scene);
        var ink = new SKColor(0xFF01AB02);
        int originalInk = styled.Pixels.Count(pixel => pixel == ink);
        int scaledInk = scaled.Pixels.Count(pixel => pixel == ink);
        check("the relative key font scales with the keyboard layer",
            originalInk > 0 && scaledInk > originalInk * 1.7 && scaledInk < originalInk * 2.9);
        key.CustomStyle = custom with { Enabled = false };
        using var disabled = Render(renderer, scene);
        key.CustomStyle = null;
        using var fallback = Render(renderer, scene);
        check("turning off key style restores inherited rendering", disabled.Pixels.SequenceEqual(fallback.Pixels));
        key.CustomStyle = custom with { Overrides = PresentationKeyStyleOverrides.Foreground };
        using var foregroundOverride = Render(renderer, scene);
        key.CustomStyle = new() { Foreground = custom.Foreground };
        using var legacyForeground = Render(renderer, scene);
        check("one explicit override ignores the remaining stored values",
            foregroundOverride.Pixels.SequenceEqual(legacyForeground.Pixels) && !foregroundOverride.Pixels.SequenceEqual(fallback.Pixels));
    }

    private static void OverlapOrder(Action<string, bool> check)
    {
        var layer = Layer();
        layer.Width = layer.Height = 140;
        layer.Presentation!.DisplayStyle = PresentationDisplayStyle.Keyboard;
        var bottom = new PresentationKeyboardKey { VirtualKey = 0x41, Label = "A", CustomStyle = new() { Foreground = 0xFF0FAF20, Background = 0xFF0000CC } };
        var top = new PresentationKeyboardKey { VirtualKey = 0x42, Label = "B", CustomStyle = new() { Foreground = 0xFF112233, Background = 0xFFAA0011 } };
        layer.Presentation.KeyboardLayout = new() { Keys = [bottom, top] };
        var scene = Scene(layer);
        using var renderer = new SceneRenderer();
        using var idle = Render(renderer, scene);
        check("overlapping keys use their saved order when idle",
            idle.Pixels.Contains(new SKColor(0xFFAA0011)) && !idle.Pixels.Contains(new SKColor(0xFF0000CC)));
        renderer.PresentationFrame = new(1, 1, TimeSpan.Zero, pressedKeys: [new(0x41, "A")]);
        using var held = Render(renderer, scene);
        check("a held key remains visible when another key covers it in the saved layout",
            held.Pixels.Contains(new SKColor(0xFF0FAF20)) && !held.Pixels.Contains(new SKColor(0xFFAA0011)));
    }

    private static void TwoColorCoverage(Action<string, bool> check)
    {
        foreach (bool pressed in new[] { false, true })
        foreach (bool projected in new[] { false, true })
        {
            var layer = Layer(); layer.Width = 240; layer.Height = 240; layer.Style.FontSize = 72;
            layer.Presentation!.DisplayStyle = PresentationDisplayStyle.Keyboard;
            layer.Presentation.KeyboardLayout = new() { Keys = [new() { VirtualKey = 0x41, Label = "A", Width = 1, Height = 1 }] };
            var scene = Scene(layer);
            if (projected) Perspective(scene);
            string state = $"pressed={pressed}, projected={projected}";
            using var renderer = new SceneRenderer { PresentationFrame = new(1, 1, TimeSpan.Zero,
                pressedKeys: pressed ? [new(0x41, "A")] : []) };
            void Colors(uint ink)
            {
                layer.Style.Foreground = pressed ? 0xFF0000FF : ink;
                layer.Style.Background = pressed ? ink : 0xFF0000FF;
            }
            Colors(0xFFFF0000); using var reference = Render(renderer, scene);
            var glyph = reference.Pixels.Select((pixel, index) => (pixel, index))
                .Where(p => p.pixel.Red == 255 && p.pixel.Blue == 0 && p.pixel.Alpha == 255).Select(p => p.index).ToArray();
            check($"two-color {state}: reference contains solid glyph pixels", glyph.Length > 20);
            var glyphSet = glyph.ToHashSet();
            var interior = glyph.Where(index =>
            {
                int x = index % reference.Width, y = index / reference.Width;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || nx >= reference.Width || ny < 0 || ny >= reference.Height ||
                        !glyphSet.Contains(ny * reference.Width + nx)) return false;
                }
                return true;
            }).ToArray();
            check($"two-color {state}: glyph has a stable interior region", interior.Length > 20);
            foreach (uint ink in new uint[] { 0x00000000, 0x80FF0000 })
            {
                Colors(ink); using var actual = Render(renderer, scene); var pixels = actual.Pixels;
                int mismatches = interior.Count(i => Math.Abs(pixels[i].Alpha - (ink >> 24)) > 1 ||
                    pixels[i].Alpha > 1 && pixels[i].Blue >= 3);
                var worst = interior.Select(i => pixels[i]).OrderByDescending(pixel => pixel.Alpha)
                    .ThenByDescending(pixel => pixel.Blue).FirstOrDefault();
                check($"two-color {state}: glyph replaces the base at alpha {ink >> 24} (mismatches={mismatches}, worst alpha={worst.Alpha}, blue={worst.Blue})",
                    mismatches == 0);
            }
            Colors(0);
            using (var composite = new SKBitmap(reference.Width, reference.Height))
            using (var canvas = new SKCanvas(composite))
            {
                canvas.Clear(SKColors.Lime);
                renderer.Draw(canvas, scene, new(), includeBackground: false, includeCanvasBackground: false, includeGrid: false);
                var pixels = composite.Pixels;
                check($"two-color {state}: transparent glyph exposes rather than erases underlying scene",
                    interior.All(i => pixels[i].Alpha == 255 && pixels[i].Green > 250 && pixels[i].Blue < 3));
            }
            layer.Style.Foreground = 0x80FF0000; layer.Style.Background = 0x800000FF;
            using var translucent = Render(renderer, scene);
            check($"two-color {state}: antialias transitions never double the two color alphas",
                translucent.Pixels.All(p => p.Alpha <= 128));
            Colors(0xFFFF0000);
            // The inner outline is deliberately 65% opaque, but must contain no blue base.
            check($"two-color {state}: translucent outline does not accumulate base color",
                reference.Pixels.Count(p => p.Red > 250 && p.Blue < 3 && p.Alpha is >= 164 and <= 167) > 100);
        }
    }

    private static void Overflow(Action<string, bool> check)
    {
        foreach (var style in new[] { PresentationDisplayStyle.Log, PresentationDisplayStyle.Keycaps })
        {
            var layer = Layer(); layer.Width = 100; layer.Height = 160; layer.Transform = Affine2D.Translation(100, 50);
            layer.Presentation!.DisplayStyle = style; layer.Presentation.KeyPanelAlignment = PresentationPanelAlignment.TopLeft;
            var scene = Scene(layer);
            using var renderer = new SceneRenderer { PresentationFrame = new(1, 1, TimeSpan.FromSeconds(1), recentChords:
                [new([new(0x41, new string('W', 40))], TimeSpan.FromSeconds(1), true)]) };
            using var image = Render(renderer, scene);
            bool beyond = false;
            for (int y = 0; y < image.Height && !beyond; y++) for (int x = 205; x < image.Width; x++)
                if (image.GetPixel(x, y).Alpha != 0) { beyond = true; break; }
            check(style + " long keyboard record remains visible beyond the selection frame", beyond);
            check(style + " dynamic damage covers overflowing content", renderer.PreparePresentation(scene).DirtyBounds.Any(bounds => bounds.Right > 200));
            layer.Transform = Affine2D.Translation(-120, 50);
            using var offscreenFrame = Render(renderer, scene);
            check(style + " overflowing record remains visible when its frame is offscreen", !Empty(offscreenFrame));
        }
    }

    private static void Budget(Action<string, bool> check)
    {
        var layout = new PresentationKeyboardLayout { Keys = Enumerable.Range(1, 128).Select(index => new PresentationKeyboardKey {
            VirtualKey = index, ScanCode = index, Label = new string('W', 64), X = (index - 1) % 16, Y = (index - 1) / 16 }).ToList() };
        var layer = Layer(); layer.Presentation!.DisplayStyle = PresentationDisplayStyle.Keyboard; layer.Presentation.KeyboardLayout = layout;
        using var renderer = new SceneRenderer(); var scene = Scene(layer); using var pixels = Render(renderer, scene);
        check("an excessive live keyboard is skipped at its own limit", renderer.PreparePresentation(scene).KeyboardLayoutBudgetExceeded && Empty(pixels));
        var small = Layer();
        small.Presentation!.DisplayStyle = PresentationDisplayStyle.Keyboard;
        small.Presentation.KeyboardLayout = new() { Keys = [new() { VirtualKey = 0x41, Label = "A" }] };
        using var healthy = Render(renderer, Scene(small));
        scene.Elements.Add(small);
        using var mixed = Render(renderer, scene);
        check("an excessive live keyboard leaves valid layers rendered",
            mixed.Pixels.SequenceEqual(healthy.Pixels) && !renderer.PreparePresentation(scene).BudgetExceeded);
    }

    internal static void WriteEvidence(string directory)
    {
        var layer = Layer(); var scene = Scene(layer); using var renderer = new SceneRenderer { PresentationFrame = Chord() };
        foreach (var style in Enum.GetValues<PresentationDisplayStyle>())
        {
            layer.Presentation!.DisplayStyle = style;
            renderer.PresentationFrame = style == PresentationDisplayStyle.Keyboard
                ? new(1, 1, TimeSpan.FromSeconds(1), pressedKeys: [new(0xA2, "Ctrl", true, 0x1D), new(0x43, "C", false, 0x2E)])
                : Chord();
            File.WriteAllBytes(Path.Combine(directory, "style-" + style + ".png"), renderer.ExportPng(scene, new()));
        }
        var layout = PresentationKeyboardLayout.CreateDefault(); foreach (var key in layout.Keys) key.Enabled = key.VirtualKey is 0x57 or 0x41 or 0x53 or 0x44 or 0x20;
        foreach (var key in layout.Keys.Where(key => key.Enabled)) { key.X = key.VirtualKey switch { 0x57 or 0x53 or 0x20 => 1, 0x41 => 0, _ => 2 }; key.Y = key.VirtualKey switch { 0x57 => 0, 0x20 => 2, _ => 1 }; key.Width = key.VirtualKey == 0x20 ? 2 : 1; }
        layer.Presentation!.KeyboardLayout = layout; renderer.PresentationFrame = new(1, 1, TimeSpan.FromSeconds(1), pressedKeys: [new(0x57, "W", false, 0x11)]);
        File.WriteAllBytes(Path.Combine(directory, "style-Keyboard-game.png"), renderer.ExportPng(scene, new()));
        layer.Style.Foreground = 0xFF2088DD; layer.Style.Background = 0;
        renderer.PresentationFrame = new(1, 2, TimeSpan.FromSeconds(1), pressedKeys: [new(0x20, "Space")]);
        File.WriteAllBytes(Path.Combine(directory, "style-Keyboard-transparent-game.png"), renderer.ExportPng(scene, new()));
        layer.Style.Foreground = 0xFFEEF4FF; layer.Style.Background = 0xFF243247;
        renderer.PresentationFrame = new(1, 1, TimeSpan.FromSeconds(1), pressedKeys: [new(0x57, "W", false, 0x11)]);
        layer.Presentation.KeyboardLayout = null; Perspective(scene);
        File.WriteAllBytes(Path.Combine(directory, "style-Keyboard-perspective.png"), renderer.ExportPng(scene, new()));
    }
    private static void Perspective(SceneDocument scene)
    {
        var matrix = new Projective2D(1, .1, 10, .02, 1, 5, .0004, .00015, 1);
        scene.Canvas.Projection.Enabled = true; scene.Canvas.Projection.Matrix = matrix;
        scene.Canvas.Projection.Quad = new[] { new PointD(0, 0), new(960, 0), new(960, 340), new(0, 340) }.Select(matrix.TransformPoint).ToList();
    }
    private static PresentationFrame Chord() => new(1, 1, TimeSpan.FromSeconds(1), recentChords: [new([new(0x11, "Ctrl", true), new(0x43, "C")], TimeSpan.FromSeconds(1), true)]);
    private static CanvasElement Layer() => new() { Kind = ElementKind.Presentation, Width = 900, Height = 280, Transform = Affine2D.Translation(25, 25), Presentation = new(),
        Style = new() { FontSize = 24, Foreground = 0xFFEEF4FF, Background = 0xFF243247 } };
    private static SceneDocument Scene(CanvasElement layer) => new() { Elements = [layer], Viewport = new(0, 0, 960, 340) };
    private static SKBitmap Render(SceneRenderer renderer, SceneDocument scene) => SKBitmap.Decode(renderer.ExportPng(scene, new()));
    private static bool Empty(SKBitmap image) => image.Pixels.All(pixel => pixel.Alpha == 0);
}
