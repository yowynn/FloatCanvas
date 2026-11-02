using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;

namespace FloatCanvas.App;

// Contexts contain identity tokens only; the worker never reads live editor or window objects.
internal sealed record CanvasPreviewContext(object Session, object Monitors, PointD Origin,
    bool Hidden, bool Calibration, bool Placing, object? DocumentEpoch = null, object? VisibilityEpoch = null, long Epoch = 0, double GuideOpacity = 1,
    object? ScreenDraftEpoch = null, object? PresentationEpoch = null, object? WindowEpoch = null, object? GroupEpoch = null)
{
    // Editing guides may lag one worker frame; never discard the matching base
    // scene while they update. Epoch still rejects a completed A→B→A transition.
    internal bool HasSameContent(CanvasPreviewContext other) => Equals(Session, other.Session) && Equals(Monitors, other.Monitors) &&
        Origin == other.Origin && Hidden == other.Hidden && Equals(DocumentEpoch, other.DocumentEpoch) &&
        Equals(VisibilityEpoch, other.VisibilityEpoch) && Equals(GroupEpoch, other.GroupEpoch) && Epoch == other.Epoch;
}

internal sealed class CanvasPreviewSnapshot : IDisposable
{
    internal required CanvasPreviewContext Context { get; init; }
    internal required object SceneIdentity { get; init; }
    internal required SceneDocument Document { get; init; }
    internal SceneDocument? SourceDocument { get; init; }
    internal EvaluatedScene? Evaluation { get; init; }
    internal FrozenSceneEvaluation? FrozenEvaluation { get; init; }
    internal FrozenSceneEvaluation? EditingEvaluation { get; init; }
    internal LayerPlacement? EditingPlacement { get; init; }
    internal RootCanvasKind EditingRootCanvas { get; init; } = RootCanvasKind.Main;
    internal RootCanvasKind? AuthorRootCanvas { get; init; }
    internal Guid? EditingScopeId { get; init; }
    internal bool IndependentCanvas { get; init; }
    internal TransientScreenElement? ScreenDraft { get; init; }
    internal Guid? ScreenDraftScopeId { get; init; }
    internal PresentationFrame? PresentationFrame { get; init; }
    internal object? PresentationPlanIdentity { get; init; }
    internal PointD PresentationOrigin { get; init; }
    internal WindowFrameSet? WindowFrames { get; init; }
    internal object? WindowOwnerContext { get; init; }
    internal required AssetResource[] Resources { get; init; }
    internal required PixelBounds[] Outputs { get; init; }
    internal long Revision { get; init; }
    internal long Transaction { get; init; }
    internal int First { get; init; } = -1;
    internal int Last { get; init; } = -1;
    internal bool BypassCache { get; init; }
    internal bool GraphChanged { get; init; }
    internal bool PreferGpu { get; init; }
    internal TimeSpan AnimationTime { get; init; }
    internal DesktopBackdrop? Backdrop { get; init; }
    internal SceneDocument? PlacementScene { get; init; }
    internal IReadOnlyList<PointD>? PlacementClip { get; init; }
    internal long Sequence { get; set; }
    internal long ContentRevision { get; init; }
    internal long InputSequence { get; init; }
    internal long CapturedTimestamp { get; set; }
    internal double CaptureMilliseconds { get; set; }
    internal IReadOnlyList<PixelBounds>? PixelDamage { get; init; }
    public void Dispose() { Backdrop?.Dispose(); WindowFrames?.Dispose(); }
}

