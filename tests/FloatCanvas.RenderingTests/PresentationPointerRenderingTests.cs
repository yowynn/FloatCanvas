using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;
using System.Text.Json;

internal static class PresentationPointerRenderingTests
{
    internal static void Run(Action<string, bool> check, string? evidence = null)
    {
        MasterAndCoordinates(check); Buttons(check); Trails(check); KeyboardSemantics(check); CanvasSeparation(check); Capacity(check);
        if (evidence is not null) WriteEvidence(evidence);
    }

    private static void MasterAndCoordinates(Action<string, bool> check)
    {
        var settings = new PresentationPointerSettings();
        var frame = new PresentationFrame(1, 1, TimeSpan.FromSeconds(1), cursorScreen: new(-1800, 330));
        var viewport = new RectD(-1920, 240, 400, 300);
        using var disabled = Render(frame, settings, viewport);
        check("global mouse master switch off produces no pixels or dirty regions", Empty(disabled) && PresentationPointerRenderer.DirtyBounds(frame, settings, viewport).Count == 0);
        settings.Enabled = true; settings.ClicksEnabled = false; settings.CursorRadiusPixels = 10;
        using var active = Render(frame, settings, viewport);
        check("global cursor uses the physical negative-monitor coordinate exactly once", active.GetPixel(120, 90).Alpha is >= 60 and <= 62 && active.GetPixel(80, 90).Alpha == 0);
        var bounds = InkBounds(active);
        check("global cursor radius is measured in screen pixels", bounds.Width is >= 20 and <= 22 && Math.Abs(bounds.Width - bounds.Height) <= 1);
        var dirty = PresentationPointerRenderer.DirtyBounds(frame, settings, viewport);
        check("global cursor damage stays in physical screen coordinates", dirty.Count == 1 && dirty[0].Contains(new(-1800, 330)) && dirty[0].Left < -1800);
        settings.CursorColor = 0x804080C0; settings.CursorOpacity = .5;
        using var alpha = Render(frame, settings, viewport);
        check("cursor color alpha is its sole opacity source and legacy opacity is not multiplied", alpha.GetPixel(120, 90).Alpha == 128);
        frame = new(1, 2, TimeSpan.FromSeconds(1), cursorScreen: new(-1921, 330));
        using var edge = Render(frame, settings, viewport);
        check("cursor support crosses the screen edge without shifting its physical center", edge.GetPixel(0, 90).Alpha > 0 && edge.GetPixel(20, 90).Alpha == 0 && PresentationPointerRenderer.DirtyBounds(frame, settings, viewport).All(rect => rect.Left >= viewport.Left));
        check("invalid or empty screen viewport has no dirty support", PresentationPointerRenderer.DirtyBounds(frame, settings, new(double.NaN, 0, 100, 100)).Count == 0 && PresentationPointerRenderer.DirtyBounds(frame, settings, default).Count == 0);
        settings.Enabled = false;
        using var stopped = Render(frame, settings, viewport);
        check("turning off the master removes all pointer pixels while retaining color and size", Empty(stopped) && settings.CursorColor == 0x804080C0 && settings.CursorRadiusPixels == 10);
    }

