using System.Diagnostics;
using System.Text.Json;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

/// <summary>Independent CPU-only hot gesture comparison; the earlier full-render baseline is preserved.</summary>
internal static class OverlayCacheBenchmarks
{
    private const int Frames = 100, WarmupFrames = 8;
    public static int Run(string[] args)
    {
        var started = DateTimeOffset.Now;
        bool clearedRaster = args.Contains("--cleared-raster", StringComparer.Ordinal);
        int outputIndex = Array.IndexOf(args, "--output");
        string output = outputIndex >= 0 && args.Length > outputIndex + 1 ? Path.GetFullPath(args[outputIndex + 1])
            : Path.Combine(Program.RepositoryRoot(), "artifacts", "e2e", "overlay-cache-benchmarks.json");
        var scenarios = new List<object>(); string? failure = null;
        Console.WriteLine("CPU-only overlay cache hot-gesture benchmark. No windows, capture, or input injection.");
        try
        {
            foreach (var (width, height) in new[] { (1920, 1080), (3840, 2160) })
            {
                scenarios.Add(Measure(width, height, [500], "one-middle-object", clearedRaster));
                scenarios.Add(Measure(width, height, [250, 750], "sparse-selection-with-large-z-span", clearedRaster));
            }
        }
        catch (Exception ex) { failure = ex.ToString(); Console.Error.WriteLine(ex); }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, startedAt = started, completedAt = DateTimeOffset.Now,
            environment = Program.Hardware(), processId = Environment.ProcessId,
            measurement = new
            {
                method = "Identical deterministic 100-frame object translations on reused BGRA premultiplied CPU SKSurfaces. Each sample includes transparent clear + production renderer/cache Draw + Flush.",
                warmupFrames = WarmupFrames, measuredFrames = Frames, dpi = 96, scale = 1.0, layerOpacity = .7,
                document = "Existing 1000 mixed vectors (including Chinese text), one 2000-point freehand and five generated 256x256 alpha images.",
                cache = "Static prefix/suffix; moving elements and every element between their z indices are drawn live. Cold first-frame raster build is reported separately.",
                percentile = "Nearest-rank over ascending samples: ceil(p*n)-1.",
                order = "Full-render reference first, raster cache second for each scenario; eight warmup frames before each 100-frame sample set.",
                noPresentation = true, noWpfOrWin32 = true, clearedRaster
            },
            scenarios, failure, completed = failure is null,
            limitations = new[]
            {
                "CPU raster timing is not native application FPS, pointer latency, layered-window presentation cost, or a dual-monitor result.",
                "Four single-process synthetic scenarios are a bounded comparison; real assets, background images, device state and selection z-span can change the benefit.",
                "Prefix/suffix rasterization introduces premultiplied 8-bit rounding. Final frame is checked against the uncached renderer with maximum channel difference <=3.",
                "Static live mode already avoids continuous redraw. This optimization targets repeated frames during active gestures.",
                "The raster cache is additional memory, limited to 128 MiB and independent of renderer image/path caches. Full-tile pixel clear/copy and global-opacity blending still occur."
            }
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Evidence: " + output); return failure is null ? 0 : 1;
    }

