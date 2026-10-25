using FloatCanvas.Windows;
using SkiaSharp;

internal static class PreviewTestPixels
{
    internal static SKBitmap Read(PreviewRasterFrame frame)
    {
        var tiles = frame.Tiles;
        int left = tiles.Min(tile => tile.Bounds.Left), top = tiles.Min(tile => tile.Bounds.Top);
        int right = tiles.Max(tile => tile.Bounds.Right), bottom = tiles.Max(tile => tile.Bounds.Bottom);
        using var surface = SKSurface.Create(new SKImageInfo(right - left, bottom - top, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent); surface.Canvas.Translate(-left, -top); frame.Draw(surface.Canvas);
        using var image = surface.Snapshot(); return SKBitmap.FromImage(image);
    }
}
