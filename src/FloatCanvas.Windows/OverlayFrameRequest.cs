namespace FloatCanvas.Windows;

/// <summary>One synchronous submission. A content presenter may extend damage before any output is drawn.</summary>
public sealed class OverlayFrameRequest
{
    public IReadOnlyList<PixelBounds>? DirtyRegions { get; set; }
    /// <summary>When supplied, observes a failed batch and prevents a second application-level error dialog.</summary>
    public Action<Exception>? FailureHandler { get; set; }
    internal OverlayFrameRequest(IReadOnlyList<PixelBounds>? dirtyRegions) => DirtyRegions = dirtyRegions;
}