    private static object Measure(int width, int height, int[] moving, string name, bool clearedRaster)
    {
        var assets = new AssetLibrary(); var document = Program.MakeScene(width, height, assets); document.LayerOpacity = .7;
        var original = moving.Select(index => document.Elements[index].Transform).ToArray();
        using var reference = new SceneRenderer(); using var renderer = new SceneRenderer();
        using var cache = new OverlaySceneCache(renderer);
        using var fullSurface = Surface(width, height); using var cachedSurface = Surface(width, height);
        void Move(int frame)
        {
            for (int i = 0; i < moving.Length; i++) document.Elements[moving[i]].Transform =
                Affine2D.Translation((frame % 31) * .4, Math.Sin(frame * .1) * 4).Compose(original[i]);
        }
        double FullFrame(int frame)
        {
            Move(frame); long time = Stopwatch.GetTimestamp(); fullSurface.Canvas.Clear(SKColors.Transparent);
            reference.Draw(fullSurface.Canvas, document, assets); fullSurface.Canvas.Flush(); return Stopwatch.GetElapsedTime(time).TotalMilliseconds;
        }
        double CacheFrame(int frame)
        {
            Move(frame); long time = Stopwatch.GetTimestamp(); cachedSurface.Canvas.Clear(SKColors.Transparent);
            if (clearedRaster) cache.DrawOnClearedRasterTarget(cachedSurface, document, assets, 1, 1, moving[0], moving[^1]);
            else cache.Draw(cachedSurface.Canvas, document, assets, 1, 1, moving[0], moving[^1]);
            cachedSurface.Canvas.Flush(); return Stopwatch.GetElapsedTime(time).TotalMilliseconds;
        }
        for (int frame = 0; frame < WarmupFrames; frame++) FullFrame(frame);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var full = Enumerable.Range(0, Frames).Select(FullFrame).ToArray();
        long fullAllocations = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        double coldBuildMilliseconds = CacheFrame(0);
        for (int frame = 0; frame < WarmupFrames; frame++) CacheFrame(frame);
        long buildsBefore = cache.BuildCount, hitsBefore = cache.HitCount;
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var hot = Enumerable.Range(0, Frames).Select(CacheFrame).ToArray();
        long cacheAllocations = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        if (cache.BuildCount != buildsBefore || cache.HitCount - hitsBefore != Frames) throw new InvalidOperationException("Hot frames did not reuse both static segments.");
        int maxDifference = PixelDifference(fullSurface, cachedSurface, width, height);
        if (maxDifference > 3) throw new InvalidOperationException($"{name} final frame differs from reference by {maxDifference}.");
        int ordinaryCacheDifference;
        using (var ordinaryRenderer = new SceneRenderer())
        using (var ordinaryCache = new OverlaySceneCache(ordinaryRenderer))
        using (var ordinarySurface = Surface(width, height))
        {
            ordinarySurface.Canvas.Clear(SKColors.Transparent);
            ordinaryCache.Draw(ordinarySurface.Canvas, document, assets, 1, 1, moving[0], moving[^1]);
            ordinaryCacheDifference = PixelDifference(ordinarySurface, cachedSurface, width, height);
        }
        if (ordinaryCacheDifference != 0) throw new InvalidOperationException($"{name} final frame differs from ordinary cache by {ordinaryCacheDifference}.");
        var fullTiming = Statistics(full); var cachedTiming = Statistics(hot);
        Console.WriteLine($"{width}x{height} {name}: P50 {fullTiming.P50:F3}->{cachedTiming.P50:F3}ms, P95 {fullTiming.P95:F3}->{cachedTiming.P95:F3}ms, cache={cache.CachedRasterBytes:N0} bytes, max pixel delta={maxDifference}.");
        return new
        {
            name, width, height, objects = document.Elements.Count, movingIndices = moving,
            liveSpan = new { first = moving[0], last = moving[^1], count = moving[^1] - moving[0] + 1 },
            fullRenderer = new { milliseconds = fullTiming, allocatedManagedBytes = fullAllocations },
            overlayCache = new { coldBuildMilliseconds, milliseconds = cachedTiming, allocatedManagedBytes = cacheAllocations,
                rasterBytes = cache.CachedRasterBytes, byteLimit = cache.RasterCacheByteLimit, cache.BuildCount, cache.HitCount,
                hotFramesBuiltNoRasters = cache.BuildCount == buildsBefore, hotFrameHits = cache.HitCount - hitsBefore },
            p50Speedup = fullTiming.P50 / cachedTiming.P50, p95Speedup = fullTiming.P95 / cachedTiming.P95,
            pixelVerification = new { maximumPremultipliedChannelDifference = maxDifference, allowedDifference = 3,
                ordinaryCacheDifference, ordinaryCacheAllowedDifference = 0, checkedPixels = width * height }
        };
    }
    private static SKSurface Surface(int width, int height) => SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul))
        ?? throw new InvalidOperationException("Could not create reusable benchmark surface.");
    private static int PixelDifference(SKSurface expected, SKSurface actual, int width, int height)
    {
        using var e = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var a = new SKBitmap(e.Info);
        if (!expected.ReadPixels(e.Info, e.GetPixels(), e.RowBytes, 0, 0) || !actual.ReadPixels(a.Info, a.GetPixels(), a.RowBytes, 0, 0))
            throw new InvalidOperationException("Could not read benchmark pixels.");
        byte[] eb = e.Bytes, ab = a.Bytes; int difference = 0;
        for (int p = 0; p < eb.Length; p++) difference = Math.Max(difference, Math.Abs(eb[p] - ab[p]));
        return difference;
    }
    private static Timing Statistics(double[] values)
    {
        var sorted = values.Order().ToArray();
        return new(values.Length, sorted[0], sorted[^1], values.Average(),
            sorted[(int)Math.Ceiling(sorted.Length * .5) - 1], sorted[(int)Math.Ceiling(sorted.Length * .95) - 1], values);
    }
    private sealed record Timing(int Samples, double Min, double Max, double Mean, double P50, double P95, double[] RawMilliseconds);
}
