using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using Microsoft.Win32;
using SkiaSharp;

internal static class Program
{
    private const int VectorCount = 1000, PathPointCount = 2000, ImageCount = 5;
    private const int WarmupFrames = 8, MeasuredFrames = 30, StaticCalls = 100, MemoryRounds = 20;

    private static int Main(string[] args)
    {
        if (args.Contains("--stability", StringComparer.Ordinal)) return StabilityProbe.Run(args);
        if (args.Contains("--overlay-cache", StringComparer.Ordinal)) return OverlayCacheBenchmarks.Run(args);
        var started = DateTimeOffset.Now;
        var outputIndex = Array.IndexOf(args, "--output");
        var outputPath = outputIndex >= 0 && args.Length > outputIndex + 1 ? Path.GetFullPath(args[outputIndex + 1])
            : Path.Combine(RepositoryRoot(), "artifacts", "e2e", "rendering-benchmarks.json");
        var scenarios = new List<ScenarioResult>();
        MemoryResult? memory = null;
        string? failure = null;
        Console.WriteLine("CPU-only offscreen rendering. No windows, desktop capture, or input injection.");
        try
        {
            scenarios.Add(MeasureScenario(1920, 1080));
            scenarios.Add(MeasureScenario(3840, 2160));
            memory = MeasureImportPruneDispose();
        }
        catch (Exception ex) { failure = ex.ToString(); Console.Error.WriteLine(ex); }
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, startedAt = started, completedAt = DateTimeOffset.Now,
            environment = Hardware(), processId = Environment.ProcessId,
            measurement = new
            {
                method = "Production SceneRenderer.Draw on one reused CPU raster SKSurface per scenario; clear + Draw + canvas flush measured.",
                dpi = 96, scale = 1.0, warmupFrames = WarmupFrames, measuredFrames = MeasuredFrames,
                percentile = "Nearest-rank over ascending samples: ceil(p*n)-1.",
                geometry = "40x25 grid scales with viewport dimensions; stroke/font sizes scale with viewport width; 1000 mixed vectors + one 2000-point freehand + five generated 256x256 alpha images.",
                noPresentation = true, noWpfOrWin32 = true
            },
            memoryMetricDefinitions = new
            {
                managedBytes = "GC.GetTotalMemory(false), approximate managed live/allocation accounting without forcing a collection for this sample.",
                managedHeapBytes = "GC.GetGCMemoryInfo().HeapSizeBytes, describing the most recent GC, not an instantaneous native-memory counter.",
                privateBytesAndWorkingSet = "Process accounting for managed + native allocations and runtime/OS effects; not attributed independently to Skia.",
                rendererCachedImageBytes = "Production SceneRenderer's decoded/effect image cache bytes; a known owned resource counter."
            },
            scenarios, importPruneDispose = memory, failure,
            completed = failure is null,
            limitations = new[]
            {
                "CPU raster time is not application FPS, pointer latency, native presentation cost, or a dual-monitor performance result.",
                "All objects are visible, but this is a deterministic synthetic document; it does not cover every real user asset, blur effect, or extreme path.",
                "A static call here deliberately asks the renderer to draw again. The application normally avoids static redraws.",
                "Private bytes and working set include managed heap, native allocators, loaded libraries and OS accounting. Native memory is not measured independently.",
                "Twenty import/prune/dispose rounds with explicit periodic collections give a bounded trend only, not a long-duration leak or stability certification.",
                "Hardware metadata is read-only. GPU model is recorded for reproducibility; the GPU is not used for these raster surfaces."
            }
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Evidence: " + outputPath);
        return failure is null ? 0 : 1;
    }

