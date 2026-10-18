using SkiaSharp;

namespace FloatCanvas.Rendering;

public sealed partial class SceneRenderer
{
    /// <summary>Successful intermediate allocations in the destination's GPU context.</summary>
    public long GpuIntermediateSurfaceCount { get; private set; }
    /// <summary>Successful CPU intermediate allocations for raster drawing and exports.</summary>
    public long RasterIntermediateSurfaceCount { get; private set; }

    private SKSurface CreateIntermediateSurface(SKCanvas destination, SKImageInfo info, string failureMessage)
    {
        var context = destination.Context;
        if (context is null)
        {
            var raster = SKSurface.Create(info) ?? throw new InvalidOperationException(failureMessage);
            RasterIntermediateSurfaceCount++;
            return raster;
        }
        // A failed GPU frame must be redrawn completely by the host on its CPU target.
        // Mixing in a raster filter here would conceal a lost context from that fallback.
        if (context.IsAbandoned) throw new InvalidOperationException("The GPU rendering context has been abandoned.");
        var gpu = SKSurface.Create(context, true, info) ?? throw new InvalidOperationException(failureMessage);
        if (gpu.Context is null || context.IsAbandoned)
        {
            gpu.Dispose();
            throw new InvalidOperationException("The GPU intermediate surface is unavailable.");
        }
        GpuIntermediateSurfaceCount++;
        return gpu;
    }

    private void ClearGpuEffectImages()
    {
        // Context-bound images must not outlive the host's current drawing context.
        // Raster effect images retain the existing bounded cross-frame cache.
        foreach (var id in _effects.Where(e => e.Value.IsGpu).Select(e => e.Key).ToArray())
        {
            _effects[id].Dispose();
            _effects.Remove(id);
        }
    }
}