internal sealed record CanvasPreviewFrame(CanvasPreviewContext Context, PreviewRasterFrame Pixels,
    OverlayRenderBackend Backend, bool GpuWorkload, string? GpuDetails, string? GpuFailure,
    double RenderMilliseconds, CanvasPreviewMetrics? Cache = null, SceneAnimationSchedule? Animation = null) : IDisposable
{
    internal long Sequence { get; init; }
    internal long ContentRevision { get; init; }
    internal long InputSequence { get; init; }
    internal long CapturedTimestamp { get; init; }
    internal double CaptureMilliseconds { get; init; }
    internal long RenderStartedTimestamp { get; init; }
    internal long RenderCompletedTimestamp { get; init; }
    internal TimeSpan AnimationTime { get; init; }
    internal SceneDocument? PickingDocument { get; init; }
    internal SceneDocument? PickingSourceDocument { get; init; }
    internal EvaluatedScene? PickingEvaluation { get; init; }
    internal SceneImageSnapshot? PickingImages { get; init; }
    internal AssetResource[]? PickingResources { get; init; }
    internal int PresentationPrimitiveCount { get; init; }
    internal bool PresentationBudgetExceeded { get; init; }
    internal PresentationFrame? PresentationFrameSnapshot { get; init; }
    internal object? PresentationPlanIdentity { get; init; }
    internal WindowFrameSet? PickingWindowFrames { get; init; }
    internal object? WindowOwnerContext { get; init; }
    internal WindowRenderInfo? WindowInfo { get; init; }
    internal CanvasPreviewFrame Retain()
    {
        var pixels = Pixels.Retain(); SceneImageSnapshot? images = null; WindowFrameSet? windows = null;
        try
        {
            images = PickingImages?.Retain(); windows = PickingWindowFrames?.Retain();
            return this with { Pixels = pixels, PickingImages = images, PickingWindowFrames = windows };
        }
        catch { windows?.Dispose(); images?.Dispose(); pixels.Dispose(); throw; }
    }
    public void Dispose() { PickingImages?.Dispose(); PickingWindowFrames?.Dispose(); Pixels.Dispose(); }
}

internal sealed record CanvasPreviewMetrics(long BuildCount, long HitCount, long CachedRasterBytes,
    long RasterCacheByteLimit, long InPlaceOpacityFrameCount, long ActivePixelBytes = 0, long PooledPixelBytes = 0,
    long RasterBufferBytes = 0, long ImageCacheBytes = 0, long CopiedPixelBytes = 0, long AnimationMetadataBytes = 0,
    long PresentationPlanBuildCount = 0, long PresentationFrameBuildCount = 0,
    long WindowPlanBuildCount = 0, long WindowFrameBuildCount = 0, long WindowFrameBytes = 0);

/// <summary>All mutable Skia/cache/GPU state belongs to the preview worker.</summary>
internal sealed class CanvasPreviewRenderer : IDisposable
{
    private readonly SceneRenderer renderer = new();
    private readonly OverlaySceneCache cache;
    private readonly PreviewRasterizer targets = new();
    private readonly AssetLibrary assets = new();
    private CanvasPreviewSnapshot? previous;
    private SceneDocument? evaluatedSource;
    private long evaluatedRevision = -1;
    private long evaluatedCommittedRevision = -1, evaluatedTransaction = -1;
    private FrozenSceneEvaluation? fallbackEvaluation;
    private PresentationRenderInfo? previousPresentation;
    private WindowRenderInfo? previousWindows;
    internal CanvasPreviewRenderer() => cache = new(renderer);

