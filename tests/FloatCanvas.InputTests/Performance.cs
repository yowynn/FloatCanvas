using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using FloatCanvas.Core;
using FloatCanvas.PlatformTests;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using Microsoft.Win32;
using SkiaSharp;
using Wpf = System.Windows;

namespace FloatCanvas.InputTests;

internal static partial class Program
{
    private const int PerformanceVectorCount = 1000, PerformancePathPoints = 2000, PerformanceImageCount = 5;
    private const int PerformanceMoves = 80, PerformanceWarmupMoves = 8;
    private static bool PerformanceOnly;
    private static string PerformanceReport => Path.Combine(Root, "artifacts", "e2e", "input-performance.json");
    private static readonly List<PerformanceFrame> PerformanceFrames = [];
    private static CacheCounters? BeforeGestureCache, WarmGestureCache, HotGestureCache, AfterGestureCache, AfterUndoCache;
    private static Guid PerformanceActiveId;
    private static int PerformanceSceneCount, PerformanceUndoBefore, PerformanceUndoAfter, PerformanceUndoAfterUndo;
    private static double PerformanceFixtureCommitMilliseconds;
    private static double? PerformanceRecoveryIntervalSeconds;
    private static object? PerformanceHardware;
    private static string[] OtherApplicationProcesses = [];
    private static readonly DateTimeOffset PerformanceStartedAt = DateTimeOffset.Now;

