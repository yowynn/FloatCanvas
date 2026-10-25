using L = FloatCanvas.Localization.AppText;
using SkiaSharp;

namespace FloatCanvas.Rendering;

/// <summary>Immutable source timing only. Contains no codec, encoded-byte copy or pixel buffers.</summary>
internal sealed class AnimationTimeline
{
    private readonly long[] ends;
    private readonly int repetitions;
    internal long CachedBytes => ends.LongLength * sizeof(long);

    internal AnimationTimeline(SKCodecFrameInfo[] frames, int repetitions)
    {
        this.repetitions = repetitions;
        ends = new long[frames.Length];
        long total = 0;
        for (int i = 0; i < frames.Length; i++)
        {
            // Match playback's bounded fallback for zero-duration source frames.
            total = checked(total + TimeSpan.FromMilliseconds(frames[i].Duration > 0 ? frames[i].Duration : 100).Ticks);
            ends[i] = total;
        }
    }

    internal static AnimationTimeline? Read(ReadOnlySpan<byte> bytes)
    {
        if (!AnimatedImage.MayAnimate(bytes)) return null;
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException(L.Get("Images.UnsupportedOrCorrupt"));
        if (codec.FrameCount <= 1) return null;
        AnimatedImage.Validate(codec.Info, codec.FrameCount);
        var frames = codec.FrameInfo;
        if (frames.Length == 0 || frames.Length != codec.FrameCount)
            throw new InvalidDataException(L.Get("Images.UnsupportedOrCorrupt"));
        return new(frames, codec.RepetitionCount);
    }

    internal (int Index, TimeSpan? Delay) Locate(TimeSpan time)
    {
        long elapsed = Math.Max(0, time.Ticks), cycle = ends[^1];
        bool finished = repetitions >= 0 && elapsed / cycle > repetitions;
        long position = finished ? cycle - 1 : elapsed % cycle;
        int index = Array.BinarySearch(ends, position);
        index = index < 0 ? ~index : index + 1;
        return (index, finished ? null : TimeSpan.FromTicks(ends[index] - position));
    }
}
