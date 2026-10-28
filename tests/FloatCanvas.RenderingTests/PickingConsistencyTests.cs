using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using SkiaSharp;

internal static class PickingConsistencyTests
{
    internal static void Run(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer(); var assets = new AssetLibrary();
        foreach (bool slanted in new[] { false, true })
        {
            var scene = Scene(slanted); var mapping = CanvasMapping.Create(scene);
            var point = new PointD(80.5, 60.5);
            mapping.TryToPlane(point, out var anchor); mapping.TryGetDepthScale(anchor, out double scale);
            var board = new CanvasElement { Kind = ElementKind.Rectangle, IsBillboard = true,
                BillboardAnchor = anchor, BillboardHeight = 0, Width = 100 / scale, Height = 80 / scale,
                Style = new() { Fill = 0xFFFF0000, StrokeWidth = 0 } };
            var plane = new CanvasElement { Kind = ElementKind.Rectangle, Width = 160, Height = 120,
                ProjectivePlacement = mapping.ScreenToPlane, Style = new() { Fill = 0xFF00FF00, StrokeWidth = 0 } };
            foreach (bool reverse in new[] { false, true })
            {
                scene.Elements = reverse ? [board, plane] : [plane, board];
                SceneValidation.Validate(scene);
                using var pixels = SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
                check($"exact plane/billboard equality shares pixel and picking order slanted={slanted} reverse={reverse}",
                    pixels.GetPixel(80, 60) == (reverse ? SKColors.Lime : SKColors.Red) && renderer.PickDepth(scene, assets, point, 0) == scene.Elements[^1]);
            }
            plane.Style.Fill = 0x8000FF00;
            var second = board.Clone(true); second.Style.Fill = 0xFF0000FF;
            foreach (var elements in new[] { new List<CanvasElement> { board, plane, second }, new List<CanvasElement> { second, board, plane } })
            {
                scene.Elements = elements; SceneValidation.Validate(scene);
                var reference = scene.Clone(); reference.Canvas.DepthPriorityOcclusion = false;
                using var actual = SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
                using var expected = SKBitmap.Decode(renderer.ExportPng(reference, assets, true));
                check($"transparent plane and multiple equal billboards retain exact layer composition slanted={slanted} planeLast={elements[^1] == plane}",
                    actual.GetPixel(80, 60) == expected.GetPixel(80, 60) && renderer.PickDepth(scene, assets, point, 0) == elements[^1]);
            }
            scene.LayerOpacity = 0;
            check($"zero canvas opacity excludes depth picking and tolerance slanted={slanted}",
                renderer.PickDepth(scene, assets, point, 0) is null && renderer.PickDepth(scene, assets, point, 7) is null &&
                !renderer.IntersectsElementInk(scene, assets, board, new(40, 40, 80, 60)));
        }
        PresentedImageLifetime(check);
        SnapshotLeaseCostAndLifetime(check);
    }

