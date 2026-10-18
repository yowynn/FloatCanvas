namespace FloatCanvas.Windows;

/// <summary>Bounded modifier transitions in Win32 input-message time. Keyboard delivery
/// can lag behind a mouse message, or can already include a later release.</summary>
internal sealed class KeyboardModifierHistory(int capacity)
{
    private readonly (long Time, OverlayModifiers Modifiers)[] samples = new (long, OverlayModifiers)[capacity];
    private int first, count;

    internal void Reset(long time, OverlayModifiers modifiers)
    {
        first = 0; count = 1; samples[0] = (time, modifiers);
    }

    internal void Record(uint eventTime, long observedTime, OverlayModifiers modifiers)
    {
        long time = Expand(eventTime, observedTime);
        if (count == 0) { Reset(time, modifiers); return; }
        var previous = samples[(first + count - 1) % samples.Length];
        if (previous.Modifiers == modifiers) return;
        if (time < previous.Time) time = previous.Time;
        int next = (first + count) % samples.Length;
        samples[next] = (time, modifiers);
        if (count < samples.Length) count++;
        else first = (first + 1) % samples.Length;
    }

    internal OverlayModifiers At(uint eventTime, long observedTime)
    {
        long time = Expand(eventTime, observedTime);
        for (int i = count - 1; i >= 0; i--)
        {
            var sample = samples[(first + i) % samples.Length];
            if (time >= sample.Time) return sample.Modifiers;
        }
        return count == 0 ? OverlayModifiers.None : samples[first].Modifiers;
    }

    // Extend the recent native uint tick into the current 64-bit clock epoch. This
    // also works if an editing lease spans more than one 49-day native tick wrap.
    private static long Expand(uint time, long observedTime) => observedTime + unchecked((int)(time - unchecked((uint)observedTime)));
}
