using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using SkiaSharp;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using MapFlags = Vortice.Direct3D11.MapFlags;
using ResultCode = Vortice.DXGI.ResultCode;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Windows;

/// <summary>A single output's ROI in desktop physical pixels. Bitmap is owned by its enclosing frame.
/// LastPresentTimestamp retains its compatibility name: QPC desktop-present time for DXGI,
/// or the captured frame's SystemRelativeTime converted to QPC for WGC.</summary>
public sealed record LiveDesktopOutputFrame(string DeviceName, PixelBounds Bounds, SKBitmap Bitmap,
    long LastPresentTimestamp, int RotationDegrees, bool ProtectedContentMaskedOut)
{
    public LiveDesktopFrameTiming? Timing { get; init; }
}

/// <summary>QPC acquisition/readback milestones; contains no captured content.</summary>
public sealed record LiveDesktopFrameTiming(long AcquiredTimestamp, long ReadbackCompletedTimestamp,
    ulong ConfigurationIteration, int FramesDrained, int FramesRejected);

/// <summary>Caller-owned, immutable desktop pixels. Dispose the frame, not its individual bitmaps.</summary>
public sealed class LiveDesktopFrame : IDisposable
{
    private int _disposed;
    internal LiveDesktopFrame(long targetVersion, LiveDesktopOutputFrame[] outputs)
    { TargetVersion = targetVersion; GeneratedAtTimestamp=Stopwatch.GetTimestamp(); Outputs = Array.AsReadOnly(outputs); }
    public long TargetVersion { get; }
    /// <summary>QPC when this ROI set was assembled; each output retains its own source pixel timestamp.</summary>
    public long GeneratedAtTimestamp { get; }
    /// <summary>Outputs retain independent QPC timestamps; this is not a synchronized multi-screen snapshot.</summary>
    public IReadOnlyList<LiveDesktopOutputFrame> Outputs { get; }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var output in Outputs) output.Bitmap.Dispose();
    }
}

/// <summary>
/// Persistent screen sampling on one background thread, capped at the display rate and 60 FPS. WGC can exclude this
/// process's windows in its own session; the compatibility DXGI mode requires caller-owned
/// global exclusion. No window hiding or image codecs are used; only requested ROIs reach CPU memory.
/// </summary>
public sealed class LiveDesktopCapture : IDisposable
{
    private static readonly RetryableCapabilityProbe SessionExclusionProbe = new(WgcDesktopSession.ProbeSessionExclusion);
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _worker;
    private CaptureTarget[] _targets = [];
    private long _targetVersion, _notBeforeTimestamp;
    private LiveDesktopFrame? _latest;
    private Exception? _lastError;
    private bool _disposed, _workerExited;
    private int _framePending, _samplingFramesPerSecond = LiveCaptureCadence.MaximumFramesPerSecond;
    private long _frameArrivalCount, _samplingPassCount;

    /// <summary>Background notification only. Marshal to the UI, then claim a frame with TryGetLatest.</summary>
    public event Action? FrameReady;
    /// <summary>Background notification. Acquisition failures discard pending pixels and rebuild DXGI state.</summary>
    public event Action<Exception>? Failed;
    public Task Completion => _completion.Task;
    public Exception? LastError { get { lock (_gate) return _lastError; } }
    public long TargetVersion { get { lock (_gate) return _targetVersion; } }
    /// <summary>True only when this source uses per-session WGC window exclusion, without global display affinity.</summary>
    public bool UsesSessionWindowExclusion { get; }
    public int SamplingFramesPerSecond => Volatile.Read(ref _samplingFramesPerSecond);
    public long FrameArrivalCount => Interlocked.Read(ref _frameArrivalCount);
    public long SamplingPassCount => Interlocked.Read(ref _samplingPassCount);

    /// <summary>Queries the actual display-session interface on an MTA thread without starting capture or showing a picker.
    /// Known absent APIs return cached false; device/policy failures allow a later retry with backoff.</summary>
    public static Task<bool> ProbeSessionExclusionAsync() => SessionExclusionProbe.GetAsync();

    public LiveDesktopCapture(bool useSessionWindowExclusion = false)
    {
        UsesSessionWindowExclusion = useSessionWindowExclusion;
        _worker = new Thread(Run) { IsBackground = true, Name = "FloatCanvas desktop effects" };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }

    /// <summary>
    /// Replaces requested global ROIs. Empty targets suspend acquisition and release GPU resources.
    /// Equal targets preserve sessions/version. The returned version identifies all subsequently delivered pixels.
    /// </summary>
    public long UpdateTargets(IReadOnlyList<MonitorInfo> monitors, IReadOnlyList<PixelBounds> regions, long notBeforeTimestamp)
    {
        ArgumentNullException.ThrowIfNull(monitors); ArgumentNullException.ThrowIfNull(regions);
        if (notBeforeTimestamp < 0 || notBeforeTimestamp > Stopwatch.GetTimestamp())
            throw new ArgumentOutOfRangeException(nameof(notBeforeTimestamp));
        var targets = BuildTargets(monitors, regions);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed || _workerExited, this);
            if (_notBeforeTimestamp == notBeforeTimestamp && _targets.SequenceEqual(targets)) return _targetVersion;
            _targets = targets; _notBeforeTimestamp = notBeforeTimestamp; _targetVersion++;
            _latest?.Dispose(); _latest = null; _lastError = null;
            _wake.Set(); return _targetVersion;
        }
    }

    /// <summary>
    /// Transfers exclusive ownership of the newest pending frame. It is returned only once; a timeout
    /// or mouse-only update never republishes stale pixels as a fresh desktop present.
    /// </summary>
    public bool TryGetLatest([NotNullWhen(true)] out LiveDesktopFrame? frame)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed || _workerExited, this);
            frame = _latest; _latest = null; return frame is not null;
        }
    }

    /// <summary>Signals shutdown without blocking the UI on a graphics driver. Await Completion when needed.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (!_workerExited) { _stop.Cancel(); _wake.Set(); }
            _latest?.Dispose(); _latest = null;
        }
    }

    private void Run()
    {
        var outputs = new Dictionary<string, ILiveDesktopOutputSession>(StringComparer.OrdinalIgnoreCase);
        long nextFrame = 0, nextWatchdog = 0, publishedVersion=-1, sampledVersion=-1, rateVersion=-1;
        long frameInterval = LiveCaptureCadence.Interval(LiveCaptureCadence.MaximumFramesPerSecond);
        bool winrtInitialized = false;
        try
        {
            if (UsesSessionWindowExclusion) { WgcDesktopSession.InitializeRuntime(); winrtInitialized = true; }
            using var dpi = new ThreadDpiAwarenessScope(WindowsCapabilities.Current);
            while (!_stop.IsCancellationRequested)
            {
                CaptureTarget[] targets; long version, boundary;
                lock (_gate) { targets = _targets; version = _targetVersion; boundary = _notBeforeTimestamp; }
                if (targets.Length == 0)
                {
                    ClearOutputs(outputs); nextFrame = 0; nextWatchdog=0; _wake.WaitOne(); continue;
                }
                long now = Stopwatch.GetTimestamp();
                if (rateVersion != version)
                {
                    int rate = LiveCaptureCadence.QueryRate(targets.Select(target=>target.Monitor));
                    Volatile.Write(ref _samplingFramesPerSecond,rate);
                    frameInterval=LiveCaptureCadence.Interval(rate); rateVersion=version;
                }
                if (UsesSessionWindowExclusion && sampledVersion==version && Volatile.Read(ref _framePending)==0 && now<nextWatchdog)
                {
                    // Static desktops need no tight polling. A low-rate watchdog still checks
                    // exclusion identities and first-frame timeout if no notification arrives.
                    _wake.WaitOne((int)Math.Clamp(Math.Ceiling((nextWatchdog-now)*1000d/Stopwatch.Frequency),1,250)); continue;
                }
                if (now < nextFrame)
                { _wake.WaitOne((int)Math.Clamp(Math.Ceiling((nextFrame - now) * 1000d / Stopwatch.Frequency), 1, 1000)); continue; }
                nextFrame = now + frameInterval; nextWatchdog=now+Stopwatch.Frequency/4;
                sampledVersion=version; Interlocked.Exchange(ref _framePending,0); Interlocked.Increment(ref _samplingPassCount);
                try
                {
                    foreach (var name in outputs.Keys.Where(name => !targets.Any(target =>
                        string.Equals(target.Monitor.DeviceName, name, StringComparison.OrdinalIgnoreCase) &&
                        target.Monitor.Bounds == outputs[name].Monitor.Bounds)).ToArray())
                    { outputs[name].Dispose(); outputs.Remove(name); }
                    bool changed = version!=publishedVersion;
                    var ownWindows = UsesSessionWindowExclusion ? WgcDesktopSession.EnumerateOwnWindows() : [];
                    foreach (var target in targets)
                    {
                        _stop.Token.ThrowIfCancellationRequested();
                        if (!outputs.TryGetValue(target.Monitor.DeviceName, out var output))
                            outputs.Add(target.Monitor.DeviceName, output = UsesSessionWindowExclusion
                                ? new WgcDesktopSession(target.Monitor, frameAvailable:SignalFrameAvailable) : OutputSession.Create(target.Monitor));
                        changed |= output.Update(target.Region, boundary, _stop.Token, ownWindows);
                    }
                    if (UsesSessionWindowExclusion && !ownWindows.SequenceEqual(WgcDesktopSession.EnumerateOwnWindows()))
                    {
                        // A new own HWND may have appeared while frames were acquired/read back.
                        // Do not publish those pixels; even a subsequent return to the previous
                        // list must establish a new exclusion generation before publication.
                        foreach (var output in outputs.Values.OfType<WgcDesktopSession>()) output.InvalidateOwnWindows();
                        Interlocked.Exchange(ref _framePending,1); continue;
                    }
                    if (changed && outputs.Values.All(output => output.Bitmap is not null))
                    { Publish(version, targets.Select(target => outputs[target.Monitor.DeviceName]).ToArray()); publishedVersion=version; }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    // AccessLost, device removal/reset and mode changes all recreate owned devices,
                    // duplication and staging state. No old output is offered under the new session.
                    try { ClearOutputs(outputs); }
                    catch (Exception cleanup) { ex = new AggregateException(ex, cleanup); }
                    publishedVersion=-1; sampledVersion=-1; ReportFailure(ex, version); _wake.WaitOne(500);
                }
            }
        }
        catch(Exception ex) { ReportFailure(ex,TargetVersion); }
        finally
        {
            try { ClearOutputs(outputs); }
            catch (Exception cleanup) { ReportFailure(cleanup, TargetVersion); }
            finally
            {
                if (winrtInitialized) WgcDesktopSession.UninitializeRuntime();
                lock (_gate) { _workerExited=true; _wake.Dispose(); _stop.Dispose(); }
                _completion.TrySetResult();
            }
        }
    }

    private void SignalFrameAvailable()
    {
        lock (_gate)
        {
            if (_disposed || _workerExited) return;
            Interlocked.Increment(ref _frameArrivalCount);
            Interlocked.Exchange(ref _framePending,1); _wake.Set();
        }
    }

    private void Publish(long version, ILiveDesktopOutputSession[] outputs)
    {
        var frames = new List<LiveDesktopOutputFrame>(outputs.Length);
        try
        {
            foreach (var output in outputs)
                frames.Add(new(output.Monitor.DeviceName, output.Region, SharePixels(output.Bitmap!), output.Timestamp,
                    output.Rotation, output.ProtectedContentMaskedOut) { Timing=output.Timing });
            var frame = new LiveDesktopFrame(version, frames.ToArray());
            lock (_gate)
            {
                if (_disposed || version != _targetVersion) { frame.Dispose(); return; }
                _latest?.Dispose(); _latest = frame; _lastError = null;
            }
        }
        catch { foreach (var frame in frames) frame.Bitmap.Dispose(); throw; }
        try { FrameReady?.Invoke(); }
        catch (Exception ex) { ReportFailure(new InvalidOperationException("A live capture notification failed.", ex), version); }
    }

    private void ReportFailure(Exception error, long version)
    {
        lock (_gate)
        {
            if (_disposed || version != _targetVersion) return;
            _latest?.Dispose(); _latest = null; _lastError = error;
        }
        try { Failed?.Invoke(error); } catch { /* A UI error handler cannot terminate GPU cleanup. */ }
    }
    private static void ClearOutputs(Dictionary<string, ILiveDesktopOutputSession> outputs)
    {
        var owned = outputs.Values.ToArray(); outputs.Clear(); List<Exception>? failures = null;
        foreach (var output in owned)
            try { output.Dispose(); } catch (Exception error) { (failures ??= []).Add(error); }
        if (failures is not null) throw new AggregateException("Live capture output cleanup failed.", failures);
    }

    // SkBitmap.extractSubset retains SkPixelRef. Each thread gets a distinct bitmap
    // wrapper, sharing only immutable pixels; static outputs need no repeated CPU copy.
    // https://api.skia.org/classSkBitmap.html (extractSubset)
    internal static SKBitmap SharePixels(SKBitmap source)
    {
        if(!source.IsImmutable)throw new InvalidOperationException("Live capture pixels must be immutable before publication.");
        var view=new SKBitmap();
        try
        {
            if(!source.ExtractSubset(view,new SKRectI(0,0,source.Width,source.Height)))
                throw new InvalidOperationException(L.Get("Capture.BufferCreationFailed"));
            return view;
        }
        catch {view.Dispose();throw;}
    }

    internal sealed record CaptureTarget(MonitorInfo Monitor, PixelBounds Region);
    internal static CaptureTarget[] BuildTargets(IReadOnlyList<MonitorInfo> monitors, IReadOnlyList<PixelBounds> regions)
    {
        foreach (var monitor in monitors)
            if (string.IsNullOrWhiteSpace(monitor.DeviceName) || !ValidBounds(monitor.Bounds, false))
                throw new ArgumentException(L.Get("Capture.InvalidDisplayLayout"), nameof(monitors));
        if (monitors.Select(monitor => monitor.DeviceName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != monitors.Count)
            throw new ArgumentException(L.Get("Capture.InvalidDisplayLayout"), nameof(monitors));
        if (regions.Any(region => !ValidBounds(region, true)))
            throw new ArgumentException("Invalid live capture region.", nameof(regions));
        var targets = new List<CaptureTarget>();
        foreach (var monitor in monitors.OrderBy(monitor => monitor.DeviceName, StringComparer.OrdinalIgnoreCase))
        {
            PixelBounds? union = null;
            foreach (var region in regions)
            {
                int left = Math.Max(region.Left, monitor.Bounds.Left), top = Math.Max(region.Top, monitor.Bounds.Top);
                int right = Math.Min(region.Right, monitor.Bounds.Right), bottom = Math.Min(region.Bottom, monitor.Bounds.Bottom);
                if (right <= left || bottom <= top) continue;
                if (union is { } previous)
                { left = Math.Min(left, previous.Left); top = Math.Min(top, previous.Top); right = Math.Max(right, previous.Right); bottom = Math.Max(bottom, previous.Bottom); }
                union = new(left, top, checked(right - left), checked(bottom - top));
            }
            if (union is { } value)
            {
                if ((long)value.Width * value.Height > 268_435_456) throw new NotSupportedException(L.Get("Capture.DesktopTooLarge"));
                targets.Add(new(monitor, value));
            }
        }
        return targets.ToArray();
    }
    private static bool ValidBounds(PixelBounds value, bool allowEmpty) => value.Width >= (allowEmpty ? 0 : 1) &&
        value.Height >= (allowEmpty ? 0 : 1) && (long)value.Left + value.Width <= int.MaxValue && (long)value.Top + value.Height <= int.MaxValue;

    internal static PixelBounds SourceRegion(PixelBounds monitor, PixelBounds region, int width, int height, int rotation)
    {
        int expectedWidth = rotation is 90 or 270 ? height : width, expectedHeight = rotation is 90 or 270 ? width : height;
        if (monitor.Width != expectedWidth || monitor.Height != expectedHeight || !ValidBounds(region, false) ||
            region.Left < monitor.Left || region.Top < monitor.Top || region.Right > monitor.Right || region.Bottom > monitor.Bottom)
            throw new InvalidOperationException(L.Get("Capture.RotationOrResolutionChanged"));
        int left = region.Left - monitor.Left, top = region.Top - monitor.Top;
        return rotation switch
        {
            0 => new(left, top, region.Width, region.Height),
            90 => new(top, height - left - region.Width, region.Height, region.Width),
            180 => new(width - left - region.Width, height - top - region.Height, region.Width, region.Height),
            270 => new(width - top - region.Height, left, region.Height, region.Width),
            _ => throw new ArgumentOutOfRangeException(nameof(rotation))
        };
    }

    internal static unsafe SKBitmap CopyRotatedRoi(nint source, int rowPitch, int width, int height,
        PixelBounds region, int rotation, CancellationToken cancellationToken)
    {
        if (source == 0 || rowPitch < checked(width * 4) || width <= 0 || height <= 0 ||
            (rotation is 90 or 270 ? height : width) != region.Width ||
            (rotation is 90 or 270 ? width : height) != region.Height || rotation is not (0 or 90 or 180 or 270))
            throw new ArgumentException("Invalid live capture ROI pixels.");
        var bitmap = new SKBitmap(new SKImageInfo(region.Width, region.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        try
        {
            if (bitmap.GetPixels() == 0) throw new InvalidOperationException(L.Get("Capture.BufferCreationFailed"));
            for (int y = 0; y < region.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = (uint*)((byte*)bitmap.GetPixels() + y * bitmap.RowBytes);
                for (int x = 0; x < region.Width; x++)
                {
                    var (sx, sy) = rotation switch
                    {
                        90 => (y, height - 1 - x), 180 => (width - 1 - x, height - 1 - y),
                        270 => (width - 1 - y, x), _ => (x, y)
                    };
                    destination[x] = *(uint*)((byte*)source + sy * rowPitch + sx * 4) | 0xFF000000;
                }
            }
            bitmap.SetImmutable(); return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private sealed class OutputSession : ILiveDesktopOutputSession
    {
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private readonly IDXGIOutputDuplication _duplication;
        private ID3D11Texture2D? _desktop, _staging;
        private long _waitingSince = Stopwatch.GetTimestamp();
        private long _boundary;
        public MonitorInfo Monitor { get; }
        public int Rotation { get; }
        public PixelBounds Region { get; private set; }
        public SKBitmap? Bitmap { get; private set; }
        public long Timestamp { get; private set; }
        public bool ProtectedContentMaskedOut { get; private set; }
        public LiveDesktopFrameTiming? Timing { get; private set; }
        private OutputSession(MonitorInfo monitor, int rotation, ID3D11Device device, ID3D11DeviceContext context, IDXGIOutputDuplication duplication)
        { Monitor = monitor; Rotation = rotation; _device = device; _context = context; _duplication = duplication; }

        public static OutputSession Create(MonitorInfo monitor)
        {
            using var factory = CreateDXGIFactory1<IDXGIFactory1>();
            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                var adapterResult = factory.EnumAdapters1(adapterIndex, out var adapter);
                if (adapterResult == ResultCode.NotFound) break;
                adapterResult.CheckError();
                using (adapter)
                for (uint outputIndex = 0; ; outputIndex++)
                {
                    var outputResult = adapter.EnumOutputs(outputIndex, out var output);
                    if (outputResult == ResultCode.NotFound) break;
                    outputResult.CheckError();
                    using (output)
                    {
                        var description = output.Description;
                        if (!description.AttachedToDesktop || !string.Equals(description.DeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase)) continue;
                        var rect = description.DesktopCoordinates;
                        if (new PixelBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top) != monitor.Bounds)
                            throw new InvalidOperationException(L.Get("Capture.DisplayLayoutChanged"));
                        D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                            [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0], out var device, out ID3D11DeviceContext context).CheckError();
                        try
                        {
                            using var output1 = output.QueryInterface<IDXGIOutput1>();
                            var duplication = output1.DuplicateOutput(device);
                            int rotation = description.Rotation switch { ModeRotation.Rotate90 => 90, ModeRotation.Rotate180 => 180, ModeRotation.Rotate270 => 270, _ => 0 };
                            return new(monitor, rotation, device, context, duplication);
                        }
                        catch { context.Dispose(); device.Dispose(); throw; }
                    }
                }
            }
            throw new InvalidOperationException(L.Format("Capture.UnmatchedDxgiOutputs",monitor.DeviceName));
        }

        public bool Update(PixelBounds region, long boundary, CancellationToken cancellationToken, nint[] ownWindows)
        {
            bool changed = Region != region || _boundary != boundary;
            if (_boundary != boundary && Timestamp < boundary)
            { _desktop?.Dispose(); _desktop = null; Timestamp = 0; }
            if (changed)
            { Bitmap?.Dispose(); Bitmap = null; Region = region; _boundary = boundary; _waitingSince = Stopwatch.GetTimestamp(); }
            cancellationToken.ThrowIfCancellationRequested();
            long acquiredAt=Stopwatch.GetTimestamp();
            var acquired = _duplication.AcquireNextFrame(0, out var info, out var resource);
            if (acquired != ResultCode.WaitTimeout)
            {
                acquired.CheckError();
                try
                {
                    using (resource)
                    {
                        if (info.LastPresentTime != 0 && info.LastPresentTime >= boundary)
                        {
                            using var texture = resource.QueryInterface<ID3D11Texture2D>();
                            var description = texture.Description;
                            if (description.Format != Format.B8G8R8A8_UNorm)
                                throw new NotSupportedException(L.Format("Capture.UnsupportedPixelFormat",description.Format));
                            if (_desktop is not null && (_desktop.Description.Width != description.Width || _desktop.Description.Height != description.Height))
                                throw new InvalidOperationException(L.Get("Capture.DisplayLayoutChanged"));
                            if (_desktop is null)
                            {
                                description.Usage = ResourceUsage.Default; description.BindFlags = BindFlags.None;
                                description.CPUAccessFlags = CpuAccessFlags.None; description.MiscFlags = ResourceOptionFlags.None;
                                _desktop = _device.CreateTexture2D(description);
                            }
                            // A GPU-only full-frame cache lets moved ROIs sample an unchanged desktop
                            // without waiting for an unrelated present or reading a whole monitor on CPU.
                            _context.CopyResource(_desktop, texture); Timestamp = info.LastPresentTime;
                            ProtectedContentMaskedOut = info.ProtectedContentMaskedOut; changed = true;
                        }
                    }
                }
                finally { _duplication.ReleaseFrame().CheckError(); }
            }
            if (_desktop is null)
            {
                if (Stopwatch.GetElapsedTime(_waitingSince) > TimeSpan.FromSeconds(3))
                    throw new TimeoutException(L.Format("Capture.FreshFrameTimedOut",Monitor.DeviceName));
                return false;
            }
            if (!changed && Bitmap is not null) return false;
            var desktop = _desktop.Description;
            var crop = SourceRegion(Monitor.Bounds, region, checked((int)desktop.Width), checked((int)desktop.Height), Rotation);
            if (_staging is null || _staging.Description.Width != crop.Width || _staging.Description.Height != crop.Height)
            {
                _staging?.Dispose();
                desktop.Width = checked((uint)crop.Width); desktop.Height = checked((uint)crop.Height);
                desktop.Usage = ResourceUsage.Staging; desktop.BindFlags = BindFlags.None;
                desktop.CPUAccessFlags = CpuAccessFlags.Read; desktop.MiscFlags = ResourceOptionFlags.None;
                _staging = _device.CreateTexture2D(desktop);
            }
            _context.CopySubresourceRegion(_staging, 0, 0, 0, 0, _desktop, 0, new Box(crop.Left, crop.Top, 0, crop.Right, crop.Bottom, 1));
            var mapped = _context.Map(_staging, 0, MapMode.Read, MapFlags.None);
            try
            {
                var bitmap = CopyRotatedRoi(mapped.DataPointer, checked((int)mapped.RowPitch), crop.Width, crop.Height, region, Rotation, cancellationToken);
                Bitmap?.Dispose(); Bitmap = bitmap;
                Timing=new(acquiredAt,Stopwatch.GetTimestamp(),0,acquired==ResultCode.WaitTimeout?0:1,0); return true;
            }
            finally { _context.Unmap(_staging, 0); }
        }
        public void Dispose()
        { Bitmap?.Dispose(); _staging?.Dispose(); _desktop?.Dispose(); _duplication.Dispose(); _context.Dispose(); _device.Dispose(); }
    }
}