    private static void Buttons(Action<string, bool> check)
    {
        var settings = PointerOnly(); settings.ClicksEnabled = true;
        settings.LeftClick.Color = 0xFFFF0000; settings.RightClick.Color = 0xFF0000FF; settings.MiddleClick.Color = 0xFF00FF00;
        settings.LeftClick.StrokeWidthPixels = settings.MiddleClick.StrokeWidthPixels = 4;
        settings.RightClick.Shape = PresentationClickShape.Disc;
        var frame = new PresentationFrame(2, 1, TimeSpan.FromSeconds(1), clicks:
            [new(new(70, 70), PresentationMouseButton.Left, TimeSpan.FromSeconds(1)),
             new(new(170, 70), PresentationMouseButton.Right, TimeSpan.FromSeconds(1)),
             new(new(270, 70), PresentationMouseButton.Middle, TimeSpan.FromSeconds(1))]);
        using var pixels = Render(frame, settings);
        check("left click has its own red ring with a transparent center", pixels.GetPixel(70, 70).Alpha == 0 && pixels.GetPixel(93, 70) == SKColors.Red);
        check("right click independently uses a filled blue disc", pixels.GetPixel(170, 70) == SKColors.Blue && pixels.GetPixel(195, 70).Alpha == 0);
        check("middle click has its own green ring with a transparent center", pixels.GetPixel(270, 70).Alpha == 0 && pixels.GetPixel(293, 70) == SKColors.Lime);
        check("button damage contains three separate physical supports", PresentationPointerRenderer.DirtyBounds(frame, settings, new(0, 0, 400, 300)).Count == 3);
        settings.LeftClick.Enabled = false;
        using var leftDisabled = Render(frame, settings);
        check("disabling left-click ink preserves right and middle-click visuals", leftDisabled.GetPixel(93, 70).Alpha == 0 && leftDisabled.GetPixel(170, 70) == SKColors.Blue && leftDisabled.GetPixel(293, 70) == SKColors.Lime);
        settings.LeftClick.Enabled = true; settings.LeftClick.DurationMilliseconds = 100; settings.RightClick.DurationMilliseconds = 2000;
        frame = new(2, 2, TimeSpan.FromMilliseconds(1250), clicks: frame.Clicks);
        using var aged = Render(frame, settings);
        check("each button expires using its own configured duration", aged.GetPixel(93, 70).Alpha == 0 && aged.GetPixel(170, 70).Alpha is >= 222 and <= 224 && PresentationPointerRenderer.DirtyBounds(frame, settings, new(0, 0, 400, 300)).Count == 2);
        frame = new(2, 3, TimeSpan.FromMilliseconds(3000), clicks: frame.Clicks);
        using var expired = Render(frame, settings);
        check("exact click lifetime boundary removes every expired ring and disc", Empty(expired) && PresentationPointerRenderer.DirtyBounds(frame, settings, new(0, 0, 400, 300)).Count == 0);
        frame = new(2, 4, TimeSpan.FromSeconds(1), clicks: [new(new(100, 100), PresentationMouseButton.X1, TimeSpan.FromSeconds(1)), new(new(200, 100), PresentationMouseButton.X2, TimeSpan.FromSeconds(1))]);
        using var sides = Render(frame, settings);
        check("side buttons are never silently rendered as a configured left right or middle button", Empty(sides));
        settings.RightClick.Opacity = .5; settings.RightClick.Color = 0x800000FF;
        frame = new(2, 5, TimeSpan.FromSeconds(1), clicks: [new(new(100, 100), PresentationMouseButton.Right, TimeSpan.FromSeconds(1))]);
        using var translucent = Render(frame, settings);
        check("click color alpha is its sole opacity source and legacy opacity is not multiplied", translucent.GetPixel(100, 100).Alpha == 128);
        var fading = new PresentationFrame(2, 6, TimeSpan.FromSeconds(2), clicks: frame.Clicks);
        using var faded = Render(fading, settings);
        check("click animation fades its color alpha once after the configuration opacity was removed", faded.GetPixel(100, 100).Alpha == 64);
    }

    private static void Trails(Action<string, bool> check)
    {
        var settings = PointerOnly(); settings.TrailEnabled = true; settings.TrailColor = 0x80FF00FF; settings.TrailOpacity = .5; settings.TrailWidthPixels = 8; settings.TrailMilliseconds = 100;
        var frame = new PresentationFrame(3, 1, TimeSpan.FromSeconds(1), trail:
            [new(new(20, 100), TimeSpan.FromMilliseconds(800)), new(new(220, 100), TimeSpan.FromMilliseconds(1000))]);
        using var cropped = Render(frame, settings);
        check("trail clips its stale time-cutoff endpoint instead of drawing the full old segment", cropped.GetPixel(80, 100).Alpha == 0 && cropped.GetPixel(180, 100).Alpha is >= 127 and <= 128);
        check("trail color and width remain independently configurable", cropped.GetPixel(180, 100).Red == 255 && cropped.GetPixel(180, 100).Blue == 255 && cropped.GetPixel(180, 106).Alpha == 0);
        using var fadedTrail = Render(new(3, 5, TimeSpan.FromMilliseconds(1050), trail: frame.Trail), settings);
        check("trail animation fades its configured color alpha without a second legacy opacity factor", fadedTrail.GetPixel(200, 100).Alpha == 64);
        frame = new(3, 2, TimeSpan.FromSeconds(1), trail:
            [new(new(20, 100), TimeSpan.FromMilliseconds(900)), new(new(220, 100), TimeSpan.FromMilliseconds(1000), true)]);
        using var discontinuity = Render(frame, settings);
        check("trail segment reset never joins two disconnected pointer runs", Empty(discontinuity));
        settings.TrailMilliseconds = 500;
        frame = new(3, 3, TimeSpan.FromSeconds(1), trail:
            [new(new(-2_000_000_000, 120), TimeSpan.FromMilliseconds(900)), new(new(2_000_000_000, 120), TimeSpan.FromMilliseconds(1000))]);
        using var huge = Render(frame, settings);
        check("huge physical trail endpoints are clipped before conversion to drawing floats", huge.GetPixel(200, 120).Alpha > 0 && PresentationPointerRenderer.DirtyBounds(frame, settings, new(0, 0, 400, 300)).Single().Width <= 400);
        frame = new(3, 4, TimeSpan.FromMilliseconds(1500), trail: frame.Trail);
        using var expired = Render(frame, settings);
        check("expired trail has no lingering pixels or damage support", Empty(expired) && PresentationPointerRenderer.DirtyBounds(frame, settings, new(0, 0, 400, 300)).Count == 0);
    }

