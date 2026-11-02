using System.Text.Json;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class PresentationRenderingTests
{
    internal static void WriteEvidence(string directory)
    {
        Directory.CreateDirectory(directory);
        PresentationKeyboardStylesTests.WriteEvidence(directory);
        var region = Region(); region.Width = 880; region.Height = 510; region.Transform = Affine2D.Translation(60, 50);
        region.Style.FontSize = 28; region.Style.Foreground = 0xFF7DD3FC; region.Style.Background = 0xE0203048;
        region.Presentation!.KeyHoldMilliseconds = 1800; region.Presentation.TrailMilliseconds = 1300;
        var scene = Scene(region); scene.Viewport = new(0, 0, 1000, 620); scene.Canvas.BackgroundColor = 0xFF101827;
        var time = TimeSpan.FromSeconds(8);
        using var renderer = new SceneRenderer { PresentationFrame = new(40, 1, time,
            pressedKeys: [new(0x11, "Ctrl", true), new(0x10, "Shift", true), new(0x47, "G")],
            recentChords: [new([new(0x11, "Ctrl", true), new(0x43, "C")], TimeSpan.FromMilliseconds(7500)),
                          new([new(0x12, "Alt", true), new(0x09, "Tab")], TimeSpan.FromMilliseconds(7800)),
                          new([new(0x11, "Ctrl", true), new(0x10, "Shift", true), new(0x47, "G")], time, true)],
            cursorScreen: new(650, 240), clicks: [new(new(650, 240), PresentationMouseButton.Left, TimeSpan.FromMilliseconds(7800))],
            trail: Enumerable.Range(0, 24).Select(index => new PresentationTrailSample(new(350 + index * 13, 220 + Math.Sin(index * .24) * 90),
                TimeSpan.FromMilliseconds(7200 + index * 34)))) };
        File.WriteAllBytes(Path.Combine(directory, "presentation-flat.png"), renderer.ExportPng(scene, new()));
        region.Width = 320; region.Height = 430; region.Transform = Affine2D.Translation(220, 50); region.Style.FontSize = 22;
        scene.Viewport = new(0, 0, 760, 620); scene.Canvas.Projection.Enabled = true;
        scene.Canvas.Projection.Matrix = new(1, 0, 0, 0, -.4, 700, 0, -.002, 1);
        scene.Canvas.Projection.Quad = [new(0, 300), new(700, 300), new(700, 620), new(0, 620)];
        var mapping = CanvasMapping.Create(scene);
        if (!BillboardGeometry.TryResolveScreenBottom(region, mapping, ElementGeometry.LocalToPlane(region), out var pose, out _)) throw new InvalidOperationException("billboard evidence");
        region.IsBillboard = true; region.Transform = pose.Linear; region.BillboardAnchor = pose.Anchor; region.BillboardHeight = pose.Height;
        renderer.PresentationFrame = new(40, 2, time, recentChords: [new([new(0x11, "Ctrl", true), new(0x53, "S")], time, true)], cursorScreen: new(400, 125),
            clicks: [new(new(400, 125), PresentationMouseButton.Right, TimeSpan.FromMilliseconds(7850))],
            trail: [new(new(280, 90), TimeSpan.FromMilliseconds(7600)), new(new(330, 115), TimeSpan.FromMilliseconds(7800)), new(new(400, 125), time)]);
        File.WriteAllBytes(Path.Combine(directory, "presentation-billboard-sky.png"), renderer.ExportPng(scene, new()));
    }

    internal static void Run(Action<string, bool> check)
    {
        PresentationKeyboardStylesTests.Run(check);
        LegacyKeyboardFlags(check); AuthoredScopes(check); InertAndPicking(check); LegacyPointerLayers(check); Keys(check); Instances(check); NestedRegion(check); Composition(check); Budget(check); InvalidRange(check);
    }

    private static void AuthoredScopes(Action<string, bool> check)
    {
        var region = Region(); region.Presentation!.KeyDisplay = PresentationKeyDisplayMode.AllPhysicalKeys;
        var scene = Scene(region); var now = TimeSpan.FromSeconds(1);
        PresentationFrame Keys(int key) => new(1, 2, now, pressedKeys: [new(key, ((char)key).ToString())],
            recentChords: [new([new(key, ((char)key).ToString())], now, true)]);
        var all = Keys(0x41); var outside = Keys(0x42); var inside = Keys(0x43);
        var partitioned = PresentationFrame.WithKeyboardScopes(all, outside, inside);
        using var renderer = new SceneRenderer();
        foreach (var style in Enum.GetValues<PresentationDisplayStyle>())
            foreach (var (scope, expected) in new[] { (PresentationKeyboardScope.AllApplications, all), (PresentationKeyboardScope.OutsideFloatCanvas, outside), (PresentationKeyboardScope.FloatCanvasOnly, inside) })
            {
                region.Presentation!.DisplayStyle = style; region.Presentation.KeyboardScope = scope;
                renderer.PresentationFrame = partitioned; using var scoped = Render(renderer, scene);
                region.Presentation.KeyboardScope = PresentationKeyboardScope.AllApplications;
                renderer.PresentationFrame = expected; using var direct = Render(renderer, scene);
                check(style + " " + scope + " renders only its independently captured keyboard frame", scoped.Pixels.SequenceEqual(direct.Pixels) && scoped.Pixels.Any(pixel => pixel.Alpha > 0));
            }
        region.Presentation!.DisplayStyle = PresentationDisplayStyle.Log; region.Presentation.KeyboardScope = PresentationKeyboardScope.OutsideFloatCanvas;
        renderer.PresentationFrame = all; using var unclassified = Render(renderer, scene);
        check("restricted keyboard layers do not expose unclassified all-application history", unclassified.Pixels.All(pixel => pixel.Alpha == 0));
    }

    private static void LegacyKeyboardFlags(Action<string, bool> check)
    {
        var region = Region(PresentationModules.Keys); var scene = Scene(region);
        using var renderer = new SceneRenderer { PresentationFrame = KeyFrame() };
        using var expected = Render(renderer, scene);
        check("a visible keyboard layer renders a semantic chord before legacy flag compatibility checks", expected.Pixels.Any(pixel => pixel.Alpha > 0));
        foreach (bool enabled in new[] { false, true })
        foreach (var modules in new[] { PresentationModules.Keys }.Concat(Enumerable.Range(0, 16).Select(mask => (PresentationModules)mask).Where(mask => mask != PresentationModules.Keys)))
        {
            region.Presentation!.Enabled = enabled; region.Presentation.Modules = modules;
            string source = JsonSerializer.Serialize(scene);
            using var pixels = Render(renderer, scene);
            check($"visible keyboard pixels ignore obsolete Enabled={enabled} and Modules={(int)modules} without rewriting source",
                pixels.Pixels.SequenceEqual(expected.Pixels) && JsonSerializer.Serialize(scene) == source);
        }
        region.Visible = false; using var hidden = Render(renderer, scene);
        check("hiding a keyboard layer removes its rendered keys", hidden.Pixels.All(pixel => pixel.Alpha == 0));
        region.Visible = true; region.Locked = true; using var locked = Render(renderer, scene);
        check("locking a visible keyboard layer does not stop its key display", locked.Pixels.SequenceEqual(expected.Pixels));
        region.Style.Opacity = 0; using var transparent = Render(renderer, scene);
        check("zero layer opacity continues to suppress keyboard pixels", transparent.Pixels.All(pixel => pixel.Alpha == 0));
        scene.Elements.Clear(); using var deleted = Render(renderer, scene);
        check("deleting the keyboard layer removes its rendered keys", deleted.Pixels.All(pixel => pixel.Alpha == 0));
    }

    private static void InertAndPicking(Action<string, bool> check)
    {
        var region = Region(); var scene = Scene(region); string original = JsonSerializer.Serialize(scene);
        using var renderer = new SceneRenderer();
        using var empty = Render(renderer, scene);
        check("presentation without an explicit frame exports empty pixels", empty.Pixels.All(pixel => pixel.Alpha == 0));
        check("inert presentation region remains editable without an observer", renderer.PickDepth(scene, new(), new(100, 100), 0)?.Id == region.Id);
        check("presentation marquee uses the configured region even with no rendered keys", renderer.IntersectsElementInk(scene, new(), region, new(80, 80, 10, 10)));
        region.Transform = Affine2D.Translation(150, 20).Compose(Affine2D.Rotation(45));
        check("rotated empty region rejects a marquee intersecting only its bounding box", !renderer.IntersectsElementInk(scene, new(), region, new(10, 30, 3, 3)));
        region.Transform = Affine2D.Translation(50, 50);
        var behind = new CanvasElement { Kind = ElementKind.Rectangle, Width = 100, Height = 100, Transform = Affine2D.Translation(60, 60), Style = new() { Fill = 0xFFFF0000 } };
        scene.Elements.Insert(0, behind); region.Locked = true;
        check("locked inert presentation region does not invent opaque ink that blocks underlying editable content", renderer.PickDepth(scene, new(), new(80, 80), 0)?.Id == behind.Id);
        region.Locked = false; scene.Elements.Remove(behind);
        check("static presentation render and picks never alter source JSON or create runtime nodes", JsonSerializer.Serialize(scene) == original && !scene.IsRuntimeEvaluated && scene.Elements.Count == 1);
    }

    private static void LegacyPointerLayers(Action<string, bool> check)
    {
        var region = Region(PresentationModules.Cursor | PresentationModules.Clicks | PresentationModules.Trail);
        var scene = Scene(region); string before = JsonSerializer.Serialize(scene);
        using var renderer = new SceneRenderer { PresentationFrame = new(1, 1, TimeSpan.FromSeconds(1), cursorScreen: new(100, 100),
            clicks: [new(new(100, 100), PresentationMouseButton.Left, TimeSpan.FromMilliseconds(900))],
            trail: [new(new(70, 90), TimeSpan.FromMilliseconds(900)), new(new(220, 100), TimeSpan.FromMilliseconds(1000))]) };
        using var pixels = Render(renderer, scene);
        check("legacy mouse modules in a scene layer no longer restrict or duplicate global mouse effects", pixels.Pixels.All(pixel => pixel.Alpha == 0));
        check("legacy mouse-only region remains editable and its serialized settings are preserved", renderer.PickDepth(scene, new(), new(100, 100), 0)?.Id == region.Id && JsonSerializer.Serialize(scene) == before);
    }
    private static void Keys(Action<string, bool> check)
    {
        var region = Region(PresentationModules.Keys); var scene = Scene(region); using var renderer = new SceneRenderer();
        var caps = new[] { new PresentationKeyCap(0x11, "Ctrl", true), new(0x41, "A") };
        renderer.PresentationFrame = new(2, 1, TimeSpan.FromSeconds(1), pressedKeys: caps, recentChords: [new(caps, TimeSpan.FromSeconds(1), true)]);
        using var keys = Render(renderer, scene); var bounds = InkBounds(keys);
        check("key panel uses local bottom alignment within its source region", !bounds.IsEmpty && bounds.Left >= 50 && bounds.Bottom <= 210 && bounds.Top > 130);
        var parent = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [region.Id], Transform = Affine2D.Translation(50, 0) };
        region.GroupId = parent.Id; scene.Groups = [parent]; scene.RootOrder = [parent.Id];
        using var moved = Render(renderer, scene); var movedBounds = InkBounds(moved);
        check("parent placement moves local keyboard layout rather than reading a global screen anchor", Math.Abs(movedBounds.Left - bounds.Left - 50) <= 1 && movedBounds.Top == bounds.Top);
        scene.Groups.Clear(); scene.RootOrder = null; region.GroupId = null;
        renderer.PresentationFrame = new(2, 2, TimeSpan.FromSeconds(2), recentChords: [new(caps, TimeSpan.FromSeconds(1))]);
        using var expired = Render(renderer, scene); check("key hold expiry clears the keyboard pixels without changing source", expired.Pixels.All(pixel => pixel.Alpha == 0));
        renderer.PresentationFrame = new(2, 3, TimeSpan.FromSeconds(2), recentChords: [new([new(0x41, "A")], TimeSpan.FromSeconds(2), true)]);
        using var shortcut = Render(renderer, scene); check("shortcut-only region hides unmodified physical letters", shortcut.Pixels.All(pixel => pixel.Alpha == 0));
        renderer.PresentationFrame = new(2, 4, TimeSpan.FromSeconds(2), recentChords: [new([new(0x10, "Shift", true), new(0x41, "A")], TimeSpan.FromSeconds(2), true)]);
        using var shiftText = Render(renderer, scene); check("shortcut-only region does not reveal Shift-only typed letters from a shared physical frame", shiftText.Pixels.All(pixel => pixel.Alpha == 0));
        region.Presentation!.KeyDisplay = PresentationKeyDisplayMode.AllPhysicalKeys;
        using var physical = Render(renderer, scene); check("all-physical mode explicitly renders a semantic Shift-letter chord", physical.Pixels.Any(pixel => pixel.Alpha > 0));
        renderer.PresentationFrame = new(2, 5, TimeSpan.FromSeconds(2), pressedKeys: caps, recentChords: [new(caps, TimeSpan.FromSeconds(2), true)], keyboardSuppressed: true);
        using var suppressed = Render(renderer, scene); check("keyboard suppression renders no held or recent key data", suppressed.Pixels.All(pixel => pixel.Alpha == 0));
        region.Presentation.Modules |= PresentationModules.Cursor;
        renderer.PresentationFrame = new(2, 6, TimeSpan.FromSeconds(2), pressedKeys: caps, cursorScreen: new(100, 100), keyboardSuppressed: true);
        using var pointer = Render(renderer, scene); check("keyboard suppression cannot revive legacy scene-bound pointer ink", pointer.Pixels.All(pixel => pixel.Alpha == 0));
        region.Presentation.Modules = PresentationModules.Keys;
        renderer.PresentationFrame = new(2, 7, TimeSpan.FromSeconds(2), recentChords: [new([new(0x70, "F1")], TimeSpan.FromSeconds(2), true)]);
        foreach (var alignment in Enum.GetValues<PresentationPanelAlignment>())
        {
            region.Presentation.KeyPanelAlignment = alignment;
            using var aligned = Render(renderer, scene); var box = InkBounds(aligned);
            bool top = alignment is PresentationPanelAlignment.TopLeft or PresentationPanelAlignment.TopCenter or PresentationPanelAlignment.TopRight;
            bool x = alignment switch
            {
                PresentationPanelAlignment.TopCenter or PresentationPanelAlignment.BottomCenter => Math.Abs(box.Center.X - 150) <= 1,
                PresentationPanelAlignment.TopRight or PresentationPanelAlignment.BottomRight => box.Left > 150 && box.Right <= 250,
                _ => box.Left >= 50 && box.Right < 150
            };
            check($"{alignment} lays out real keyboard pixels in region-local units", !box.IsEmpty && x && (top ? box.Top < 100 : box.Top > 130));
        }
    }

    private static void NestedRegion(Action<string, bool> check)
    {
        var region = Region(PresentationModules.Keys); region.Transform = Affine2D.Identity;
        var inner = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [region.Id], Transform = Affine2D.Translation(10, 5) };
        region.GroupId = inner.Id;
        var definition = new PrefabDefinition { Graph = new() { Elements = [region], Groups = [inner], RootOrder = [inner.Id] } };
        var instance = new PrefabInstance { DefinitionId = definition.Id, Transform = Affine2D.Translation(30, 20).Compose(Affine2D.Rotation(8)) };
        var parent = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [instance.Id], Transform = Affine2D.Translation(15, 10), Locked = true };
        instance.ParentId = parent.Id;
        var scene = Scene(); scene.Groups = [parent]; scene.Instances = [instance]; scene.Definitions = [definition]; scene.RootOrder = [parent.Id];
        var evaluated = SceneEvaluation.Build(scene); var world = evaluated.Leaves.Single().RenderElement;
        using var renderer = new SceneRenderer { PresentationFrame = KeyFrame() };
        using var pixels = Render(renderer, scene);
        var panelPoint = CanvasMapping.Create(scene).ElementToScreen(world).TransformPoint(new(20, 140));
        check("nested shared keyboard layout produces pixels at its mapped local panel position", pixels.GetPixel((int)panelPoint.X, (int)panelPoint.Y).Alpha > 0);
        check("inherited editing lock does not disable runtime keyboard playback", world.Locked && pixels.Pixels.Any(pixel => pixel.Alpha > 0));
    }

    private static void Instances(Action<string, bool> check)
    {
        var source = Region(PresentationModules.Keys); source.Width = source.Height = 100; source.Transform = Affine2D.Identity;
        var definition = new PrefabDefinition { Graph = new() { Elements = [source] } };
        var first = new PrefabInstance { DefinitionId = definition.Id, Transform = Affine2D.Translation(50, 50) };
        var second = new PrefabInstance { DefinitionId = definition.Id, Transform = Affine2D.Translation(220, 50) };
        var scene = Scene(); scene.Instances = [first, second]; scene.Definitions = [definition]; scene.RootOrder = [first.Id, second.Id];
        string before = JsonSerializer.Serialize(scene);
        using var renderer = new SceneRenderer { PresentationFrame = KeyFrame() };
        using var pixels = Render(renderer, scene);
        bool Ink(int left) => Enumerable.Range(left, 100).Any(x => Enumerable.Range(50, 100).Any(y => pixels.GetPixel(x, y).Alpha > 0));
        check("shared instances show the same global keyboard event within each independently placed keyboard layer", Ink(50) && Ink(220));
        var hit = renderer.PickDepth(scene, new(), new(245, 75), 0); var evaluated = SceneEvaluation.Build(scene);
        check("empty portion of a repeated keyboard instance picks its distinct occurrence and real host", hit is not null && evaluated.ResolveSelectableNode(hit.Id) == second.Id && hit.Id != source.Id);
        check("keyboard timeline never writes source definitions or adds runtime selection nodes", JsonSerializer.Serialize(scene) == before && scene.Elements.Count == 0 && definition.Graph.Elements.Count == 1);
    }

    private static void Composition(Action<string, bool> check)
    {
        var region = Region(PresentationModules.Keys); region.Style.Foreground = 0; region.Style.Background = 0x80FF0000; region.Style.Opacity = .5;
        var scene = Scene(region); scene.LayerOpacity = .5;
        using var renderer = new SceneRenderer { PresentationFrame = KeyFrame() };
        using var alpha = Render(renderer, scene); int value = alpha.GetPixel(65, 190).Alpha;
        check("keyboard panel applies element and global opacity once", value is >= 31 and <= 33);
        scene.LayerOpacity = 1; region.Style.Opacity = 1;
        var front = new CanvasElement { Kind = ElementKind.Rectangle, Width = 260, Height = 220, Transform = Affine2D.Translation(40, 40), Style = new() { Fill = 0xFF00FF00, StrokeWidth = 0 } };
        scene.Elements.Add(front);
        using var ordered = Render(renderer, scene); check("keyboard panel keeps its real painter slot below later ordinary nodes", ordered.GetPixel(65, 190) == SKColors.Lime);
        scene.Elements.Remove(front); scene.Canvas.BackgroundColor = 0xFF0000FF;
        using var lower = Render(renderer, scene);
        scene.Elements.Add(new() { Kind = ElementKind.LivePixelate, Width = 50, Height = 50, Transform = Affine2D.Translation(50, 165), Effects = [new() { Kind = ImageEffectKind.Pixelate, Amount = 1 }] });
        using var filtered = Render(renderer, scene); check("live filters sample the same semantic keyboard prefix frame", filtered.GetPixel(65, 190) == lower.GetPixel(65, 190));
        renderer.PresentationFrame = null; using var emptyRegion = Render(renderer, scene); scene.Elements.Remove(region); using var withoutRegion = Render(renderer, scene);
        check("inert keyboard layers add no static export ink", emptyRegion.Pixels.SequenceEqual(withoutRegion.Pixels));
    }

    private static void Budget(Action<string, bool> check)
    {
        var regions = Enumerable.Range(0, 9).Select(_ => Region(PresentationModules.Keys)).ToArray();
        foreach (var region in regions) region.Presentation!.KeyDisplay = PresentationKeyDisplayMode.AllPhysicalKeys;
        var scene = Scene(regions); string before = JsonSerializer.Serialize(scene);
        var caps = Enumerable.Range(0, 16).Select(index => new PresentationKeyCap(0x41 + index, new string('W', 64))).ToArray();
        using var renderer = new SceneRenderer { PresentationFrame = new(5, 1, TimeSpan.FromSeconds(1), recentChords: [new(caps, TimeSpan.FromSeconds(1), true)]) };
        var world = SceneEvaluation.Build(scene).RenderDocument; var info = renderer.PreparePresentation(world);
        using var limited = Render(renderer, world);
        check("primitive budget counts semantic chord glyphs and clears the entire keyboard frame on overflow", info.BudgetExceeded && info.PrimitiveCount > PresentationLimits.MaximumPrimitives && limited.Pixels.All(pixel => pixel.Alpha == 0));
        check("keyboard budget overflow does not persist events or mutate source nodes", JsonSerializer.Serialize(scene) == before && scene.Elements.Count == 9);
        renderer.PresentationFrame = KeyFrame(); using var recovered = Render(renderer, world);
        check("a later bounded semantic frame renders with the same occurrence plan after overflow", !renderer.PreparePresentation(world).BudgetExceeded && recovered.Pixels.Any(pixel => pixel.Alpha > 0) && renderer.PresentationPlanBuildCount == 1);
    }

    private static PresentationFrame KeyFrame() => new(4, 1, TimeSpan.FromSeconds(1),
        recentChords: [new([new(0x11, "Ctrl", true), new(0x43, "C")], TimeSpan.FromSeconds(1), true)]);
    private static void InvalidRange(Action<string, bool> check)
    {
        var ordinary = new CanvasElement { Kind = ElementKind.Rectangle, Width = 12, Height = 12, Style = new() { Fill = 0xFF0000FF, StrokeWidth = 0 } };
        var scene = Scene(ordinary, Region()); var assets = new AssetLibrary();
        using var renderer = new SceneRenderer(); using var cache = new OverlaySceneCache(renderer);
        using var target = SKSurface.Create(new SKImageInfo(400, 300)); target.Canvas.Clear(SKColors.Transparent);
        cache.DrawOnClearedRasterTarget(target, scene, assets, 1, 0);
        ordinary.Style.Fill = 0xFFFFA500;
        target.Canvas.Clear(SKColors.Transparent); cache.DrawOnClearedRasterTarget(target, scene, assets, 1, 0, 3, 1);
        using var image = target.Snapshot(); using var pixels = SKBitmap.FromImage(image);
        check("presentation dynamic span never repairs a malformed caller range into stale static-cache eligibility", pixels.GetPixel(5, 5) == SKColors.Orange);
    }

    private static CanvasElement Region(PresentationModules modules = PresentationModules.Keys | PresentationModules.Cursor | PresentationModules.Clicks | PresentationModules.Trail) => new()
    {
        Kind = ElementKind.Presentation, Width = 200, Height = 160, Transform = Affine2D.Translation(50, 50), Presentation = new() { Modules = modules, KeyDisplay = PresentationKeyDisplayMode.ShortcutChords },
        Style = new() { Foreground = 0xFFFFFFFF, Background = 0xD0203040, FontSize = 20 }
    };
    private static SceneDocument Scene(params CanvasElement[] elements) => new() { Elements = elements.ToList(), Viewport = new(0, 0, 400, 300) };
    private static SKBitmap Render(SceneRenderer renderer, SceneDocument scene) => SKBitmap.Decode(renderer.ExportPng(scene, new()));
    private static RectD InkBounds(SKBitmap bitmap)
    {
        int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
        for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++) if (bitmap.GetPixel(x, y).Alpha > 0)
        { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
        return right < left ? default : new(left, top, right - left + 1, bottom - top + 1);
    }
}
