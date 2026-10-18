using L = FloatCanvas.Localization.AppText;
using System.Diagnostics;
using SkiaSharp;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using ResultCode = Vortice.DXGI.ResultCode;
using MapFlags = Vortice.Direct3D11.MapFlags;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace FloatCanvas.Windows;

public sealed record CapturedOutputInfo(string DeviceName, PixelBounds Bounds, long LastPresentTimestamp, int RotationDegrees,
    bool ProtectedContentMaskedOut = false)
{
    public CaptureFrameRequirement FrameRequirement { get; init; }
    public long FrameBoundaryTimestamp { get; init; }
    public long DuplicationStartedTimestamp { get; init; }
    public long AcquiredTimestamp { get; init; }
    public bool AcceptedUnchangedDesktop => FrameRequirement == CaptureFrameRequirement.CurrentDesktopFromNewDuplication &&
        LastPresentTimestamp > 0 && LastPresentTimestamp < FrameBoundaryTimestamp;
}

public sealed record CapturedDesktop(PixelBounds Bounds, byte[] PngBytes)
{
    public IReadOnlyList<CapturedOutputInfo> Outputs { get; init; } = [];
    public DateTimeOffset CapturedAtUtc { get; init; }
}

/// <summary>
/// Captures SDR desktop pixels using a new DXGI duplication session for each request. The caller
/// establishes its capture window policy on the UI thread; all GPU acquisition and PNG encoding run off-thread.
/// No old screenshot cache is used. Output timestamps are retained because monitors are not synchronized.
/// </summary>
public sealed class CaptureService : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private Task<CapturedDesktop>? _active;

    public Task<CapturedDesktop> CaptureAsync(IReadOnlyList<MonitorInfo> monitors, CancellationToken cancellationToken = default) =>
        CaptureAsync(monitors, 0, cancellationToken);

    /// <param name="notBeforeTimestamp">QPC/Stopwatch timestamp after all application windows have been hidden. Older desktop presents are rejected.</param>
    public Task<CapturedDesktop> CaptureAsync(IReadOnlyList<MonitorInfo> monitors, long notBeforeTimestamp, CancellationToken cancellationToken = default)
    {
        if (notBeforeTimestamp < 0 || notBeforeTimestamp > Stopwatch.GetTimestamp())
            throw new ArgumentOutOfRangeException(nameof(notBeforeTimestamp));
        return CaptureAsync(monitors, new CaptureFrameBoundary(notBeforeTimestamp,
            CaptureFrameRequirement.RequirePresentAfterBoundary), cancellationToken);
    }

    /// <summary>
    /// Acquires from a newly created per-output duplication after the caller's window policy boundary.
    /// CurrentDesktopFromNewDuplication permits an unchanged desktop, not frames from a previous request.
    /// A boundary does not itself prove exclusion pixels: the caller must establish the window policy.
    /// </summary>
    public Task<CapturedDesktop> CaptureAsync(IReadOnlyList<MonitorInfo> monitors, CaptureFrameBoundary boundary,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        var snapshot = monitors.ToArray();
        if (snapshot.Length == 0) throw new ArgumentException(L.Get("Capture.AtLeastOneMonitorRequired"), nameof(monitors));
        if (snapshot.Any(m => m.Bounds.Width <= 0 || m.Bounds.Height <= 0) ||
            snapshot.Select(m => m.DeviceName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != snapshot.Length)
            throw new ArgumentException(L.Get("Capture.InvalidDisplayLayout"), nameof(monitors));
        boundary.Validate(Stopwatch.GetTimestamp());
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active is { IsCompleted: false }) throw new InvalidOperationException(L.Get("Capture.DesktopCaptureAlreadyActive"));
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            _active = Task.Run(() =>
            {
                using (linked) return Capture(snapshot, boundary, linked.Token);
            });
            return _active;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            // Per-request COM objects are disposed by the worker's finally blocks before its task ends.
            if (_active is { IsCompleted: false } active)
                _ = active.ContinueWith(_ => _lifetime.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            else _lifetime.Dispose();
        }
    }

    private static CapturedDesktop Capture(MonitorInfo[] monitors, CaptureFrameBoundary boundary, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var left = monitors.Min(m => m.Bounds.Left);
        var top = monitors.Min(m => m.Bounds.Top);
        var width = checked(monitors.Max(m => m.Bounds.Right) - left);
        var height = checked(monitors.Max(m => m.Bounds.Bottom) - top);
        if ((long)width * height > 268_435_456) throw new NotSupportedException(L.Get("Capture.DesktopTooLarge"));
        var bounds = new PixelBounds(left, top, width, height);
        using var result = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque))
            ?? throw new InvalidOperationException(L.Get("Capture.BufferCreationFailed"));
        result.Canvas.Clear(SKColors.Black);
        var pending = monitors.ToDictionary(m => m.DeviceName, StringComparer.OrdinalIgnoreCase);
        var captured = new List<CapturedOutputInfo>();
        using var factory = CreateDXGIFactory1<IDXGIFactory1>();
        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            var adapterResult = factory.EnumAdapters1(adapterIndex, out var adapter);
            if (adapterResult == ResultCode.NotFound) break;
            adapterResult.CheckError();
            using (adapter)
            {
                ID3D11Device? device = null;
                ID3D11DeviceContext? context = null;
                try
                {
                    for (uint outputIndex = 0; ; outputIndex++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var outputResult = adapter.EnumOutputs(outputIndex, out var output);
                        if (outputResult == ResultCode.NotFound) break;
                        outputResult.CheckError();
                        using (output)
                        {
                            var description = output.Description;
                            if (!description.AttachedToDesktop || !pending.TryGetValue(description.DeviceName, out var monitor)) continue;
                            var rect = description.DesktopCoordinates;
                            var actualBounds = new PixelBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
                            if (actualBounds != monitor.Bounds)
                                throw new InvalidOperationException(L.Get("Capture.DisplayLayoutChanged"));
                            if (device is null)
                            {
                                var created = D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                                    [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0], out var createdDevice, out ID3D11DeviceContext createdContext);
                                created.CheckError();
                                device = createdDevice;
                                context = createdContext;
                            }
                            using var output1 = output.QueryInterface<IDXGIOutput1>();
                            // DuplicateOutput/AcquireNextFrame provide the current desktop image.
                            // LastPresentTime describes its last content update, not session creation:
                            // https://learn.microsoft.com/windows/win32/api/dxgi1_2/ns-dxgi1_2-dxgi_outdupl_frame_info
                            long duplicationStarted = Stopwatch.GetTimestamp();
                            using var duplication = output1.DuplicateOutput(device);
                            var frame = CaptureOutput(duplication, device, context!, monitor, description.Rotation, boundary,
                                duplicationStarted, cancellationToken);
                            using (frame.Bitmap)
                            using (var image = SKImage.FromBitmap(frame.Bitmap))
                                result.Canvas.DrawImage(image, monitor.Bounds.Left - left, monitor.Bounds.Top - top, new SKSamplingOptions(SKFilterMode.Nearest));
                            captured.Add(new(monitor.DeviceName, monitor.Bounds, frame.Timestamp, RotationDegrees(description.Rotation), frame.ProtectedContentMaskedOut)
                            {
                                FrameRequirement = boundary.Requirement, FrameBoundaryTimestamp = boundary.Timestamp,
                                DuplicationStartedTimestamp = duplicationStarted, AcquiredTimestamp = frame.AcquiredTimestamp
                            });
                            pending.Remove(monitor.DeviceName);
                        }
                    }
                }
                finally { context?.Dispose(); device?.Dispose(); }
            }
        }
        if (pending.Count != 0) throw new InvalidOperationException(L.Format("Capture.UnmatchedDxgiOutputs", string.Join(", ", pending.Keys)));
        cancellationToken.ThrowIfCancellationRequested();
        using var snapshot = result.Snapshot();
        using var encoded = snapshot.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException(L.Get("Capture.EncodingFailed"));
        return new(bounds, encoded.ToArray()) { Outputs = captured.AsReadOnly(), CapturedAtUtc = DateTimeOffset.UtcNow };
    }

    private static (SKBitmap Bitmap, long Timestamp, bool ProtectedContentMaskedOut, long AcquiredTimestamp) CaptureOutput(
        IDXGIOutputDuplication duplication, ID3D11Device device, ID3D11DeviceContext context, MonitorInfo monitor,
        ModeRotation rotation, CaptureFrameBoundary boundary, long duplicationStarted, CancellationToken cancellationToken)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(3))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var acquired = duplication.AcquireNextFrame(100, out var info, out var resource);
            long acquiredTimestamp = Stopwatch.GetTimestamp();
            if (acquired == ResultCode.WaitTimeout) continue;
            if (acquired == ResultCode.AccessLost)
                throw new InvalidOperationException(L.Get("Capture.ConnectionReset"));
            acquired.CheckError();
            try
            {
                using (resource)
                {
                    // Visual changes retain the old strict present gate. An unchanged, already-excluded
                    // desktop is eligible only through the explicit fresh-session policy.
                    if (!boundary.Accepts(info.LastPresentTime, duplicationStarted, acquiredTimestamp)) continue;
                    // This flag describes already-masked regions, not capture failure. Preserve the
                    // actual pixels and report metadata so callers can explain partial masking.
                    using var texture = resource.QueryInterface<ID3D11Texture2D>();
                    var description = texture.Description;
                    if (description.Format != Format.B8G8R8A8_UNorm)
                        throw new NotSupportedException(L.Format("Capture.UnsupportedPixelFormat", description.Format));
                    var stagingDescription = description;
                    stagingDescription.Usage = ResourceUsage.Staging;
                    stagingDescription.BindFlags = BindFlags.None;
                    stagingDescription.CPUAccessFlags = CpuAccessFlags.Read;
                    stagingDescription.MiscFlags = ResourceOptionFlags.None;
                    using var staging = device.CreateTexture2D(stagingDescription);
                    context.CopyResource(staging, texture);
                    var mapped = context.Map(staging, 0, MapMode.Read, MapFlags.None);
                    try
                    {
                        return (CopyRotated(mapped.DataPointer, checked((int)mapped.RowPitch), checked((int)description.Width),
                            checked((int)description.Height), monitor.Bounds, rotation, cancellationToken), info.LastPresentTime,
                            info.ProtectedContentMaskedOut, acquiredTimestamp);
                    }
                    finally { context.Unmap(staging, 0); }
                }
            }
            finally { duplication.ReleaseFrame().CheckError(); }
        }
        throw new TimeoutException(L.Format("Capture.FreshFrameTimedOut", monitor.DeviceName));
    }

    internal static unsafe SKBitmap CopyRotated(nint source, int rowPitch, int sourceWidth, int sourceHeight,
        PixelBounds destination, ModeRotation rotation, CancellationToken cancellationToken)
    {
        var angle = RotationDegrees(rotation);
        var expectedWidth = angle is 90 or 270 ? sourceHeight : sourceWidth;
        var expectedHeight = angle is 90 or 270 ? sourceWidth : sourceHeight;
        if (destination.Width != expectedWidth || destination.Height != expectedHeight)
            throw new InvalidOperationException(L.Get("Capture.RotationOrResolutionChanged"));
        var bitmap = new SKBitmap(new SKImageInfo(destination.Width, destination.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        try
        {
            var pixels = (byte*)bitmap.GetPixels();
            for (var y = 0; y < destination.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetRow = (uint*)(pixels + y * bitmap.RowBytes);
                for (var x = 0; x < destination.Width; x++)
                {
                    var (sx, sy) = angle switch
                    {
                        90 => (y, sourceHeight - 1 - x),
                        180 => (sourceWidth - 1 - x, sourceHeight - 1 - y),
                        270 => (sourceWidth - 1 - y, x),
                        _ => (x, y)
                    };
                    targetRow[x] = *(uint*)((byte*)source + sy * rowPitch + sx * 4) | 0xFF000000;
                }
            }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static int RotationDegrees(ModeRotation rotation) => rotation switch
    {
        ModeRotation.Rotate90 => 90,
        ModeRotation.Rotate180 => 180,
        ModeRotation.Rotate270 => 270,
        _ => 0
    };
}
