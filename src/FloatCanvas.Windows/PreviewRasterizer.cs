using SkiaSharp;

namespace FloatCanvas.Windows;

/// <summary>Thread-affine offscreen CPU/GPU targets. Create, render and dispose on one worker.
/// Completed tiles contain independent immutable CPU pixels, safe to present on another thread.</summary>
public sealed class PreviewRasterizer : IDisposable
{
    public const int BlockSize = 256;
    private readonly Dictionary<PixelBounds, DibBuffer> buffers = [];
    private readonly PreviewPixelPool pool = new();
    private Dictionary<PixelBounds, PreviewRasterFrame.Tile> published = [];
    private GpuRenderContext? gpu;
    private bool attemptedGpu, preferGpu, requireFullFrame, disposed;
    public OverlayRenderBackend Backend { get; private set; }
    public string? GpuDetails => gpu?.Details;
    public string? GpuFailure { get; private set; }
    public long ActivePixelBytes => pool.ActiveBytes;
    public long PooledPixelBytes => pool.FreeBytes;
    public long RasterBufferBytes => buffers.Values.Sum(buffer => (long)buffer.ImageInfo.RowBytes * buffer.ImageInfo.Height);
    public long PixelPoolByteLimit { get => pool.ByteLimit; set => pool.ByteLimit = value; }

    public PreviewRasterFrame Render(IReadOnlyList<PixelBounds> outputs, bool preferAcceleration,
        bool gpuWorkload, Action<SKSurface> draw, IReadOnlyList<PixelBounds>? damage = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (preferGpu != preferAcceleration)
        {
            preferGpu = preferAcceleration; attemptedGpu = false; GpuFailure = null;
            gpu?.Dispose(); gpu = null;
        }
        if (buffers.Keys.Any(bounds => !outputs.Contains(bounds)))
        {
            if (gpu is not null) { using var current = gpu.MakeCurrent(); gpu.ReleaseSurfaces(); }
            foreach (var bounds in buffers.Keys.Where(bounds => !outputs.Contains(bounds)).ToArray())
            { buffers[bounds].Dispose(); buffers.Remove(bounds); }
        }
        if (preferGpu && gpuWorkload && !attemptedGpu)
        {
            attemptedGpu = true;
            try { gpu = new(); }
            catch (Exception error) { GpuFailure = error.GetType().Name + ": " + error.Message; }
        }
        if (preferGpu && gpuWorkload && gpu is not null)
        {
            try
            {
                using var current = gpu.MakeCurrent();
                var frame = Draw(gpu); Backend = OverlayRenderBackend.Gpu; return frame;
            }
            catch (Exception error)
            {
                GpuFailure = error.GetType().Name + ": " + error.Message;
                gpu.Dispose(); gpu = null;
            }
        }
        var cpu = Draw(null); Backend = OverlayRenderBackend.Cpu; return cpu;

        PreviewRasterFrame Draw(GpuRenderContext? acceleration)
        {
            var next = new Dictionary<PixelBounds, PreviewRasterFrame.Tile>();
            long copiedBytes = 0;
            try
            {
                foreach (var bounds in outputs)
                {
                    bool initial = !buffers.ContainsKey(bounds) || requireFullFrame;
                    if (!buffers.TryGetValue(bounds, out var buffer))
                        buffers.Add(bounds, buffer = new(bounds.Width, bounds.Height));
                    bool redraw = initial || damage is null || damage.Any(region => Intersects(region, bounds));
                    if (redraw)
                    {
                        // Draw the scene once per output, not once per published block.
                        var surface = acceleration?.SurfaceFor(buffer) ?? buffer.Surface;
                        var canvas = surface.Canvas;
                        canvas.RestoreToCount(1); canvas.ResetMatrix(); canvas.Clear(SKColors.Transparent);
                        canvas.Save(); canvas.Translate(-bounds.Left, -bounds.Top);
                        draw(surface);
                        canvas.RestoreToCount(1); canvas.Flush();
                        acceleration?.ReadPixels(surface, buffer);
                    }
                    for (int y = 0; y < bounds.Height; y += BlockSize)
                    for (int x = 0; x < bounds.Width; x += BlockSize)
                    {
                        var block = new PixelBounds(bounds.Left + x, bounds.Top + y,
                            Math.Min(BlockSize, bounds.Width - x), Math.Min(BlockSize, bounds.Height - y));
                        published.TryGetValue(block, out var previous);
                        bool changed = initial || previous is null || redraw && (damage is null || damage.Any(region => Intersects(region, block)));
                        if (changed && previous is not null && previous.Matches(buffer.Pixels, buffer.ImageInfo.RowBytes, x, y)) changed = false;
                        PreviewRasterFrame.Tile tile;
                        if (changed)
                        {
                            tile = pool.Copy(block, buffer.Pixels, buffer.ImageInfo.RowBytes, x, y);
                            copiedBytes += (long)block.Width * block.Height * 4;
                        }
                        else tile = previous!.Retain();
                        if (!next.TryAdd(block, tile)) tile.Release();
                    }
                }
                var frame = new PreviewRasterFrame(next.Values.Select(tile => tile.Retain()), copiedBytes);
                foreach (var tile in published.Values) tile.Release();
                published = next; requireFullFrame = false;
                return frame;
            }
            catch
            {
                foreach (var tile in next.Values) tile.Release();
                requireFullFrame = true;
                throw;
            }
        }
    }

