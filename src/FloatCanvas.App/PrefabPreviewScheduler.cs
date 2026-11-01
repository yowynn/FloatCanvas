using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

namespace FloatCanvas.App;

internal sealed record PrefabPreviewFrame(BitmapSource Image, bool HasLiveEffectPlaceholder);

/// <summary>One bounded worker with a coalesced current request and an epoch for context lifetime.</summary>
internal sealed class PrefabPreviewScheduler : IDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer timer;
    private readonly Func<PrefabPreviewInput, CancellationToken, PrefabPreviewFrame> render;
    private Func<PrefabPreviewInput>? capture;
    private object? context;
    private long revision = -1, epoch;
    private bool dirty, running, disposed;
    private CancellationTokenSource? active;
    internal int CaptureCount { get; private set; }
    internal int RenderCount { get; private set; }
    internal int MaximumConcurrentWorkers { get; private set; }
    internal bool IsRunning => running;
    internal event Action<PrefabPreviewFrame>? FrameReady;
    internal event Action<string>? Failed;

    internal PrefabPreviewScheduler(Dispatcher dispatcher,
        Func<PrefabPreviewInput, CancellationToken, PrefabPreviewFrame>? render = null, TimeSpan? interval = null)
    {
        this.dispatcher = dispatcher; this.render = render ?? Render;
        timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = interval ?? TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => { timer.Stop(); if (!running) Start(); };
    }
    internal void Request(object identity, long contentRevision, Func<PrefabPreviewInput> capture)
    {
        dispatcher.VerifyAccess(); if (disposed) return;
        if (ReferenceEquals(context, identity) && revision == contentRevision) return;
        if (!ReferenceEquals(context, identity)) Cancel();
        context = identity; revision = contentRevision; this.capture = capture; dirty = true;
        // Do not restart the clock for each pointer sample: continuous drawing still previews.
        if (!timer.IsEnabled && !running) timer.Start();
    }
    internal void Cancel()
    {
        dispatcher.VerifyAccess(); epoch++; timer.Stop(); active?.Cancel();
        context = null; revision = -1; capture = null; dirty = false;
    }
    private void Start()
    {
        if (disposed || running || !dirty || capture is null || context is null) return;
        PrefabPreviewInput input;
        try { input = capture(); CaptureCount++; }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { dirty = false; Failed?.Invoke(ex.Message); return; }
        dirty = false; running = true; MaximumConcurrentWorkers = Math.Max(MaximumConcurrentWorkers, 1);
        var token = active = new CancellationTokenSource(); long renderingEpoch = epoch;
        _ = WorkAsync(input, token, renderingEpoch);
    }
    private async Task WorkAsync(PrefabPreviewInput input, CancellationTokenSource cancellation, long renderingEpoch)
    {
        PrefabPreviewFrame? frame = null; Exception? error = null;
        try { frame = await Task.Run(() => render(input, cancellation.Token)).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { error = ex; }
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) { cancellation.Dispose(); return; }
        await dispatcher.InvokeAsync(() =>
        {
            running = false; RenderCount++; if (ReferenceEquals(active, cancellation)) active = null;
            bool current = !disposed && renderingEpoch == epoch && !cancellation.IsCancellationRequested && context is not null;
            cancellation.Dispose();
            if (current && frame is not null) FrameReady?.Invoke(frame);
            if (current && error is not null) Failed?.Invoke(error.Message);
            if (!disposed && dirty && capture is not null && !timer.IsEnabled) timer.Start();
        });
    }
    internal static PrefabPreviewFrame Render(PrefabPreviewInput input, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var scene = SceneEvaluation.Build(input.Document).RenderDocument;
        bool live = false;
        foreach (var element in scene.Elements.Where(element => ElementGeometry.IsLiveEffect(element.Kind)))
        {
            live = true; element.Kind = ElementKind.Rectangle; element.Effects.Clear();
            element.Style = new() { StrokeWidth = 1, Stroke = 0xff87919d, Fill = 0x55687380, StrokePattern = StrokePattern.Dashed };
        }
        var viewport = scene.Viewport.Normalize();
        if (!double.IsFinite(viewport.X + viewport.Y + viewport.Width + viewport.Height) || viewport.Width <= 0 || viewport.Height <= 0)
            throw new InvalidDataException("Invalid prefab preview viewport.");
        double fit = Math.Min(1, Math.Min(1000 / viewport.Width, 700 / viewport.Height));
        int width = Math.Clamp((int)Math.Ceiling(viewport.Width * fit), 1, 1000), height = Math.Clamp((int)Math.Ceiling(viewport.Height * fit), 1, 700);
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul)) ?? throw new InvalidOperationException("Cannot create prefab preview surface.");
        var canvas = surface.Canvas; canvas.Clear(new SKColor(238, 241, 245));
        using (var checker = new SKPaint { Color = new SKColor(220, 225, 232) })
            for (int y = 0; y < height; y += 16) for (int x = (y / 16 % 2) * 16; x < width; x += 32) canvas.DrawRect(x, y, 16, 16, checker);
        canvas.Scale((float)fit); canvas.Translate((float)-viewport.X, (float)-viewport.Y);
        var assets = new AssetLibrary(); foreach (var resource in input.Resources) assets.Add(resource);
        using var renderer = new SceneRenderer { CacheByteLimit = 32L * 1024 * 1024, AnimationTime = input.AnimationTime };
        renderer.Draw(canvas, scene, assets, includeBackground: true, includeCanvasBackground: true, includeGrid: true);
        cancellation.ThrowIfCancellationRequested();
        using var image = surface.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(data.ToArray());
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        return new(bitmap, live);
    }
    public void Dispose()
    {
        dispatcher.VerifyAccess(); if (disposed) return; disposed = true; Cancel();
    }
}
