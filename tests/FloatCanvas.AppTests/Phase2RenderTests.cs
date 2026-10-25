using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;

internal static class Phase2RenderTests
{
    internal static void Run(Action<string, bool> check)
    {
        CompletedFramePreemptsInput(check);
        var dispatcher = Dispatcher.CurrentDispatcher;
        var context = new CanvasPreviewContext(new object(), new object(), new(0, 0), false, false, false);
        var source = new SceneDocument { Viewport = new(0, 0, 96, 64), LayerOpacity = .01 };
        var identity = new object();
        var values = new ConcurrentQueue<int>(); var threads = new ConcurrentQueue<int>();
        using var started = new SemaphoreSlim(0);
        using var first = new ManualResetEventSlim();
        using var second = new ManualResetEventSlim();
        int captures = 0, uiThread = Environment.CurrentManagedThreadId;
        CanvasPreviewSnapshot Capture()
        {
            captures++;
            return new() { Context = context, SceneIdentity = identity, Document = source.Clone(), Resources = [],
                Outputs = [new(0, 0, 96, 64)], Revision = 1 };
        }
        using (var pump = new CanvasPreviewPump(dispatcher, Capture, snapshot =>
        {
            int value = (int)Math.Round(snapshot.Document.LayerOpacity * 100);
            threads.Enqueue(Environment.CurrentManagedThreadId); values.Enqueue(value); started.Release();
            if (value == 1 && !first.Wait(5000) || value == 45 && !second.Wait(5000))
                throw new TimeoutException("Fixture render was never released.");
            // Reading after the UI changes proves this is a detached value, not a live document.
            if ((int)Math.Round(snapshot.Document.LayerOpacity * 100) != value) throw new Exception("Mutable document reached renderer.");
            return new(snapshot.Context, new([]), OverlayRenderBackend.Cpu, false, null, null, value);
        }))
        {
            var request = Stopwatch.StartNew(); pump.Request(); request.Stop();
            check("starting a blocked preview does not block the UI caller", request.ElapsedMilliseconds < 100 && started.Wait(2000));
            int inputSteps = 0;
            for (int value = 2; value <= 45; value++)
            {
                source.LayerOpacity = value / 100d; pump.Request();
                dispatcher.BeginInvoke(DispatcherPriority.Input, () => inputSteps++);
            }
            Until(() => inputSteps == 44);
            check("all UI input values advance while the first renderer is blocked", source.LayerOpacity == .45 && captures == 1 && pump.Busy);
            first.Set();
            Until(() => captures == 2);
            check("completion captures only the newest value instead of a backlog", started.Wait(2000) && values.SequenceEqual([1, 45]));
            source.LayerOpacity = 1; pump.Request(); second.Set();
            Until(() => pump.CompletedFrames == 3);
            check("the final released value is eventually rendered", values.SequenceEqual([1, 45, 100]) && captures == 3 && pump.Frame!.RenderMilliseconds == 100);
            check("one renderer thread owns all frames and never runs on the UI", threads.Distinct().Count() == 1 && threads.All(id => id != uiThread));
        }
        using var blocked = new ManualResetEventSlim();
        using var began = new ManualResetEventSlim();
        var closing = new CanvasPreviewPump(dispatcher, Capture, snapshot =>
        {
            began.Set(); blocked.Wait(5000);
            return new(snapshot.Context, new([]), OverlayRenderBackend.Cpu, false, null, null, 0);
        });
        int late = 0; closing.FrameReady += () => late++;
        closing.Request(); check("shutdown fixture starts its own blocked worker", began.Wait(2000));
        var shutdown = Stopwatch.StartNew(); closing.Dispose(); shutdown.Stop(); blocked.Set();
        Until(() => !closing.Busy, required: false);
        check("closing does not wait for a frame or present a late result", shutdown.ElapsedMilliseconds < 100 && late == 0);
        source.LayerOpacity = .01; first.Reset();
        while (started.Wait(0)) { }
        int failures = 0;
        using (var failing = new CanvasPreviewPump(dispatcher, Capture, snapshot =>
        {
            if (snapshot.Document.LayerOpacity == .01)
            { started.Release(); first.Wait(5000); throw new InvalidOperationException("Own fixture frame failure."); }
            return new(snapshot.Context, new([]), OverlayRenderBackend.Cpu, false, null, null, 100);
        }))
        {
            failing.Failed += _ => failures++;
            failing.Request(); check("failure fixture reaches its own worker", started.Wait(2000));
            source.LayerOpacity = 1; failing.Request(); first.Set();
            Until(() => failing.CompletedFrames == 1);
            check("a failed preview does not lose the final pending input", failures == 1 && failing.Frame!.RenderMilliseconds == 100);
        }
        RasterSnapshots(check, context);
    }

