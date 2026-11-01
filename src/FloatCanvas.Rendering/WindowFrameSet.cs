using System.Collections.ObjectModel;
using FloatCanvas.Core;
using SkiaSharp;

namespace FloatCanvas.Rendering;

public readonly record struct WindowSourceKey(Guid OwnerResourceId, SourceNodeAddress Source);
public readonly record struct WindowFrameId(Guid ConnectionId, long Generation, long Sequence);

/// <summary>Counts unique CPU frame backings until the final render/picking lease is released.</summary>
public sealed class WindowFrameBudget(long maximumBytes = 128L * 1024 * 1024)
{
    public const int MaximumWidth = 1920, MaximumHeight = 1080, MaximumSources = 4;
    public static WindowFrameBudget Shared { get; } = new();
    public long MaximumBytes { get; } = maximumBytes > 0 ? maximumBytes : throw new ArgumentOutOfRangeException(nameof(maximumBytes));
    private long retainedBytes;
    public long RetainedBytes => Interlocked.Read(ref retainedBytes);
    internal bool TryReserve(long bytes)
    {
        long observed = RetainedBytes;
        while (bytes <= MaximumBytes - observed)
        {
            long prior = Interlocked.CompareExchange(ref retainedBytes, observed + bytes, observed);
            if (prior == observed) return true;
            observed = prior;
        }
        return false;
    }
    internal void Release(long bytes) => Interlocked.Add(ref retainedBytes, -bytes);
}

/// <summary>Immutable CPU raster frame. No codec, capture item, source graph or observer is retained.</summary>
public sealed class WindowFrameLease : IDisposable
{
    private SharedFrame? shared;
    public WindowSourceKey Source { get; }
    public Guid BindingIntent { get; }
    public WindowFrameId Id { get; }
    public int ContentWidth { get; }
    public int ContentHeight { get; }
    public int Width { get; }
    public int Height { get; }
    public long PixelBytes { get; }
    internal SKImage Image => Shared.Image;
    internal object BackingIdentity => Shared;
    private SharedFrame Shared => Volatile.Read(ref shared) ?? throw new ObjectDisposedException(nameof(WindowFrameLease));

    private WindowFrameLease(WindowSourceKey source, Guid bindingIntent, WindowFrameId id, SharedFrame shared, int contentWidth, int contentHeight)
    {
        Source = source; BindingIntent = bindingIntent; Id = id; this.shared = shared;
        Width = shared.Image.Width; Height = shared.Image.Height; PixelBytes = shared.Bytes;
        ContentWidth = contentWidth; ContentHeight = contentHeight;
    }

    /// <summary>Copies the bitmap once. Caller retains and may immediately mutate/dispose the bitmap.</summary>
    public static WindowFrameLease FromBitmapCopy(WindowSourceKey source, Guid bindingIntent, WindowFrameId id,
        SKBitmap bitmap, int contentWidth = 0, int contentHeight = 0, WindowFrameBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        Validate(source, bindingIntent, id, bitmap.Width, bitmap.Height, ref contentWidth, ref contentHeight);
        budget ??= WindowFrameBudget.Shared;
        long bytes = checked((long)bitmap.Width * bitmap.Height * 4);
        if (!budget.TryReserve(bytes)) throw new InvalidOperationException("The retained window frame budget is full.");
        SKImage? image = null;
        try
        {
            using var converted = bitmap.ColorType is SKColorType.Bgra8888 or SKColorType.Rgba8888 ? null :
                bitmap.Copy(SKColorType.Bgra8888) ?? throw new InvalidOperationException("Cannot copy window pixels.");
            using var pixels = (converted ?? bitmap).PeekPixels();
            image = SKImage.FromPixelCopy(pixels) ?? throw new InvalidOperationException("Cannot copy window pixels.");
            using var ownedPixels = image.PeekPixels();
            long ownedBytes = checked((long)ownedPixels.RowBytes * ownedPixels.Height);
            if (ownedBytes > bytes && !budget.TryReserve(ownedBytes - bytes)) throw new InvalidOperationException("The retained window frame budget is full.");
            if (ownedBytes < bytes) budget.Release(bytes - ownedBytes);
            bytes = ownedBytes;
            var result = new WindowFrameLease(source, bindingIntent, id, new(image, budget, bytes), contentWidth, contentHeight);
            image = null; return result;
        }
        catch { image?.Dispose(); budget.Release(bytes); throw; }
    }

    /// <summary>Transfers an owned, CPU-backed immutable 32-bit image on success. On failure ownership stays with the caller.</summary>
    public static WindowFrameLease FromImage(WindowSourceKey source, Guid bindingIntent, WindowFrameId id,
        SKImage image, int contentWidth = 0, int contentHeight = 0, WindowFrameBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        Validate(source, bindingIntent, id, image.Width, image.Height, ref contentWidth, ref contentHeight);
        using var pixels = image.PeekPixels();
        if (pixels is null || pixels.ColorType is not (SKColorType.Bgra8888 or SKColorType.Rgba8888))
            throw new ArgumentException("A CPU-backed 32-bit window image is required.", nameof(image));
        long bytes = checked((long)pixels.RowBytes * pixels.Height); budget ??= WindowFrameBudget.Shared;
        if (!budget.TryReserve(bytes)) throw new InvalidOperationException("The retained window frame budget is full.");
        try { return new(source, bindingIntent, id, new(image, budget, bytes), contentWidth, contentHeight); }
        catch { budget.Release(bytes); throw; }
    }

