namespace FloatCanvas.Core;

/// <summary>Moves a marker counter in its configured direction without wrapping at either integer limit.</summary>
public static class NumberSequence
{
    public static int Advance(int current, int marker, int step)
    {
        int next = (int)Math.Clamp((long)marker + step, int.MinValue, int.MaxValue);
        return step > 0 ? Math.Max(current, next) : step < 0 ? Math.Min(current, next) : current;
    }
}
