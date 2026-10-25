using FloatCanvas.Core;

namespace FloatCanvas.Rendering;

/// <summary>Detached scheduling facts published by the preview worker. Safe to read on the UI.</summary>
public sealed class SceneAnimationSchedule
{
    public TimeSpan SampleTime { get; }
    public bool HasAnimations { get; }
    public TimeSpan? NextDelay { get; }
    public IReadOnlyList<RectD> DirtyBounds { get; }

    internal SceneAnimationSchedule(TimeSpan sampleTime, bool hasAnimations, TimeSpan? nextDelay, IEnumerable<RectD> dirtyBounds)
    {
        SampleTime = sampleTime;
        HasAnimations = hasAnimations;
        NextDelay = nextDelay;
        DirtyBounds = Array.AsReadOnly(dirtyBounds.ToArray());
    }
}
