using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using SkiaSharp;

internal static class RootCanvasRenderingTests
{
    internal static void Run(Action<string, bool> check, string? evidence = null)
    {
        using var renderer = new SceneRenderer();
        var assets = new AssetLibrary();
        var background = Box(5, 5, 75, 65, 0xFF0000FF);
        var main = Box(5, 5, 75, 65, 0xFFFF0000);
        var foreground = Box(20, 20, 20, 20, 0xFF00FF00);
        var scene = Scene();
        // Intentionally not in root paint order: source storage order is preserved.
        scene.Elements = [foreground, main, background];
        SceneCanvasRoots.Assign(scene, foreground.Id, RootCanvasKind.Foreground);
        SceneCanvasRoots.Assign(scene, background.Id, RootCanvasKind.Background);
        using (var pixels = Pixels(renderer, scene, assets))
        {
            check("auxiliary roots stay on the screen while the main root is projected", pixels.GetPixel(10, 10) == SKColors.Blue && pixels.GetPixel(90, 10) == SKColors.Red);
            check("foreground author content is above both roots independent of storage order", pixels.GetPixel(25, 25) == SKColors.Lime);
            Evidence(pixels, evidence, "three-root-composite.png");
        }
        check("foreground bounds use its own screen mapping", SceneRenderer.ProjectedBounds(scene, foreground) == new RectD(20, 20, 20, 20));
        check("main bounds retain main projection", SceneRenderer.ProjectedBounds(scene, main) == new RectD(45, 5, 75, 65));
        check("foreground ink rectangle picking stays in screen space", renderer.IntersectsElementInk(scene, assets, foreground, new(21, 21, 2, 2)) &&
            !renderer.IntersectsElementInk(scene, assets, foreground, new(61, 21, 2, 2)));
        check("unfiltered picking respects fixed foreground order", renderer.PickDepth(scene, assets, new(25, 25), 0)?.Id == foreground.Id);
        check("active background root remains editable under an opaque foreground", renderer.PickDepth(scene, assets, new(25, 25), 0, rootCanvas: RootCanvasKind.Background)?.Id == background.Id);
        check("active main root ignores inactive foreground ink", renderer.PickDepth(scene, assets, new(50, 25), 0, rootCanvas: RootCanvasKind.Main)?.Id == main.Id);
        using (var plane = Pixels(renderer, scene, assets, SceneRenderSpace.PlaneContent))
            check("plane content exports only unprojected main author content", plane.GetPixel(25, 25) == SKColors.Red && plane.GetPixel(90, 25).Alpha == 0);
        scene.Canvas.Projection.Enabled = false;
        scene.Canvas.Grid.FlatTranslation = new(40, 0);
        using (var flat = Pixels(renderer, scene, assets))
            check("screen roots do not inherit flat main grid translation", flat.GetPixel(10, 10) == SKColors.Blue && flat.GetPixel(90, 10) == SKColors.Red);
        scene.Canvas.Projection.Enabled = true;
        scene.Canvas.Grid.FlatTranslation = default;
        StorageRoundtrip(renderer, scene, assets, check);
        OpacityAndCache(renderer, assets, check);
        GroundAndDepth(renderer, assets, check, evidence);
        EffectPrefixes(renderer, assets, check);
        CrossTileFilters(renderer, assets, check);
        FrozenBase(renderer, check);
        NestedRoots(renderer, assets, check);
        DynamicRegions(renderer, check);
    }

