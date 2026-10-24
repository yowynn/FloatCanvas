using L = FloatCanvas.Localization.AppText;
using SkiaSharp;

namespace FloatCanvas.Rendering;

/// <summary>One decoded frame is at most 64 MiB BGRA; import batches allow 256 MiB of decoded pixels.
/// Check codec metadata before allocating a bitmap, including resources reopened from a document.</summary>
public static class ImageDecodeBudget
{
    public const long MaximumPixels = 16_777_216;
    public const long MaximumBatchPixels = 67_108_864;
    public static long ValidateDimensions(int width, int height)
    {
        long pixels = (long)width * height;
        if (width <= 0 || height <= 0 || pixels > MaximumPixels)
            throw new InvalidDataException(L.Get("Images.BufferTooLarge"));
        return pixels;
    }
    public static long ReadPixelCount(ReadOnlySpan<byte> bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException(L.Get("Images.UnsupportedOrCorrupt"));
        return ValidateDimensions(codec.Info.Width, codec.Info.Height);
    }
}
