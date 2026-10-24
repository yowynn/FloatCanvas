using FloatCanvas.Core;
using SkiaSharp;

namespace FloatCanvas.Rendering;

/// <summary>Immutable raw desktop tiles. Owned by the caller, borrowed by SceneRenderer on one thread.</summary>
public sealed class DesktopBackdrop(IEnumerable<DesktopBackdrop.Tile> tiles) : IDisposable
{
    public sealed record Tile(RectD Bounds, SKImage Image);
    public IReadOnlyList<Tile> Tiles { get; } = tiles.ToArray();
    /// <summary>Retains independent image handles for a worker snapshot. Dispose either owner independently.</summary>
    public DesktopBackdrop Retain()
    {
        var retained = new List<Tile>();
        try
        {
            foreach (var tile in Tiles)
                retained.Add(new(tile.Bounds, tile.Image.Subset(new(0, 0, tile.Image.Width, tile.Image.Height))
                    ?? throw new InvalidOperationException("Could not retain desktop pixels.")));
            return new(retained);
        }
        catch { foreach (var tile in retained) tile.Image.Dispose(); throw; }
    }
    /// <summary>Decodes one screen-aligned capture. The caller owns and must dispose the returned backdrop.</summary>
    public static DesktopBackdrop FromEncoded(RectD bounds, ReadOnlySpan<byte> encoded)
    {
        var image = AssetImport.Decode(encoded);
        return new([new(bounds, image)]);
    }
    public void Dispose() { foreach (var tile in Tiles) tile.Image.Dispose(); }
    internal void Draw(SKCanvas canvas)
    {
        foreach (var tile in Tiles)
            canvas.DrawImage(tile.Image, new SKRect((float)tile.Bounds.Left,(float)tile.Bounds.Top,
                (float)tile.Bounds.Right,(float)tile.Bounds.Bottom), new SKSamplingOptions(SKFilterMode.Linear));
    }
}
