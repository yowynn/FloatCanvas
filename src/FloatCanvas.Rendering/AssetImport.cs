using L = FloatCanvas.Localization.AppText;
using SkiaSharp;

namespace FloatCanvas.Rendering;

// PngBytes is retained for source compatibility; MediaType identifies preserved animated bytes.
public sealed record NormalizedImage(byte[] PngBytes, int Width, int Height, string MediaType = "image/png");

/// <summary>Normalizes still images with EXIF/alpha intact, retaining GIF and animated WebP source data.</summary>
public static class AssetImport
{
    public static NormalizedImage Normalize(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var image = Decode(bytes, out bool preserveEncoded, out string mediaType);
        // Validate the first frame and complete animation metadata before retaining source data.
        // An upright PNG needs no re-encoding: a premultiplied round trip can change straight-alpha RGB.
        if (preserveEncoded) return new((byte[])bytes.Clone(), image.Width, image.Height, mediaType);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidDataException(L.Get("Images.ImportEncodingFailed"));
        return new(data.ToArray(), image.Width, image.Height);
    }

    internal static SKImage Decode(ReadOnlySpan<byte> bytes) => Decode(bytes, out _, out _);

    private static SKImage Decode(ReadOnlySpan<byte> bytes, out bool preserveEncoded, out string mediaType)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException(L.Get("Images.UnsupportedOrCorrupt"));
        bool animated = codec.EncodedFormat is SKEncodedImageFormat.Gif or SKEncodedImageFormat.Webp && codec.FrameCount > 1;
        if (animated || codec.EncodedFormat == SKEncodedImageFormat.Gif) AnimatedImage.Validate(codec.Info, codec.FrameCount);
        if (animated && codec.FrameInfo.Any(frame => !frame.FullyRecieved))
            throw new InvalidDataException(L.Format("Images.DecodeFailed", SKCodecResult.IncompleteInput));
        mediaType = codec.EncodedFormat == SKEncodedImageFormat.Gif ? "image/gif" : animated ? "image/webp" : "image/png";
        preserveEncoded = codec.EncodedFormat == SKEncodedImageFormat.Png && codec.EncodedOrigin == SKEncodedOrigin.TopLeft ||
            codec.EncodedFormat == SKEncodedImageFormat.Gif || animated;
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success) throw new InvalidDataException(L.Format("Images.DecodeFailed", result));
        bitmap.SetImmutable();
        using var original = SKImage.FromBitmap(bitmap);
        return ApplyOrientation(original, codec.EncodedOrigin);
    }

    internal static SKImage ApplyOrientation(SKImage original, SKEncodedOrigin origin)
    {
        var info = original.Info;
        bool swap = (int)origin >= (int)SKEncodedOrigin.LeftTop;
        int width = swap ? info.Height : info.Width, height = swap ? info.Width : info.Height;
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul))
            ?? throw new InvalidDataException(L.Get("Images.BufferTooLarge"));
        surface.Canvas.Clear(SKColors.Transparent);
        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, info.Width, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, info.Width, 0, -1, info.Height, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, info.Height, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, info.Height, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, info.Height, -1, 0, info.Width, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, info.Width, 0, 0, 1),
            _ => SKMatrix.Identity
        };
        surface.Canvas.Concat(in matrix);
        surface.Canvas.DrawImage(original, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        return surface.Snapshot();
    }
}