    private static void SnapshotLeaseCostAndLifetime(Action<string, bool> check)
    {
        var assets = new AssetLibrary(); var scene = new SceneDocument { Viewport = new(0, 0, 16, 16) };
        using var pixels = SKSurface.Create(new SKImageInfo(2, 2));
        for (int index = 0; index < 512; index++)
        {
            pixels.Canvas.Clear(new SKColor((byte)index, (byte)(index >> 8), 80));
            using var image = pixels.Snapshot(); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            scene.Elements.Add(new() { Kind = ElementKind.Image, AssetId = assets.Add(png.ToArray()), Width = 16, Height = 16 });
        }
        using var worker = new SceneRenderer(); worker.ExportPng(scene, assets, true);
        using var large = worker.CaptureImageSnapshot();
        using var smallWorker = new SceneRenderer();
        var smallScene = new SceneDocument { Viewport = scene.Viewport, Elements = [scene.Elements[0]] };
        smallWorker.ExportPng(smallScene, assets, true); using var small = smallWorker.CaptureImageSnapshot();
        static long Allocations(SceneImageSnapshot snapshot)
        {
            using (var warm = snapshot.Retain()) { }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int count = 0; count < 200; count++) { using var lease = snapshot.Retain(); }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        long smallBytes = Allocations(small), largeBytes = Allocations(large);
        check($"snapshot retain allocation is independent of 512 cached images ({smallBytes} vs {largeBytes} bytes)", largeBytes <= smallBytes + 4096 && largeBytes < 16384);
        var nativeImages = scene.Elements.Select(element =>
        {
            large.TryGet(element.AssetId!, out var image, out _); return image;
        }).ToArray();
        using var retained = large.Retain();
        worker.Dispose(); large.Dispose();
        Parallel.For(0, 16, _ =>
        {
            for (int count = 0; count < 100; count++) { using var lease = retained.Retain(); }
        });
        check("independent collection leases survive renderer/source disposal across threads", nativeImages.All(image => image.Handle != IntPtr.Zero));
        var duplicateRelease = retained.Retain();
        Parallel.For(0, 16, _ => duplicateRelease.Dispose());
        check("concurrent disposal of one lease preserves other leases", nativeImages.All(image => image.Handle != IntPtr.Zero));
        retained.Dispose();
        check("last collection lease releases every owned native image exactly once", nativeImages.All(image => image.Handle == IntPtr.Zero));
        bool rejected = false;
        try { using var invalid = retained.Retain(); }
        catch (ObjectDisposedException) { rejected = true; }
        check("disposed collection lease cannot resurrect released images", rejected && nativeImages.All(image => image.Handle == IntPtr.Zero));
    }

    private static void PresentedImageLifetime(Action<string, bool> check)
    {
        var assets = new AssetLibrary();
        string id = assets.Add(Convert.FromBase64String("R0lGODlhAQABAPIAAAAAAP8AAAD/AAAA////AP///wD///8A/yH/C05FVFNDQVBFMi4wAwEAAAAh+QQJCgAAACwAAAAAAQABAAADAggJACH5BAkKAAAALAAAAAABAAEAAAMCGAkAOw=="), "image/gif");
        var scene = new SceneDocument { Viewport = new(0, 0, 10, 10), Elements = [new() { Kind = ElementKind.Image, AssetId = id, Width = 10, Height = 10 }] };
        using var worker = new SceneRenderer();
        worker.AnimationTime = TimeSpan.FromMilliseconds(150);
        worker.ExportPng(scene, assets, true);
        using var images = worker.CaptureImageSnapshot();
        using var retained = images.Retain();
        images.Dispose();
        worker.AnimationTime = TimeSpan.Zero; worker.ExportPng(scene, assets, true); worker.Dispose();
        using var input = new SceneRenderer { PresentedImages = retained, AnimationTime = TimeSpan.Zero };
        check("presented decoded frame survives worker advancement, eviction and disposal",
            input.IntersectsElementInk(scene, assets, scene.Elements[0], new(4, 4, 1, 1)));
        check("input consumes presented animation frame without codec cold seek or decode", input.ImageDecodeCount == 0 && input.AnimationFrameDecodeCount == 0);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        bool cancelled = false;
        try { input.IntersectsElementInk(scene, assets, scene.Elements[0], scene.Viewport, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        check("raster rectangle query cooperatively cancels before another tile", cancelled);
    }

    private static SceneDocument Scene(bool slanted)
    {
        var matrix = new Projective2D(1, 0, 0, 0, 1, 0, slanted ? .001 : 0, .005, 1);
        return new() { Viewport = new(0, 0, 160, 120), Canvas = new() { DepthPriorityOcclusion = true,
            Projection = new() { Enabled = true, CellSize = 20, Matrix = matrix,
                Quad = new[] { new PointD(0, 0), new(160, 0), new(160, 120), new(0, 120) }.Select(matrix.TransformPoint).ToList() } } };
    }
}
