using FloatCanvas.Core;

namespace FloatCanvas.App;

/// <summary>Run-local presentation options. Never modify the authored canvas or its history.</summary>
internal sealed class CanvasPreviewAppearance
{
    internal bool DepthPriorityOcclusion { get; private set; }
    private CanvasSettings? source, resolved;

    internal bool SetDepth(bool enabled)
    {
        if (DepthPriorityOcclusion == enabled) return false;
        DepthPriorityOcclusion = enabled; source = resolved = null;
        return true;
    }

    // Input is an immutable frozen canvas (or a replacement calibration candidate).
    // Retain only its current override, so unchanged frames reuse their evaluation.
    internal CanvasSettings Resolve(CanvasSettings canvas)
    {
        if (ReferenceEquals(source, canvas) && resolved is not null) return resolved;
        source = canvas; resolved = canvas.Clone();
        resolved.DepthPriorityOcclusion = DepthPriorityOcclusion;
        return resolved;
    }
}
