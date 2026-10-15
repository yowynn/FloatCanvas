using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class OverlaySceneCacheTests
{
    public static void Run(Action<string, bool> assert)
    {
        var assets = new AssetLibrary(); var document = Scene(assets);
        using var renderer = new SceneRenderer(); using var reference = new SceneRenderer();
        using var cache = new OverlaySceneCache(renderer);
        Compare("committed raster matches full renderer", document, assets, cache, reference, assert);
        long builds = cache.BuildCount;
        Compare("committed scene cache hit", document, assets, cache, reference, assert);
        assert("committed cache reuses one raster surface", cache.BuildCount == builds && cache.HitCount > 0 && cache.CachedRasterBytes == 240L * 180 * 4);
        var transform1 = document.Elements[1].Transform; var transform3 = document.Elements[3].Transform;
        for (int frame = 0; frame < 100; frame++)
        {
            document.Elements[1].Transform = Affine2D.Translation(frame * .25, -frame * .05).Compose(transform1);
            document.Elements[3].Transform = Affine2D.Translation(frame * .25, -frame * .05).Compose(transform3);
            document.LayerOpacity = frame % 3 == 0 ? .4 : .8;
            Compare("noncontiguous selection z-order frame " + frame, document, assets, cache, reference, assert, transaction: 1, first: 1, last: 3);
            if (frame == 0) builds = cache.BuildCount;
        }
        assert("100 gesture frames reuse prefix and suffix without ghosts", cache.BuildCount == builds && cache.HitCount >= 99);
        Compare("commit rebuilds a whole annotation cache", document, assets, cache, reference, assert, revision: 2);
        document.Elements.Add(new() { Kind = ElementKind.Freehand, Points = [new(8, 8)], Style = new() { Stroke = 0xFFFFF000, StrokeWidth = 7 } });
        for (int frame = 0; frame < 8; frame++)
        {
            document.Elements[^1].Points.Add(new(20 + frame * 20, 25 + frame * 10));
            Compare("pending freehand preview " + frame, document, assets, cache, reference, assert, revision: 2, transaction: 2, first: 5, last: 5);
        }
        document.Elements.RemoveAt(5);
        Compare("cancel removes pending stroke pixels", document, assets, cache, reference, assert, revision: 2);
        var beforeErase = document.Clone(); document.Elements.RemoveAt(1);
        Compare("erase bypass removes original object pixels", document, assets, cache, reference, assert, revision: 2, transaction: 3, bypass: true);
        assert("bypass drops every stale raster", cache.CachedRasterBytes == 0);
        Compare("leaving bypass cannot resurrect erased object", document, assets, cache, reference, assert, revision: 2, transaction: 3);
        document = beforeErase;
        Compare("cancel replaces document identity safely", document, assets, cache, reference, assert, revision: 2);
        Compare("undo revision invalidates cached scene", document, assets, cache, reference, assert, revision: 3);
        document.Viewport = new(15.5, 9.25, 130.5, 110.25);
        Compare("fractional viewport is clipped once", document, assets, cache, reference, assert, revision: 3);
        document.Viewport = new(-400, -300, 1200, 900);
        Compare("negative monitor and screen origin", document, assets, cache, reference, assert, revision: 3, tx: 55.5f, ty: 21.25f);
        Compare("second monitor tile", document, assets, cache, reference, assert, revision: 3, tx: -45.5f, ty: -20.25f);
        long tileBytes = cache.CachedRasterBytes;
        Compare("first monitor tile stays cached", document, assets, cache, reference, assert, revision: 3, tx: 55.5f, ty: 21.25f);
        assert("multiple monitor tiles share stamp but retain independent pixels", cache.CachedRasterBytes == tileBytes && tileBytes == 2 * 240L * 180 * 4);
        cache.RasterCacheByteLimit = 240L * 180 * 4;
        assert("budget setter immediately evicts old tiles", cache.CachedRasterBytes <= cache.RasterCacheByteLimit);
        Compare("LRU evicted tile rebuilds correctly", document, assets, cache, reference, assert, revision: 3, tx: 55.5f, ty: 21.25f);
        cache.RasterCacheByteLimit = 100;
        Compare("too-small cache budget falls back safely", document, assets, cache, reference, assert, revision: 3);
        assert("oversize tile does not allocate raster cache", cache.CachedRasterBytes == 0);
        cache.RasterCacheByteLimit = 0;
        Compare("zero budget follows reference renderer", document, assets, cache, reference, assert, revision: 3);
        cache.RasterCacheByteLimit = 128L * 1024 * 1024;
        Compare("non-unit scale falls back safely", document, assets, cache, reference, assert, revision: 3, scale: 1.25f);
        assert("matrix fallback does not retain stale tiles", cache.CachedRasterBytes == 0);
        Compare("canvas rotation follows reference fallback", document, assets, cache, reference, assert, revision: 3, rotation: 17);
        assert("rotation does not allocate incorrectly sampled rasters", cache.CachedRasterBytes == 0);
        Compare("nonrectangular caller clip follows reference", document, assets, cache, reference, assert, revision: 3, roundClip: true);
        assert("nonrectangular clip safely clears raster state", cache.CachedRasterBytes == 0);
        Compare("rectangular inset device clip", document, assets, cache, reference, assert, revision: 3, insetClip: true);
        Compare("fractional antialiased caller clip", document, assets, cache, reference, assert, revision: 3, insetClip: true, antialiasClip: true);
        assert("antialiased clip bypasses flattened edge coverage", cache.CachedRasterBytes == 0);
        Compare("invalid dynamic indices follow reference", document, assets, cache, reference, assert, revision: 3, first: 20, last: 21);
        assert("invalid intent clears cache", cache.CachedRasterBytes == 0);
        Compare("whole-layer opacity zero retains bright background", document, assets, cache, reference, assert, revision: 4, zeroOpacity: true);
        cache.Invalidate(); assert("explicit invalidation releases owned rasters", cache.CachedRasterBytes == 0);
        cache.Dispose();
        assert("cache disposal does not own supplied renderer", renderer.ExportPng(document, assets).Length > 0);
        using var emptyCache = new OverlaySceneCache(renderer); var empty = new SceneDocument { Viewport = new(0, 0, 240, 180) };
        Compare("empty segments render without raster allocation", empty, assets, emptyCache, reference, assert);
        assert("empty segment has no raster bytes or surface builds", emptyCache.CachedRasterBytes == 0 && emptyCache.BuildCount == 0);
        MoreGeometry(assets, renderer, reference, assert);
    }

    private static void MoreGeometry(AssetLibrary assets, SceneRenderer renderer, SceneRenderer reference, Action<string, bool> assert)
    {
        var scene = Scene(assets);
        scene.Elements.Add(new() { Kind = ElementKind.Text, Text = "浮绘 A\n第二行", Width = 100, Height = 80,
            Transform = Affine2D.Translation(26, 35).Compose(Affine2D.Rotation(23)).Compose(Affine2D.Scale(.8, 1.1)),
            Style = new() { Stroke = 0xBBFFFFFF, FontSize = 19 } });
        scene.Elements.Add(new() { Kind = ElementKind.Number, Number = 17, Width = 45, Height = 45,
            Transform = Affine2D.Translation(102, 57).Compose(Affine2D.Rotation(-17)), Style = new() { Stroke = 0xCC1188EF, FontSize = 22, StrokeWidth = 2 } });
        scene.Elements.Add(new() { Kind = ElementKind.Highlighter, Points = [new(-5, -5), new(60, 40), new(110, 0)],
            Transform = Affine2D.Translation(45, 70).Compose(Affine2D.Rotation(12)), Style = new() { Stroke = 0xAAFFFF00, StrokeWidth = 12 } });
        using var cache = new OverlaySceneCache(renderer);
        foreach (var translation in new[] { (0f, 0f), (33.5f, -14.25f), (-25.75f, 3.5f) })
        {
            Compare("text badges highlighter tile " + translation, scene, assets, cache, reference, assert, tx: translation.Item1, ty: translation.Item2);
            Compare("text with dynamic z span " + translation, scene, assets, cache, reference, assert, transaction: 1, first: 1, last: 6, tx: translation.Item1, ty: translation.Item2);
        }
        long builds = cache.BuildCount;
        Compare("whole scene dynamic needs no static raster", scene, assets, cache, reference, assert, transaction: 2, first: 0, last: scene.Elements.Count - 1);
        assert("all-dynamic span allocates no empty prefix or suffix", cache.CachedRasterBytes == 0 && cache.BuildCount == builds);
        scene.Elements[0].Visible = false;
        Compare("hidden prefix has no raster allocation", scene, assets, cache, reference, assert, revision: 2, transaction: 3, first: 1, last: scene.Elements.Count - 1);
        assert("fully hidden prefix remains allocation-free", cache.CachedRasterBytes == 0 && cache.BuildCount == builds);
        scene.Elements[0].Visible = true;
        Compare("visible prefix restored by revision", scene, assets, cache, reference, assert, revision: 3, transaction: 3, first: 1, last: scene.Elements.Count - 1);
        builds = cache.BuildCount;
        scene.Background!.Transform = Affine2D.Translation(17, 11);
        Compare("background changes redraw independently", scene, assets, cache, reference, assert, revision: 3, transaction: 3, first: 1, last: scene.Elements.Count - 1);
        assert("background alone never rebuilds annotation raster", cache.BuildCount == builds);
        cache.RasterCacheByteLimit = 240L * 180 * 4;
        Compare("two static segments exceeding budget use full renderer", scene, assets, cache, reference, assert, revision: 3, transaction: 4, first: 1, last: 2);
        assert("two-segment budget is checked before allocation", cache.CachedRasterBytes == 0 && cache.BuildCount == builds);
    }

    private static void Compare(string name, SceneDocument scene, AssetLibrary assets, OverlaySceneCache cache, SceneRenderer reference, Action<string, bool> assert,
        long revision = 1, long transaction = 0, int first = -1, int last = -1, bool bypass = false, float tx = 0, float ty = 0, float scale = 1, bool insetClip = false, bool zeroOpacity = false, bool antialiasClip = false, float rotation = 0, bool roundClip = false)
    {
        using var expected = SKSurface.Create(new SKImageInfo(240, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var actual = SKSurface.Create(new SKImageInfo(240, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
        double opacity = scene.LayerOpacity; if (zeroOpacity) scene.LayerOpacity = 0;
        foreach (var canvas in new[] { expected.Canvas, actual.Canvas })
        {
            canvas.Clear(SKColors.Transparent); canvas.Translate(tx, ty); canvas.Scale(scale);
            if (rotation != 0) canvas.RotateDegrees(rotation);
            if (insetClip) canvas.ClipRect(antialiasClip ? new(10.25f, 8.5f, 210.75f, 160.5f) : new(10, 8, 210, 160), SKClipOperation.Intersect, antialiasClip);
            if (roundClip)
            {
                using var builder = new SKPathBuilder(); builder.AddCircle(120, 90, 70);
                using var path = builder.Detach(); canvas.ClipPath(path, SKClipOperation.Intersect, true);
            }
        }
        int count = actual.Canvas.SaveCount; var matrix = actual.Canvas.TotalMatrix; var clip = actual.Canvas.DeviceClipBounds;
        reference.Draw(expected.Canvas, scene, assets); cache.Draw(actual.Canvas, scene, assets, revision, transaction, first, last, bypass);
        using var e = new SKBitmap(new SKImageInfo(240, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var a = new SKBitmap(e.Info); expected.ReadPixels(e.Info, e.GetPixels(), e.RowBytes, 0, 0); actual.ReadPixels(a.Info, a.GetPixels(), a.RowBytes, 0, 0);
        byte[] eb = e.Bytes, ab = a.Bytes; int maximum = 0;
        for (int p = 0; p < eb.Length; p++) maximum = Math.Max(maximum, Math.Abs(eb[p] - ab[p]));
        scene.LayerOpacity = opacity;
        if (maximum > 3) throw new InvalidOperationException($"{name}: raster/reference maximum premultiplied channel difference {maximum}.");
        assert(name, maximum <= 3 && actual.Canvas.SaveCount == count && actual.Canvas.TotalMatrix == matrix && actual.Canvas.DeviceClipBounds == clip);
    }

    private static SceneDocument Scene(AssetLibrary assets)
    {
        using var bitmap = new SKBitmap(32, 24);
        for (int y = 0; y < 24; y++) for (int x = 0; x < 32; x++) bitmap.SetPixel(x, y, new SKColor((byte)(x * 7), (byte)(y * 10), 220, (byte)(x < 4 ? 0 : 170)));
        using var image = SKImage.FromBitmap(bitmap); using var png = image.Encode(SKEncodedImageFormat.Png, 100); var id = assets.Add(png.ToArray());
        return new()
        {
            Viewport = new(0, 0, 240, 180), LayerOpacity = .4,
            Background = new() { Kind = ElementKind.Image, AssetId = id, Width = 240, Height = 180 },
            Elements = [
                new() { Kind = ElementKind.Rectangle, Width = 170, Height = 100, Transform = Affine2D.Translation(5, 10), Style = new() { Fill = 0xAAF02020, StrokeWidth = 3 } },
                new() { Kind = ElementKind.Ellipse, Width = 80, Height = 60, Transform = Affine2D.Translation(55, 40), Style = new() { Fill = 0xAA20F020, StrokeWidth = 3, Opacity = .65 } },
                new() { Kind = ElementKind.Rectangle, Width = 100, Height = 40, Transform = Affine2D.Translation(60, 60), Style = new() { Fill = 0xAABB9911, StrokeWidth = 0 } },
                new() { Kind = ElementKind.Image, AssetId = id, Width = 100, Height = 60, Transform = Affine2D.Translation(75, 75).Compose(Affine2D.Rotation(12)), ImageCrop = new(.1, .1, .8, .8), Effects = [new() { Kind = ImageEffectKind.Blur, Bounds = new(.2, .2, .5, .5), Amount = 2 }] },
                new() { Kind = ElementKind.DoubleArrow, Points = [new(0, 0), new(150, 70)], Transform = Affine2D.Translation(35, 40), Style = new() { Stroke = 0xBBFFFFFF, StrokeWidth = 5, ArrowHeadStyle = ArrowHeadStyle.Open } }
            ]
        };
    }
}