    private static void CompletedFramePreemptsInput(Action<string, bool> check)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        int uiThread = Environment.CurrentManagedThreadId;
        using var queued = new ManualResetEventSlim();
        var context = new CanvasPreviewContext(new object(), new object(), new(0, 0), false, false, false);
        using var pump = new CanvasPreviewPump(dispatcher, () => new()
        {
            Context = context, SceneIdentity = context.Session, Document = new(), Resources = [], Outputs = []
        }, snapshot => new(snapshot.Context, new([]), OverlayRenderBackend.Cpu, false, null, null, 0));
        DispatcherHookEventHandler posted = (_, _) =>
        { if (Environment.CurrentManagedThreadId != uiThread) queued.Set(); };
        dispatcher.Hooks.OperationPosted += posted;
        try
        {
            pump.Request();
            check("completed preview is queued while the UI is processing input", queued.Wait(2000));
            bool inputProcessed = false, presentedBeforeInput = false;
            dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            { presentedBeforeInput = pump.CompletedFrames == 1; inputProcessed = true; });
            Until(() => inputProcessed);
            check("a completed preview is presented before another queued pointer input", presentedBeforeInput);
        }
        finally { dispatcher.Hooks.OperationPosted -= posted; }
    }

    private static void RasterSnapshots(Action<string, bool> check, CanvasPreviewContext context)
    {
        var doc = new SceneDocument { Viewport = new(0, 0, 96, 64),
            Elements = [new() { Kind = ElementKind.Rectangle, Width = 25, Height = 25,
                Transform = Affine2D.Translation(8, 9), Style = new() { Fill = 0xFFFF0000, StrokeWidth = 0 } }] };
        var detached = doc.Clone(); doc.Elements[0].Style.Fill = 0xFF0000FF;
        var identity = new object();
        using var renderer = new CanvasPreviewRenderer();
        using var snapshot = new CanvasPreviewSnapshot { Context = context, SceneIdentity = identity,
            Document = detached, Resources = [], Outputs = [new(0, 0, 96, 64)], Revision = 1 };
        using var frame = renderer.Render(snapshot);
        using var pixels = PreviewTestPixels.Read(frame.Pixels);
        check("actual worker raster uses detached document pixels", pixels.GetPixel(15, 15) == SKColors.Red);
        using var composite = SKSurface.Create(new SKImageInfo(96, 64, SKColorType.Bgra8888, SKAlphaType.Premul));
        composite.Canvas.Clear(SKColors.Transparent); frame.Pixels.Draw(composite.Canvas);
        using var result = composite.Snapshot(); using var read = SKBitmap.FromImage(result);
        check("presenting tiles preserves transparent gaps and rendered content", read.GetPixel(15, 15) == SKColors.Red && read.GetPixel(80, 50).Alpha == 0);
        var gridScene = detached.Clone();
        gridScene.Canvas.Grid.Visible = true;
        var calibration = CanvasCalibration.CreateInitial(gridScene.Viewport, 8, 6);
        check("preview fixture obtains perspective geometry", CanvasCalibration.TryCalibrate(calibration,
            [new(15, 12), new(75, 8), new(90, 55), new(5, 60)], gridScene.Viewport, out var projected, out _));
        projected.Enabled = true; gridScene.Canvas.Projection = projected;
        using (var projectedSnapshot = new CanvasPreviewSnapshot
        {
            Context = context with { Origin = new(-150, 25), Calibration = true }, SceneIdentity = gridScene,
            Document = gridScene, Resources = [], Outputs = [new(-150, 25, 96, 64)], Revision = 1
        })
        using (var projectedFrame = renderer.Render(projectedSnapshot))
        using (var expectedRenderer = new SceneRenderer())
        using (var expectedSurface = SKSurface.Create(new SKImageInfo(96, 64, SKColorType.Bgra8888, SKAlphaType.Premul)))
        {
            expectedSurface.Canvas.Clear(SKColors.Transparent);
            expectedRenderer.Draw(expectedSurface.Canvas, gridScene, new(), calibrationReference: true);
            using var expectedImage = expectedSurface.Snapshot(); using var expected = SKBitmap.FromImage(expectedImage);
            using var actual = PreviewTestPixels.Read(projectedFrame.Pixels);
            check("perspective grid tiles match direct pixels at a negative monitor origin", actual.Bytes.SequenceEqual(expected.Bytes));
        }

        // A live backdrop can be replaced/disposed on the UI while a worker still owns this frame.
        using var desktop = SKSurface.Create(new SKImageInfo(96, 64)); desktop.Canvas.Clear(SKColors.Green);
        var original = new DesktopBackdrop([new(new(0, 0, 96, 64), desktop.Snapshot())]);
        var retained = original.Retain(); original.Dispose();
        doc.Elements = [new() { Kind = ElementKind.LivePixelate, Width = 60, Height = 50,
            Effects = [new() { Kind = ImageEffectKind.Pixelate, Amount = 8 }] }];
        using var effect = new CanvasPreviewSnapshot { Context = context, SceneIdentity = identity,
            Document = doc.Clone(), Resources = [], Outputs = [new(0, 0, 96, 64)], Revision = 2, PreferGpu = true, Backdrop = retained };
        using var effectFrame = renderer.Render(effect); using var effectPixels = PreviewTestPixels.Read(effectFrame.Pixels);
        check("retained desktop tiles outlive the source and render through the worker backend", effectPixels.GetPixel(20, 20) == SKColors.Green);
        Console.WriteLine($"PREVIEW_BACKEND {effectFrame.Backend}; device={effectFrame.GpuDetails}; fallback={effectFrame.GpuFailure}");
        // A later buffer reuse may not overwrite immutable pixels already sent to the UI.
        check("later renders do not mutate an already published frame", pixels.GetPixel(15, 15) == SKColors.Red);

        using (var target = new PreviewRasterizer())
        {
            int gpuCalls = 0, cpuCalls = 0;
            PreviewRasterFrame DrawFault() => target.Render([new(0, 0, 96, 64)], true, true, surface =>
            {
                if (surface.Context is not null) { gpuCalls++; throw new InvalidOperationException("Own preview GPU fault."); }
                cpuCalls++; surface.Canvas.Clear(SKColors.Red);
            });
            using var fallback = DrawFault();
            using var fallbackPixels = PreviewTestPixels.Read(fallback);
            check("worker GPU failure redraws the same frame on CPU", gpuCalls == 1 && cpuCalls == 1 &&
                target.Backend == OverlayRenderBackend.Cpu && fallbackPixels.GetPixel(20, 20) == SKColors.Red &&
                target.GpuFailure?.Contains("Own preview GPU fault") == true);
            using var again = DrawFault();
            check("a failed worker GPU is not retried on every frame", gpuCalls == 1 && cpuCalls == 2);
        }

        var large = new SceneDocument { Viewport = new(0, 0, 640, 360), LayerOpacity = .5 };
        for (int i = 0; i < 3000; i++)
            large.Elements.Add(new() { Kind = ElementKind.Freehand,
                Points = Enumerable.Range(0, 48).Select(n => new PointD(n * 5 + i % 20, 80 + Math.Sin(n + i) * 50 + i % 120)).ToList(),
                Style = new() { StrokeWidth = 4, Stroke = 0xFF2383E2 } });
        using var heavySnapshot = new CanvasPreviewSnapshot { Context = context, SceneIdentity = large,
            Document = large.Clone(), Resources = [], Outputs = [new(0, 0, 640, 360)], Revision = 1 };
        var direct = Stopwatch.StartNew(); using var reference = renderer.Render(heavySnapshot); direct.Stop();
        var dispatcher = Dispatcher.CurrentDispatcher;
        using var pump = new CanvasPreviewPump(dispatcher, () => new() { Context = context, SceneIdentity = large,
            Document = large.Clone(), Resources = [], Outputs = [new(0, 0, 640, 360)], Revision = 1 });
        var ui = Stopwatch.StartNew(); pump.Request(); ui.Stop();
        int heartbeats = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) => heartbeats++;
        timer.Start(); Until(() => pump.CompletedFrames > 0, timeout: 30000); timer.Stop();
        check("complex opacity preview returns control before the heavy raster completes", ui.Elapsed.TotalMilliseconds < direct.Elapsed.TotalMilliseconds && heartbeats > 0);
        Console.WriteLine($"PREVIEW_PERF layers=3000 points=144000 syncRasterMs={direct.Elapsed.TotalMilliseconds:F2} uiSnapshotRequestMs={ui.Elapsed.TotalMilliseconds:F2} workerRasterMs={pump.Frame!.RenderMilliseconds:F2} inputHeartbeats={heartbeats}");
    }

    internal static void Until(Func<bool> ready, int timeout = 5000, bool required = true)
    {
        var timer = Stopwatch.StartNew();
        while (!ready() && timer.ElapsedMilliseconds < timeout)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame); Thread.Sleep(1);
        }
        if (required && !ready()) throw new TimeoutException("Preview condition did not complete.");
    }
}