    private static void KeyboardSemantics(Action<string, bool> check)
    {
        var layer = Keyboard(); var scene = new SceneDocument { Viewport = new(0, 0, 400, 300), Elements = [layer] };
        var ctrl = new PresentationKeyCap(0x11, "Ctrl", true); var copy = new PresentationKeyCap(0x43, "C");
        using var renderer = new SceneRenderer();
        renderer.PresentationFrame = new(4, 1, TimeSpan.FromSeconds(1), pressedKeys: [ctrl]);
        using var modifier = Scene(renderer, scene);
        check("pressing only Ctrl produces no independent keyboard history row", Empty(modifier));
        renderer.PresentationFrame = new(4, 2, TimeSpan.FromSeconds(1), pressedKeys: [ctrl, copy], recentChords: [new([ctrl, copy], TimeSpan.FromSeconds(1), true)]);
        using var combined = Scene(renderer, scene); var combinedBounds = InkBounds(combined);
        renderer.PresentationFrame = new(4, 3, TimeSpan.FromSeconds(1), recentChords: [new([ctrl, copy], TimeSpan.FromSeconds(1), true)]);
        using var semanticOnly = Scene(renderer, scene);
        check("physical held-state data cannot add a duplicate row beside the semantic Ctrl+C", combined.Bytes.SequenceEqual(semanticOnly.Bytes) && !Empty(combined));
        renderer.PresentationFrame = new(4, 4, TimeSpan.FromMilliseconds(1100), pressedKeys: [ctrl], recentChords: [new([ctrl, copy], TimeSpan.FromMilliseconds(1100))]);
        using var releasedMain = Scene(renderer, scene);
        check("releasing C keeps Ctrl+C as one row instead of appending remaining Ctrl", InkBounds(releasedMain) == combinedBounds);
        renderer.PresentationFrame = new(4, 5, TimeSpan.FromSeconds(4), pressedKeys: [ctrl, copy], recentChords: [new([ctrl, copy], TimeSpan.FromSeconds(1), true)]);
        using var held = Scene(renderer, scene);
        check("a long-held semantic shortcut stays visible beyond its release-fade duration", held.Bytes.SequenceEqual(semanticOnly.Bytes));
        renderer.PresentationFrame = new(4, 6, TimeSpan.FromSeconds(4), recentChords: [new([ctrl, copy], TimeSpan.FromMilliseconds(3900))]);
        using var fading = Scene(renderer, scene);
        check("releasing a shortcut fades its complete row", !Empty(fading) && fading.GetPixel(65, 190).Alpha < combined.GetPixel(65, 190).Alpha);
        renderer.PresentationFrame = new(4, 7, TimeSpan.FromSeconds(5), recentChords: [new([ctrl, copy], TimeSpan.FromMilliseconds(3900))]);
        using var faded = Scene(renderer, scene);
        check("released shortcut expires without an orphan modifier row", Empty(faded));
        layer.Presentation!.KeyDisplay = PresentationKeyDisplayMode.AllPhysicalKeys;
        renderer.PresentationFrame = new(4, 8, TimeSpan.FromSeconds(5), recentChords: [new([ctrl], TimeSpan.FromSeconds(5), true)]);
        using var malformedModifierOnly = Scene(renderer, scene);
        check("even all-physical display does not render modifier-only semantic fragments", Empty(malformedModifierOnly));
    }

