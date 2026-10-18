namespace FloatCanvas.Windows;

/// <summary>Input categories only; never a virtual key, character, scan code, specific button or pointer coordinate.</summary>
public enum CaptureInputSource { KeyboardDown, PointerPress, PointerWheel }

/// <summary>
/// Metadata for external input and ignored intercepted tails. Suppressed means this
/// application's hook withheld the event; it does not claim delivery by subsequent hooks.
/// </summary>
public readonly record struct CaptureInputObservation(long Version, CaptureInputSource Source, uint EventTime,
    uint Flags, long ForegroundWindow, long TargetWindow, bool Suppressed, bool TailSuppressed)
{
    public bool Injected => (Flags & (Source == CaptureInputSource.KeyboardDown ? 0x10u : 1u)) != 0;
    public bool CountsAsExternalInput => !Suppressed;
}

/// <summary>Owned under the isolation gate. Hook writes never call UI, observers, serializers or disk IO.</summary>
internal sealed class CaptureInputObservationBuffer
{
    internal const int MaximumCount = 32;
    private readonly Queue<CaptureInputObservation> recent = new(MaximumCount);

    internal void Record(CaptureInputObservation observation)
    {
        if (recent.Count == MaximumCount) recent.Dequeue();
        recent.Enqueue(observation);
    }

    internal IReadOnlyList<CaptureInputObservation> SnapshotAfter(long version, uint? startTime = null) =>
        Array.AsReadOnly(recent.Where(item => item.Version > version ||
            !item.CountsAsExternalInput && startTime is { } start && unchecked((int)(item.EventTime - start)) >= 0).ToArray());
}
