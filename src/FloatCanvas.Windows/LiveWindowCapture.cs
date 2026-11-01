using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using SkiaSharp;

namespace FloatCanvas.Windows;

public enum WindowCaptureStatus { Starting, Live, Paused, Hidden, Minimized, Resizing, Closed, Unavailable, Stopped }

public interface IWindowCaptureSource : IDisposable
{
    WindowCaptureTarget Target { get; }
    long StreamId { get; }
    long ConnectionVersion { get; }
    WindowCaptureStatus Status { get; }
    Exception? LastError { get; }
    Task Completion { get; }
    Task FirstResult { get; }
    event Action? FrameReady;
    event Action? StateChanged;
    void SetActive(bool enabled);
    void Resume();
    bool TryGetLatest([NotNullWhen(true)] out LiveWindowFrame? frame);
}

/// <summary>Exclusive owned pixels transferred once by TryGetLatest. No project or asset identity is stored here.</summary>
public sealed class LiveWindowFrame : IDisposable
{
    private int disposed;
    /// <summary>Transfers the immutable bitmap on success; failure leaves it owned by the caller.</summary>
    public LiveWindowFrame(long stream, long connection, long sequence, WindowCaptureSize content, SKBitmap bitmap, long capturedAt)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (stream <= 0 || connection < 0 || sequence <= 0 || capturedAt < 0 || !bitmap.IsImmutable ||
            bitmap.ColorType is not (SKColorType.Bgra8888 or SKColorType.Rgba8888) ||
            bitmap.Width is < 1 or > WindowCapturePolicy.MaximumWidth || bitmap.Height is < 1 or > WindowCapturePolicy.MaximumHeight ||
            (long)bitmap.RowBytes * bitmap.Height > (long)WindowCapturePolicy.MaximumWidth * WindowCapturePolicy.MaximumHeight * 4 ||
            content.Width < bitmap.Width || content.Height < bitmap.Height || !WindowCapturePolicy.TryOutputSize(content.Width, content.Height, out _))
            throw new ArgumentException("Invalid window frame identity, dimensions or ownership.");
        StreamId = stream; ConnectionVersion = connection; Sequence = sequence; ContentSize = content; Bitmap = bitmap; CapturedAt = capturedAt;
    }
    public long StreamId { get; }
    public long ConnectionVersion { get; }
    public long Sequence { get; }
    public WindowCaptureSize ContentSize { get; }
    public SKBitmap Bitmap { get; }
    public long CapturedAt { get; }
    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) Bitmap.Dispose(); }
}

/// <summary>One explicitly selected window and its item identity, with a latest-only 30 FPS capture worker.</summary>
public sealed class LiveWindowCapture : IWindowCaptureSource
{
    private static long streams;
    private readonly object gate = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource firstResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread worker;
    private LiveWindowFrame? latest;
    private Exception? failure;
    private bool active = true, disposed;
    private long connection = 1, sequence, readbacks, arrivals;
    private int status = (int)WindowCaptureStatus.Starting, framePending;
    public WindowCaptureTarget Target { get; }
    public long StreamId { get; } = Interlocked.Increment(ref streams);
    public long ConnectionVersion { get { lock (gate) return connection; } }
    public WindowCaptureStatus Status => (WindowCaptureStatus)Volatile.Read(ref status);
    public Exception? LastError { get { lock (gate) return failure; } }
    public Task Completion => completion.Task;
    /// <summary>Completes after the first frame or terminal outcome; inspect TryGetLatest/Status afterwards.</summary>
    public Task FirstResult => firstResult.Task;
    public long ReadbackCount => Interlocked.Read(ref readbacks);
    public long ArrivalCount => Interlocked.Read(ref arrivals);
    /// <summary>Background notification, coalesced while one unclaimed frame remains.</summary>
    public event Action? FrameReady;
    /// <summary>Background metadata notification. Marshal to the owning UI context.</summary>
    public event Action? StateChanged;

