namespace FloatCanvas.Windows;

public readonly record struct WindowCaptureSize(int Width, int Height);

/// <summary>Limits apply before allocation or GPU readback. The frame pool still uses the complete source size.</summary>
public static class WindowCapturePolicy
{
    public const int MaximumWidth = 1920, MaximumHeight = 1080, FramesPerSecond = 30;
    public const int MaximumSourceDimension = 16384;
    public const long MaximumSourcePixels = 67_108_864;

    public static bool TryOutputSize(int width, int height, out WindowCaptureSize output)
    {
        output = default;
        if (width is <= 0 or > MaximumSourceDimension || height is <= 0 or > MaximumSourceDimension || (long)width * height > MaximumSourcePixels) return false;
        double scale = Math.Min(1, Math.Min((double)MaximumWidth / width, (double)MaximumHeight / height));
        output = new(Math.Max(1, (int)Math.Floor(width * scale)), Math.Max(1, (int)Math.Floor(height * scale)));
        return true;
    }
}
