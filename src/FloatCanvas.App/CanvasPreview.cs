using System.Diagnostics;
using System.Windows.Threading;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;

namespace FloatCanvas.App;

// Contexts contain identity tokens only; the worker never reads live editor or window objects.
internal sealed record CanvasPreviewContext(object Session, object Monitors, PointD Origin,
    bool Hidden, bool Calibration, bool Placing, object? DocumentEpoch = null, object? VisibilityEpoch = null);

internal sealed class CanvasPreviewSnapshot : IDisposable
{
    internal required CanvasPreviewContext Context { get; init; }
    internal required object SceneIdentity { get; init; }
    internal required SceneDocument Document { get; init; }
    internal required AssetResource[] Resources { get; init; }
    internal required PixelBounds[] Outputs { get; init; }
    internal long Revision { get; init; }
    internal long Transaction { get; init; }
    internal int First { get; init; } = -1;
    internal int Last { get; init; } = -1;
    internal bool BypassCache { get; init; }
    internal bool PreferGpu { get; init; }
    internal TimeSpan AnimationTime { get; init; }
    internal DesktopBackdrop? Backdrop { get; init; }
    internal SceneDocument? PlacementScene { get; init; }
    internal IReadOnlyList<PointD>? PlacementClip { get; init; }
    public void Dispose() => Backdrop?.Dispose();
}

internal sealed record CanvasPreviewFrame(CanvasPreviewContext Context, PreviewRasterFrame Pixels,
    OverlayRenderBackend Backend, bool GpuWorkload, string? GpuDetails, string? GpuFailure,
    double RenderMilliseconds) : IDisposable
{
    public void Dispose() => Pixels.Dispose();
}

/// <summary>All mutable Skia/cache/GPU state belongs to the preview worker.</summary>
internal sealed class CanvasPreviewRenderer : IDisposable
{
    private readonly SceneRenderer renderer = new();
    private readonly OverlaySceneCache cache;
    private readonly PreviewRasterizer targets = new();
    private readonly AssetLibrary assets = new();
    internal CanvasPreviewRenderer() => cache = new(renderer);

    internal CanvasPreviewFrame Render(CanvasPreviewSnapshot snapshot)
    {
        var timer = Stopwatch.StartNew();
        foreach (var resource in snapshot.Resources) assets.Add(resource);
        assets.Prune(snapshot.Document.ReferencedAssets);
        renderer.AnimationTime = snapshot.AnimationTime;
        renderer.LiveBackdrop = snapshot.Backdrop;
        var document = snapshot.Document;
        bool Filter(CanvasElement element) => element.Kind == ElementKind.Image && element.Visible && element.Style.Opacity > 0 &&
            SceneRenderer.ProjectedBounds(document, element).Intersects(document.Viewport) &&
            element.Effects.Any(effect => effect.Kind is ImageEffectKind.Blur or ImageEffectKind.Pixelate);
        bool gpuWorkload = SceneRenderer.HasLiveEffects(document) || document.Background is { } background && Filter(background) ||
            document.LayerOpacity > 0 && document.Elements.Any(Filter);
        try
        {
            var pixels = targets.Render(snapshot.Outputs, snapshot.PreferGpu, gpuWorkload, surface =>
            {
                var canvas = surface.Canvas;
                int saved = canvas.Save();
                try
                {
                    canvas.Translate((float)snapshot.Context.Origin.X, (float)snapshot.Context.Origin.Y);
                    if (snapshot.Context.Calibration)
                        renderer.Draw(canvas, document, assets, calibrationReference: true, includeGrid: !snapshot.Context.Placing);
                    else cache.DrawOnClearedRasterTarget(surface, document, assets, snapshot.Revision, snapshot.Transaction,
                        snapshot.First, snapshot.Last, snapshot.BypassCache, snapshot.SceneIdentity);
                    if (snapshot.PlacementScene is { } placement && snapshot.PlacementClip is { Count: 4 } quad)
                    {
                        using var builder = new SKPathBuilder();
                        builder.MoveTo((float)quad[0].X, (float)quad[0].Y);
                        foreach (var point in quad.Skip(1)) builder.LineTo((float)point.X, (float)point.Y);
                        builder.Close(); using var clip = builder.Detach(); canvas.ClipPath(clip);
                        renderer.Draw(canvas, placement, assets, includeBackground: false,
                            includeCanvasBackground: false, calibrationReference: true);
                    }
                }
                finally { canvas.RestoreToCount(saved); }
            });
            return new(snapshot.Context, pixels, targets.Backend, gpuWorkload,
                targets.GpuDetails, targets.GpuFailure, timer.Elapsed.TotalMilliseconds);
        }
        finally { renderer.LiveBackdrop = null; }
    }