    internal CanvasPreviewFrame Render(CanvasPreviewSnapshot snapshot)
    {
        snapshot = ResolveWorldSnapshot(snapshot);
        var timer = Stopwatch.StartNew();
        long renderStarted = Stopwatch.GetTimestamp();
        foreach (var resource in snapshot.Resources) assets.Add(resource);
        assets.Prune(snapshot.Document.ReferencedAssets.Concat(snapshot.ScreenDraft?.ScreenElement.AssetId is { } asset ? [asset] : []));
        renderer.AnimationTime = snapshot.AnimationTime;
        renderer.LiveBackdrop = snapshot.Backdrop;
        renderer.PresentationFrame = snapshot.PresentationFrame;
        renderer.PresentationOrigin = snapshot.PresentationOrigin;
        var windowFrames = snapshot.WindowFrames is not null && ReferenceEquals(snapshot.WindowFrames.OwnerContext, snapshot.WindowOwnerContext) &&
            ReferenceEquals(snapshot.WindowOwnerContext, snapshot.Context.WindowEpoch) ? snapshot.WindowFrames : null;
        renderer.WindowFrames = windowFrames; renderer.WindowOwnerContext = windowFrames?.OwnerContext;
        var document = snapshot.Document;
        var presentation = renderer.PreparePresentation(document);
        var presentationDamage = ReferenceEquals(previousPresentation, presentation) ? [] :
            (previousPresentation?.DirtyBounds ?? []).Concat(presentation.DirtyBounds).ToArray();
        var windows = renderer.PrepareWindowFrames(document);
        var windowDamage = ReferenceEquals(previousWindows, windows) ? [] : (previousWindows?.DirtyBounds ?? []).Concat(windows.DirtyBounds).ToArray();
        // A graph revision invalidates static tiles once. Animation ticks reuse the
        // same immutable world view and must not permanently bypass those tiles.
        if (snapshot.GraphChanged && !ReferenceEquals(previous?.Document, document)) cache.Invalidate();
        bool Filter(CanvasElement element) => element.Kind == ElementKind.Image && element.Visible && element.Style.Opacity > 0 &&
            SceneRenderer.ProjectedBounds(document, element).Intersects(document.Viewport) &&
            element.Effects.Any(effect => effect.Kind is ImageEffectKind.Blur or ImageEffectKind.Pixelate);
        bool gpuWorkload = SceneRenderer.HasLiveEffects(document) || document.Background is { } background && Filter(background) ||
            document.LayerOpacity > 0 && document.Elements.Any(Filter);
        try
        {
            var damage = CanvasPreviewDamage.Between(previous, snapshot, renderer, assets, presentationDamage, windowDamage);
            var pixels = targets.Render(snapshot.Outputs, snapshot.PreferGpu, gpuWorkload, surface =>
            {
                var canvas = surface.Canvas;
                int saved = canvas.Save();
                try
                {
                    canvas.Translate((float)snapshot.Context.Origin.X, (float)snapshot.Context.Origin.Y);
                    if (snapshot.ScreenDraft is { } screenDraft) renderer.DrawWithScreenDraft(canvas, document, assets, screenDraft);
                    else cache.DrawOnClearedRasterTarget(surface, document, assets, snapshot.Revision, snapshot.Transaction,
                        snapshot.First, snapshot.Last, snapshot.BypassCache, snapshot.SceneIdentity);
                    if (snapshot.Context.Calibration && !snapshot.Context.Placing)
                        renderer.DrawCalibrationGuides(canvas, document, snapshot.Context.GuideOpacity);
                    if (snapshot.PlacementScene is { } placement && snapshot.PlacementClip is { Count: 4 } quad)
                    {
                        using var builder = new SKPathBuilder();
                        builder.MoveTo((float)quad[0].X, (float)quad[0].Y);
                        foreach (var point in quad.Skip(1)) builder.LineTo((float)point.X, (float)point.Y);
                        builder.Close(); using var clip = builder.Detach(); canvas.ClipPath(clip);
                        renderer.DrawCalibrationGuides(canvas, placement, snapshot.Context.GuideOpacity);
                    }
                }
                finally { canvas.RestoreToCount(saved); }
            }, damage);
            previous = snapshot;
            previousPresentation = presentation;
            previousWindows = windows;
            var animation = renderer.CaptureAnimationSchedule(document, assets, snapshot.AnimationTime);
            var editing = snapshot.EditingEvaluation?.Scene;
            var picking = editing is not null && snapshot.EditingPlacement is { } placement
                ? EditingDocument(editing, document, placement, snapshot.EditingRootCanvas, snapshot.AuthorRootCanvas, snapshot.EditingScopeId, snapshot.IndependentCanvas) : snapshot.Document;
            return new(snapshot.Context, pixels, targets.Backend, gpuWorkload,
                targets.GpuDetails, targets.GpuFailure, timer.Elapsed.TotalMilliseconds,
                new(cache.BuildCount, cache.HitCount, cache.CachedRasterBytes, cache.RasterCacheByteLimit, cache.InPlaceOpacityFrameCount,
                    targets.ActivePixelBytes, targets.PooledPixelBytes, targets.RasterBufferBytes, renderer.CachedImageBytes,
                    pixels.CopiedBytes, renderer.CachedAnimationMetadataBytes, renderer.PresentationPlanBuildCount,
                    renderer.PresentationFrameBuildCount, renderer.WindowPlanBuildCount, renderer.WindowFrameBuildCount, windows.PixelBytes), animation)
            {
                Sequence = snapshot.Sequence, ContentRevision = snapshot.ContentRevision, InputSequence = snapshot.InputSequence,
                CapturedTimestamp = snapshot.CapturedTimestamp, CaptureMilliseconds = snapshot.CaptureMilliseconds,
                RenderStartedTimestamp = renderStarted, RenderCompletedTimestamp = Stopwatch.GetTimestamp(),
                AnimationTime = snapshot.AnimationTime, PickingDocument = picking,
                PickingSourceDocument = snapshot.SourceDocument, PickingEvaluation = editing ?? snapshot.Evaluation,
                PickingImages = renderer.CaptureImageSnapshot(), PickingResources = snapshot.Resources,
                PresentationPrimitiveCount = presentation.PrimitiveCount, PresentationBudgetExceeded = presentation.BudgetExceeded,
                PresentationFrameSnapshot = presentation.BudgetExceeded ? null : snapshot.PresentationFrame,
                PresentationPlanIdentity = snapshot.PresentationPlanIdentity,
                PickingWindowFrames = windowFrames?.Retain(), WindowOwnerContext = windowFrames?.OwnerContext, WindowInfo = windows
            };
        }
        finally { renderer.LiveBackdrop = null; renderer.PresentationFrame = null; renderer.WindowFrames = null; renderer.WindowOwnerContext = null; }
    }