    private static void OpacityAndCache(SceneRenderer renderer, AssetLibrary assets, Action<string, bool> check)
    {
        var scene = Scene(); scene.Canvas.Projection.Enabled = false;
        scene.LayerOpacity = .5;
        var blue = Box(0, 0, 160, 140, 0xFF0000FF); var red = Box(0, 0, 160, 140, 0xFFFF0000); var green = Box(0, 0, 160, 140, 0xFF00FF00);
        scene.Elements = [green, blue, red];
        SceneCanvasRoots.Assign(scene, blue.Id, RootCanvasKind.Background);
        SceneCanvasRoots.Assign(scene, green.Id, RootCanvasKind.Foreground);
        using var direct = Pixels(renderer, scene, assets);
        check("all roots share one final annotation opacity group", direct.GetPixel(80, 70) == new SKColor(0, 255, 0, 128));
        using var cache = new OverlaySceneCache(renderer);
        using var surface = SKSurface.Create(new SKImageInfo(160, 140, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        cache.DrawOnClearedRasterTarget(surface, scene, assets, 1, 0);
        using var image = surface.Snapshot(); using var cached = SKBitmap.FromImage(image);
        check("mixed roots cache their complete composition without double opacity", direct.Bytes.SequenceEqual(cached.Bytes) && cache.BuildCount == 1 && cache.InPlaceOpacityFrameCount == 0);
        surface.Canvas.Clear(SKColors.Transparent); cache.DrawOnClearedRasterTarget(surface, scene, assets, 1, 0);
        check("mixed roots reuse the static complete composition", cache.BuildCount == 1 && cache.HitCount == 1);
    }

    private static void GroundAndDepth(SceneRenderer renderer, AssetLibrary assets, Action<string, bool> check, string? evidence)
    {
        var scene = DepthScene();
        var board = scene.Elements.Single();
        var background = Box(0, 0, 160, 140, 0xFF0000FF);
        var foreground = Box(65, 90, 30, 25, 0xFFFFFF00);
        scene.Elements.Insert(0, foreground); scene.Elements.Add(background);
        SceneCanvasRoots.Assign(scene, background.Id, RootCanvasKind.Background);
        SceneCanvasRoots.Assign(scene, foreground.Id, RootCanvasKind.Foreground);
        using (var pixels = Pixels(renderer, scene, assets))
        {
            check("main ground still occludes submerged main billboard with auxiliary roots", pixels.GetPixel(80, 35) == SKColors.Red && pixels.GetPixel(55, 100) == SKColors.Lime);
            check("foreground remains above main ground and depth composition", pixels.GetPixel(80, 100) == SKColors.Yellow);
            Evidence(pixels, evidence, "three-root-depth.png");
        }
        check("main root picking ignores foreground but retains main ground occlusion", renderer.PickDepth(scene, assets, new(80, 100), 0, rootCanvas: RootCanvasKind.Main) is null &&
            renderer.PickDepth(scene, assets, new(80, 35), 0, rootCanvas: RootCanvasKind.Main)?.Id == board.Id);
        scene.Elements.Remove(board);
        using (var emptyMain = Pixels(renderer, scene, assets))
            check("main ground is drawn even when the main root contains no author leaves", emptyMain.GetPixel(20, 20) == SKColors.Lime);
        check("unfiltered picking cannot see background through opaque empty main ground", renderer.PickDepth(scene, assets, new(20, 20), 0) is null);
        check("background editing ignores an inactive opaque main ground", renderer.PickDepth(scene, assets, new(20, 20), 0, rootCanvas: RootCanvasKind.Background)?.Id == background.Id);
        scene.LayerOpacity = .5;
        using var opacity = Pixels(renderer, scene, assets);
        check("empty main ground and both screen roots receive opacity only once", opacity.GetPixel(20, 20).Alpha == 128 && opacity.GetPixel(80, 100).Alpha == 128);
    }

    private static void EffectPrefixes(SceneRenderer renderer, AssetLibrary assets, Action<string, bool> check)
    {
        var scene = Scene(); scene.Canvas.Projection.Enabled = false;
        var blue = Box(0, 0, 160, 140, 0xFF0000FF);
        var effect = Mask(15, 15, 100, 100);
        scene.Elements = [effect, blue];
        SceneCanvasRoots.Assign(scene, blue.Id, RootCanvasKind.Background);
        using (var mainEffect = Pixels(renderer, scene, assets))
            check("a main live filter samples the already composed background root", mainEffect.GetPixel(30, 30) == SKColors.Blue);
        scene.Canvas.BackgroundColor = 0xFF00FF00;
        SceneCanvasRoots.Assign(scene, effect.Id, RootCanvasKind.Background);
        scene.Elements = [blue, effect];
        using (var backgroundEffect = Pixels(renderer, scene, assets, includeCanvasBackground: false))
            check("background live filter excludes the higher main ground from sampling", backgroundEffect.GetPixel(30, 30) == SKColors.Blue);
        var depth = DepthScene();
        var backdrop = Box(0, 0, 160, 140, 0xFF0000FF);
        var frontFilter = Mask(30, 20, 100, 100);
        depth.Elements.Add(backdrop); depth.Elements.Add(frontFilter);
        SceneCanvasRoots.Assign(depth, backdrop.Id, RootCanvasKind.Background);
        SceneCanvasRoots.Assign(depth, frontFilter.Id, RootCanvasKind.Foreground);
        using (var foregroundEffect = Pixels(renderer, depth, assets))
            check("foreground live filter samples actual main depth output including its ground", foregroundEffect.GetPixel(80, 35) == SKColors.Red && foregroundEffect.GetPixel(80, 100) == SKColors.Lime);
        var backgroundFilter = Mask(5, 5, 145, 125);
        var mainFilter = Mask(25, 20, 100, 100);
        depth.Elements.Insert(depth.Elements.IndexOf(backdrop) + 1, backgroundFilter);
        depth.Elements.Insert(1, mainFilter);
        SceneCanvasRoots.Assign(depth, backgroundFilter.Id, RootCanvasKind.Background);
        using (var chained = Pixels(renderer, depth, assets))
            check("filters in all three roots compose finite lower prefixes through main depth", chained.GetPixel(80, 35) == SKColors.Red && chained.GetPixel(80, 100) == SKColors.Lime);
        depth.Elements.Remove(mainFilter);
        // An auxiliary live effect must not prevent an otherwise valid main-plane export.
        using var plane = Pixels(renderer, depth, assets, SceneRenderSpace.PlaneContent);
        check("auxiliary live filters are omitted from plane rectification without throwing", plane.GetPixel(150, 130).Alpha == 0);
    }

    private static void FrozenBase(SceneRenderer renderer, Action<string, bool> check)
    {
        var assets = new AssetLibrary();
        using var surface = SKSurface.Create(new SKImageInfo(160, 140)); surface.Canvas.Clear(SKColors.White);
        using var image = surface.Snapshot(); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var scene = Scene(); scene.Canvas.Projection.Enabled = false; scene.Kind = DocumentKind.FrozenCapture; scene.LayerOpacity = .5;
        scene.Background = new() { Kind = ElementKind.Image, AssetId = assets.Add(png.ToArray()), Width = 160, Height = 140 };
        var blue = Box(10, 10, 50, 50, 0xFF0000FF); scene.Elements = [blue];
        SceneCanvasRoots.Assign(scene, blue.Id, RootCanvasKind.Background);
        using var pixels = Pixels(renderer, scene, assets);
        check("frozen photograph remains below all authored roots and outside annotation opacity", pixels.GetPixel(100, 100) == SKColors.White && pixels.GetPixel(20, 20).Blue == 255 && pixels.GetPixel(20, 20).Red is >= 126 and <= 128);
    }

    private static void CrossTileFilters(SceneRenderer renderer, AssetLibrary assets, Action<string, bool> check)
    {
        var scene = DepthScene(); scene.LayerOpacity = .6;
        var blue = Box(0, 0, 160, 140, 0xFF0000FF);
        var blur = Mask(55, 15, 70, 110); blur.Kind = ElementKind.LiveBlur;
        blur.Effects = [new() { Kind = ImageEffectKind.Blur, Amount = 4 }];
        scene.Elements.Add(blue); scene.Elements.Add(blur);
        SceneCanvasRoots.Assign(scene, blue.Id, RootCanvasKind.Background);
        SceneCanvasRoots.Assign(scene, blur.Id, RootCanvasKind.Foreground);
        using var full = Pixels(renderer, scene, assets);
        using var tileSurface = SKSurface.Create(new SKImageInfo(80, 140, SKColorType.Bgra8888, SKAlphaType.Premul));
        tileSurface.Canvas.Clear(SKColors.Transparent); tileSurface.Canvas.Translate(-80, 0);
        renderer.Draw(tileSurface.Canvas, scene, assets);
        using var tileImage = tileSurface.Snapshot(); using var tile = SKBitmap.FromImage(tileImage);
        check("cross-tile foreground blur samples full main depth and background with identical opacity", Enumerable.Range(0, 140).All(y =>
            Enumerable.Range(0, 80).All(x => full.GetPixel(x + 80, y) == tile.GetPixel(x, y))));
    }

    private static void NestedRoots(SceneRenderer renderer, AssetLibrary assets, Action<string, bool> check)
    {
        var scene = Scene(); var group = new CanvasGroup { Transform = Affine2D.Translation(20, 10) };
        var leaf = Box(5, 5, 20, 20, 0xFF00FF00); leaf.GroupId = group.Id;
        scene.Groups = [group]; scene.Elements = [leaf];
        SceneCanvasRoots.Assign(scene, group.Id, RootCanvasKind.Foreground);
        using var pixels = Pixels(renderer, scene, assets);
        check("root ownership follows the top-level group while local transforms remain active", pixels.GetPixel(30, 20) == SKColors.Lime && pixels.GetPixel(70, 20).Alpha == 0);
        check("nested foreground selection uses the same transformed ink as pixels", renderer.PickDepth(scene, assets, new(30, 20), 0, rootCanvas: RootCanvasKind.Foreground)?.Id == leaf.Id);
        var definition = new PrefabDefinition { Graph = new() { Elements = [Box(0, 0, 15, 15, 0xFFFF0000)] } };
        var frontInstance = new PrefabInstance { DefinitionId = definition.Id, Transform = Affine2D.Translation(60, 15) };
        var mainInstance = new PrefabInstance { DefinitionId = definition.Id, Transform = Affine2D.Translation(60, 45) };
        scene.Definitions = [definition]; scene.Instances = [frontInstance, mainInstance];
        scene.RootOrder = [leaf.Id, frontInstance.Id, mainInstance.Id];
        SceneCanvasRoots.Assign(scene, frontInstance.Id, RootCanvasKind.Foreground);
        using var instances = Pixels(renderer, scene, assets);
        check("shared definitions use each host instance root mapping without duplicating their source", instances.GetPixel(65, 20) == SKColors.Red &&
            instances.GetPixel(105, 50) == SKColors.Red && instances.GetPixel(105, 20).Alpha == 0 && scene.Definitions.Count == 1);
    }

    private static void DynamicRegions(SceneRenderer renderer, Action<string, bool> check)
    {
        var scene = Scene();
        var window = new CanvasElement { Kind = ElementKind.Image, Width = 25, Height = 15,
            Transform = Affine2D.Translation(10, 10), WindowSource = new() { BindingIntent = Guid.NewGuid() }, Style = new() { StrokeWidth = 0 } };
        var keyboard = new CanvasElement { Kind = ElementKind.Presentation, Width = 100, Height = 55,
            Transform = Affine2D.Translation(5, 65), Style = new() { StrokeWidth = 0, FontSize = 12 },
            Presentation = new() { DisplayStyle = PresentationDisplayStyle.Keyboard } };
        scene.Elements = [window, keyboard];
        SceneCanvasRoots.Assign(scene, window.Id, RootCanvasKind.Foreground);
        SceneCanvasRoots.Assign(scene, keyboard.Id, RootCanvasKind.Foreground);
        var windows = renderer.PrepareWindowFrames(scene);
        check("window frame dirty regions use the host screen root mapping", windows.DirtyBounds.Count == 1 && windows.DirtyBounds[0] == new RectD(10, 10, 25, 15));
        var keys = renderer.PreparePresentation(scene);
        check("keyboard runtime plan uses the host screen root for all key damage", keys.DirtyBounds.Count > 0 && keys.DirtyBounds.All(bound => bound.Left >= 5 && bound.Right <= 105 && bound.Top >= 65 && bound.Bottom <= 120));
        using var pixels = Pixels(renderer, scene, new());
        check("idle realtime keyboard still draws authored normal-state keys on a screen root", Enumerable.Range(65, 55).Any(y => Enumerable.Range(5, 100).Any(x => pixels.GetPixel(x, y).Alpha > 0)));
    }

    private static void StorageRoundtrip(SceneRenderer renderer, SceneDocument scene, AssetLibrary assets, Action<string, bool> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "FloatCanvas-root-render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "three-roots.floatcanvas");
            var store = new ProjectStore(); store.SaveAsync(path, scene, assets).GetAwaiter().GetResult();
            var loaded = store.LoadAsync(path).GetAwaiter().GetResult();
            using var before = Pixels(renderer, scene, assets); using var after = Pixels(renderer, loaded.Document, loaded.Assets);
            check("three-root project save and reopen preserve the exact composite", before.Bytes.SequenceEqual(after.Bytes));
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    private static SceneDocument Scene() => new() { Viewport = new(0, 0, 160, 140), Canvas = new() { Projection = new() {
        Enabled = true, PlaneBounds = new(0, 0, 160, 140), Matrix = Projective2D.FromAffine(Affine2D.Translation(40, 0)),
        Quad = [new(40, 0), new(200, 0), new(200, 140), new(40, 140)] } } };

    private static SceneDocument DepthScene()
    {
        var scene = Scene(); scene.Canvas.BackgroundColor = 0xFF00FF00; scene.Canvas.DepthPriorityOcclusion = true;
        scene.Canvas.Projection.Matrix = new(1, 0, 0, 0, 1, 0, 0, -.003, 1);
        var mapping = CanvasMapping.Create(scene); mapping.TryToPlane(new(80, 50), out var anchor);
        mapping.TryGetDepthScale(anchor, out double scale);
        scene.Elements.Add(new() { Kind = ElementKind.Rectangle, IsBillboard = true, BillboardAnchor = anchor,
            BillboardHeight = -20 / scale, Width = 100 / scale, Height = 90 / scale, Style = new() { Fill = 0xFFFF0000, StrokeWidth = 0 } });
        return scene;
    }

    private static CanvasElement Box(double x, double y, double width, double height, uint color) => new() {
        Kind = ElementKind.Rectangle, Width = width, Height = height, Transform = Affine2D.Translation(x, y), Style = new() { Fill = color, StrokeWidth = 0 } };

    private static CanvasElement Mask(double x, double y, double width, double height) => new() {
        Kind = ElementKind.LivePixelate, Width = width, Height = height, Transform = Affine2D.Translation(x, y), Style = new() { StrokeWidth = 0 },
        Effects = [new() { Kind = ImageEffectKind.Pixelate, Amount = 1 }] };

    private static SKBitmap Pixels(SceneRenderer renderer, SceneDocument scene, AssetLibrary assets,
        SceneRenderSpace space = SceneRenderSpace.ScreenComposite, bool includeCanvasBackground = true)
    {
        using var surface = SKSurface.Create(new SKImageInfo(160, 140, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        renderer.Draw(surface.Canvas, scene, assets, space: space, includeCanvasBackground: includeCanvasBackground);
        using var image = surface.Snapshot(); return SKBitmap.FromImage(image);
    }

    private static void Evidence(SKBitmap bitmap, string? directory, string file)
    {
        if (directory is null) return; Directory.CreateDirectory(directory);
        using var image = SKImage.FromBitmap(bitmap); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(directory, file), png.ToArray());
    }
}