    private static bool Intersects(PixelBounds a, PixelBounds b) => a.Width > 0 && a.Height > 0 &&
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        gpu?.Dispose(); gpu = null;
        foreach (var buffer in buffers.Values) buffer.Dispose();
        buffers.Clear();
        foreach (var tile in published.Values) tile.Release();
        published.Clear(); pool.Dispose();
    }
}

/// <summary>Caller-owned immutable preview tiles. Draw and dispose sequentially on the presenting thread.</summary>
public sealed class PreviewRasterFrame : IDisposable
{
    public sealed class Tile(PixelBounds bounds, SKImage image)
    {
        private int references = 1;
        internal nint Pixels { get; init; }
        public PixelBounds Bounds { get; } = bounds;
        public SKImage Image { get; } = image;
        public long Version { get; } = Interlocked.Increment(ref version);
        private static long version;
        internal Tile Retain() { Interlocked.Increment(ref references); return this; }
        internal void Release() { if (Interlocked.Decrement(ref references) == 0) Image.Dispose(); }
        internal unsafe bool Matches(nint source, int sourceRowBytes, int x, int y)
        {
            if (Pixels == 0) return false;
            int rowBytes = Bounds.Width * 4;
            for (int row = 0; row < Bounds.Height; row++)
                if (!new ReadOnlySpan<byte>((void*)(source + (y + row) * sourceRowBytes + x * 4), rowBytes)
                    .SequenceEqual(new ReadOnlySpan<byte>((void*)(Pixels + row * rowBytes), rowBytes))) return false;
            return true;
        }
    }
    private int disposed;
    public PreviewRasterFrame(IEnumerable<Tile> tiles, long copiedBytes = 0) { Tiles = tiles.ToArray(); CopiedBytes = copiedBytes; }
    public IReadOnlyList<Tile> Tiles { get; }
    public long CopiedBytes { get; }
    public long PixelBytes => Tiles.Sum(tile => (long)tile.Bounds.Width * tile.Bounds.Height * 4);
    public PreviewRasterFrame Retain()
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        return new(Tiles.Select(tile => tile.Retain()), CopiedBytes);
    }
    public IReadOnlyList<PixelBounds> DamageComparedTo(PreviewRasterFrame? previous)
    {
        if (previous is null) return Tiles.Select(tile => tile.Bounds).ToArray();
        var old = previous.Tiles.ToDictionary(tile => tile.Bounds);
        var damage = new List<PixelBounds>();
        foreach (var tile in Tiles)
        {
            if (!old.Remove(tile.Bounds, out var before) || !ReferenceEquals(tile, before)) damage.Add(tile.Bounds);
        }
        damage.AddRange(old.Keys);
        return damage;
    }
    public void Draw(SKCanvas canvas)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        var clip = canvas.LocalClipBounds;
        foreach (var tile in Tiles)
            if (clip.Left < tile.Bounds.Right && clip.Right > tile.Bounds.Left &&
                clip.Top < tile.Bounds.Bottom && clip.Bottom > tile.Bounds.Top)
                canvas.DrawImage(tile.Image, tile.Bounds.Left, tile.Bounds.Top, new SKSamplingOptions(SKFilterMode.Nearest));
    }
    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) foreach (var tile in Tiles) tile.Release(); }
}