    internal static SceneDocument EditingDocument(EvaluatedScene editing, SceneDocument display, LayerPlacement placement,
        RootCanvasKind rootCanvas = RootCanvasKind.Main, RootCanvasKind? authorRootCanvas = null, Guid? scopeId = null, bool independent = false)
    {
        var source = editing.RenderDocument;
        var visible = independent ? SceneEvaluation.Build(display).Leaves.ToDictionary(leaf => leaf.RuntimeElementId, leaf => leaf.RenderElement.Visible) : null;
        return new()
        {
            ResourceId = source.ResourceId, Kind = source.Kind, Viewport = display.Viewport,
            Canvas = rootCanvas == RootCanvasKind.Main ? display.Canvas.Clone() : new(), LayerOpacity = source.LayerOpacity,
            Elements = source.Elements.Where(element => authorRootCanvas is null || editing.RootCanvasOf(element.Id) == authorRootCanvas &&
                editing.ResolveSelectableNode(element.Id, scopeId) is not null).Select(element =>
            {
                var result = element.Clone(); SceneGraph.ApplyPlacement(result, placement);
                if (visible is not null) result.Visible = visible.GetValueOrDefault(element.Id);
                result.Locked = !editing.IsPickableOccurrence(element.Id, visibleOverride: true);
                return result;
            }).ToList()
        };
    }

    private CanvasPreviewSnapshot ResolveWorldSnapshot(CanvasPreviewSnapshot source)
    {
        if (source.Document.IsRuntimeEvaluated || source.ScreenDraft is null && source.PresentationFrame is null && source.WindowFrames is null && source.FrozenEvaluation is null && source.Document.Groups.Count == 0 && source.Document.Instances.Count == 0 && source.Document.RootOrder is null) return source;
        var frozen = source.FrozenEvaluation;
        if (frozen is null)
        {
            if (!ReferenceEquals(evaluatedSource, source.Document) || evaluatedRevision != source.ContentRevision ||
                evaluatedCommittedRevision != source.Revision || evaluatedTransaction != source.Transaction || fallbackEvaluation is null)
            {
                evaluatedSource = source.Document; evaluatedRevision = source.ContentRevision;
                evaluatedCommittedRevision = source.Revision; evaluatedTransaction = source.Transaction;
                fallbackEvaluation = new(source.Document);
            }
            frozen = fallbackEvaluation;
        }
        var evaluation = frozen.Scene; var range = frozen.RenderRange(source.First, source.Last);
        var screenDraft = source.ScreenDraft;
        if (screenDraft is not null)
        {
            if (!frozen.TryScreenDraftSlot(source.ScreenDraftScopeId, out int insertionIndex, out bool visible)) screenDraft = null;
            else
            {
                var raw = screenDraft.ScreenElement.Clone(); raw.Visible &= visible;
                var candidate = screenDraft.ResolvedWorldElement?.Clone(); if (candidate is not null) candidate.Visible &= visible;
                screenDraft = new(raw, insertionIndex, candidate, screenDraft.RootCanvas);
            }
        }
        return new()
        {
            Context = source.Context, SceneIdentity = source.SceneIdentity, Document = evaluation.RenderDocument,
            SourceDocument = source.SourceDocument ?? source.Document, Evaluation = evaluation, FrozenEvaluation = frozen,
            EditingEvaluation = source.EditingEvaluation, EditingPlacement = source.EditingPlacement, EditingRootCanvas = source.EditingRootCanvas,
            AuthorRootCanvas = source.AuthorRootCanvas, EditingScopeId = source.EditingScopeId,
            IndependentCanvas = source.IndependentCanvas,
            ScreenDraft = screenDraft, ScreenDraftScopeId = source.ScreenDraftScopeId,
            PresentationFrame = source.PresentationFrame, PresentationOrigin = source.PresentationOrigin,
            PresentationPlanIdentity = source.PresentationPlanIdentity,
            WindowFrames = source.WindowFrames, WindowOwnerContext = source.WindowOwnerContext,
            Resources = source.Resources, Outputs = source.Outputs, Revision = source.Revision, Transaction = source.Transaction,
            First = range.First, Last = range.Last, BypassCache = source.BypassCache, GraphChanged = source.GraphChanged, PreferGpu = source.PreferGpu,
            AnimationTime = source.AnimationTime, Backdrop = source.Backdrop, PlacementScene = source.PlacementScene, PlacementClip = source.PlacementClip,
            Sequence = source.Sequence, ContentRevision = source.ContentRevision, InputSequence = source.InputSequence,
            CapturedTimestamp = source.CapturedTimestamp, CaptureMilliseconds = source.CaptureMilliseconds, PixelDamage = source.PixelDamage
        };
    }

