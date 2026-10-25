using System.Runtime.InteropServices;
using SkiaSharp;

namespace FloatCanvas.Windows;

/// <summary>Pixel leases are returned by Skia only after the last native image reader releases them.</summary>
internal sealed class PreviewPixelPool : IDisposable
{
    private sealed record Buffer(nint Pixels, int Bytes);
    private readonly object gate = new();
    private readonly List<Buffer> free = [];
    private long activeBytes, freeBytes, byteLimit = 32L * 1024 * 1024;
    private bool disposed;
    public long ActiveBytes { get { lock (gate) return activeBytes; } }
    public long FreeBytes { get { lock (gate) return freeBytes; } }
    public long ByteLimit
    {
        get { lock (gate) return byteLimit; }
        set { lock (gate) { byteLimit = Math.Max(0, value); Trim(); } }
    }

    internal unsafe PreviewRasterFrame.Tile Copy(PixelBounds bounds, nint source, int sourceRowBytes, int x, int y)
    {
        int bytes = checked(bounds.Width * bounds.Height * 4), rowBytes = bounds.Width * 4;
        Buffer buffer;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            int index = free.FindIndex(item => item.Bytes == bytes);
            if (index < 0) buffer = new(Marshal.AllocHGlobal(bytes), bytes);
            else { buffer = free[index]; free.RemoveAt(index); freeBytes -= bytes; }
            activeBytes += bytes;
        }
        int returned = 0;
        SKImage? image = null;
        void ReturnOnce() { if (Interlocked.Exchange(ref returned, 1) == 0) Return(buffer); }
        try
        {
            for (int row = 0; row < bounds.Height; row++)
                new ReadOnlySpan<byte>((void*)(source + (y + row) * sourceRowBytes + x * 4), rowBytes)
                    .CopyTo(new Span<byte>((void*)(buffer.Pixels + row * rowBytes), rowBytes));
            using var pixels = new SKPixmap(new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul), buffer.Pixels, rowBytes);
            image = SKImage.FromPixels(pixels, (_, _) => ReturnOnce())
                ?? throw new OutOfMemoryException("Preview pixel image allocation failed.");
            return new(bounds, image) { Pixels = buffer.Pixels };
        }
        catch { image?.Dispose(); ReturnOnce(); throw; }
    }

    private void Return(Buffer buffer)
    {
        lock (gate)
        {
            activeBytes -= buffer.Bytes;
            if (disposed || buffer.Bytes > byteLimit - freeBytes) Marshal.FreeHGlobal(buffer.Pixels);
            else { free.Add(buffer); freeBytes += buffer.Bytes; }
        }
    }
    private void Trim()
    {
        while (freeBytes > byteLimit && free.Count > 0)
        {
            var buffer = free[^1]; free.RemoveAt(free.Count - 1); freeBytes -= buffer.Bytes;
            Marshal.FreeHGlobal(buffer.Pixels);
        }
    }
    public void Dispose()
    {
        lock (gate) { disposed = true; byteLimit = 0; Trim(); }
    }
}