    public void Dispose() { cache.Dispose(); renderer.Dispose(); targets.Dispose(); }
}

/// <summary>UI requests carry no queued snapshots. While busy, remember only that a newer value exists;
/// after completion capture that value on the UI thread. Shutdown never waits for a heavy render.</summary>
internal sealed class CanvasPreviewPump : IDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly Func<CanvasPreviewSnapshot> capture;
    private readonly Func<CanvasPreviewSnapshot, CanvasPreviewFrame>? renderOverride;
    private readonly object gate = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly Thread worker;
    private CanvasPreviewSnapshot? next;
    private bool disposed, pending, stopped;
    internal bool Busy { get; private set; }
    internal CanvasPreviewFrame? Frame { get; private set; }
    internal long CompletedFrames { get; private set; }
    internal long GpuFrames { get; private set; }
    internal long CpuFrames { get; private set; }
    internal event Action? FrameReady;
    internal event Action<Exception>? Failed;

    internal CanvasPreviewPump(Dispatcher dispatcher, Func<CanvasPreviewSnapshot> capture,
        Func<CanvasPreviewSnapshot, CanvasPreviewFrame>? renderOverride = null)
    {
        this.dispatcher = dispatcher; this.capture = capture; this.renderOverride = renderOverride;
        worker = new(Work) { IsBackground = true, Name = "FloatCanvas preview" };
        worker.Start();
    }

    internal void Request()
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        pending = true;
        StartLatest();
    }

    private void StartLatest()
    {
        if (Busy || !pending || disposed) return;
        var snapshot = capture(); // Called only when the worker is ready, never for every slider tick.
        pending = false; Busy = true;
        lock (gate) next = snapshot;
        wake.Set();
    }

    private void Work()
    {
        CanvasPreviewRenderer? renderer = null;
        try
        {
            while (true)
            {
                wake.WaitOne();
                CanvasPreviewSnapshot? snapshot;
                lock (gate)
                {
                    snapshot = next; next = null;
                    if (disposed) { snapshot?.Dispose(); return; }
                }
                if (snapshot is null) continue;
                CanvasPreviewFrame? frame = null; Exception? error = null;
                try
                {
                    frame = renderOverride is null ? (renderer ??= new CanvasPreviewRenderer()).Render(snapshot) : renderOverride(snapshot);
                }
                catch (Exception failure) { error = failure; }
                finally { snapshot.Dispose(); }
                lock (gate)
                    if (disposed || dispatcher.HasShutdownStarted) { frame?.Dispose(); return; }
                var completed = frame;
                DispatcherOperation operation;
                try { operation = dispatcher.InvokeAsync(() => Complete(completed, error), DispatcherPriority.Background); }
                catch (InvalidOperationException) when (dispatcher.HasShutdownStarted) { completed?.Dispose(); return; }
                // A shutdown can cancel a callback after it was queued. Its result still owns pixels.
                _ = operation.Task.ContinueWith(task => { if (task.IsCanceled) completed?.Dispose(); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        finally
        {
            try { renderer?.Dispose(); }
            catch (Exception error) { Trace.TraceError("Preview cleanup failed: {0}", error); }
            lock (gate) { stopped = true; wake.Dispose(); }
        }
    }

    private void Complete(CanvasPreviewFrame? frame, Exception? error)
    {
        Busy = false;
        if (disposed) { frame?.Dispose(); return; }
        try
        {
            if (frame is not null)
            {
                Frame?.Dispose(); Frame = frame; CompletedFrames++;
                if (frame.Backend == OverlayRenderBackend.Gpu) GpuFrames++; else CpuFrames++;
                FrameReady?.Invoke();
            }
            if (error is not null) Failed?.Invoke(error);
        }
        // Even a failed frame must not drop a newer final input value.
        finally { StartLatest(); }
    }

    public void Dispose()
    {
        dispatcher.VerifyAccess();
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            // Wake while holding the gate, before the worker may dispose the signal.
            if (!stopped) wake.Set();
        }
        pending = false;
        Busy = false;
        Frame?.Dispose(); Frame = null;
    }
}
