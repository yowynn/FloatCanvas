using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;

/// <summary>Opt-in offscreen measurements. No windows, input, desktop capture or user profile.
/// Reports CPU pipeline stages, not DWM display latency or a product performance threshold.</summary>
internal static class Phase3PreviewInvestigation
{
    internal static void Run(Action<string, bool> check)
    {
        var results = new List<object>();
        foreach (var spec in new[] { ("one-720p",1280,720,1,0,60), ("one-4k",3840,2160,1,0,40),
            ("hundred-720p",1280,720,100,0,60), ("paths-720p",1280,720,3000,48,12) })
        {
            var document = new SceneDocument { Viewport = new(0, 0, spec.Item2, spec.Item3), LayerOpacity = .5 };
            for (int i = 0; i < spec.Item4; i++)
                document.Elements.Add(new() { Kind = spec.Item5 == 0 ? ElementKind.Rectangle : ElementKind.Freehand,
                    Width = 120, Height = 90, Transform = Affine2D.Translation(50 + i % 20, 70 + i % 120),
                    Points = Enumerable.Range(0, spec.Item5).Select(n => new PointD(n * 5, Math.Sin(n + i) * 50)).ToList(),
                    Style = new() { Stroke = 0xFF2383E2, StrokeWidth = 4 } });
            var bounds = new PixelBounds(0, 0, spec.Item2, spec.Item3);
            var context = new CanvasPreviewContext(document, bounds, new(0, 0), false, false, false);
            using var target = SKSurface.Create(new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var directRenderer = new SceneRenderer();
            using var cache = new OverlaySceneCache(directRenderer);
            var assets = new AssetLibrary();
            var direct = new List<double>(); var clone = new List<double>(); var render = new List<double>();
            var present = new List<double>(); var latency = new List<double>(); var allocated = new List<long>();
            var dispatcher = Dispatcher.CurrentDispatcher;
            long requestStart = 0, cloneBytes = 0; double cloneMs = 0, presentMs = 0;
            DispatcherFrame? wait = null;
            using var pump = new CanvasPreviewPump(dispatcher, () =>
            {
                long before = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
                var detached = document.Clone();
                var resources = detached.ReferencedAssets.Select(assets.Get).ToArray();
                cloneBytes = GC.GetAllocatedBytesForCurrentThread() - before;
                cloneMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                return new() { Context = context, SceneIdentity = document, Document = detached,
                    Resources = resources, Outputs = [bounds], Revision = 1, Transaction = 1, First = spec.Item4 - 1, Last = spec.Item4 - 1 };
            });
            pump.FrameReady += () =>
            {
                long start = Stopwatch.GetTimestamp();
                target.Canvas.Clear(SKColors.Transparent); pump.Frame!.Pixels.Draw(target.Canvas); target.Canvas.Flush();
                presentMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (wait is not null) wait.Continue = false;
            };
            pump.Failed += error => throw error;
            for (int i = -5; i < spec.Item6; i++)
            {
                // A real changing final layer, preserving the same cached prefix on both paths.
                document.Elements[^1].Transform = Affine2D.Translation(50 + i, 80);
                long start = Stopwatch.GetTimestamp();
                target.Canvas.Clear(SKColors.Transparent);
                cache.DrawOnClearedRasterTarget(target, document, assets, 1, 1, spec.Item4 - 1, spec.Item4 - 1, false, document);
                target.Canvas.Flush();
                double directMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                wait = new DispatcherFrame(); requestStart = Stopwatch.GetTimestamp();
                pump.Request(); Dispatcher.PushFrame(wait); wait = null;
                double elapsed = Stopwatch.GetElapsedTime(requestStart).TotalMilliseconds;
                if (i < 0) continue;
                direct.Add(directMs); clone.Add(cloneMs); render.Add(pump.Frame!.RenderMilliseconds);
                present.Add(presentMs); latency.Add(elapsed); allocated.Add(cloneBytes);
            }
            var result = new { Case = spec.Item1, Width = bounds.Width, Height = bounds.Height, Elements = spec.Item4,
                Points = spec.Item4 * spec.Item5, Samples = spec.Item6,
                InlineRaster = Stats(direct), UiSnapshot = Stats(clone), WorkerIncludingImmutableCopy = Stats(render),
                UiOffscreenCopy = Stats(present), RequestThroughOffscreenCopy = Stats(latency),
                UiSnapshotAllocatedBytesMedian = allocated.Order().ElementAt(allocated.Count / 2),
                MinimumPixelCopyBytesPerCompletedFrame = (long)bounds.Width * bounds.Height * 4 * 2,
                Scope = "Offscreen CPU comparison; native UpdateLayeredWindow/DWM and editor mutation costs excluded" };
            results.Add(result); Console.WriteLine("PREVIEW_INVESTIGATION " + JsonSerializer.Serialize(result));
            check(spec.Item1 + " finishes without changing the live scene", !pump.Busy && document.Elements.Count == spec.Item4);
        }
        CompletionPriority(check, results);
        SupersededFrame(check, results);
        string root = Environment.GetEnvironmentVariable("FLOATCANVAS_PHASE3_ARTIFACTS") ??
            Path.Combine(Path.GetTempPath(), "FloatCanvas-phase3");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "preview-investigation.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void CompletionPriority(Action<string, bool> check, List<object> results)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        using var finished = new ManualResetEventSlim();
        var context = new CanvasPreviewContext(new object(), new object(), new(0, 0), false, false, false);
        using var pump = new CanvasPreviewPump(dispatcher, () => new() { Context = context, SceneIdentity = context,
            Document = new(), Resources = [], Outputs = [] }, snapshot =>
        { finished.Set(); return new(context, new([]), OverlayRenderBackend.Cpu, false, null, null, 0); });
        pump.Request();
        check("priority probe reaches its own worker", finished.Wait(2000));
        // All operations are bounded and perform no work. Demonstrates queue ordering, not elapsed lag.
        int inputs = 0; bool renderedDuringInput = false;
        for (int i = 0; i < 200; i++) dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        { inputs++; renderedDuringInput |= pump.CompletedFrames > 0; });
        Phase2RenderTests.Until(() => pump.CompletedFrames == 1);
        var result = new { Case = "completion-priority", InputsBeforeFirstFrame = inputs,
            FramePublishedDuringInput = renderedDuringInput, Scope = "Bounded dispatcher ordering probe, not a physical-input timing measurement" };
        results.Add(result); Console.WriteLine("PREVIEW_INVESTIGATION " + JsonSerializer.Serialize(result));
        check("priority probe completes all queued work", inputs == 200 && pump.CompletedFrames == 1);
    }