    private static ScenarioResult MeasureScenario(int width, int height)
    {
        var assets = new AssetLibrary();
        var document = MakeScene(width, height, assets);
        using var renderer = new SceneRenderer();
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Could not create reusable CPU surface.");
        for (var i = 0; i < WarmupFrames; i++) DrawFrame(renderer, surface, document, assets);
        var warmCache = Cache(renderer);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var frameTimes = new List<double>(MeasuredFrames);
        for (var i = 0; i < MeasuredFrames; i++) frameTimes.Add(DrawFrame(renderer, surface, document, assets));
        var measuredAllocations = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var measuredCache = Cache(renderer);
        Assert(measuredCache.PathBuilds == warmCache.PathBuilds && measuredCache.ImageDecodes == warmCache.ImageDecodes,
            "Measured frames unexpectedly rebuilt unmodified paths or decoded unchanged images.");
        var staticTimes = new List<double>(StaticCalls);
        for (var i = 0; i < StaticCalls; i++) staticTimes.Add(DrawFrame(renderer, surface, document, assets));
        var staticCache = Cache(renderer);
        Assert(staticCache.PathBuilds == measuredCache.PathBuilds && staticCache.ImageDecodes == measuredCache.ImageDecodes,
            "Static draw calls unexpectedly rebuilt unmodified paths or decoded unchanged images.");
        Assert(staticCache.CachedImageBytes == ImageCount * 256L * 256 * 4, "Expected all five decoded image resources in the production cache.");
        var pixelProbe = VisiblePixelProbe(surface, width, height);
        Assert(pixelProbe.OpaqueOrTranslucentSamples > 20, "Rendered surface has insufficient real nontransparent pixels.");
        var result = new ScenarioResult(width, height, 96, document.Elements.Count,
            document.Elements.GroupBy(e => e.Kind).ToDictionary(g => g.Key.ToString(), g => g.Count()), PathPointCount, assets.Count,
            Statistics(frameTimes), measuredAllocations, warmCache, measuredCache,
            new StaticResult(StaticCalls, Statistics(staticTimes), staticCache, true), pixelProbe);
        Console.WriteLine($"{width}x{height}: {document.Elements.Count} objects, P50={result.FrameMilliseconds.P50:F3}ms, P95={result.FrameMilliseconds.P95:F3}ms, static cache reused.");
        return result;
    }

