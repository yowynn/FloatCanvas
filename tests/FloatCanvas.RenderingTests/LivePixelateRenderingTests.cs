using System.Text.Json;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class LivePixelateRenderingTests
{
    public static void Run(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer(); var assets = new AssetLibrary();
        var effect = Mask(20, 20, 80, 60, 20);
        var scene = new SceneDocument { Viewport = new(0, 0, 160, 120), Elements = [effect] };
        check("a visible in-viewport effect requires live capture", SceneRenderer.HasLiveEffects(scene));
        scene.LayerOpacity = 0;
        check("a fully transparent annotation layer does not keep desktop capture running", !SceneRenderer.HasLiveEffects(scene));
        scene.LayerOpacity = 1; effect.Visible = false;
        check("a hidden effect does not keep desktop capture running", !SceneRenderer.HasLiveEffects(scene));
        effect.Visible = true; effect.Transform = Affine2D.Translation(400, 300);
        check("an offscreen effect does not exclude unrelated annotations from capture", !SceneRenderer.HasLiveEffects(scene));
        effect.Transform = Affine2D.Translation(20, 20);
        using var red = Backdrop(scene.Viewport, (_, _) => SKColors.Red);
        using var blue = Backdrop(scene.Viewport, (_, _) => SKColors.Blue);
        renderer.LiveBackdrop = red;
        using var first = Bitmap(renderer, scene, assets);
        renderer.LiveBackdrop = blue;
        using var second = Bitmap(renderer, scene, assets);
        check("live pixelation refreshes from two distinct raw desktop frames", first.GetPixel(40, 40) == SKColors.Red && second.GetPixel(40, 40) == SKColors.Blue);
        check("live pixelation remains a resource-free editable region", effect.AssetId is null && assets.Count == 0 && !scene.ReferencedAssets.Any() && effect.Effects.Single().Amount == 20);
        check("transparent export contains the effect while omitting the bare desktop", second.GetPixel(40, 40).Alpha == 255 && second.GetPixel(5, 5).Alpha == 0);

        renderer.LiveBackdrop = null;
        using var missing = Bitmap(renderer, scene, assets);
        check("missing desktop pixels produce an opaque cover instead of revealing content", missing.GetPixel(40, 40) == new SKColor(0xFF50545B) && missing.GetPixel(5, 5).Alpha == 0);
        PrefixAndOrder(renderer, assets, check);
        FrozenBackground(renderer, check);
        TransformsAndHistory(renderer, assets, check);
        CacheFrames(renderer, assets, check);
        renderer.LiveBackdrop = null;
    }

    private static void PrefixAndOrder(SceneRenderer renderer, AssetLibrary assets, Action<string, bool> check)
    {
        var viewport = new RectD(0, 0, 160, 120);
        using var black = Backdrop(viewport, (_, _) => SKColors.Black); renderer.LiveBackdrop = black;
        var below = Box(28, 20, 8, 60, 0xFFFF0000);
        var above = Box(65, 20, 3, 60, 0xFF00FF00);
        var effect = Mask(20, 20, 80, 60, 20);
        var scene = new SceneDocument { Viewport = viewport, Elements = [below, effect, above] };
        using var result = Bitmap(renderer, scene, assets);
        var lower = result.GetPixel(30, 40);
        check("a lower vector mark participates in the pixelated desktop composition", lower.Alpha == 255 && lower.Red is > 15 and < 245 && lower.Green == 0 && result.GetPixel(21, 40) == lower);
        check("an upper vector remains sharp and does not contaminate the mask's sampled blocks", result.GetPixel(66, 40) == SKColors.Lime && result.GetPixel(70, 40) == SKColors.Black);

        using var stripes = Backdrop(viewport, (x, y) => x % 13 < 5 ? SKColors.White : y % 9 < 4 ? SKColors.Blue : SKColors.Red);
        renderer.LiveBackdrop = stripes;
        var a = Mask(10, 20, 90, 60, 17); var b = Mask(30, 10, 80, 80, 13);
        scene.Elements = [a, b]; using var ab = Bitmap(renderer, scene, assets);
        scene.Elements = [b, a]; using var ba = Bitmap(renderer, scene, assets);
        int changed = 0;
        for (int y = 25; y < 75; y++) for (int x = 35; x < 90; x++) if (ab.GetPixel(x, y) != ba.GetPixel(x, y)) changed++;
        check("overlapping live masks respect their z-order when sampling earlier masks", changed > 100);
        scene.Elements = Enumerable.Range(0, 12).Select(_ => Mask(20, 20, 80, 60, 12)).ToList();
        long builds = renderer.LiveEffectBuildCount;
        using var nested = Bitmap(renderer, scene, assets);
        check("each overlapping mask builds its sampled prefix only once per render", renderer.LiveEffectBuildCount - builds == 12 && nested.GetPixel(40, 40).Alpha == 255);
        renderer.LiveBackdrop = null;
    }

    private static void FrozenBackground(SceneRenderer renderer, Action<string, bool> check)
    {
        var assets = new AssetLibrary();
        using var surface = SKSurface.Create(new SKImageInfo(160, 120)); surface.Canvas.Clear(SKColors.Green);
        using var image = surface.Snapshot(); using var bytes = image.Encode(SKEncodedImageFormat.Png, 100);
        string asset = assets.Add(bytes.ToArray());
        var scene = new SceneDocument { Kind = DocumentKind.FrozenCapture, Viewport = new(0, 0, 160, 120),
            Background = new() { Kind = ElementKind.Image, AssetId = asset, Width = 160, Height = 120 }, Elements = [Mask(20, 20, 80, 60, 16)] };
        using var red = Backdrop(scene.Viewport, (_, _) => SKColors.Red); renderer.LiveBackdrop = red;
        using var normal = SKBitmap.Decode(renderer.ExportPng(scene, assets));
        using var transparent = Bitmap(renderer, scene, assets);
        check("a frozen document samples its frozen background before any live desktop frame", normal.GetPixel(40, 40) == SKColors.Green && normal.GetPixel(5, 5) == SKColors.Green);
        check("transparent screenshot export retains pixelation sampled from the frozen background", transparent.GetPixel(40, 40) == SKColors.Green && transparent.GetPixel(5, 5).Alpha == 0);
        scene.Elements.Insert(0, Box(20, 20, 80, 60, 0xFFFF0000));
        var selection = new SceneDocument { Viewport = new(19, 19, 82, 62), Elements = [scene.Elements[^1].Clone()] };
        renderer.LiveEffectContext = scene;
        using var selected = Bitmap(renderer, selection, assets);
        check("an isolated editable selection renders its unselected lower context and frozen source", selected.GetPixel(21, 21) == SKColors.Red && selected.GetPixel(0, 0).Alpha == 0);
        renderer.LiveEffectContext = null;
        renderer.LiveBackdrop = null;
    }

    private static void TransformsAndHistory(SceneRenderer renderer, AssetLibrary assets, Action<string, bool> check)
    {
        var viewport = new RectD(-150, -100, 300, 200);
        using var blue = Backdrop(viewport, (_, _) => SKColors.Blue); renderer.LiveBackdrop = blue;
        var effect = Mask(-70, -30, 80, 40, 12);
        effect.Transform = effect.Transform.Compose(Affine2D.Rotation(30)).Compose(Affine2D.Scale(1.5, .75));
        var scene = new SceneDocument { Viewport = viewport, Elements = [effect] };
        var center = effect.Transform.TransformPoint(new(40, 20)); var bounds = ElementGeometry.Bounds(effect, false);
        using var bitmap = Bitmap(renderer, scene, assets);
        var centerPixel = bitmap.GetPixel((int)Math.Floor(center.X - viewport.X), (int)Math.Floor(center.Y - viewport.Y));
        check("translated rotated nonuniform masks render at negative document coordinates", centerPixel == SKColors.Blue && bounds.Contains(center));
        check("live mask hit testing uses its transformed geometry instead of its bounding rectangle", ElementGeometry.HitTest(effect, center, 0) && !ElementGeometry.HitTest(effect, new(bounds.Left + 1, bounds.Top + 1), 0));
        check("live mask bounds exclude unused stroke width", ElementGeometry.Bounds(effect) == bounds);
        var session = new EditorSession(scene, assets); session.SelectAll(); string before = JsonSerializer.Serialize(session.Document);
        session.TransformSelection(Affine2D.Translation(22, -14));
        check("live mask movement preserves a vector region and is reversible", session.Document.Elements[0].Kind == ElementKind.LivePixelate && session.Document.Elements[0].AssetId is null &&
            session.Undo() && JsonSerializer.Serialize(session.Document) == before && session.Redo());
        renderer.LiveBackdrop = null;
    }

    private static void CacheFrames(SceneRenderer renderer, AssetLibrary assets, Action<string, bool> check)
    {
        var scene = new SceneDocument { Viewport = new(0, 0, 160, 120), Elements = [Mask(20, 20, 80, 60, 16)] };
        using var red = Backdrop(scene.Viewport, (_, _) => SKColors.Red); using var blue = Backdrop(scene.Viewport, (_, _) => SKColors.Blue);
        using var cache = new OverlaySceneCache(renderer);
        using var surface = SKSurface.Create(new SKImageInfo(160, 120, SKColorType.Bgra8888, SKAlphaType.Premul));
        renderer.LiveBackdrop = red; surface.Canvas.Clear(SKColors.Transparent); cache.DrawOnClearedRasterTarget(surface, scene, assets, 1, 1);
        using var first = surface.Snapshot(); using var firstPixels = SKBitmap.FromImage(first);
        renderer.LiveBackdrop = blue; surface.Canvas.Clear(SKColors.Transparent); cache.DrawOnClearedRasterTarget(surface, scene, assets, 1, 1);
        using var second = surface.Snapshot(); using var secondPixels = SKBitmap.FromImage(second);
        check("static scene revision does not leave a live effect in stale raster cache", firstPixels.GetPixel(40, 40) == SKColors.Red && secondPixels.GetPixel(40, 40) == SKColors.Blue && cache.CachedRasterBytes == 0);
        renderer.LiveBackdrop = null;
    }

    private static CanvasElement Mask(double x, double y, double width, double height, double amount) => new()
    {
        Kind = ElementKind.LivePixelate, Width = width, Height = height, Transform = Affine2D.Translation(x, y), Style = new() { StrokeWidth = 0 },
        Effects = [new() { Kind = ImageEffectKind.Pixelate, Amount = amount, Bounds = new(0, 0, 1, 1) }]
    };
    private static CanvasElement Box(double x, double y, double width, double height, uint fill) => new()
    { Kind = ElementKind.Rectangle, Width = width, Height = height, Transform = Affine2D.Translation(x, y), Style = new() { Fill = fill, StrokeWidth = 0 } };
    private static SKBitmap Bitmap(SceneRenderer renderer, SceneDocument scene, AssetLibrary assets) => SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
    private static DesktopBackdrop Backdrop(RectD bounds, Func<int, int, SKColor> color)
    {
        using var bitmap = new SKBitmap((int)bounds.Width, (int)bounds.Height);
        for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++) bitmap.SetPixel(x, y, color(x, y));
        bitmap.SetImmutable(); return new([new(bounds, SKImage.FromBitmap(bitmap))]);
    }
}
