using SkiaSharp;

namespace FloatCanvas.Windows;

/// <summary>Thread-affine offscreen CPU/GPU targets. Create, render and dispose on one worker.
/// Completed tiles contain independent immutable CPU pixels, safe to present on another thread.</summary>
public sealed class PreviewRasterizer : IDisposable
{
    private readonly Dictionary<PixelBounds, DibBuffer> buffers = [];
    private GpuRenderContext? gpu;
    private bool attemptedGpu, preferGpu;
    public OverlayRenderBackend Backend { get; private set; }
    public string? GpuDetails => gpu?.Details;
    public string? GpuFailure { get; private set; }

    public PreviewRasterFrame Render(IReadOnlyList<PixelBounds> outputs, bool preferAcceleration,
        bool gpuWorkload, Action<SKSurface> draw)
    {
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
            var tiles = new List<PreviewRasterFrame.Tile>();
            try
            {
                foreach (var bounds in outputs)
                {
                    if (!buffers.TryGetValue(bounds, out var buffer))
                        buffers.Add(bounds, buffer = new(bounds.Width, bounds.Height));
                    var surface = acceleration?.SurfaceFor(buffer) ?? buffer.Surface;
                    var canvas = surface.Canvas;
                    canvas.RestoreToCount(1); canvas.ResetMatrix(); canvas.Clear(SKColors.Transparent);
                    canvas.Save(); canvas.Translate(-bounds.Left, -bounds.Top);
                    draw(surface);
                    canvas.RestoreToCount(1); canvas.Flush();
                    acceleration?.ReadPixels(surface, buffer);
                    using var pixels = buffer.Surface.PeekPixels();
                    // DIBs are reused by later frames; never publish their borrowed memory.
                    var image = SKImage.FromPixelCopy(pixels) ?? throw new OutOfMemoryException("Preview tile allocation failed.");
                    tiles.Add(new(bounds, image));
                }
                return new(tiles);
            }
            catch { foreach (var tile in tiles) tile.Image.Dispose(); throw; }
        }
    }

    public void Dispose()
    {
        gpu?.Dispose(); gpu = null;
        foreach (var buffer in buffers.Values) buffer.Dispose();
        buffers.Clear();
    }
}

/// <summary>Caller-owned immutable preview tiles. Draw and dispose sequentially on the presenting thread.</summary>
public sealed class PreviewRasterFrame(IEnumerable<PreviewRasterFrame.Tile> tiles) : IDisposable
{
    public sealed record Tile(PixelBounds Bounds, SKImage Image);
    public IReadOnlyList<Tile> Tiles { get; } = tiles.ToArray();
    public void Draw(SKCanvas canvas)
    {
        var clip = canvas.LocalClipBounds;
        foreach (var tile in Tiles)
            if (clip.Left < tile.Bounds.Right && clip.Right > tile.Bounds.Left &&
                clip.Top < tile.Bounds.Bottom && clip.Bottom > tile.Bounds.Top)
                canvas.DrawImage(tile.Image, tile.Bounds.Left, tile.Bounds.Top, new SKSamplingOptions(SKFilterMode.Nearest));
    }
    public void Dispose() { foreach (var tile in Tiles) tile.Image.Dispose(); }
}
