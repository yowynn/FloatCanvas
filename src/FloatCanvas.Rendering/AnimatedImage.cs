using L = FloatCanvas.Localization.AppText;
using SkiaSharp;

namespace FloatCanvas.Rendering;

/// <summary>A single-threaded codec with two reusable compositing buffers and one displayed frame.</summary>
internal sealed class AnimatedImage : IDisposable
{
    private const long MaximumPixels = 16_777_216;
    private const int MaximumFrames = 10_000;
    private readonly SKData data;
    private readonly SKCodec codec;
    private readonly SKCodecFrameInfo[] frames;
    private readonly long[] ends;
    private readonly int repetitions;
    private SKBitmap? decode, prior;
    private SKImage? image;
    private int priorIndex = -1;
    internal int FrameIndex { get; private set; } = -1;
    internal SKImage Image => image ?? throw new InvalidOperationException("Animation frame has not been selected.");
    internal long CachedBytes => (long)data.Size + (decode is null ? 0 : (long)decode.ByteCount) +
        (prior is null ? 0 : (long)prior.ByteCount) + (image is null ? 0 : (long)image.Width * image.Height * 4) +
        frames.LongLength * 64 + ends.LongLength * sizeof(long);
    internal TimeSpan? NextDelay { get; private set; }

    internal static bool MayAnimate(ReadOnlySpan<byte> bytes) => bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8) ||
        bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8);

    internal static void Validate(SKImageInfo info, int count)
    {
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaximumPixels || count > MaximumFrames)
            throw new InvalidDataException(L.Get("Images.BufferTooLarge"));
    }

    internal static AnimatedImage? TryCreate(ReadOnlySpan<byte> bytes)
    {
        if (!MayAnimate(bytes)) return null;
        var data = SKData.CreateCopy(bytes); SKCodec? codec = null;
        try
        {
            codec = SKCodec.Create(data) ?? throw new InvalidDataException(L.Get("Images.UnsupportedOrCorrupt"));
            if (codec.FrameCount <= 1) { codec.Dispose(); data.Dispose(); return null; }
            Validate(codec.Info, codec.FrameCount);
            return new(data, codec);
        }
        catch { codec?.Dispose(); data.Dispose(); throw; }
    }

    private AnimatedImage(SKData data, SKCodec codec)
    {
        this.data = data; this.codec = codec;
        frames = codec.FrameInfo; repetitions = codec.RepetitionCount;
        if (frames.Length == 0 || frames.Length != codec.FrameCount) throw new InvalidDataException(L.Get("Images.UnsupportedOrCorrupt"));
        ends = new long[frames.Length]; long total = 0;
        for (int i = 0; i < frames.Length; i++)
        {
            // Preserve positive source durations. Zero-duration GIFs need a bounded fallback,
            // rather than a busy loop. The App may coalesce timer wakes without changing time.
            total = checked(total + TimeSpan.FromMilliseconds(frames[i].Duration > 0 ? frames[i].Duration : 100).Ticks);
            ends[i] = total;
        }
    }

    internal TimeSpan? NextDelayAt(TimeSpan time) => Locate(time).Delay;

    private (int Index, TimeSpan? Delay) Locate(TimeSpan time)
    {
        long elapsed = Math.Max(0, time.Ticks), cycle = ends[^1];
        bool finished = repetitions >= 0 && elapsed / cycle > repetitions;
        long position = finished ? cycle - 1 : elapsed % cycle;
        int index = Array.BinarySearch(ends, position);
        index = index < 0 ? ~index : index + 1;
        return (index, finished ? null : TimeSpan.FromTicks(ends[index] - position));
    }

    internal bool Select(TimeSpan time)
    {
        var (index, delay) = Locate(time); NextDelay = delay;
        if (index == FrameIndex) return false;
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        decode ??= new SKBitmap(info); prior ??= new SKBitmap(info);
        SKBitmap selected;
        int required = frames[index].RequiredFrame;
        if (priorIndex == index) selected = prior;
        else if (required < 0 || priorIndex >= required && priorIndex < index) selected = DecodeFrame(index, info);
        else
        {
            // Avoid asking the native codec to recursively rebuild a long dependency chain on a
            // first seek or rewind. Sequentially compose in the same two buffers; keep no frame list.
            selected = decode;
            int first = priorIndex >= 0 && priorIndex < index ? priorIndex + 1 : 0;
            for (int frame = first; frame <= index; frame++) selected = DecodeFrame(frame, info);
        }
        using var raw = SKImage.FromBitmap(selected);
        var next = AssetImport.ApplyOrientation(raw, codec.EncodedOrigin);
        image?.Dispose(); image = next; FrameIndex = index;
        return true;
    }

    private SKBitmap DecodeFrame(int index, SKImageInfo info)
    {
        int required = frames[index].RequiredFrame;
        bool reuse = required >= 0 && priorIndex >= required && priorIndex < index;
        if (reuse) prior!.GetPixelSpan().CopyTo(decode!.GetPixelSpan());
        else decode!.Erase(SKColors.Transparent);
        // Skia applies the frame's blend and the prior frame's disposal. RestorePrevious frames
        // never become a reusable prior; retain the last eligible composite separately.
        var result = codec.GetPixels(info, decode.GetPixels(), decode.RowBytes, new SKCodecOptions(index, reuse ? priorIndex : -1));
        if (result != SKCodecResult.Success) throw new InvalidDataException(L.Format("Images.DecodeFailed", result));
        var selected = decode;
        if (frames[index].DisposalMethod != SKCodecAnimationDisposalMethod.RestorePrevious)
        { (prior, decode) = (decode, prior); priorIndex = index; }
        return selected;
    }

    public void Dispose() { image?.Dispose(); decode?.Dispose(); prior?.Dispose(); codec.Dispose(); data.Dispose(); }
}
