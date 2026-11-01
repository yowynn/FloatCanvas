using System.Text.Json;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class PresentationKeyboardStylesTests
{
    internal static void Run(Action<string, bool> check)
    {
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
        using var modifierOnly = Render(renderer, scene); check("keycap logs do not add modifier-only or raw-pressed rows", Empty(modifierOnly));

        layer.Presentation.DisplayStyle = PresentationDisplayStyle.Keyboard; renderer.PresentationFrame = null;
        using var idle = Render(renderer, scene); string authored = JsonSerializer.Serialize(scene);
        check("live keyboard draws the normal full layout with no input and no serialized 104-key default", !Empty(idle) && !authored.Contains("KeyboardLayout", StringComparison.Ordinal));
        renderer.PresentationFrame = new(1, 4, TimeSpan.FromSeconds(1), pressedKeys: [new(0xA2, "Ctrl", true, 0x1D, false)]);
        using var leftCtrl = Render(renderer, scene);
        renderer.PresentationFrame = new(1, 5, TimeSpan.FromSeconds(1), pressedKeys: [new(0xA3, "Ctrl", true, 0x1D, true)]);
        using var rightCtrl = Render(renderer, scene);
        check("live modifier-only input lights the correct physical side with no log history", !leftCtrl.Pixels.SequenceEqual(idle.Pixels) && !rightCtrl.Pixels.SequenceEqual(idle.Pixels) && !leftCtrl.Pixels.SequenceEqual(rightCtrl.Pixels));
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
    }

    private static void Budget(Action<string, bool> check)
    {
        var layout = new PresentationKeyboardLayout { Keys = Enumerable.Range(1, 128).Select(index => new PresentationKeyboardKey {
            VirtualKey = index, ScanCode = index, Label = new string('W', 64), X = (index - 1) % 16, Y = (index - 1) / 16 }).ToList() };
        var layer = Layer(); layer.Presentation!.DisplayStyle = PresentationDisplayStyle.Keyboard; layer.Presentation.KeyboardLayout = layout;
        using var renderer = new SceneRenderer(); var scene = Scene(layer); using var pixels = Render(renderer, scene);
        check("live keyboard primitive budget includes every static key body and shaped label and clears on overflow", renderer.PreparePresentation(scene).BudgetExceeded && Empty(pixels));
    }

    internal static void WriteEvidence(string directory)
    {
        var layer = Layer(); var scene = Scene(layer); using var renderer = new SceneRenderer { PresentationFrame = Chord() };
        foreach (var style in Enum.GetValues<PresentationDisplayStyle>())
        {
            layer.Presentation!.DisplayStyle = style;
            renderer.PresentationFrame = style == PresentationDisplayStyle.Keyboard ? new(1, 1, TimeSpan.FromSeconds(1), pressedKeys: [new(0xA2, "Ctrl", true, 0x1D), new(0x43, "C", false, 0x2E)]) : Chord();
            File.WriteAllBytes(Path.Combine(directory, "style-" + style + ".png"), renderer.ExportPng(scene, new()));
        }
        var layout = PresentationKeyboardLayout.CreateDefault(); foreach (var key in layout.Keys) key.Enabled = key.VirtualKey is 0x57 or 0x41 or 0x53 or 0x44 or 0x20;
        foreach (var key in layout.Keys.Where(key => key.Enabled)) { key.X = key.VirtualKey switch { 0x57 or 0x53 or 0x20 => 1, 0x41 => 0, _ => 2 }; key.Y = key.VirtualKey switch { 0x57 => 0, 0x20 => 2, _ => 1 }; key.Width = key.VirtualKey == 0x20 ? 2 : 1; }
        layer.Presentation!.KeyboardLayout = layout; renderer.PresentationFrame = new(1, 1, TimeSpan.FromSeconds(1), pressedKeys: [new(0x57, "W", false, 0x11)]);
        File.WriteAllBytes(Path.Combine(directory, "style-Keyboard-game.png"), renderer.ExportPng(scene, new()));
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