    private static double DrawFrame(SceneRenderer renderer, SKSurface surface, SceneDocument document, AssetLibrary assets)
    {
        var started = Stopwatch.GetTimestamp();
        surface.Canvas.Clear(SKColors.Transparent);
        renderer.Draw(surface.Canvas, document, assets);
        surface.Canvas.Flush();
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    internal static SceneDocument MakeScene(int width, int height, AssetLibrary assets)
    {
        var document = new SceneDocument { Viewport = new(0, 0, width, height) };
        double sx = width / 1920d, sy = height / 1080d;
        ElementKind[] kinds = [ElementKind.Rectangle, ElementKind.RoundedRectangle, ElementKind.Ellipse, ElementKind.Triangle,
            ElementKind.Arrow, ElementKind.DoubleArrow, ElementKind.Line, ElementKind.Text];
        for (var i = 0; i < VectorCount; i++)
        {
            var kind = kinds[i % kinds.Length];
            var item = new CanvasElement
            {
                Kind = kind, Width = 34 * sx, Height = 25 * sy,
                Transform = Affine2D.Translation((i % 40 * 47 + 5) * sx, (i / 40 * 42 + 5) * sy)
                    .Compose(Affine2D.Rotation(i % 5 * 7)),
                Style = new() { Stroke = i % 2 == 0 ? 0xFF2383E2 : 0xFFEF4444, Fill = 0x305B8DEF,
                    StrokeWidth = 1.5 * sx, FontSize = 11 * sx, FontFamily = "Microsoft YaHei UI" },
                Text = $"标记{i}\nA{i % 10}"
            };
            if (kind is ElementKind.Line or ElementKind.Arrow or ElementKind.DoubleArrow)
                item.Points = [new(0, 0), new(29 * sx, 20 * sy)];
            document.Elements.Add(item);
        }
        document.Elements.Add(new CanvasElement
        {
            Kind = ElementKind.Freehand, Style = new() { Stroke = 0xFF17A673, StrokeWidth = 3 * sx },
            Points = Enumerable.Range(0, PathPointCount).Select(i =>
                new PointD(width * .025 + width * .95 * i / (PathPointCount - 1), height * .5 + Math.Sin(i * .027) * height * .08)).ToList()
        });
        for (var i = 0; i < ImageCount; i++)
        {
            var id = assets.Add(GeneratedImage(i, 256));
            document.Elements.Add(new CanvasElement { Kind = ElementKind.Image, AssetId = id, Width = 150 * sx, Height = 150 * sy,
                Transform = Affine2D.Translation((100 + i * 335) * sx, 840 * sy).Compose(Affine2D.Rotation(i * 5 - 10)) });
        }
        Assert(document.Elements.Count == VectorCount + 1 + ImageCount, "Unexpected scene size.");
        return document;
    }

    private static byte[] GeneratedImage(int seed, int size)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { IsAntialias = true, Color = new SKColor((byte)(30 + seed * 17 % 220), (byte)(60 + seed * 29 % 190), 220, 150) };
        canvas.DrawCircle(size * .5f, size * .5f, size * .42f, paint);
        paint.Color = new SKColor(240, (byte)(seed * 41 % 255), 70, 100);
        canvas.DrawRect(size * .15f, size * .25f, size * .7f, size * .5f, paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidOperationException("Could not encode generated image.");
        return encoded.ToArray();
    }

    private static MemoryResult MeasureImportPruneDispose()
    {
        var samples = new List<MemorySample>();
        Collect();
        samples.Add(MemorySample.Read(0, "baseline-after-scenario-disposal-and-explicit-GC", true));
        var cleanupChecks = 0;
        for (var round = 1; round <= MemoryRounds; round++)
        {
            var assets = new AssetLibrary();
            var document = new SceneDocument { Viewport = new(0, 0, 1280, 720) };
            var renderer = new SceneRenderer();
            using (var surface = SKSurface.Create(new SKImageInfo(1280, 720, SKColorType.Bgra8888, SKAlphaType.Premul))
                ?? throw new InvalidOperationException("Could not create lifecycle surface."))
            {
                try
                {
                    for (var i = 0; i < ImageCount; i++)
                    {
                        var normalized = AssetImport.Normalize(GeneratedImage(round * 7 + i, 512));
                        var id = assets.Add(normalized.PngBytes);
                        document.Elements.Add(new CanvasElement { Kind = ElementKind.Image, AssetId = id, Width = 300, Height = 300,
                            Transform = Affine2D.Translation(i * 190, i % 2 * 200) });
                    }
                    for (var i = 0; i < 3; i++) DrawFrame(renderer, surface, document, assets);
                    Assert(assets.Count == ImageCount && renderer.CachedImageBytes == ImageCount * 512L * 512 * 4,
                        "Lifecycle round did not actually import and decode five images.");
                    samples.Add(MemorySample.Read(round, "after-normalize-import-and-draw", false, assets, renderer));
                    document.Elements.Clear(); assets.Prune([]);
                    renderer.CacheByteLimit = 0;
                    for (var i = 0; i < 4; i++) DrawFrame(renderer, surface, document, assets);
                    Assert(assets.Count == 0 && renderer.CachedImageBytes == 0, "Prune/cache eviction did not release owned resources.");
                    cleanupChecks++;
                    samples.Add(MemorySample.Read(round, "after-prune-and-cache-eviction", false, assets, renderer));
                }
                finally { renderer.Dispose(); }
            }
            samples.Add(MemorySample.Read(round, "after-renderer-and-surface-disposal", false, assets, renderer));
            if (round % 4 == 0)
            {
                Collect();
                samples.Add(MemorySample.Read(round, "after-explicit-periodic-GC", true, assets, renderer));
            }
        }
        var collected = samples.Where(s => s.ExplicitCollection && s.Round > 0).ToArray();
        var first = collected[0]; var last = collected[^1];
        Console.WriteLine($"Lifecycle: {MemoryRounds} rounds, {cleanupChecks} owned cache/asset cleanup checks; post-GC private delta rounds {first.Round}->{last.Round}: {last.PrivateBytes - first.PrivateBytes:N0} bytes (bounded trend only).");
        return new(MemoryRounds, ImageCount, 512, cleanupChecks, samples,
            new { firstRound = first.Round, lastRound = last.Round, managedBytesDelta = last.ManagedBytes - first.ManagedBytes,
                privateBytesDelta = last.PrivateBytes - first.PrivateBytes, workingSetDelta = last.WorkingSetBytes - first.WorkingSetBytes },
            "Explicit full blocking GC before round 1 and after rounds 4,8,12,16,20. Samples before and after collections are retained; no memory stability pass/fail threshold inferred.");
    }

    private static void Collect() { GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); GC.Collect(); }
    private static CacheSample Cache(SceneRenderer renderer) => new(renderer.PathBuildCount, renderer.ImageDecodeCount, renderer.CachedImageBytes);
    private static Timing Statistics(List<double> values)
    {
        var ordered = values.Order().ToArray();
        return new(values.Count, ordered[0], ordered[^1], values.Average(),
            ordered[(int)Math.Ceiling(ordered.Length * .5) - 1], ordered[(int)Math.Ceiling(ordered.Length * .95) - 1], values);
    }
    private static PixelProbe VisiblePixelProbe(SKSurface surface, int width, int height)
    {
        using var snapshot = surface.Snapshot();
        using var pixels = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        if (!snapshot.ReadPixels(pixels.Info, pixels.GetPixels(), pixels.RowBytes, 0, 0)) throw new InvalidOperationException("Could not read CPU raster pixels.");
        int count = 0, sampled = 0;
        for (var y = 3; y < height; y += Math.Max(1, height / 70))
            for (var x = 3; x < width; x += Math.Max(1, width / 100))
            { sampled++; if (pixels.GetPixel(x, y).Alpha > 0) count++; }
        return new(sampled, count);
    }
    internal static object Hardware()
    {
        string? cpu = null; string[] gpu = []; string? metadataFailure = null;
        var warnings = new List<string>();
        var method = "Read-only registry metadata";
        if (OperatingSystem.IsWindows())
        {
            try { (cpu, gpu) = WindowsHardware(); } catch (Exception ex) { warnings.Add(ex.GetType().Name + ": " + ex.Message); }
            if (cpu is null || gpu.Length == 0)
            {
                method = "Read-only Get-CimInstance Win32_Processor/Win32_VideoController using a hidden, noninteractive PowerShell helper; registry fallback warning retained.";
                try { (cpu, gpu) = CimHardware(); }
                catch (Exception ex) { metadataFailure = ex.GetType().Name + ": " + ex.Message; }
            }
        }
        return new { os = RuntimeInformation.OSDescription, framework = RuntimeInformation.FrameworkDescription,
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(), logicalProcessors = Environment.ProcessorCount,
            cpu, gpu, metadataFailure, metadataWarnings = warnings, metadataMethod = method };
    }

