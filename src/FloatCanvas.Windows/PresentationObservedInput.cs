namespace FloatCanvas.Windows;

public enum PresentationObservedKind { KeyDown, KeyUp, ButtonDown, ButtonUp, DeviceRemoved }
public enum PresentationInputOwner { Unspecified, OtherApplication, FloatCanvas, Unknown }

/// <summary>Transient physical input metadata, never input text or a persisted recording.</summary>
public readonly record struct PresentationObservedInput(PresentationObservedKind Kind, long Milliseconds,
    long Device = 0, int VirtualKey = 0, int ScanCode = 0, uint KeyFlags = 0, string? KeyLabel = null,
    int X = 0, int Y = 0, bool HasPosition = false, int Button = 0, PresentationInputOwner InputOwner = PresentationInputOwner.Unspecified);

public readonly record struct PresentationObservedCursor(int X, int Y, long Milliseconds);

public sealed class PresentationObservedBatch
{
    public long Epoch { get; }
    public IReadOnlyList<PresentationObservedInput> Changes { get; }
    public PresentationObservedCursor? Cursor { get; }
    public bool Overflowed { get; }
    public PresentationObservedBatch(long epoch, IEnumerable<PresentationObservedInput> changes,
        PresentationObservedCursor? cursor, bool overflowed)
    {
        if (epoch < 0) throw new ArgumentOutOfRangeException(nameof(epoch));
        var owned = changes.Take(PresentationInputBuffer.Capacity + 1).ToArray();
        if (owned.Length > PresentationInputBuffer.Capacity) throw new ArgumentException("Presentation input capacity exceeded.");
        Epoch = epoch; Changes = Array.AsReadOnly(owned); Cursor = cursor; Overflowed = overflowed;
    }
}

public interface IPresentationInputSource : IDisposable
{
    long Epoch { get; }
    bool IsRunning { get; }
    Task Completion { get; }
    event Action? Available;
    PresentationObservedBatch Drain();
    /// <summary>Closes the gate and unregisters owned devices without joining the worker.</summary>
    bool Stop();
}

/// <summary>Transitions are bounded; movement has only one pending value.</summary>
internal sealed class PresentationInputBuffer
{
    internal const int Capacity = 512;
    private readonly object gate = new();
    private readonly Queue<PresentationObservedInput> changes = new();
    private PresentationObservedCursor? cursor;
    private bool overflowed, notified, closed;
    internal int HighWater { get; private set; }
    internal long Dropped { get; private set; }
    internal bool Add(PresentationObservedInput input)
    {
        lock (gate)
        {
            if (closed) return false;
            if (changes.Count == Capacity)
            {
                Dropped += changes.Count; changes.Clear(); overflowed = true;
            }
            changes.Enqueue(input); HighWater = Math.Max(HighWater, changes.Count);
            bool signal = !notified; notified = true; return signal;
        }
    }
    internal bool Move(PresentationObservedCursor point)
    {
        lock (gate) { if (closed) return false; cursor = point; bool signal = !notified; notified = true; return signal; }
    }
    internal PresentationObservedBatch Drain(long epoch)
    {
        lock (gate)
        {
            var batch = new PresentationObservedBatch(epoch, changes, cursor, overflowed);
            changes.Clear(); cursor = null; overflowed = notified = false; return batch;
        }
    }
    internal void Clear()
    { lock (gate) { changes.Clear(); cursor = null; overflowed = notified = false; } }
    internal void Close()
    { lock (gate) { closed = true; changes.Clear(); cursor = null; overflowed = notified = false; } }
}
