namespace FloatCanvas.Windows;

/// <summary>Opt-in diagnostics at the native submission boundary, not DWM or display exposure.</summary>
public readonly record struct OverlayOutputTiming(string DeviceName, PixelBounds Bounds,
    long StartedTimestamp, long CompletedTimestamp, double DrawAndFlushMilliseconds, double PresentMilliseconds);

/// <summary>A complete Render call. Timestamps use Stopwatch.GetTimestamp/Frequency.</summary>
public readonly record struct OverlayFrameTiming(long StartedTimestamp, long CompletedTimestamp,
    bool Succeeded, IReadOnlyList<OverlayOutputTiming> Outputs);