    private static async Task RunPerformance(Wpf.Application app)
    {
        // Do not close, focus or send input to an independently running application or test fixture.
        OtherApplicationProcesses = new[] { "FloatCanvas", "FloatCanvas.SessionTests", "FloatCanvas.InputTests", "FloatCanvas.PlatformTests" }
            .SelectMany(Process.GetProcessesByName).Where(p => p.Id != Environment.ProcessId)
            .Select(p => { using (p) return p.ProcessName + ":" + p.Id; }).ToArray();
        Check(OtherApplicationProcesses.Length == 0, "No other FloatCanvas application or desktop test process is running before creating our fixture");
        PerformanceHardware = ReadPerformanceHardware();
        Lower = new NativeTestWindow(); Lower.Activate();
        await WaitForOwnFixtureActivation();
        // Keep the purpose-built native client above unrelated desktop windows before the overlay is created.
        Native.SetWindowPos(Lower.Handle, new nint(-1), 0, 0, 0, 0, 0x0013);
        Session = Activator.CreateInstance(DesktopType, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, [app], null)!;
        await CallAsync("InitializeDocumentsAsync", true);
        if (Field<DispatcherTimer?>("_documentRecoveryTimer") is { } recoveryTimer)
        { recoveryTimer.Stop(); recoveryTimer.Interval = TimeSpan.FromHours(1); PerformanceRecoveryIntervalSeconds = recoveryTimer.Interval.TotalSeconds; }
        Host = Field<OverlayHost>("_host"); Monitors = Host.Monitors;
        // This reads the existing production object. No alternate rendering or production diagnostic API is installed.
        _ = ReadOverlayCache();
        var origin = Lower.ScreenPoint(190, 220);
        Anchor = new(origin.X + 60, origin.Y + 40);
        var last = new Native.Point(Anchor.X + PerformanceMoves * 2, Anchor.Y + (int)Math.Round(Math.Sin(PerformanceMoves * .12) * 12));
        Check(Lower.OwnsPoint(new(origin.X - 25, origin.Y - 40)) && Lower.OwnsPoint(new(last.X + 85, last.Y + 70)),
            "The active rectangle, complete drag and selection controls fit strictly inside our own native client");
        var monitor = Monitors.Single(m => m.Bounds.Contains(new(Anchor.X, Anchor.Y)));
        var editor = Active();
        var fixture = CreatePerformanceScene(monitor, origin, Field<AssetLibrary>("_assets"));
        PerformanceActiveId = fixture.Elements[500].Id;
        var commitStart = Stopwatch.GetTimestamp();
        editor.Execute("插入自建性能验收样例", document =>
        {
            document.Elements = fixture.Elements;
            document.Background = null; document.Viewport = fixture.Viewport; document.LayerOpacity = 1;
        });
        PerformanceFixtureCommitMilliseconds = Stopwatch.GetElapsedTime(commitStart).TotalMilliseconds;
        PerformanceSceneCount = editor.Document.Elements.Count;
        Check(PerformanceSceneCount == 1006 && editor.Document.Elements.Count(e => e.Kind == ElementKind.Image) == 5 &&
            editor.Document.Elements.Single(e => e.Kind == ElementKind.Freehand).Points.Count == 2000,
            "One production Execute committed 1000 mixed vectors, one 2000-point path and five generated alpha images");
        Check(editor.UndoCount == 1, "The entire synthetic fixture insertion is a single committed history item");
        SelectTool("Select"); editor.SetSelection([PerformanceActiveId]);
        Call("SetEditing", true); await Task.Delay(100); await ActivateInput();
        await WaitForRenderToSettle();
        var active = editor.Document.Elements.Single(e => e.Id == PerformanceActiveId);
        var baseline = active.Transform;
        Check(editor.HitTest(new(Anchor.X, Anchor.Y))?.Id == PerformanceActiveId,
            "The safe native drag point hits the intended retained rectangle, not a synthetic neighbour");
        BeforeGestureCache = ReadOverlayCache();
        PerformanceUndoBefore = editor.UndoCount;
        Move(Anchor); await Task.Delay(20);
        var downFrames = Host.PresentedFrameCount;
        Down();
        await WaitPerformance(() => editor.IsTransactionActive && Host.PresentedFrameCount > downFrames,
            "The production pointer-down did not begin and present a selection transaction");
        Check(editor.Selection.SetEquals([PerformanceActiveId]), "Real pointer-down keeps exactly the intended rectangle selected");
        Check(editor.IsTransactionActive, "The measured movement is inside one production pointer gesture");

        PendingMove? pending = null;
        void ObserveMove(OverlayPointerEvent e)
        {
            if (pending is not { ObservedAt: 0 } sample || e.Kind != OverlayPointerKind.Move ||
                Math.Abs(e.Position.X - sample.Expected.X) > 1 || Math.Abs(e.Position.Y - sample.Expected.Y) > 1) return;
            // This observer is appended after DesktopSession's actual EditorController handler.
            // The separate injection-to-present metric includes the preceding controller work too.
            sample.ObservedAt = Stopwatch.GetTimestamp(); sample.ActualX = e.Position.X; sample.ActualY = e.Position.Y;
            sample.FramesAtEvent = Host!.PresentedFrameCount;
        }
        Host.Pointer += ObserveMove;
        try
        {
            for (int step = 1; step <= PerformanceMoves; step++)
            {
                var point = new Native.Point(Anchor.X + step * 2, Anchor.Y + (int)Math.Round(Math.Sin(step * .12) * 12));
                pending = new PendingMove(point) { InjectedAt = Stopwatch.GetTimestamp() };
                Move(point);
                int polls = 0;
                await WaitPerformance(() =>
                {
                    polls++;
                    return pending.ObservedAt != 0 && Host.PresentedFrameCount > pending.FramesAtEvent;
                }, $"Move {step} was not observed and presented by the actual production session");
                long observedAt = Stopwatch.GetTimestamp();
                var cache = ReadOverlayCache();
                PerformanceFrames.Add(new(step, step <= PerformanceWarmupMoves, point.X, point.Y, pending.ActualX, pending.ActualY,
                    Host.LastRenderMilliseconds, Stopwatch.GetElapsedTime(pending.ObservedAt, observedAt).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(pending.InjectedAt, observedAt).TotalMilliseconds,
                    Host.PresentedFrameCount - pending.FramesAtEvent, polls, cache));
                pending = null;
                if (step == PerformanceWarmupMoves) WarmGestureCache = cache;
            }
            HotGestureCache = ReadOverlayCache();
        }
        finally { Host.Pointer -= ObserveMove; Up(); }
        await WaitPerformance(() => !editor.IsTransactionActive, "Actual pointer-up left an uncommitted selection transaction");
        await WaitForRenderToSettle(); AfterGestureCache = ReadOverlayCache();
        PerformanceUndoAfter = editor.UndoCount;
        Check(PerformanceFrames.Count == 80 && PerformanceFrames.All(f => f.PresentedOutputsAfterEvent >= 1),
            "All 80 real pointer moves were individually observed before a later production presentation");
        Check(WarmGestureCache!.BuildCount == HotGestureCache!.BuildCount &&
            PerformanceFrames.Skip(PerformanceWarmupMoves).All(f => f.Cache.BuildCount == WarmGestureCache.BuildCount),
            "After eight warm moves, all 72 measured hot moves reuse static rasters without another cache build");
        Check(HotGestureCache.HitCount > WarmGestureCache.HitCount && HotGestureCache.CachedRasterBytes > 0,
            "Hot production frames actually hit a nonempty static prefix/suffix raster cache");
        Check(PerformanceUndoAfter == PerformanceUndoBefore + 1,
            "The complete 80-move real native drag creates exactly one additional undo item");
        var moved = editor.Document.Elements.Single(e => e.Id == PerformanceActiveId);
        Check(Math.Abs(moved.Transform.OffsetX - baseline.OffsetX - (last.X - Anchor.X)) <= 2 &&
            Math.Abs(moved.Transform.OffsetY - baseline.OffsetY - (last.Y - Anchor.Y)) <= 2,
            "The production retained rectangle follows the full measured physical-pixel drag");
        Check(Native.GetCapture() == 0, "The measured pointer gesture releases native mouse capture");
        await Undo(); PerformanceUndoAfterUndo = editor.UndoCount; await WaitForRenderToSettle(); AfterUndoCache = ReadOverlayCache();
        Check(PerformanceUndoAfterUndo == PerformanceUndoBefore && Near(editor.Document.Elements.Single(e => e.Id == PerformanceActiveId).Transform, baseline) &&
            editor.Document.Elements.Count == 1006,
            "One actual guarded Ctrl+Z restores the rectangle while retaining the entire 1006-object scene");
        Check(Lower.Counts.Clicks == 0 && Lower.Counts.DoubleClicks == 0 && Lower.Counts.DragMoves == 0,
            "Measured native drawing input never leaked to the lower fixture");
        Call("SetEditing", false);
        var timing = PerformanceStatistics(PerformanceFrames.Skip(PerformanceWarmupMoves).Select(f => f.HostRenderMilliseconds));
        Console.WriteLine($"Production hot render: P50={timing.P50:F3}ms; P95={timing.P95:F3}ms; max={timing.Max:F3}ms; static raster builds={WarmGestureCache.BuildCount}->{HotGestureCache.BuildCount}; hits={WarmGestureCache.HitCount}->{HotGestureCache.HitCount}");
    }