    public void Dispose() { cache.Dispose(); renderer.Dispose(); targets.Dispose(); }
}

/// <summary>UI requests carry no queued snapshots. While busy, remember only that a newer value exists;
/// after completion capture that value on the UI thread. Shutdown never waits for a heavy render.</summary>
internal sealed class CanvasPreviewPump : IDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly Func<CanvasPreviewSnapshot> capture;
    private readonly Func<ValueTask<CanvasPreviewSnapshot>>? captureAsync;
    private readonly Func<CanvasPreviewSnapshot, CanvasPreviewFrame>? renderOverride;
    private readonly object gate = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly Thread worker;
    private CanvasPreviewSnapshot? next;
    private bool disposed, pending, stopped;
    private long requestedSequence;
    internal bool Busy { get; private set; }
    internal CanvasPreviewFrame? Frame { get; private set; }
    internal CanvasPreviewMetrics CacheMetrics => Frame?.Cache ?? new(0, 0, 0, 128L * 1024 * 1024, 0);
    internal long CompletedFrames { get; private set; }
    internal long RequestedSequence => requestedSequence;
    internal long GpuFrames { get; private set; }
    internal long CpuFrames { get; private set; }
    internal event Action? FrameReady;
    internal event Action<Exception>? Failed;
    internal event Action<CanvasPreviewContext?, long, Exception>? RequestFailed;

    internal CanvasPreviewPump(Dispatcher dispatcher, Func<CanvasPreviewSnapshot> capture,
        Func<CanvasPreviewSnapshot, CanvasPreviewFrame>? renderOverride = null,
        Func<ValueTask<CanvasPreviewSnapshot>>? captureAsync = null)
    {
        this.dispatcher = dispatcher; this.capture = capture; this.renderOverride = renderOverride; this.captureAsync = captureAsync;
        worker = new(Work) { IsBackground = true, Name = "FloatCanvas preview" };
        worker.Start();
    }

    internal void Request()
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        requestedSequence++;
        pending = true;
        StartLatest();
    }

    private void StartLatest()
    {
        if (Busy || !pending || disposed) return;
        if (captureAsync is not null)
        {
            pending = false; Busy = true;
            CaptureLatestAsync();
            return;
        }
        long began = Stopwatch.GetTimestamp();
        pending = false; Busy = true;
        long sequence = requestedSequence;
        try { PublishSnapshot(capture(), began); }
        catch (Exception error) { CompleteCapture(null, error, began, sequence); }
    }

    private async void CaptureLatestAsync()
    {
        long began = Stopwatch.GetTimestamp();
        long sequence = requestedSequence;
        CanvasPreviewSnapshot? snapshot = null;
        Exception? error = null;
        try { snapshot = await captureAsync!(); }
        catch (Exception failure) { error = failure; }
        if (dispatcher.CheckAccess()) { CompleteCapture(snapshot, error, began, sequence); return; }
        lock (gate)
        {
            if (disposed || dispatcher.HasShutdownStarted) { snapshot?.Dispose(); return; }
        }
        DispatcherOperation operation;
        try { operation = dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => CompleteCapture(snapshot, error, began, sequence))); }
        catch (InvalidOperationException) when (dispatcher.HasShutdownStarted) { snapshot?.Dispose(); return; }
        _ = operation.Task.ContinueWith(task => { if (task.IsCanceled) snapshot?.Dispose(); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void CompleteCapture(CanvasPreviewSnapshot? snapshot, Exception? error, long began, long sequence)
    {
        dispatcher.VerifyAccess();
        if (disposed || dispatcher.HasShutdownStarted) { snapshot?.Dispose(); Busy = false; return; }
        if (error is null) { PublishSnapshot(snapshot!, began); return; }
        snapshot?.Dispose(); Busy = false;
        try { RequestFailed?.Invoke(null, sequence, error); Failed?.Invoke(error); }
        finally { StartLatest(); }
    }

    private void PublishSnapshot(CanvasPreviewSnapshot snapshot, long began)
    {
        snapshot.Sequence = requestedSequence;
        snapshot.CapturedTimestamp = Stopwatch.GetTimestamp();
        snapshot.CaptureMilliseconds = Stopwatch.GetElapsedTime(began, snapshot.CapturedTimestamp).TotalMilliseconds;
        lock (gate) next = snapshot;
        wake.Set();
    }

    private void Work()
    {
        CanvasPreviewRenderer? renderer = null;
        bool interactivePolicy = false;
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
                bool interactive = snapshot.Context.Calibration && !snapshot.Context.Hidden;
                try
                {
                    if (interactive != interactivePolicy)
                    {
                        SetInteractiveExecutionSpeed(interactive);
                        interactivePolicy = interactive;
                    }
                    frame = renderOverride is null ? (renderer ??= new CanvasPreviewRenderer()).Render(snapshot) : renderOverride(snapshot);
                }
                catch (Exception failure) { error = failure; }
                finally { snapshot.Dispose(); }
                lock (gate)
                    if (disposed || dispatcher.HasShutdownStarted) { frame?.Dispose(); return; }
                var completed = frame;
                DispatcherOperation operation;
                // Finished pixels are render work. Input-priority pointer traffic must not
                // starve their presentation or the capture of the newest pending value.
                var failedContext = snapshot.Context;
                long failedSequence = snapshot.Sequence;
                try { operation = dispatcher.InvokeAsync(() => Complete(completed, error, failedContext, failedSequence), DispatcherPriority.Render); }
                catch (InvalidOperationException) when (dispatcher.HasShutdownStarted) { completed?.Dispose(); return; }
                // A shutdown can cancel a callback after it was queued. Its result still owns pixels.
                _ = operation.Task.ContinueWith(task => { if (task.IsCanceled) completed?.Dispose(); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        finally
        {
            if (interactivePolicy) SetInteractiveExecutionSpeed(false);
            try { renderer?.Dispose(); }
            catch (Exception error) { Trace.TraceError("Preview cleanup failed: {0}", error); }
            lock (gate) { stopped = true; wake.Dispose(); }
        }
    }

    // Calibration contributes to the current interaction even though this is a
    // background thread. Ask Windows for performance while drawing, then restore
    // its default heuristics. This neither raises priority nor pins a processor.
    private static void SetInteractiveExecutionSpeed(bool interactive)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299)) return;
        var policy = new ThreadPowerThrottling { Version = 1, ControlMask = interactive ? 1u : 0u };
        _ = SetThreadInformation(GetCurrentThread(), 3, ref policy, (uint)Marshal.SizeOf<ThreadPowerThrottling>());
        // Older systems may reject the optional information class; rendering is unchanged.
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadPowerThrottling { internal uint Version, ControlMask, StateMask; }
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadInformation(IntPtr thread, int informationClass, ref ThreadPowerThrottling policy, uint size);

    private void Complete(CanvasPreviewFrame? frame, Exception? error, CanvasPreviewContext context, long sequence)
    {
        Busy = false;
        if (disposed) { frame?.Dispose(); return; }
        try
        {
            if (frame is not null)
            {
                Frame?.Dispose(); Frame = frame; CompletedFrames++;
                if (frame.Backend == OverlayRenderBackend.Gpu) GpuFrames++; else CpuFrames++;
                // Advance computation before notifying presentation observers. Observers
                // queue their UI work; they never hold the worker behind native submission.
                StartLatest();
                FrameReady?.Invoke();
            }
            if (error is not null) { RequestFailed?.Invoke(context, sequence, error); Failed?.Invoke(error); }
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
