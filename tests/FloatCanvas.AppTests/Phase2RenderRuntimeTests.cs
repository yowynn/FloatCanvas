using System.Diagnostics;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using SkiaSharp;

internal static class Phase2RenderRuntimeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, DesktopSession session, OverlayHost host)
    {
        var editor = Field<EditorController>("_editor");
        var preview = Field<CanvasPreviewPump>("_canvasPreview");
        var opacity = Field<Slider>("_opacitySlider");
        host.SetVisible(true);
        var layers = Enumerable.Range(0, 3000).Select(i => new CanvasElement { Kind = ElementKind.Freehand,
            Points = Enumerable.Range(0, 48).Select(n => new PointD(n * 5 + i % 20, 80 + Math.Sin(n + i) * 50 + i % 120)).ToList(),
            Style = new() { StrokeWidth = 4, Stroke = 0xFF2383E2 } }).ToList();
        editor.Session.Execute("preview responsiveness fixture", doc => doc.Elements = layers);
        editor.Session.SelectAll(); editor.Session.BeginTransaction("opacity gesture");
        opacity.Value = .01;
        var ui = Stopwatch.StartNew(); Invoke("RenderFrame"); ui.Stop();
        check("production render submits a complex scene without doing its raster work on UI", preview.Busy && ui.ElapsedMilliseconds < 150);
        long start = Stopwatch.GetTimestamp(), last = start; double maxGap = 0; int steps = 1;
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) =>
        {
            long now = Stopwatch.GetTimestamp(); maxGap = Math.Max(maxGap, Stopwatch.GetElapsedTime(last, now).TotalMilliseconds); last = now;
            opacity.Value = ++steps / 100d;
            if (steps == 100) { timer.Stop(); editor.Session.Commit(); }
        };
        timer.Start();
        Phase2RenderTests.Until(() => steps == 100, timeout: 15000);
        Phase2RenderTests.Until(() => !preview.Busy, timeout: 30000);
        Console.WriteLine($"PRODUCTION_PREVIEW_INPUT value={editor.Session.Document.LayerOpacity} steps={steps} maxInputGapMs={maxGap:F2} completedFrames={preview.CompletedFrames}");
        check("production opacity reaches its released final value with UI input still running", editor.Session.Document.LayerOpacity == 1 && maxGap < 150);
        check("completed preview keeps the current canvas identity", preview.Frame?.Context.Session == editor.Session);
        Console.WriteLine($"PRODUCTION_PREVIEW_PERF requestMs={ui.Elapsed.TotalMilliseconds:F2} inputSteps={steps} maxInputGapMs={maxGap:F2} rasterMs={preview.Frame?.RenderMilliseconds:F2} completedFrames={preview.CompletedFrames}");

        // The previous visible frame must never leak through a hidden-mode draft or another canvas.
        editor.Session.Execute("old red layer", doc => doc.Elements = [new() { Kind = ElementKind.Rectangle,
            Width = 120, Height = 100, Transform = Affine2D.Translation(40, 40),
            Style = new() { Fill = 0xFFFF0000, StrokeWidth = 0 } }]);
        Invoke("RequestRender"); Phase2RenderTests.Until(() => !preview.Busy && preview.Frame is not null);
        Invoke("ToggleCanvasVisibility");
        bool leaked = false;
        Action<SKSurface> observe = surface =>
        {
            using var image = surface.Snapshot(); using var pixels = SKBitmap.FromImage(image);
            leaked |= pixels.GetPixel(60, 60).Alpha != 0;
        };
        host.Draw += observe;
        try { host.SetPreviewVisible(true); host.Render(); }
        finally { host.Draw -= observe; }
        check("a visible-frame result is never painted into a hidden preview context", !host.IsVisible && !leaked);
        var writes = Task.WhenAll(Enumerable.Range(0, 3).Select(_ => (Task)Invoke("FlushRecoveryAsync")!));
        Phase2RenderTests.Until(() => writes.IsCompleted, timeout: 30000); writes.GetAwaiter().GetResult();
        var recovered = Field<RecoveryStore>("_recoveryStore").LoadAsync();
        Phase2RenderTests.Until(() => recovered.IsCompleted);
        check("overlapping recovery flushes preserve the final committed scene", recovered.GetAwaiter().GetResult()!.State.LiveDocument.Elements.Single().Id ==
            editor.Session.Document.Elements.Single().Id);

        T Field<T>(string field) => (T)typeof(DesktopSession).GetField(field, Private)!.GetValue(session)!;
        object? Invoke(string method) => typeof(DesktopSession).GetMethod(method, Private)!.Invoke(session, null);
    }
}