    private static SceneDocument CreatePerformanceScene(MonitorInfo monitor, Native.Point activeOrigin, AssetLibrary assets)
    {
        var bounds = monitor.Bounds;
        Native.GetClientRect(Lower!.Handle, out var client);
        var lowerRight = Lower.ScreenPoint(client.Right, client.Bottom).X;
        double gridLeft = Math.Max(bounds.Left + bounds.Width * .34, lowerRight + 24);
        double gridWidth = bounds.Right - 30 - gridLeft, gridTop = bounds.Top + bounds.Height * .10, gridHeight = bounds.Height * .72;
        if (gridWidth < 200 || gridHeight < 300) throw new InvalidOperationException("Insufficient independent monitor area for a non-overlapping 1006-object fixture; no pointer input has been injected.");
        var document = new SceneDocument { Viewport = Monitors.Select(m => new RectD(m.Bounds.Left, m.Bounds.Top, m.Bounds.Width, m.Bounds.Height)).Aggregate(RectD.Union) };
        double cellWidth = gridWidth / 40, cellHeight = gridHeight / 25;
        ElementKind[] kinds = [ElementKind.Rectangle, ElementKind.RoundedRectangle, ElementKind.Ellipse, ElementKind.Triangle,
            ElementKind.Arrow, ElementKind.DoubleArrow, ElementKind.Line, ElementKind.Text];
        for (int i = 0; i < PerformanceVectorCount; i++)
        {
            var element = new CanvasElement
            {
                Kind = kinds[i % kinds.Length], Width = cellWidth * .7, Height = cellHeight * .6,
                Transform = Affine2D.Translation(gridLeft + i % 40 * cellWidth, gridTop + i / 40 * cellHeight).Compose(Affine2D.Rotation(i % 5 * 7)),
                Style = new() { Stroke = i % 2 == 0 ? 0xFF2383E2 : 0xFFEF4444, Fill = 0x305B8DEF, StrokeWidth = 2.5,
                    FontSize = Math.Max(11, cellHeight * .32), FontFamily = "Microsoft YaHei UI" }, Text = $"标记{i}\nA{i % 10}"
            };
            if (element.Kind is ElementKind.Line or ElementKind.Arrow or ElementKind.DoubleArrow)
                element.Points = [new(0, 0), new(element.Width, element.Height)];
            if (i == 500)
            {
                element.Kind = ElementKind.Rectangle; element.Width = 120; element.Height = 80;
                element.Transform = Affine2D.Translation(activeOrigin.X, activeOrigin.Y);
                element.Style = new() { Stroke = 0xFF2383E2, StrokeWidth = 4, Fill = 0x702383E2 };
            }
            document.Elements.Add(element);
        }
        document.Elements.Add(new CanvasElement
        {
            Kind = ElementKind.Freehand, Style = new() { Stroke = 0xFF17A673, StrokeWidth = 5 },
            Points = Enumerable.Range(0, PerformancePathPoints).Select(i => new PointD(gridLeft + gridWidth * i / (PerformancePathPoints - 1),
                bounds.Top + bounds.Height * .53 + Math.Sin(i * .027) * bounds.Height * .07)).ToList()
        });
        for (int i = 0; i < PerformanceImageCount; i++)
        {
            string assetId = assets.Add(CreatePerformanceImage(i));
            document.Elements.Add(new CanvasElement { Kind = ElementKind.Image, AssetId = assetId, Width = 150, Height = 150,
                Transform = Affine2D.Translation(gridLeft + i * (gridWidth - 180) / 4, bounds.Top + bounds.Height * .84).Compose(Affine2D.Rotation(i * 5 - 10)) });
        }
        return document;
    }