    private static void Validate(WindowSourceKey source, Guid bindingIntent, WindowFrameId id, int width, int height, ref int contentWidth, ref int contentHeight)
    {
        if (source.OwnerResourceId == Guid.Empty || source.Source.NodeId == Guid.Empty || source.Source.DefinitionId == Guid.Empty ||
            bindingIntent == Guid.Empty || id.ConnectionId == Guid.Empty || id.Generation < 0 || id.Sequence < 0)
            throw new ArgumentException("Invalid window source or frame identity.");
        if (width is < 1 or > WindowFrameBudget.MaximumWidth || height is < 1 or > WindowFrameBudget.MaximumHeight)
            throw new ArgumentOutOfRangeException(nameof(width), "The published full-window frame must fit 1920 by 1080 pixels.");
        if (contentWidth == 0) contentWidth = width; if (contentHeight == 0) contentHeight = height;
        if (contentWidth < width || contentHeight < height) throw new ArgumentOutOfRangeException(nameof(contentWidth));
    }

    public WindowFrameLease Retain()
    {
        var value = Shared;
        if (!value.TryRetain()) throw new ObjectDisposedException(nameof(WindowFrameLease));
        return new(Source, BindingIntent, Id, value, ContentWidth, ContentHeight);
    }
    /// <summary>Shares one backing for an explicitly mapped source in the same resource owner.</summary>
    public WindowFrameLease ForSource(WindowSourceKey source, Guid bindingIntent)
    {
        if (source.OwnerResourceId != Source.OwnerResourceId) throw new ArgumentException("Window frames cannot cross resource owners.", nameof(source));
        int contentWidth = ContentWidth, contentHeight = ContentHeight;
        Validate(source, bindingIntent, Id, Width, Height, ref contentWidth, ref contentHeight);
        var value = Shared;
        if (!value.TryRetain()) throw new ObjectDisposedException(nameof(WindowFrameLease));
        return new(source, bindingIntent, Id, value, contentWidth, contentHeight);
    }
    public void Dispose() => Interlocked.Exchange(ref shared, null)?.Release();

    private sealed class SharedFrame(SKImage image, WindowFrameBudget budget, long bytes)
    {
        private int references = 1;
        internal SKImage Image { get; } = image;
        internal long Bytes { get; } = bytes;
        internal bool TryRetain()
        {
            int observed = Volatile.Read(ref references);
            while (observed != 0)
            {
                int prior = Interlocked.CompareExchange(ref references, checked(observed + 1), observed);
                if (prior == observed) return true; observed = prior;
            }
            return false;
        }
        internal void Release()
        {
            if (Interlocked.Decrement(ref references) != 0) return;
            try { Image.Dispose(); } finally { budget.Release(Bytes); }
        }
    }
}

/// <summary>An immutable, explicitly owner-scoped frame bundle. Retain is O(1), independent of source/occurrence count.</summary>
public sealed class WindowFrameSet : IDisposable
{
    private SharedSet? shared;
    public object OwnerContext { get; }
    public long PixelBytes { get; }
    public int Count { get; }
    private SharedSet Shared => Volatile.Read(ref shared) ?? throw new ObjectDisposedException(nameof(WindowFrameSet));
    private WindowFrameSet(object ownerContext, SharedSet shared)
    { OwnerContext = ownerContext; this.shared = shared; PixelBytes = shared.PixelBytes; Count = shared.Frames.Count; }

    /// <summary>Retains each input lease. Caller retains its own handles on success or failure.</summary>
    public static WindowFrameSet Capture(object ownerContext, IEnumerable<WindowFrameLease> frames)
    {
        ArgumentNullException.ThrowIfNull(ownerContext); ArgumentNullException.ThrowIfNull(frames);
        var owned = new Dictionary<WindowSourceKey, WindowFrameLease>();
        try
        {
            foreach (var frame in frames)
            {
                ArgumentNullException.ThrowIfNull(frame);
                if (owned.Count == WindowFrameBudget.MaximumSources) throw new ArgumentOutOfRangeException(nameof(frames), "At most four window sources are supported.");
                if (owned.ContainsKey(frame.Source)) throw new ArgumentException("Duplicate window source.", nameof(frames));
                owned.Add(frame.Source, frame.Retain());
            }
            return new(ownerContext, new(owned));
        }
        catch { foreach (var frame in owned.Values) frame.Dispose(); throw; }
    }
    /// <summary>Returns a borrowed handle. Retain it before outliving this set; do not Dispose the borrowed handle.</summary>
    public bool TryGet(WindowSourceKey source, out WindowFrameLease frame) => Shared.Frames.TryGetValue(source, out frame!);
    public WindowFrameSet Retain()
    {
        var value = Shared;
        if (!value.TryRetain()) throw new ObjectDisposedException(nameof(WindowFrameSet));
        return new(OwnerContext, value);
    }
    public void Dispose() => Interlocked.Exchange(ref shared, null)?.Release();
    private sealed class SharedSet(Dictionary<WindowSourceKey, WindowFrameLease> frames)
    {
        private int references = 1;
        internal IReadOnlyDictionary<WindowSourceKey, WindowFrameLease> Frames { get; } = new ReadOnlyDictionary<WindowSourceKey, WindowFrameLease>(frames);
        internal long PixelBytes { get; } = frames.Values.GroupBy(frame => frame.BackingIdentity, ReferenceEqualityComparer.Instance).Sum(group => group.First().PixelBytes);
        internal bool TryRetain()
        {
            int observed = Volatile.Read(ref references);
            while (observed != 0)
            {
                int prior = Interlocked.CompareExchange(ref references, checked(observed + 1), observed);
                if (prior == observed) return true; observed = prior;
            }
            return false;
        }
        internal void Release() { if (Interlocked.Decrement(ref references) == 0) foreach (var frame in Frames.Values) frame.Dispose(); }
    }
}
