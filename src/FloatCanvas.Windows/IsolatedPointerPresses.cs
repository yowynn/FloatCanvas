namespace FloatCanvas.Windows;

/// <summary>Keyboard/mouse hook ordering is finer than Win32's millisecond timestamps.
/// Retain exact button-down snapshots until the UI consumes its matching native press.</summary>
internal sealed class IsolatedPointerPresses(int capacity)
{
    private readonly (uint Time, OverlayPoint Position, OverlayMouseButton Button, OverlayModifiers Modifiers)[] presses =
        new (uint, OverlayPoint, OverlayMouseButton, OverlayModifiers)[capacity];
    private int first, count;

    internal void Clear() { first = count = 0; }

    internal void Record(uint message, uint time, OverlayPoint position, OverlayModifiers modifiers)
    {
        var button = message switch
        {
            0x201 => OverlayMouseButton.Left,
            0x204 => OverlayMouseButton.Right,
            0x207 => OverlayMouseButton.Middle,
            _ => OverlayMouseButton.None
        };
        if (button == OverlayMouseButton.None) return;
        int next = (first + count) % presses.Length;
        presses[next] = (time, position, button, modifiers);
        if (count < presses.Length) count++;
        else first = (first + 1) % presses.Length;
    }

    internal bool TryTake(uint time, OverlayPoint position, OverlayMouseButton button, out OverlayModifiers modifiers)
    {
        for (int i = 0; i < count; i++)
        {
            var press = presses[(first + i) % presses.Length];
            if (press.Time != time || press.Position != position || press.Button != button) continue;
            first = (first + i + 1) % presses.Length;
            count -= i + 1;
            modifiers = press.Modifiers;
            return true;
        }
        modifiers = OverlayModifiers.None;
        return false;
    }
}