    private static object Stats(List<double> samples)
    {
        var sorted = samples.Order().ToArray();
        return new { MedianMs = sorted[sorted.Length / 2], P95Ms = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1], MaximumMs = sorted[^1] };
    }

    private static void SupersededFrame(Action<string, bool> check, List<object> results)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        int latest = 1;
        var presented = new List<int>();
        var context = new CanvasPreviewContext(new object(), new object(), new(0, 0), false, false, false);
        using var pump = new CanvasPreviewPump(dispatcher, () => new() { Context = context, SceneIdentity = context,
            Document = new() { NextNumber = latest }, Resources = [], Outputs = [] }, snapshot =>
        {
            if (snapshot.Document.NextNumber == 1) { entered.Set(); if (!release.Wait(2000)) throw new TimeoutException(); }
            return new(context, new([]), OverlayRenderBackend.Cpu, false, null, null, snapshot.Document.NextNumber);
        });
        pump.FrameReady += () => presented.Add((int)pump.Frame!.RenderMilliseconds);
        pump.Request(); check("superseded-frame probe reaches its own worker", entered.Wait(2000));
        latest = 2; pump.Request(); release.Set();
        Phase2RenderTests.Until(() => pump.CompletedFrames == 2);
        var result = new { Case = "superseded-frame", LatestRequestedBeforeFirstCompletion = 2,
            PublishedValues = presented.ToArray(), Scope = "Deterministic blocked renderer; proves publication policy, not user-visible display duration" };
        results.Add(result); Console.WriteLine("PREVIEW_INVESTIGATION " + JsonSerializer.Serialize(result));
        check("superseded-frame probe eventually publishes the latest state", presented.Last() == 2);
    }
}
