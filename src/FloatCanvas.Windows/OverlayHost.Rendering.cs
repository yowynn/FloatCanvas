namespace FloatCanvas.Windows;

public enum OverlayRenderBackend { Cpu, Gpu }

public sealed partial class OverlayHost
{
    private bool _preferGpuAcceleration, _gpuAttempted, _gpuWorkload = true;
    private GpuRenderContext? _gpuRenderer;
    public OverlayRenderBackend RenderBackend { get; private set; }
    /// <summary>The backend of the most recent draw that completed every output.</summary>
    public OverlayRenderBackend LastCompletedRenderBackend { get; private set; }
    public string? GpuDetails => _gpuRenderer?.Details;
    public string? LastGpuFailure { get; private set; }
    public long GpuFrameCount { get; private set; }
    public long CpuFrameCount { get; private set; }
    /// <summary>Whether the latest scene hint warrants GPU work rather than the raster cache.</summary>
    public bool CurrentGpuWorkload => _gpuWorkload;
    public event Action? RenderBackendChanged;

    /// <summary>The caller can retain the cheaper raster cache for simple scenes. This is a
    /// per-frame cost hint, independent from the user's persistent GPU preference.</summary>
    public void SetGpuWorkload(bool eligible)
    { CheckThread(); _gpuWorkload = eligible; }

    public void ApplyRenderPreference(bool preferGpu)
    {
        CheckThread();
        if (_preferGpuAcceleration == preferGpu && (preferGpu || _gpuRenderer is null)) return;
        _preferGpuAcceleration = preferGpu; _gpuAttempted = false; LastGpuFailure = null;
        ReleaseGpuRenderer();
        SetRenderBackend(OverlayRenderBackend.Cpu);
    }

    private GpuRenderContext? PrepareGpuRenderer()
    {
        if (!_preferGpuAcceleration || !_gpuWorkload) return null;
        if (_gpuAttempted) return _gpuRenderer;
        _gpuAttempted = true;
        try { return _gpuRenderer = new GpuRenderContext(); }
        catch (Exception ex) { FailGpu(ex); return null; }
    }

    private void FailGpu(Exception exception)
    {
        LastGpuFailure = exception.GetType().Name + ": " + exception.Message;
        ReleaseGpuRenderer();
        bool alreadyCpu = RenderBackend == OverlayRenderBackend.Cpu;
        SetRenderBackend(OverlayRenderBackend.Cpu);
        if (alreadyCpu) NotifyRenderBackendChanged();
    }

    private void SetRenderBackend(OverlayRenderBackend backend)
    {
        if (RenderBackend == backend) return;
        RenderBackend = backend; NotifyRenderBackendChanged();
    }

    private void ReleaseGpuRenderer()
    {
        var gpu = _gpuRenderer; _gpuRenderer = null;
        try { gpu?.Dispose(); }
        catch (Exception exception)
        { LastGpuFailure = (LastGpuFailure is null ? "" : LastGpuFailure + "; ") + "Cleanup: " + exception.Message; }
    }

    private void NotifyRenderBackendChanged()
    {
        // Status/diagnostic observers cannot turn a completed GPU frame into a failure
        // or prevent the CPU redraw that follows a real graphics failure.
        foreach (Action observer in RenderBackendChanged?.GetInvocationList() ?? [])
            try { observer(); }
            catch (Exception exception) { System.Diagnostics.Trace.TraceError("Render backend observer failed: {0}", exception); }
    }
}