    private static byte[] CreatePerformanceImage(int seed)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(256, 256, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap); canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { IsAntialias = true, Color = new SKColor((byte)(35 + seed * 30), (byte)(80 + seed * 20), 220, 145) };
        canvas.DrawCircle(128, 128, 105, paint); paint.Color = new SKColor(240, (byte)(seed * 40), 70, 100); canvas.DrawRect(35, 65, 180, 120, paint);
        using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Could not encode the self-generated alpha fixture.");
        return encoded.ToArray();
    }

    private static async Task WaitForRenderToSettle()
    {
        await WaitPerformance(() => !(bool)(DesktopType.GetProperty("IsRenderPending", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(Session)
            ?? throw new InvalidOperationException("The production render scheduler's IsRenderPending property is unavailable.")),
            "A scheduled production render did not complete");
        await Task.Delay(20);
    }
    private static async Task WaitPerformance(Func<bool> predicate, string error)
    {
        long started = Stopwatch.GetTimestamp();
        while (!predicate())
        {
            if (Stopwatch.GetElapsedTime(started).TotalSeconds > 10) throw new TimeoutException(error);
            await Task.Delay(1);
        }
    }
    private static CacheCounters ReadOverlayCache()
    {
        var field = DesktopType.GetField("_overlayCache", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Production _overlayCache has not been integrated; performance mode requires that completed integration.");
        var cache = field.GetValue(Session) ?? throw new InvalidOperationException("The production overlay cache is null.");
        long Read(string name) => (long)(cache.GetType().GetProperty(name)?.GetValue(cache)
            ?? throw new InvalidOperationException("Missing existing cache counter " + name));
        return new(Read("BuildCount"), Read("HitCount"), Read("CachedRasterBytes"), Read("RasterCacheByteLimit"));
    }
    private static object ReadPerformanceHardware()
    {
        string? cpu = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string;
        using var adapters = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}", false);
        var gpus = new List<string>(); var metadataWarnings = new List<string>();
        // The class also contains protected configuration keys which are not adapter instances.
        foreach (string name in (adapters?.GetSubKeyNames() ?? []).Where(n => n.Length == 4 && n.All(char.IsDigit)))
        {
            try
            {
                using var key = adapters!.OpenSubKey(name, false);
                if (key?.GetValue("DriverDesc") is string description && !string.IsNullOrWhiteSpace(description)) gpus.Add(description);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            { metadataWarnings.Add("Adapter " + name + ": " + ex.GetType().Name); }
        }
        return new { Os = Environment.OSVersion.ToString(), Runtime = RuntimeInformation.FrameworkDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Cpu = cpu?.Trim(), Gpus = gpus.Distinct(StringComparer.Ordinal).ToArray(),
            ProcessorCount = Environment.ProcessorCount, ProcessId = Environment.ProcessId, StopwatchFrequency = Stopwatch.Frequency, MetadataWarnings = metadataWarnings };
    }
    private static PerformanceTiming PerformanceStatistics(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        return sorted.Length == 0 ? new(0, 0, 0, 0, 0, 0) : new(sorted.Length, sorted[0], sorted[^1], sorted.Average(),
            sorted[(int)Math.Ceiling(sorted.Length * .5) - 1], sorted[(int)Math.Ceiling(sorted.Length * .95) - 1]);
    }
    private static void WritePerformanceReport(bool passed)
    {
        var hot = PerformanceFrames.Where(f => !f.Warmup).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(PerformanceReport)!);
        File.WriteAllText(PerformanceReport, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, Passed = passed, Failure, StartedAt = PerformanceStartedAt, CompletedAt = DateTimeOffset.Now, Profile,
            Environment = PerformanceHardware, Monitors, OtherApplicationProcesses, Checks,
            Scene = new { ObjectCount = PerformanceSceneCount, MixedVectors = PerformanceVectorCount, FreehandPoints = PerformancePathPoints,
                GeneratedAlphaImages = PerformanceImageCount, ActiveId = PerformanceActiveId, SyntheticFixture = true, BulkCommitMilliseconds = PerformanceFixtureCommitMilliseconds },
            Measurement = new
            {
                TotalActualMoves = PerformanceFrames.Count, WarmupMoves = PerformanceWarmupMoves, HotMeasuredMoves = hot.Length, RequestedPollingDelayMilliseconds = 1,
                AutosaveTimerIntervalSeconds = PerformanceRecoveryIntervalSeconds,
                AutosaveCondition = "The existing recovery timer is stopped and extended to one hour before scene insertion. Subsequent edits may start that delayed timer; no recovery snapshot/disk write runs during the measured gesture. This is a measurement condition, not the default production autosave cadence.",
                ColdFrameTreatment = "Initial scene insertion/first render, pointer-down cache construction and the first eight real moves are excluded from hot percentiles. The eight movement warmups remain in Frames. Hot P50/P95/max use exactly the 72 remaining movement samples; pointer-up/commit/Undo renders are excluded.",
                Method = "Real guarded SendInput -> native WM_MOUSEMOVE -> production DesktopSession/EditorController -> coalesced Render-priority dispatcher -> OverlayHost.Render and UpdateLayeredWindowIndirect return. Each move waits for a later PresentedFrameCount before injecting the next.",
                HostRender = "Existing Host.LastRenderMilliseconds covers the whole Render call, including clear, production drawing, cache composition and ULW return across all monitor outputs.",
                EventToPresentedObservation = "QPC timestamp from an appended Host.Pointer Move observer, after the production controller callback, until the polling continuation sees PresentedFrameCount grow. It includes dispatcher scheduling, rendering and polling delay but excludes preceding controller work in that event.",
                InjectionToPresentedObservation = "QPC timestamp immediately before guarded Move injection until the same presented-count observation; includes the input queue, controller work, rendering and polling.",
                PresentedCounter = "PresentedFrameCount counts output presentations, one per monitor per Render; count growth is observed after Render returns on the UI thread.",
                PresentationBoundary = "ULW/native call return and observer polling are not DWM composition, physical display exposure, end-to-end screen latency or measured application FPS.",
                Percentile = "Nearest rank: ascending sample at ceil(p*n)-1. All hot samples and warmups are preserved below."
            },
            HotTimingMilliseconds = new { HostRender = PerformanceStatistics(hot.Select(f => f.HostRenderMilliseconds)),
                PointerEventToPresentedObservation = PerformanceStatistics(hot.Select(f => f.PointerEventToPresentedObservationMilliseconds)),
                InjectionToPresentedObservation = PerformanceStatistics(hot.Select(f => f.InjectionToPresentedObservationMilliseconds)) },
            Cache = new { BeforeGestureCache, WarmGestureCache, HotGestureCache, AfterGestureCache, AfterUndoCache,
                HotBuildDelta = HotGestureCache?.BuildCount - WarmGestureCache?.BuildCount, HotHitDelta = HotGestureCache?.HitCount - WarmGestureCache?.HitCount },
            History = new { PerformanceUndoBefore, PerformanceUndoAfter, PerformanceUndoAfterUndo },
            PointerGuards, KeyboardGuards, PointerInjections, KeyboardInjections,
            Frames = PerformanceFrames,
            Limitations = new[] { "The 1006-object scene is generated in memory and inserted through Execute, not manually drawn user content.",
                "Only the selected rectangle drag and Undo use actual native input; the tool and initial selection are chosen through production entry points.",
                "This run uses only the current physical monitor layout and DPI. It does not establish dual-4K, mixed-DPI or all-hardware performance.",
                "No desktop image or other application content is captured or written. All five PNG assets are synthetic transparent graphics.",
                "No FPS is inferred from CPU render time or sequential pointer samples. Frame observation includes dispatcher scheduling and polling." }
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private sealed class PendingMove(Native.Point expected)
    {
        public Native.Point Expected { get; } = expected;
        public long InjectedAt, ObservedAt, FramesAtEvent;
        public double ActualX, ActualY;
    }
    private sealed record CacheCounters(long BuildCount, long HitCount, long CachedRasterBytes, long RasterCacheByteLimit);
    private sealed record PerformanceTiming(int Samples, double Min, double Max, double Mean, double P50, double P95);
    private sealed record PerformanceFrame(int Step, bool Warmup, int ExpectedX, int ExpectedY, double ActualX, double ActualY,
        double HostRenderMilliseconds, double PointerEventToPresentedObservationMilliseconds, double InjectionToPresentedObservationMilliseconds,
        long PresentedOutputsAfterEvent, int Polls, CacheCounters Cache);
}