    public LiveWindowCapture(WindowCaptureTarget target)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        worker = new Thread(Run) { IsBackground = true, Name = "FloatCanvas window source" };
        worker.SetApartmentState(ApartmentState.MTA); worker.Start();
    }

    public void SetActive(bool enabled) => SetActive(enabled, false);
    /// <summary>Explicit retry from the same still-live capture item. Never creates a new item for the HWND.</summary>
    public void Resume() => SetActive(true, true);
    private void SetActive(bool enabled, bool retry)
    {
        lock (gate)
        {
            if (disposed || active == enabled && !retry) return;
            active = enabled; connection++; failure = null; latest?.Dispose(); latest = null;
        }
        Wake();
    }
    public bool TryGetLatest([NotNullWhen(true)] out LiveWindowFrame? frame)
    {
        lock (gate)
        {
            frame = latest; latest = null;
            if (frame is null) return false;
            if (!disposed && active && frame.ConnectionVersion == connection) return true;
            frame.Dispose(); frame = null; return false;
        }
    }

    private void Run()
    {
        WgcWindowItem? item = null; WgcWindowSession? capture = null;
        bool initialized = false, faulted = false; long currentConnection = 0, nextSample = 0, nextMetadata = 0;
        bool visible = false, minimized = false;
        try
        {
            WgcDesktopSession.InitializeRuntime(); initialized = true;
            item = new(Target, Wake);
            while (!stop.IsCancellationRequested)
            {
                bool requested; long expected;
                lock (gate) { requested = active; expected = connection; }
                if (expected != currentConnection)
                { ReleaseCapture(ref capture); currentConnection = expected; faulted = false; nextSample = nextMetadata = 0; }
                if (item.IsClosed) { SetStatus(WindowCaptureStatus.Closed); firstResult.TrySetResult(); break; }
                long now = Stopwatch.GetTimestamp();
                if (now >= nextMetadata)
                {
                    if (!Target.IsCurrent) { SetStatus(WindowCaptureStatus.Closed); firstResult.TrySetResult(); break; }
                    visible = WindowCaptureCatalog.IsVisible(Target) && PresentationDesktopAvailability.IsCurrentInputDesktop();
                    minimized = WindowCaptureCatalog.IsMinimized(Target);
                    nextMetadata = now + Stopwatch.Frequency / 4;
                }
                if (!requested || !visible || minimized || faulted)
                {
                    ReleaseCapture(ref capture);
                    SetStatus(faulted ? WindowCaptureStatus.Unavailable : !requested ? WindowCaptureStatus.Paused : minimized ? WindowCaptureStatus.Minimized : WindowCaptureStatus.Hidden);
                    if (faulted) firstResult.TrySetResult();
                    wake.WaitOne(250); continue;
                }
                try
                {
                    if (capture is null) { SetStatus(WindowCaptureStatus.Starting); capture = new(item, FrameArrived); nextSample = 0; }
                    now = Stopwatch.GetTimestamp();
                    if (now < nextSample)
                    {
                        int delay = Math.Max(1, (int)Math.Ceiling((nextSample - now) * 1000d / Stopwatch.Frequency));
                        wake.WaitOne(Math.Min(delay, 250)); continue;
                    }
                    Interlocked.Exchange(ref framePending, 0);
                    nextSample = now + (Stopwatch.Frequency + WindowCapturePolicy.FramesPerSecond - 1) / WindowCapturePolicy.FramesPerSecond;
                    if (capture.TryRead(stop.Token, out var bitmap, out var size))
                    {
                        Interlocked.Increment(ref readbacks);
                        var frame = new LiveWindowFrame(StreamId, expected, ++sequence, size, bitmap!, capture.Timestamp);
                        bool notify = false, accepted = false;
                        lock (gate)
                        {
                            if (!disposed && active && connection == expected && !item.IsClosed)
                            { notify = latest is null; accepted = true; latest?.Dispose(); latest = frame; frame = null!; }
                        }
                        frame?.Dispose();
                        if (accepted) { SetStatus(WindowCaptureStatus.Live); firstResult.TrySetResult(); }
                        if (notify) Notify(FrameReady);
                    }
                    else if (capture.Resizing)
                    {
                        // A static source may not invalidate again after a pool resize.
                        // Restart capture from the retained item so its resized pixels
                        // arrive without requiring a repaint of the target window.
                        ReleaseCapture(ref capture); SetStatus(WindowCaptureStatus.Resizing);
                        nextSample = 0; continue;
                    }
                    // A static window need not supply a fresh frame. Keep its last pixels,
                    // then wait for a notification or the next metadata check.
                    wake.WaitOne(Math.Max(1, (int)Math.Min(250, Math.Ceiling((nextMetadata - Stopwatch.GetTimestamp()) * 1000d / Stopwatch.Frequency))));
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    lock (gate) failure = error;
                    ReleaseCapture(ref capture); faulted = true; SetStatus(WindowCaptureStatus.Unavailable); firstResult.TrySetResult();
                }
            }
        }
        catch (Exception error)
        { lock (gate) failure = error; SetStatus(WindowCaptureStatus.Unavailable); firstResult.TrySetResult(); }
        finally
        {
            try { ReleaseCapture(ref capture); }
            finally
            {
                try { item?.Dispose(); }
                catch (Exception error) { lock (gate) failure ??= error; }
                finally
                {
                    if (initialized) WgcDesktopSession.UninitializeRuntime();
                    if (stop.IsCancellationRequested) SetStatus(WindowCaptureStatus.Stopped);
                    lock (gate) { latest?.Dispose(); latest = null; stop.Dispose(); }
                    firstResult.TrySetResult(); completion.TrySetResult();
                    wake.Dispose();
                }
            }
        }
    }
    private void ReleaseCapture(ref WgcWindowSession? capture)
    {
        var old = capture; capture = null;
        try { old?.Dispose(); }
        catch (Exception error) { lock (gate) failure ??= error; }
    }
    private void FrameArrived()
    {
        Interlocked.Increment(ref arrivals);
        if (Interlocked.Exchange(ref framePending, 1) == 0) Wake();
    }
    private void Wake() { try { wake.Set(); } catch (ObjectDisposedException) { } }
    private void SetStatus(WindowCaptureStatus value)
    { if (Interlocked.Exchange(ref status, (int)value) != (int)value) Notify(StateChanged); }
    private static void Notify(Action? action) { try { action?.Invoke(); } catch { } }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; active = false; connection++; latest?.Dispose(); latest = null;
            try { stop.Cancel(); } catch (ObjectDisposedException) { }
        }
        Wake();
    }
}
