using SkiaSharp;
using FloatCanvas.Windows;

namespace FloatCanvas.WindowsTests;

internal static class PreviewRasterPublicationTests
{
    internal static void Performance()
    {
        foreach (var size in new[] { (1920, 1080), (3840, 2160) })
        {
            using var rasterizer = new PreviewRasterizer();
            var samples = new List<double>(); var copied = new List<long>();
            int x = 100;
            for (int frame = 0; frame < 70; frame++)
            {
                int oldX = x; x += 3;
                var timer = System.Diagnostics.Stopwatch.StartNew();
                using var pixels = rasterizer.Render([new(0, 0, size.Item1, size.Item2)], false, false, surface =>
                {
                    using var paint = new SKPaint { Color = SKColors.Red };
                    surface.Canvas.DrawRect(x, 100, 100, 60, paint);
                }, [new(oldX, 100, 103, 60)]);
                timer.Stop();
                if (frame >= 10) { samples.Add(timer.Elapsed.TotalMilliseconds); copied.Add(pixels.CopiedBytes); }
            }
            double Percentile(double percentile) => samples.Order().ElementAt((int)Math.Ceiling(samples.Count * percentile) - 1);
            Console.WriteLine($"PREVIEW_BLOCK_PUBLICATION {size.Item1}x{size.Item2} samples=60 p50ms={Percentile(.5):F3} p95ms={Percentile(.95):F3} copiedBytesMax={copied.Max()} activeBytes={rasterizer.ActivePixelBytes} pooledBytes={rasterizer.PooledPixelBytes} rasterBytes={rasterizer.RasterBufferBytes}");
        }
    }

    internal static void Run(Action<bool, string> check)
    {
        using var rasterizer = new PreviewRasterizer();
        using var frame = rasterizer.Render([new(0, 0, 512, 512)], false, false, surface =>
        {
            using var paint = new SKPaint { Color = SKColors.Red };
            surface.Canvas.DrawRect(10, 10, 40, 40, paint);
        });
        check(frame.Tiles.Count == 4, "a full 512px output publishes four independently owned pixel blocks");
        PartialFrames(check);
        RetainedHost(check);
    }

    private static void PartialFrames(Action<bool, string> check)
    {
        using var rasterizer = new PreviewRasterizer { PixelPoolByteLimit = 256 * 256 * 4 };
        PixelBounds[] outputs = [new(-512, 0, 512, 512), new(0, 0, 512, 512)];
        int rectangleX = -500, draws = 0;
        bool visible = true;
        void Draw(SKSurface surface)
        {
            draws++;
            if (!visible) return;
            using var paint = new SKPaint { Color = SKColors.Red };
            surface.Canvas.DrawRect(rectangleX, 10, 50, 40, paint);
        }
        using var first = rasterizer.Render(outputs, false, false, Draw);
        check(draws == 2 && first.Tiles.Count == 8 && first.CopiedBytes == 1024 * 512 * 4,
            "the worker traverses each output once while publishing a complete block map");
        rectangleX = -490; draws = 0;
        using var second = rasterizer.Render(outputs, false, false, Draw, [new(-500, 10, 60, 40)]);
        check(draws == 1 && second.CopiedBytes == 256 * 256 * 4 && second.DamageComparedTo(first).Count == 1,
            "a local movement draws one output and copies only its changed block");
        check(ReferenceEquals(first.Tiles.Single(tile => tile.Bounds.Left == 0 && tile.Bounds.Top == 0),
            second.Tiles.Single(tile => tile.Bounds.Left == 0 && tile.Bounds.Top == 0)),
            "unchanged outputs share immutable published blocks");
        using var noDamage = rasterizer.Render(outputs, false, false, Draw, []);
        check(draws == 1 && noDamage.CopiedBytes == 0 && noDamage.DamageComparedTo(second).Count == 0,
            "an empty invalidation neither draws nor allocates pixel copies");
        rectangleX = -200;
        using var latest = rasterizer.Render(outputs, false, false, Draw, [new(-490, 10, 340, 40)]);
        check(latest.DamageComparedTo(first).Count == 2, "a consumer skipping an intermediate frame still sees both old and new changed blocks");
        using (var pixels = Composite(first))
            check(pixels.GetPixel(17, 20) == SKColors.Red && pixels.GetPixel(322, 20).Alpha == 0,
                "later worker writes cannot mutate an earlier held frame");
        using (var pixels = Composite(latest))
            check(pixels.GetPixel(17, 20).Alpha == 0 && pixels.GetPixel(322, 20) == SKColors.Red,
                "the complete latest map contains erasure and the moved rectangle");
        visible = false;
        using var removed = rasterizer.Render(outputs, false, false, Draw, [new(-200, 10, 50, 40)]);
        using (var pixels = Composite(removed))
            check(pixels.GetPixel(322, 20).Alpha == 0 && removed.CopiedBytes == 256 * 256 * 4,
                "a transparent deletion replaces the formerly opaque pixel block");
        using var retained = removed.Retain();
        removed.Dispose(); rasterizer.Dispose();
        using (var pixels = Composite(retained))
            check(pixels.GetPixel(322, 20).Alpha == 0, "a retained frame outlives both the original frame and worker targets");
        retained.Dispose(); latest.Dispose(); noDamage.Dispose(); second.Dispose(); first.Dispose();
        check(rasterizer.ActivePixelBytes == 0 && rasterizer.PooledPixelBytes == 0,
            "native image release returns every lease after worker disposal");

        using var bounded = new PreviewRasterizer { PixelPoolByteLimit = 256 * 256 * 4 };
        for (int i = 0; i < 20; i++)
        {
            using var item = bounded.Render([new(0, 0, 512, 512)], false, false, surface =>
                surface.Canvas.Clear(i % 2 == 0 ? SKColors.Red : SKColors.Blue));
            check(bounded.PooledPixelBytes <= bounded.PixelPoolByteLimit && bounded.ActivePixelBytes <= 512 * 512 * 4,
                "the free pool and one retained worker map remain bounded " + i);
        }

        static SKBitmap Composite(PreviewRasterFrame frame)
        {
            using var surface = SKSurface.Create(new SKImageInfo(1024, 512, SKColorType.Bgra8888, SKAlphaType.Premul));
            surface.Canvas.Clear(SKColors.Transparent); surface.Canvas.Translate(512, 0); frame.Draw(surface.Canvas);
            using var image = surface.Snapshot(); return SKBitmap.FromImage(image);
        }
    }