    private static (string?, string[]) CimHardware()
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
            "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); [pscustomobject]@{cpu=(Get-CimInstance Win32_Processor -ErrorAction Stop | Select-Object -First 1 -ExpandProperty Name);gpu=@(Get-CimInstance Win32_VideoController -ErrorAction Stop | Select-Object -ExpandProperty Name)} | ConvertTo-Json -Compress" })
            start.ArgumentList.Add(arg);
        using var helper = Process.Start(start) ?? throw new InvalidOperationException("Could not start read-only hardware metadata helper.");
        if (!helper.WaitForExit(10000)) { helper.Kill(); throw new TimeoutException("Hardware metadata helper timed out."); }
        var output = helper.StandardOutput.ReadToEnd();
        var error = helper.StandardError.ReadToEnd();
        if (helper.ExitCode != 0) throw new InvalidOperationException("Hardware metadata helper failed: " + error);
        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        return (root.GetProperty("cpu").GetString()?.Trim(),
            root.GetProperty("gpu").EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>().ToArray());
    }
    [SupportedOSPlatform("windows")]
    private static (string?, string[]) WindowsHardware()
    {
        var cpu = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string;
        using var adapters = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}", false);
        var names = new List<string>();
        foreach (var name in adapters?.GetSubKeyNames() ?? [])
        {
            using var key = adapters!.OpenSubKey(name, false);
            if (key?.GetValue("DriverDesc") is string description && !string.IsNullOrWhiteSpace(description)) names.Add(description);
        }
        return (cpu?.Trim(), names.Distinct(StringComparer.Ordinal).ToArray());
    }
    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Docs"))) directory = directory.Parent;
        return directory?.FullName ?? Environment.CurrentDirectory;
    }
    private static void Assert(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }

    private sealed record Timing(int Samples, double Min, double Max, double Mean, double P50, double P95, List<double> RawMilliseconds);
    private sealed record CacheSample(long PathBuilds, long ImageDecodes, long CachedImageBytes);
    private sealed record PixelProbe(int Samples, int OpaqueOrTranslucentSamples);
    private sealed record StaticResult(int DrawCalls, Timing Milliseconds, CacheSample CacheAfter, bool UnchangedGeometryAndImagesReused);
    private sealed record ScenarioResult(int Width, int Height, int Dpi, int TotalObjects, Dictionary<string, int> ObjectsByKind,
        int LongFreehandPoints, int ImageResources, Timing FrameMilliseconds, long ManagedAllocatedBytesDuringMeasuredFrames,
        CacheSample CacheAfterWarmup, CacheSample CacheAfterMeasurement, StaticResult StaticCalls, PixelProbe RealPixelProbe);
    private sealed record MemoryResult(int Rounds, int ImagesPerRound, int ImagePixelsPerAxis, int OwnedResourceCleanupChecks,
        List<MemorySample> Samples, object PostCollectionEndpointDelta, string CollectionPolicy);
    private sealed record MemorySample(int Round, string Phase, bool ExplicitCollection, long ManagedBytes, long ManagedHeapBytes,
        long ManagedFragmentedBytes, long PrivateBytes, long WorkingSetBytes, int ProcessHandles, int AssetResources,
        long EncodedAssetBytes, long RendererCachedImageBytes)
    {
        public static MemorySample Read(int round, string phase, bool collection, AssetLibrary? assets = null, SceneRenderer? renderer = null)
        {
            using var process = Process.GetCurrentProcess(); process.Refresh();
            var heap = GC.GetGCMemoryInfo();
            return new(round, phase, collection, GC.GetTotalMemory(false), heap.HeapSizeBytes, heap.FragmentedBytes,
                process.PrivateMemorySize64, process.WorkingSet64, process.HandleCount, assets?.Count ?? 0, assets?.TotalBytes ?? 0, renderer?.CachedImageBytes ?? 0);
        }
    }
}
