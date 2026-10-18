using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;

namespace FloatCanvas.WindowsTests;

/// <summary>Explicit opt-in hardware tests. All pixels are generated fixtures; no desktop capture or input.</summary>
internal static class GpuRenderingTests
{
    private static string ResultPath => Path.GetFullPath(Environment.GetEnvironmentVariable("FLOATCANVAS_GPU_RESULTS") ??
        Path.Combine(AppContext.BaseDirectory, "test-results", "gpu-rendering-results.json"));
    private const string LoopGif = "R0lGODlhAwABAPIAAAAAAP8AAAD/AAAA////AP///wD///8A/yH/C05FVFNDQVBFMi4wAwEAAAAh+QQFCgAAACwAAAAAAwABAAADBBgYGAkAIfkEBQoAAAAsAAAAAAMAAQAAAwQ4ODgJADs=";

    internal static void Run(Action<bool, string> check, bool probeOnly)
    {
        var report = new Report { StartedAt = DateTimeOffset.Now, ProbeOnly = probeOnly };
        try
        {
            using var gpu = new GpuRenderContext();
            report.Hardware = gpu.Details;
            using (var buffer = new DibBuffer(8, 8))
            using (gpu.MakeCurrent())
            {
                var surface = gpu.SurfaceFor(buffer);
                check(surface.Context is { IsAbandoned: false } && !string.IsNullOrWhiteSpace(gpu.Details), "A real hardware GPU recording context is available");
                surface.Canvas.Clear(new SKColor(255, 0, 0, 128));
                gpu.ReadPixels(surface, buffer);
                byte[] readback = Copy(buffer);
                check(readback[2] is >= 127 and <= 128 && readback[3] == 128 && readback[0] == 0,
                    "GPU readback writes premultiplied BGRA into the existing DIB");
                gpu.ReleaseSurfaces();
            }
            Console.WriteLine("GPU hardware: " + gpu.Details);
            if (!probeOnly)
            {
                PixelAgreement(gpu, check, report);
                Benchmark(gpu, report);
            }
            report.Passed = true;
        }
        catch (Exception ex)
        {
            report.Failure = ex.ToString();
            throw;
        }
        finally
        {
            report.FinishedAt = DateTimeOffset.Now;
            Directory.CreateDirectory(Path.GetDirectoryName(ResultPath)!);
            File.WriteAllText(ResultPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("GPU report: " + ResultPath);
        }
    }

    private static void PixelAgreement(GpuRenderContext gpu, Action<bool, string> check, Report report)
    {
        var assets = new AssetLibrary();
        var scene = new SceneDocument { Viewport = new(-40, -25, 400, 250), LayerOpacity = .55 };
        scene.Elements = [Box(-28, -12, 110, 80, 0xCCED2727),
            new() { Kind = ElementKind.Ellipse, Width = 130, Height = 95, Transform = Affine2D.Translation(65, 38),
                Style = new() { StrokeWidth = 3, Stroke = 0xFF2747DB, Fill = 0xA020C080 } },
            new() { Kind = ElementKind.RoundedRectangle, Width = 95, Height = 65, CornerRadius = 9,
                Transform = Affine2D.Translation(200, 85).Compose(Affine2D.Rotation(17)),
                Style = new() { StrokeWidth = 2, Stroke = 0xFF71329D, Fill = 0xB0F0B83A } }];
        byte[] cpu = RenderPixels(null, scene, assets, 440, 280, null, TimeSpan.Zero, out _);
        byte[] hardware = RenderPixels(gpu, scene, assets, 440, 280, null, TimeSpan.Zero, out _);
        var geometry = Compare("negative viewport, clip, alpha and group opacity", cpu, hardware);
        report.Pixels.Add(geometry);
        check(geometry.MeanChannelDifference < 1.5 && geometry.FractionPixelsOver32 < .015,
            "CPU and GPU agree on transformed geometry, clipping and layer alpha within edge antialias tolerance");
        check(hardware[(260 * 440 + 420) * 4 + 3] == 0 && hardware[(30 * 440 + 30) * 4 + 3] is >= 110 and <= 114,
            "GPU viewport clipping leaves outside pixels clear and applies layer opacity once");

        var projectionReference = CanvasCalibration.CreateInitial(scene.Viewport, 8, 6);
        check(CanvasCalibration.TryCalibrate(projectionReference,
            [new(0, 0), new(290, 8), new(320, 190), new(12, 205)], scene.Viewport, out var projection, out _),
            "GPU projection fixture obtains a valid calibration");
        projection.Enabled = true; scene.Canvas.Projection = projection;
        scene.Canvas.Grid.Visible = true;
        cpu = RenderPixels(null, scene, assets, 440, 280, null, TimeSpan.Zero, out _);
        hardware = RenderPixels(gpu, scene, assets, 440, 280, null, TimeSpan.Zero, out _);
        var projected = Compare("projected geometry and grid", cpu, hardware); report.Pixels.Add(projected);
        check(projected.MeanChannelDifference < 2 && projected.FractionPixelsOver32 < .025,
            "Real GPU projected geometry and grid agree with CPU within edge tolerance");

        using var backdrop = Backdrop(440, 280);
        scene = new() { Viewport = new(0, 0, 440, 280), LayerOpacity = .7,
            Elements = [Box(35, 35, 100, 100, 0xAAE83030), Mask(ElementKind.LivePixelate, 20, 20, 180, 180, 13),
                Mask(ElementKind.LiveBlur, 140, 40, 220, 180, 7)] };
        cpu = RenderPixels(null, scene, assets, 440, 280, backdrop, TimeSpan.Zero, out _);
        hardware = RenderPixels(gpu, scene, assets, 440, 280, backdrop, TimeSpan.Zero, out var counts);
        var live = Compare("live pixelation and padded Gaussian blur", cpu, hardware); report.Pixels.Add(live);
        check(live.MeanChannelDifference < 2.5 && live.FractionPixelsOver32 < .02,
            "GPU live pixelation and blur preserve CPU lower-layer sampling and mask pixels within filter tolerance");
        check(counts.Gpu >= 3 && counts.Raster == 0,
            "Both live filter intermediates use the actual GPU context without raster fallback");

        string animated = assets.Add(Convert.FromBase64String(LoopGif), "image/gif");
        scene = new() { Viewport = new(0, 0, 440, 280), Elements = [
            new() { Kind = ElementKind.Image, AssetId = animated, Width = 150, Height = 80, Transform = Affine2D.Translation(20, 20),
                Effects = [new() { Kind = ImageEffectKind.Blur, Bounds = new(0, 0, 1, 1), Amount = 1 },
                    new() { Kind = ImageEffectKind.Pixelate, Bounds = new(0, 0, 1, 1), Amount = 2 }] }] };
        byte[]? previous = null;
        foreach (int time in new[] { 0, 100, 0 })
        {
            cpu = RenderPixels(null, scene, assets, 440, 280, null, TimeSpan.FromMilliseconds(time), out _);
            hardware = RenderPixels(gpu, scene, assets, 440, 280, null, TimeSpan.FromMilliseconds(time), out counts);
            var animation = Compare($"animated image effects at {time}ms", cpu, hardware); report.Pixels.Add(animation);
            check(animation.MeanChannelDifference < 1 && animation.FractionPixelsOver32 < .01 && counts.Gpu == 2 && counts.Raster == 0,
                $"Animated image effects at {time}ms use GPU intermediates and match the current CPU source frame");
            if (previous is not null) check(!hardware.AsSpan().SequenceEqual(previous), "GPU animated pixels advance or rewind with caller time");
            previous = hardware;
        }
    }

    private static byte[] RenderPixels(GpuRenderContext? gpu, SceneDocument scene, AssetLibrary assets, int width, int height,
        DesktopBackdrop? backdrop, TimeSpan time, out AllocationCounts counts)
    {
        using var renderer = new SceneRenderer { LiveBackdrop = backdrop, AnimationTime = time };
        using var cache = new OverlaySceneCache(renderer);
        using var buffer = new DibBuffer(width, height);
        try
        {
            Draw(gpu, buffer, cache, scene, assets);
            counts = new(renderer.GpuIntermediateSurfaceCount, renderer.RasterIntermediateSurfaceCount);
            return Copy(buffer);
        }
        finally { if (gpu is not null) using (gpu.MakeCurrent()) gpu.ReleaseSurfaces(); }
    }

    private static void Draw(GpuRenderContext? gpu, DibBuffer buffer, OverlaySceneCache cache, SceneDocument scene, AssetLibrary assets)
    {
        using var current = gpu?.MakeCurrent();
        var surface = gpu?.SurfaceFor(buffer) ?? buffer.Surface;
        var canvas = surface.Canvas;
        canvas.RestoreToCount(1); canvas.ResetMatrix(); canvas.Clear(SKColors.Transparent); canvas.Save();
        canvas.Translate(-(float)scene.Viewport.X, -(float)scene.Viewport.Y);
        cache.DrawOnClearedRasterTarget(surface, scene, assets, 1, 1);
        canvas.RestoreToCount(1); canvas.Flush();
        gpu?.ReadPixels(surface, buffer);
    }

    private static void Benchmark(GpuRenderContext gpu, Report report)
    {
        const int width = 3840, height = 2160;
        var assets = new AssetLibrary();
        var shapes = new SceneDocument { Viewport = new(0, 0, width, height) };
        for (int i = 0; i < 500; i++) shapes.Elements.Add(new()
        {
            Kind = i % 3 == 0 ? ElementKind.Ellipse : ElementKind.RoundedRectangle,
            Width = 95 + i % 130, Height = 55 + i % 85, CornerRadius = 9,
            Transform = Affine2D.Translation(i * 173 % 3600, i * 97 % 1940).Compose(Affine2D.Rotation(i % 30)),
            Style = new() { StrokeWidth = 3, Stroke = 0xFF204070, Fill = 0xB080B8EC }
        });
        using var backdrop = Backdrop(width, height);
        var filters = new SceneDocument { Viewport = shapes.Viewport,
            Elements = [Mask(ElementKind.LivePixelate, 100, 100, 2300, 1600, 18),
                Mask(ElementKind.LiveBlur, 1100, 350, 2200, 1600, 12)] };
        string id = assets.Add(Convert.FromBase64String(LoopGif), "image/gif");
        var sprites = new SceneDocument { Viewport = shapes.Viewport };
        for (int i = 0; i < 4; i++) sprites.Elements.Add(new() { Kind = ElementKind.Image, AssetId = id, Width = 700, Height = 420,
            Transform = Affine2D.Translation(100 + i * 900, 800) });
        foreach (var item in new[] { ("500 ordinary shapes", shapes, (DesktopBackdrop?)null),
            ("large live pixelation and blur", filters, backdrop), ("four animated sprites", sprites, (DesktopBackdrop?)null) })
        {
            report.Performance.Add(Measure(null, item.Item1, item.Item2, assets, item.Item3, width, height));
            report.Performance.Add(Measure(gpu, item.Item1, item.Item2, assets, item.Item3, width, height));
        }
    }

    private static Performance Measure(GpuRenderContext? gpu, string name, SceneDocument scene, AssetLibrary assets,
        DesktopBackdrop? backdrop, int width, int height)
    {
        using var renderer = new SceneRenderer { LiveBackdrop = backdrop };
        using var cache = new OverlaySceneCache(renderer);
        using var buffer = new DibBuffer(width, height);
        try
        {
            for (int i = 0; i < 3; i++) { renderer.AnimationTime = TimeSpan.FromMilliseconds(i * 100); Draw(gpu, buffer, cache, scene, assets); }
            using var process = Process.GetCurrentProcess();
            var elapsed = new double[12];
            TimeSpan cpuStart = process.TotalProcessorTime;
            for (int i = 0; i < elapsed.Length; i++)
            {
                renderer.AnimationTime = TimeSpan.FromMilliseconds(i * 100);
                long start = Stopwatch.GetTimestamp(); Draw(gpu, buffer, cache, scene, assets);
                elapsed[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            process.Refresh();
            double cpuMilliseconds = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
            Array.Sort(elapsed);
            var result = new Performance(name, gpu is null ? "CPU" : "GPU plus DIB readback", width, height, 3, elapsed.Length,
                (elapsed[5] + elapsed[6]) / 2, elapsed[(int)Math.Ceiling(elapsed.Length * .95) - 1], cpuMilliseconds,
                renderer.GpuIntermediateSurfaceCount, renderer.RasterIntermediateSurfaceCount, cache.HitCount);
            Console.WriteLine($"{name}; {result.Backend}; median={result.MedianMilliseconds:F2}ms; p95={result.P95Milliseconds:F2}ms; processCPU={cpuMilliseconds:F2}ms");
            return result;
        }
        finally { if (gpu is not null) using (gpu.MakeCurrent()) gpu.ReleaseSurfaces(); }
    }

    private static byte[] Copy(DibBuffer buffer)
    {
        var pixels = new byte[buffer.ImageInfo.RowBytes * buffer.ImageInfo.Height];
        Marshal.Copy(buffer.Pixels, pixels, 0, pixels.Length); return pixels;
    }

    private static PixelDifference Compare(string name, byte[] cpu, byte[] gpu)
    {
        long sum = 0; int maximum = 0, largePixels = 0;
        for (int offset = 0; offset < cpu.Length; offset += 4)
        {
            int pixelMaximum = 0;
            for (int channel = 0; channel < 4; channel++)
            {
                int difference = Math.Abs(cpu[offset + channel] - gpu[offset + channel]);
                sum += difference; maximum = Math.Max(maximum, difference); pixelMaximum = Math.Max(pixelMaximum, difference);
            }
            if (pixelMaximum > 32) largePixels++;
        }
        return new(name, maximum, (double)sum / cpu.Length, (double)largePixels / (cpu.Length / 4));
    }

    private static CanvasElement Box(double x, double y, double width, double height, uint color) => new()
    { Kind = ElementKind.Rectangle, Width = width, Height = height, Transform = Affine2D.Translation(x, y), Style = new() { StrokeWidth = 0, Fill = color } };
    private static CanvasElement Mask(ElementKind kind, double x, double y, double width, double height, double amount) => new()
    { Kind = kind, Width = width, Height = height, Transform = Affine2D.Translation(x, y),
        Effects = [new() { Kind = kind == ElementKind.LiveBlur ? ImageEffectKind.Blur : ImageEffectKind.Pixelate, Bounds = new(0, 0, 1, 1), Amount = amount }] };
    private static DesktopBackdrop Backdrop(int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height)); surface.Canvas.Clear(SKColors.DarkBlue);
        using var paint = new SKPaint { Color = SKColors.Orange };
        for (int x = 0; x < width; x += 64) surface.Canvas.DrawRect(x, 0, 32, height, paint);
        paint.Color = SKColors.Lime; surface.Canvas.DrawRect(0, height / 2, width, 30, paint);
        return new([new(new(0, 0, width, height), surface.Snapshot())]);
    }

    private sealed record AllocationCounts(long Gpu, long Raster);
    private sealed record PixelDifference(string Scene, int MaximumChannelDifference, double MeanChannelDifference, double FractionPixelsOver32);
    private sealed record Performance(string Scene, string Backend, int Width, int Height, int WarmupFrames, int SampleFrames,
        double MedianMilliseconds, double P95Milliseconds, double ProcessCpuMilliseconds, long GpuIntermediateSurfaces, long RasterIntermediateSurfaces, long RasterTileHits);
    private sealed class Report
    {
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset FinishedAt { get; set; }
        public bool ProbeOnly { get; set; }
        public bool Passed { get; set; }
        public string? Hardware { get; set; }
        public string? Failure { get; set; }
        public List<PixelDifference> Pixels { get; } = [];
        public List<Performance> Performance { get; } = [];
        public string Boundary { get; } = "Own synthetic offscreen pixels only. GPU time includes synchronous readback to the existing DIB; ULW presentation, desktop capture, visible UI, input and user profiles are not exercised.";
    }
}