    private static void RetainedHost(Action<bool, string> check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                int left = NativeMethods.GetSystemMetrics(76) + NativeMethods.GetSystemMetrics(78) + 4096;
                PixelBounds bounds = new(left, 0, 512, 512);
                var other = new PixelBounds(left + 512, 0, 512, 512);
                using var host = new OverlayHost(OverlayMonitorScope.AllScreens,
                    () => [new("retained", bounds, bounds, 96, 96, true), new("retained-other", other, other, 96, 96, false)], false);
                bool erase = false; SKColor preserved = default, changed = default, between = default;
                host.Draw += surface =>
                {
                    if (!erase) surface.Canvas.Clear(SKColors.Red);
                };
                host.SetVisible(true);
                var submitted = new List<string>();
                OverlayFrameTiming lastFrame = default;
                host.FrameSubmitted += frame => { lastFrame = frame; submitted.AddRange(frame.Outputs.Select(output => output.DeviceName)); };
                host.Draw += surface =>
                {
                    using var pixels = surface.PeekPixels();
                    preserved = pixels.GetPixelColor(300, 300); changed = pixels.GetPixelColor(20, 20);
                    between = pixels.GetPixelColor(75, 75);
                };
                erase = true;
                long before = host.PresentedFrameCount;
                host.SetPresentationStamp(10, 12, 14);
                host.RenderRegions([new(left + 10, 10, 40, 40)]);
                check(host.PresentedFrameCount == before + 1 && host.LastRenderedPixelCount == 40 * 40,
                    "retained presentation submits only the local dirty rectangle");
                check(preserved == SKColors.Red && changed.Alpha == 0,
                    "retained presentation clears transparent damage while preserving pixels outside it");
                check(submitted.SequenceEqual(["retained"]), "retained composition skips an unrelated output");
                check(lastFrame.ContentSequence == 10 && lastFrame.InputSequence == 12 && lastFrame.ControlsSequence == 14,
                    "native submission records the adopted content and independent input/control sequences");
                host.RenderRegions([new(left + 10, 10, 40, 40), new(left + 100, 100, 40, 40)]);
                check(between == SKColors.Red, "disjoint damage preserves pixels between the dirty regions");
                submitted.Clear();
                host.RenderRegions([new(left + 500, 10, 24, 40)]);
                check(submitted.Count == 2 && host.LastRenderedPixelCount == 24 * 40,
                    "cross-output retained damage clips and updates both physical outputs");
                before = host.PresentedFrameCount;
                host.RenderRegions([]);
                check(host.PresentedFrameCount == before, "empty retained damage performs no native submission");
                host.SetVisible(false);
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