    private static void CanvasSeparation(Action<string, bool> check)
    {
        var settings = PointerOnly(); settings.CursorEnabled = true; settings.CursorOpacity = 1; settings.CursorColor = 0xFFFF0000;
        var layer = Keyboard(); var scene = new SceneDocument { Viewport = new(0, 0, 400, 300), Elements = [layer] };
        var frame = new PresentationFrame(5, 1, TimeSpan.FromSeconds(1), recentChords: [new([new(0x11, "Ctrl", true), new(0x43, "C")], TimeSpan.FromSeconds(1), true)], cursorScreen: new(330, 240));
        using var renderer = new SceneRenderer { PresentationFrame = frame };
        using var flat = Scene(renderer, scene);
        var h = new Projective2D(1, .1, 20, .05, 1, 10, .0015, .0006, 1);
        scene.Canvas.Projection.Enabled = true; scene.Canvas.Projection.Matrix = h;
        scene.Canvas.Projection.Quad = [new(0, 0), new(400, 0), new(400, 300), new(0, 300)];
        using var projected = Scene(renderer, scene);
        check("keyboard display remains real canvas content and follows the projective mapping", !Empty(projected) && !projected.Bytes.SequenceEqual(flat.Bytes) && InkBounds(projected) != InkBounds(flat));
        var point = CanvasMapping.Create(scene).ElementToScreen(layer).TransformPoint(new(20, 140));
        check("perspective keyboard panel has pixels at the mapped local panel position", projected.GetPixel((int)point.X, (int)point.Y).Alpha > 0);
        using var composite = SKSurface.Create(new SKImageInfo(400, 300, SKColorType.Bgra8888, SKAlphaType.Premul)); composite.Canvas.Clear(SKColors.Transparent);
        renderer.Draw(composite.Canvas, scene, new()); PresentationPointerRenderer.Draw(composite.Canvas, frame, settings, new(0, 0, 400, 300));
        using var compositeImage = composite.Snapshot(); using var pixels = SKBitmap.FromImage(compositeImage);
        check("global pointer is still visible outside the keyboard layer and its projected quadrilateral", pixels.GetPixel(330, 240) == SKColors.Red);
        scene.Viewport = new(0, 0, 100, 80); scene.Canvas.Projection.Matrix = Projective2D.Identity; layer.Transform = Affine2D.Translation(1000, 1000);
        composite.Canvas.Clear(SKColors.Transparent); renderer.Draw(composite.Canvas, scene, new()); PresentationPointerRenderer.Draw(composite.Canvas, frame, settings, new(0, 0, 400, 300));
        using var movedImage = composite.Snapshot(); using var moved = SKBitmap.FromImage(movedImage);
        check("canvas viewport and keyboard placement cannot clip or move the screen-plane pointer", moved.GetPixel(330, 240) == pixels.GetPixel(330, 240) && moved.GetPixel(350, 240).Alpha == 0);
    }

    private static void Capacity(Action<string, bool> check)
    {
        var settings = PointerOnly(); settings.CursorEnabled = settings.ClicksEnabled = settings.TrailEnabled = true;
        var frame = new PresentationFrame(6, 1, TimeSpan.FromSeconds(1), cursorScreen: new(200, 150),
            clicks: Enumerable.Range(0, PresentationLimits.MaximumClicks).Select(i => new PresentationClick(new(50 + i * 8, 90), (PresentationMouseButton)(i % 3), TimeSpan.FromMilliseconds(900))),
            trail: Enumerable.Range(0, PresentationLimits.MaximumTrailPoints).Select(i => new PresentationTrailSample(new(20 + i % 360, 180 + i % 20), TimeSpan.FromMilliseconds(500 + i * .9))));
        string before = JsonSerializer.Serialize(frame); var dirty = PresentationPointerRenderer.DirtyBounds(frame, settings, new(0, 0, 400, 300));
        using var pixels = Render(frame, settings);
        check("maximum input payload yields a bounded pointer primitive plan", dirty.Count <= PresentationLimits.MaximumClicks + PresentationLimits.MaximumTrailPoints && dirty.Count < PresentationLimits.MaximumPrimitives && !Empty(pixels));
        check("all pointer damage is bounded by the physical output viewport", dirty.All(rect => rect.Left >= 0 && rect.Top >= 0 && rect.Right <= 400 && rect.Bottom <= 300));
        check("pointer planning and drawing do not mutate immutable input samples or preferences", JsonSerializer.Serialize(frame) == before && settings.IsValid());
        settings.LeftClick.DurationMilliseconds = double.NaN; bool rejected = false;
        try { PresentationPointerRenderer.DirtyBounds(frame, settings, new(0, 0, 400, 300)); } catch (ArgumentException) { rejected = true; }
        check("invalid hidden button values fail before constructing drawing primitives", rejected);
    }

