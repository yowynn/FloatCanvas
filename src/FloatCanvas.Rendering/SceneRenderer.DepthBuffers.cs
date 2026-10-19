using SkiaSharp;

namespace FloatCanvas.Rendering;

public sealed partial class SceneRenderer
{
    private const long DepthBufferByteLimit = 128L * 1024 * 1024;
    private const int DepthBufferCountLimit = 8;
    private readonly List<DepthBuffer> _depthBuffers = [];
    private long _depthBufferClock;

    /// <summary>Retained CPU depth work memory, including surfaces currently leased by a draw.</summary>
    public long CachedDepthSurfaceBytes { get; private set; }
    public int CachedDepthSurfaceCount => _depthBuffers.Count;
    /// <summary>CPU depth work surfaces reused instead of allocating a new surface.</summary>
    public long DepthSurfaceReuseCount { get; private set; }

    private DepthSurfaceLease RentDepthSurface(SKCanvas destination, SKImageInfo info, string failureMessage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // GPU surfaces belong to the host's current context, whose lifetime ends outside this renderer.
        if (destination.Context is not null)
            return new(this, CreateIntermediateSurface(destination, info, failureMessage), null);

        var buffer = _depthBuffers.FirstOrDefault(e => !e.InUse && e.Matches(info));
        if (buffer is not null)
        {
            buffer.InUse = true;
            DepthSurfaceReuseCount++;
            return new(this, buffer.Surface, buffer);
        }

        long bytes = checked((long)info.Width * info.Height * info.BytesPerPixel);
        if (bytes <= DepthBufferByteLimit)
        {
            while (_depthBuffers.Count >= DepthBufferCountLimit || CachedDepthSurfaceBytes > DepthBufferByteLimit - bytes)
            {
                var oldest = _depthBuffers.Where(e => !e.InUse).MinBy(e => e.Used);
                if (oldest is null) break;
                _depthBuffers.Remove(oldest);
                CachedDepthSurfaceBytes -= oldest.Bytes;
                oldest.Surface.Dispose();
            }
        }

        var surface = CreateIntermediateSurface(destination, info, failureMessage);
        if (bytes <= DepthBufferByteLimit && _depthBuffers.Count < DepthBufferCountLimit &&
            CachedDepthSurfaceBytes <= DepthBufferByteLimit - bytes)
        {
            buffer = new(surface, info, bytes) { InUse = true };
            _depthBuffers.Add(buffer);
            CachedDepthSurfaceBytes += bytes;
        }
        // Oversized or concurrently exhausted draws use temporary memory without growing the pool.
        return new(this, surface, buffer);
    }

    private void ReturnDepthSurface(SKSurface surface, DepthBuffer? buffer, int saveCount)
    {
        // Restore the complete clip and matrix. ResetMatrix alone would retain the last frame's clip.
        surface.Canvas.RestoreToCount(saveCount);
        if (buffer is null || _disposed) surface.Dispose();
        else
        {
            buffer.InUse = false;
            buffer.Used = ++_depthBufferClock;
        }
    }

    private void DisposeDepthBuffers()
    {
        foreach (var buffer in _depthBuffers)
            if (!buffer.InUse) buffer.Surface.Dispose();
        // A currently leased surface, if any, is disposed when its draw unwinds.
        _depthBuffers.Clear();
        CachedDepthSurfaceBytes = 0;
    }

    private sealed class DepthBuffer(SKSurface surface, SKImageInfo info, long bytes)
    {
        public SKSurface Surface { get; } = surface;
        public long Bytes { get; } = bytes;
        public bool InUse { get; set; }
        public long Used { get; set; }
        public bool Matches(SKImageInfo requested) => info.Width == requested.Width && info.Height == requested.Height &&
            info.ColorType == requested.ColorType && info.AlphaType == requested.AlphaType && Equals(info.ColorSpace, requested.ColorSpace);
    }

    /// <summary>All snapshots must be disposed before returning this work surface.</summary>
    private sealed class DepthSurfaceLease : IDisposable
    {
        private SceneRenderer? _owner;
        private readonly DepthBuffer? _buffer;
        private readonly int _saveCount;
        public SKSurface Surface { get; }

        public DepthSurfaceLease(SceneRenderer owner, SKSurface surface, DepthBuffer? buffer)
        {
            _owner = owner; Surface = surface; _buffer = buffer;
            _saveCount = surface.Canvas.Save();
            // Each depth caller clears the full surface before drawing its new contents.
        }

        public void Dispose()
        {
            var owner = _owner;
            if (owner is null) return;
            _owner = null;
            owner.ReturnDepthSurface(Surface, _buffer, _saveCount);
        }
    }
}
