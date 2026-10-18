using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class AnimatedOverlayCacheTests
{
    private const string LoopGif = "R0lGODlhAwABAPIAAAAAAP8AAAD/AAAA////AP///wD///8A/yH/C05FVFNDQVBFMi4wAwEAAAAh+QQFCgAAACwAAAAAAwABAAADBBgYGAkAIfkEBQoAAAAsAAAAAAMAAQAAAwQ4ODgJADs=";

    internal static void Run(Action<string, bool> check)
    {
        var assets = new AssetLibrary();
        string id = assets.Add(Convert.FromBase64String(LoopGif), "image/gif");
        var scene = new SceneDocument
        {
            Viewport = new(0, 0, 128, 96), LayerOpacity = .55,
            Elements = [Box(0, 0, 128, 96, 0xA0304B90), Sprite(id, 8, 10, 65, 40),
                new() { Kind = ElementKind.Ellipse, Width = 48, Height = 40, Transform = Affine2D.Translation(36, 20),
                    Style = new() { Fill = 0xB030CC70, Stroke = 0xFFEC8020, StrokeWidth = 2 } },
                Sprite(id, 60, 24, 55, 35), Box(42, 33, 40, 40, 0xB0F5C34B)]
        };
        using var renderer = new SceneRenderer();
        using var reference = new SceneRenderer();
        using var cache = new OverlaySceneCache(renderer);
        byte[] first = Compare("multiple animated objects preserve static z-order and alpha", 0);
        long built = cache.BuildCount;
        byte[] second = Compare("animated span updates while both static sides remain cached", 100);
        check("animation changes pixels without rebuilding static prefix or suffix", !first.AsSpan().SequenceEqual(second) &&
            built == 2 && cache.BuildCount == built && cache.CachedRasterBytes == 2L * 128 * 96 * 4);
        scene.LayerOpacity = .3;
        Compare("changing whole-layer opacity still composites cached and animated pixels once", 200);
        check("whole-layer opacity and animation time are independent of the static content key", cache.BuildCount == built);

        // A pointer gesture can begin before or after the automatically animated span.
        for (int i = 0; i < 3; i++)
        {
            scene.Elements[0].Transform = Affine2D.Translation(i * 3, i * 2);
            Compare("gesture before animations merges the complete dynamic span " + i, i * 100, transaction: 1, firstDynamic: 0, lastDynamic: 0);
            if (i == 0) built = cache.BuildCount;
        }
        check("gesture before animations reuses its still-static suffix", cache.BuildCount == built);
        for (int i = 0; i < 3; i++)
        {
            scene.Elements[4].Transform = Affine2D.Translation(42 + i * 2, 33 - i);
            Compare("gesture after animations merges the complete dynamic span " + i, i * 100, transaction: 2, firstDynamic: 4, lastDynamic: 4);
            if (i == 0) built = cache.BuildCount;
        }
        check("gesture after animations reuses its still-static prefix", cache.BuildCount == built);
        scene.Elements[0].Style.Fill = 0xC0903080;
        Compare("committed static changes invalidate animated-scene tiles by revision", 100, revision: 2);
        check("new committed revision rebuilds both static sides", cache.BuildCount == built + 2);
        Compare("invalid gesture indices remain a safe full-render bypass with animations", 100, revision: 2, firstDynamic: 90, lastDynamic: 91);
        check("invalid gesture ownership cannot be concealed by a valid animated span", cache.CachedRasterBytes == 0);

        // The background is already drawn every frame, outside the annotation tile cache.
        scene.Background = Sprite(id, 0, 0, 128, 96);
        scene.Elements = [Box(30, 20, 40, 40, 0xA0F5C34B)];
        first = Compare("animated background first frame keeps annotations static", 0, revision: 3);
        built = cache.BuildCount;
        second = Compare("animated background next frame redraws beneath cached annotations", 100, revision: 3);
        check("animated background changes without invalidating the annotation tile", !first.AsSpan().SequenceEqual(second) && cache.BuildCount == built &&
            cache.CachedRasterBytes == 128L * 96 * 4);

        byte[] Compare(string name, int milliseconds, long revision = 1, long transaction = 0, int firstDynamic = -1, int lastDynamic = -1)
        {
            var info = new SKImageInfo(128, 96, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var expected = SKSurface.Create(info);
            using var actual = SKSurface.Create(info);
            expected.Canvas.Clear(SKColors.Transparent); actual.Canvas.Clear(SKColors.Transparent);
            renderer.AnimationTime = reference.AnimationTime = TimeSpan.FromMilliseconds(milliseconds);
            reference.Draw(expected.Canvas, scene, assets);
            cache.DrawOnClearedRasterTarget(actual, scene, assets, revision, transaction, firstDynamic, lastDynamic);
            using var expectedPixels = new SKBitmap(info); using var actualPixels = new SKBitmap(info);
            expected.ReadPixels(info, expectedPixels.GetPixels(), expectedPixels.RowBytes, 0, 0);
            actual.ReadPixels(info, actualPixels.GetPixels(), actualPixels.RowBytes, 0, 0);
            byte[] e = expectedPixels.Bytes, a = actualPixels.Bytes;
            int maximum = 0; for (int i = 0; i < e.Length; i++) maximum = Math.Max(maximum, Math.Abs(e[i] - a[i]));
            check(name, maximum <= 3);
            return a;
        }
    }

    private static CanvasElement Box(double x, double y, double width, double height, uint fill) => new()
    { Kind = ElementKind.Rectangle, Width = width, Height = height, Transform = Affine2D.Translation(x, y), Style = new() { Fill = fill, StrokeWidth = 0 } };
    private static CanvasElement Sprite(string id, double x, double y, double width, double height) => new()
    { Kind = ElementKind.Image, AssetId = id, Width = width, Height = height, Transform = Affine2D.Translation(x, y), Style = new() { Opacity = .75 } };
}
