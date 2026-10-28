using SkiaSharp;

namespace FloatCanvas.Rendering;

/// <summary>Immutable decoded raster frames captured on the render thread. Each lease owns its lifetime.</summary>
public sealed class SceneImageSnapshot : IDisposable
{
    private SharedSnapshot? shared;
    internal SceneImageSnapshot(Dictionary<string, (SharedSceneImage Image, int Frame)> images) => shared = new(images);
    private SceneImageSnapshot(SharedSnapshot shared) => this.shared = shared;
    public SceneImageSnapshot Retain()
    {
        var snapshot = Volatile.Read(ref shared);
        ObjectDisposedException.ThrowIf(snapshot is null || !snapshot.TryRetain(), this);
        return new(snapshot);
    }
    internal bool TryGet(string id, out SKImage image, out int frame)
    {
        var snapshot = Volatile.Read(ref shared);
        ObjectDisposedException.ThrowIf(snapshot is null, this);
        if (snapshot.Images.TryGetValue(id, out var entry)) { image = entry.Image.Image; frame = entry.Frame; return true; }
        image = null!; frame = -1; return false;
    }
    public void Dispose()
    {
        Interlocked.Exchange(ref shared, null)?.Release();
    }

    // The dictionary and its image ownership are captured once on the worker. UI
    // adoption and pointer queries retain only this collection, regardless of N.
    private sealed class SharedSnapshot(Dictionary<string, (SharedSceneImage Image, int Frame)> images)
    {
        private int references = 1;
        internal Dictionary<string, (SharedSceneImage Image, int Frame)> Images { get; } = images;
        internal bool TryRetain()
        {
            int observed = Volatile.Read(ref references);
            while (observed != 0)
            {
                int previous = Interlocked.CompareExchange(ref references, checked(observed + 1), observed);
                if (previous == observed) return true;
                observed = previous;
            }
            return false;
        }
        internal void Release()
        {
            if (Interlocked.Decrement(ref references) != 0) return;
            foreach (var entry in Images.Values) entry.Image.Release();
        }
    }
}

internal sealed class SharedSceneImage(SKImage image)
{
    private int references = 1;
    internal SKImage Image { get; } = image;
    internal SharedSceneImage Retain() { Interlocked.Increment(ref references); return this; }
    internal void Release() { if (Interlocked.Decrement(ref references) == 0) Image.Dispose(); }
}

public sealed partial class SceneRenderer
{
    /// <summary>Caller owns the lease. Only immutable frames cross threads; codecs remain render-thread owned.</summary>
    public SceneImageSnapshot CaptureImageSnapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new(_images.Where(p => p.Value.HasImage).ToDictionary(p => p.Key,
            p => (p.Value.SharedImage.Retain(), p.Value.FrameIndex), StringComparer.Ordinal));
    }
    /// <summary>Borrowed lease of frames that have actually been presented. Does not decode on the input thread.</summary>
    public SceneImageSnapshot? PresentedImages { get; set; }
    private int ImageFrameIndex(string id) => PresentedImages?.TryGet(id, out _, out int frame) == true ? frame : _images[id].FrameIndex;
}