    internal static void WriteEvidence(string directory)
    {
        Directory.CreateDirectory(directory); var settings = new PresentationPointerSettings { Enabled = true, TrailEnabled = true };
        settings.RightClick.Shape = PresentationClickShape.Disc;
        var time = TimeSpan.FromSeconds(1); var frame = new PresentationFrame(9, 1, time, cursorScreen: new(330, 140),
            clicks: [new(new(80, 80), PresentationMouseButton.Left, time), new(new(180, 80), PresentationMouseButton.Right, time), new(new(280, 80), PresentationMouseButton.Middle, time)],
            trail: Enumerable.Range(0, 25).Select(i => new PresentationTrailSample(new(50 + i * 12, 180 + Math.Sin(i * .3) * 35), TimeSpan.FromMilliseconds(500 + i * 20))));
        using var active = Render(frame, settings); Save(active, Path.Combine(directory, "global-pointer-buttons-trail.png"));
        settings.Enabled = false; using var stopped = Render(frame, settings); Save(stopped, Path.Combine(directory, "global-pointer-disabled.png"));
        var layer = Keyboard(); var scene = new SceneDocument { Viewport = new(0, 0, 400, 300), Elements = [layer] };
        scene.Canvas.Projection.Enabled = true; scene.Canvas.Projection.Matrix = new(1, .1, 20, .05, 1, 10, .0015, .0006, 1);
        scene.Canvas.Projection.Quad = [new(0, 0), new(400, 0), new(400, 300), new(0, 300)];
        using var renderer = new SceneRenderer { PresentationFrame = new(9, 2, time, recentChords: [new([new(0x11, "Ctrl", true), new(0x43, "C")], time, true)]) };
        using var keyboard = Scene(renderer, scene); Save(keyboard, Path.Combine(directory, "keyboard-perspective-ctrl-c.png"));
    }

    private static CanvasElement Keyboard() => new() { Kind = ElementKind.Presentation, Width = 200, Height = 160, Transform = Affine2D.Translation(50, 50),
        Presentation = new() { Modules = PresentationModules.Keys }, Style = new() { Foreground = 0xFFFFFFFF, Background = 0xD0203040, FontSize = 20 } };
    private static PresentationPointerSettings PointerOnly() => new() { Enabled = true, CursorEnabled = false, ClicksEnabled = false, TrailEnabled = false };
    private static SKBitmap Scene(SceneRenderer renderer, SceneDocument scene) => SKBitmap.Decode(renderer.ExportPng(scene, new()));
    private static SKBitmap Render(PresentationFrame frame, PresentationPointerSettings settings) => Render(frame, settings, new(0, 0, 400, 300));
    private static SKBitmap Render(PresentationFrame frame, PresentationPointerSettings settings, RectD viewport)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)viewport.Width, (int)viewport.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent); surface.Canvas.Translate((float)-viewport.Left, (float)-viewport.Top);
        PresentationPointerRenderer.Draw(surface.Canvas, frame, settings, viewport);
        using var image = surface.Snapshot(); return SKBitmap.FromImage(image);
    }
    private static void Save(SKBitmap bitmap, string path) { using var image = SKImage.FromBitmap(bitmap); using var bytes = image.Encode(SKEncodedImageFormat.Png, 100); File.WriteAllBytes(path, bytes.ToArray()); }
    private static bool Empty(SKBitmap bitmap) => bitmap.Pixels.All(pixel => pixel.Alpha == 0);
    private static RectD InkBounds(SKBitmap bitmap)
    {
        int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
        for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++) if (bitmap.GetPixel(x, y).Alpha > 0)
        { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
        return right < left ? default : new(left, top, right - left + 1, bottom - top + 1);
    }
}
