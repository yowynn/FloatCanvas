using FloatCanvas.Core;
using SkiaSharp;

namespace FloatCanvas.Rendering;

/// <summary>Immutable raw desktop tiles. Owned by the caller, borrowed by SceneRenderer on one thread.</summary>
public sealed class DesktopBackdrop(IEnumerable<DesktopBackdrop.Tile> tiles) : IDisposable
{
    public sealed record Tile(RectD Bounds, SKImage Image);
    public IReadOnlyList<Tile> Tiles { get; } = tiles.ToArray();
    public void Dispose() { foreach (var tile in Tiles) tile.Image.Dispose(); }
    internal void Draw(SKCanvas canvas)
    {
        foreach (var tile in Tiles)
            canvas.DrawImage(tile.Image, new SKRect((float)tile.Bounds.Left,(float)tile.Bounds.Top,
                (float)tile.Bounds.Right,(float)tile.Bounds.Bottom), new SKSamplingOptions(SKFilterMode.Linear));
    }
}
