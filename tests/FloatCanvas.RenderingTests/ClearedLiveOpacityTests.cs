using System.Text.Json;
using System.Runtime.InteropServices;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class ClearedLiveOpacityTests
{
    private const int Width = 240, Height = 180;
    public static void Run(Action<string, bool> assert)
    {
        Console.WriteLine($"Raster opacity managed vector acceleration: {System.Numerics.Vector.IsHardwareAccelerated}");
        var assets = new AssetLibrary();
        string imageId = Image(assets);
        var scene = Scene(imageId);
        using var reference = new SceneRenderer();
        using var cachedRenderer = new SceneRenderer();
        using var optimizedRenderer = new SceneRenderer();
        using var ordinary = new OverlaySceneCache(cachedRenderer);
        using var optimized = new OverlaySceneCache(optimizedRenderer);
        foreach (double opacity in Enumerable.Range(0, 256).Select(alpha => alpha / 255d))
        {
            scene.LayerOpacity = opacity;
            Compare($"cleared live opacity {opacity:R}", scene, assets, reference, ordinary, optimized, assert);
        }
        assert("cleared-live opt-in actually used its in-place opacity path", optimized.InPlaceOpacityFrameCount == 254 && ordinary.InPlaceOpacityFrameCount == 0);
        long builds = optimized.BuildCount;
        scene.LayerOpacity = .7;
        Compare("cleared live cached frame keeps overlap and control alpha", scene, assets, reference, ordinary, optimized, assert, controls: true);
        assert("changing group opacity reuses the same annotation raster", optimized.BuildCount == builds);

        var session = new EditorSession(scene, assets);
        session.BeginTransaction("move one image");
        session.Document.Elements[2].Transform = Affine2D.Translation(18, -7).Compose(session.Document.Elements[2].Transform);
        session.NotifyChanged();
        CompareSession("cleared live transaction preview", session, 2, 2);
        session.Document.Elements[2].Transform = Affine2D.Rotation(4).Compose(session.Document.Elements[2].Transform);
        session.NotifyChanged();
        CompareSession("cleared live hot preview retains prefix suffix overlap", session, 2, 2);
        session.Cancel();
        CompareSession("cleared live cancellation removes preview pixels", session);
        session.BeginTransaction("multi selection");
        foreach (int index in new[] { 0, 3 })
            session.Document.Elements[index].Transform = Affine2D.Translation(5.25, 3.5).Compose(session.Document.Elements[index].Transform);
        session.NotifyChanged();
        CompareSession("cleared live sparse selection keeps continuous z order", session, 0, 3);
        session.Commit();
        CompareSession("cleared live committed scene", session);
        session.Undo(); CompareSession("cleared live undo restores previous pixels", session);
        session.Redo(); CompareSession("cleared live redo restores committed pixels", session);
        session.BeginTransaction("erase unknown span"); session.Document.Elements.RemoveAt(1); session.NotifyChanged();
        CompareSession("cleared live unknown mutation uses reference bypass", session, bypass: true);
        assert("cleared live bypass releases raster ownership", optimized.CachedRasterBytes == 0 && ordinary.CachedRasterBytes == 0);
        session.Cancel(); CompareSession("cleared live bypass cancellation rebuilds clean scene", session);

        scene = Scene(imageId); scene.LayerOpacity = .7;
        Compare("cleared live integer translated monitor tile", scene, assets, reference, ordinary, optimized, assert, tx: -17, ty: -9);
        Compare("cleared live fractional translated tile", scene, assets, reference, ordinary, optimized, assert, tx: 2.25f, ty: -1.75f);
        scene.Viewport = new(9.25, 7.75, 211.5, 158.25);
        Compare("cleared live fractional hard viewport", scene, assets, reference, ordinary, optimized, assert, revision: 2);
        Compare("cleared live inset hard caller clip", scene, assets, reference, ordinary, optimized, assert, revision: 2, hardClip: true);
        Compare("cleared live antialiased clip safely falls back", scene, assets, reference, ordinary, optimized, assert, revision: 2, antialiasClip: true);
        Compare("cleared live nonrectangular clip safely falls back", scene, assets, reference, ordinary, optimized, assert, revision: 2, roundClip: true);
        Compare("cleared live scaling safely falls back", scene, assets, reference, ordinary, optimized, assert, revision: 2, scale: 1.1f);
        scene = Scene(imageId); scene.LayerOpacity = .7;
        scene.Viewport = new(-240, -180, Width, Height);
        foreach (var element in scene.Elements) element.Transform = Affine2D.Translation(-240, -180).Compose(element.Transform);
        Compare("cleared live negative screen origin", scene, assets, reference, ordinary, optimized, assert, revision: 3, tx: 240, ty: 180);

        scene = Scene(imageId); scene.LayerOpacity = .7;
        optimized.RasterCacheByteLimit = ordinary.RasterCacheByteLimit = 1;
        Compare("cleared live budget fallback keeps opacity", scene, assets, reference, ordinary, optimized, assert, revision: 4);
        optimized.RasterCacheByteLimit = ordinary.RasterCacheByteLimit = 128L * 1024 * 1024;
        long inPlaceBeforeBackground = optimized.InPlaceOpacityFrameCount;
        scene.Background = new() { Kind = ElementKind.Image, AssetId = imageId, Width = Width, Height = Height };
        Compare("cleared live with background retains ordinary composition", scene, assets, reference, ordinary, optimized, assert, revision: 5);
        scene.Kind = DocumentKind.FrozenCapture;
        Compare("frozen background remains unaffected by layer opacity", scene, assets, reference, ordinary, optimized, assert, revision: 6);
        scene.LayerOpacity = 0;
        Compare("frozen zero annotation opacity retains background", scene, assets, reference, ordinary, optimized, assert, revision: 6);
        assert("background and frozen draws never enter in-place opacity", optimized.InPlaceOpacityFrameCount == inPlaceBeforeBackground);

        scene = Scene(imageId); scene.LayerOpacity = .7;
        Compare("ordinary Draw retains a nontransparent destination", scene, assets, reference, ordinary, optimized, assert,
            revision: 7, existingDestination: true);
        string beforeDocument = JsonSerializer.Serialize(scene);
        byte[] png = reference.ExportPng(scene, assets, true);
        Compare("cleared live does not mutate export inputs", scene, assets, reference, ordinary, optimized, assert, revision: 8);
        assert("cleared live preserves document and original export bytes", beforeDocument == JsonSerializer.Serialize(scene) && png.AsSpan().SequenceEqual(reference.ExportPng(scene, assets, true)));
        RasterRowsAndSnapshots(assert);

        void CompareSession(string name, EditorSession current, int first = -1, int last = -1, bool bypass = false) =>
            Compare(name, current.Document, assets, reference, ordinary, optimized, assert,
                current.SceneRevision, current.TransactionId, first, last, bypass);
    }

    private static void RasterRowsAndSnapshots(Action<string, bool> assert)
    {
        const int width = 239, height = 7, stride = width * 4 + 12;
        var storage = Marshal.AllocHGlobal(stride * height);
        try
        {
            Marshal.Copy(Enumerable.Repeat((byte)0xA5, stride * height).ToArray(), 0, storage, stride * height);
            using var target = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), storage, stride)!;
            using var expected = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul))!;
            using var ordinaryRenderer = new SceneRenderer(); using var fastRenderer = new SceneRenderer();
            using var ordinary = new OverlaySceneCache(ordinaryRenderer); using var fast = new OverlaySceneCache(fastRenderer);
            var assets = new AssetLibrary();
            var scene = new SceneDocument { Viewport = new(0, 0, width, height), LayerOpacity = .7,
                Elements = [new() { Kind = ElementKind.Rectangle, Width = width, Height = height,
                    Style = new() { Fill = 0xBBE7396F, StrokeWidth = 0 } }] };
            bool exact = true, paddingIntact = true;
            for (int alpha = 1; alpha < 255; alpha++)
            {
                scene.LayerOpacity = alpha / 255d;
                target.Canvas.Clear(SKColors.Transparent); expected.Canvas.Clear(SKColors.Transparent);
                fast.DrawOnClearedRasterTarget(target, scene, assets, 1, 0);
                ordinary.Draw(expected.Canvas, scene, assets, 1, 0);
                using var e = expected.PeekPixels(); using var a = target.PeekPixels();
                for (int y = 0; y < height; y++)
                {
                    exact &= e.GetPixelSpan().Slice(y * e.RowBytes, width * 4).SequenceEqual(a.GetPixelSpan().Slice(y * stride, width * 4));
                    for (int p = width * 4; p < stride; p++) paddingIntact &= Marshal.ReadByte(storage, y * stride + p) == 0xA5;
                }
            }
            assert("raster opacity preserves odd row tails for every alpha", exact);
            assert("raster opacity never modifies stride padding", paddingIntact);
            using var retained = target.Snapshot(); using var retainedBefore = SKBitmap.FromImage(retained);
            byte[] before = retainedBefore.Bytes;
            target.Canvas.Clear(SKColors.Transparent); scene.LayerOpacity = .3;
            fast.DrawOnClearedRasterTarget(target, scene, assets, 1, 0);
            using var retainedAfter = SKBitmap.FromImage(retained);
            assert("cleared raster drawing preserves prior immutable snapshots", before.AsSpan().SequenceEqual(retainedAfter.Bytes));
        }
        finally { Marshal.FreeHGlobal(storage); }
    }

    private static void Compare(string name, SceneDocument scene, AssetLibrary assets, SceneRenderer reference,
        OverlaySceneCache ordinary, OverlaySceneCache optimized, Action<string, bool> assert,
        long revision = 1, long transaction = 0, int first = -1, int last = -1, bool bypass = false,
        float tx = 0, float ty = 0, float scale = 1, bool hardClip = false, bool antialiasClip = false,
        bool roundClip = false, bool controls = false, bool existingDestination = false)
    {
        using var expected = Surface(); using var originalCache = Surface(); using var actual = Surface();
        foreach (var canvas in new[] { expected.Canvas, originalCache.Canvas, actual.Canvas })
        {
            canvas.Clear(existingDestination ? new SKColor(47, 23, 83, 255) : SKColors.Transparent);
            canvas.Translate(tx, ty); canvas.Scale(scale);
            if (hardClip || antialiasClip) canvas.ClipRect(new(10.25f, 8.5f, 220.75f, 165.5f), SKClipOperation.Intersect, antialiasClip);
            if (roundClip)
            {
                using var builder = new SKPathBuilder(); builder.AddCircle(120, 90, 70);
                using var path = builder.Detach(); canvas.ClipPath(path, SKClipOperation.Intersect, true);
            }
        }
        int saves = actual.Canvas.SaveCount; var matrix = actual.Canvas.TotalMatrix; var clip = actual.Canvas.DeviceClipBounds;
        reference.Draw(expected.Canvas, scene, assets);
        ordinary.Draw(originalCache.Canvas, scene, assets, revision, transaction, first, last, bypass);
        if (existingDestination) optimized.Draw(actual.Canvas, scene, assets, revision, transaction, first, last, bypass);
        else optimized.DrawOnClearedRasterTarget(actual, scene, assets, revision, transaction, first, last, bypass);
        if (controls)
            foreach (var canvas in new[] { expected.Canvas, originalCache.Canvas, actual.Canvas })
            {
                using var paint = new SKPaint { Color = SKColors.Cyan };
                canvas.DrawRect(2, 2, 8, 8, paint);
            }
        using var e = Pixels(expected); using var legacy = Pixels(originalCache); using var a = Pixels(actual);
        int optimizationDelta = Difference(legacy.Bytes, a.Bytes), referenceDelta = Difference(e.Bytes, a.Bytes);
        assert(name + " exact ordinary-cache pixels", optimizationDelta == 0 && referenceDelta <= 3 &&
            actual.Canvas.SaveCount == saves && actual.Canvas.TotalMatrix == matrix && actual.Canvas.DeviceClipBounds == clip);
        if (controls) assert("post-scene controls retain full opacity", a.GetPixel(5, 5) == SKColors.Cyan);
    }

    private static int Difference(byte[] expected, byte[] actual)
    {
        int maximum = 0;
        for (int i = 0; i < expected.Length; i++) maximum = Math.Max(maximum, Math.Abs(expected[i] - actual[i]));
        return maximum;
    }
    private static SKSurface Surface() => SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul))
        ?? throw new OutOfMemoryException();
    private static SKBitmap Pixels(SKSurface surface)
    {
        var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (!surface.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0)) { bitmap.Dispose(); throw new InvalidOperationException("ReadPixels failed."); }
        return bitmap;
    }
    private static string Image(AssetLibrary assets)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(32, 24, SKColorType.Bgra8888, SKAlphaType.Premul));
        for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++)
            bitmap.SetPixel(x, y, new SKColor((byte)(x * 7), (byte)(y * 10), 220, (byte)(x < 4 ? 0 : 170)));
        using var image = SKImage.FromBitmap(bitmap); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return assets.Add(png.ToArray());
    }
    private static SceneDocument Scene(string imageId) => new()
    {
        Viewport = new(0, 0, Width, Height),
        Elements =
        [
            new() { Kind = ElementKind.Rectangle, Width = 100, Height = 75, Transform = Affine2D.Translation(10, 15), Style = new() { Fill = 0xFFFF0000, StrokeWidth = 0 } },
            new() { Kind = ElementKind.Rectangle, Width = 100, Height = 75, Transform = Affine2D.Translation(45, 40), Style = new() { Fill = 0xAA20F020, StrokeWidth = 3, Opacity = .65 } },
            new() { Kind = ElementKind.Image, AssetId = imageId, Width = 90, Height = 60, Transform = Affine2D.Translation(85, 68).Compose(Affine2D.Rotation(17)) },
            new() { Kind = ElementKind.DoubleArrow, Points = [new(0, 0), new(115, 65)], Transform = Affine2D.Translation(25, 45), Style = new() { Stroke = 0xBBFFFFFF, StrokeWidth = 5, ArrowHeadStyle = ArrowHeadStyle.Open } },
            new() { Kind = ElementKind.Text, Text = "浮绘 A", Width = 90, Transform = Affine2D.Translation(100, 15).Compose(Affine2D.Rotation(-9)), Style = new() { FontSize = 20, Stroke = 0xFF2383E2 } },
            new() { Kind = ElementKind.Freehand, Points = [new(0, 0), new(25, 12), new(49, -7), new(83, 9)], Transform = Affine2D.Translation(110, 130), Style = new() { Stroke = 0xC0EF4444, StrokeWidth = 8 } }
        ]
    };
}
