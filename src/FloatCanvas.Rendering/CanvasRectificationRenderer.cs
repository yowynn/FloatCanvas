using FloatCanvas.Core;
using SkiaSharp;

namespace FloatCanvas.Rendering;

/// <summary>Samples the original screen raster once through the inverse projection; preview size never changes document data.</summary>
public static class CanvasRectificationRenderer
{
    public static byte[] Render(byte[] screenPng, RectD screenBounds, CanvasProjectionSettings projection, int maximumEdge = 1200)
    {
        ArgumentNullException.ThrowIfNull(screenPng);
        if (!double.IsFinite(screenBounds.X) || !double.IsFinite(screenBounds.Y) || !double.IsFinite(screenBounds.Width) ||
            !double.IsFinite(screenBounds.Height) || screenBounds.Width <= 0 || screenBounds.Height <= 0)
            throw new ArgumentException("Invalid screen raster bounds.", nameof(screenBounds));
        if (maximumEdge is < 1 or > 8192) throw new ArgumentOutOfRangeException(nameof(maximumEdge));
        var plane = CanvasProjectionGeometry.ResolvePlaneBounds(projection);
        if (!CanvasProjectionGeometry.IsValidSize(plane.Width, plane.Height) || !projection.Matrix.TryInverse(out var inverse))
            throw new ArgumentException("Invalid source plane.", nameof(projection));
        double scale = Math.Min(1, maximumEdge / Math.Max(plane.Width, plane.Height));
        int width = Math.Max(1, (int)Math.Round(plane.Width * scale)), height = Math.Max(1, (int)Math.Round(plane.Height * scale));
        using var image = SKImage.FromEncodedData(screenPng) ?? throw new ArgumentException("Invalid screen image.", nameof(screenPng));
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Cannot allocate rectification preview.");
        surface.Canvas.Clear(SKColors.Transparent);
        var output = Projective2D.FromAffine(Affine2D.Scale(width / plane.Width, height / plane.Height)
            .Compose(Affine2D.Translation(-plane.X, -plane.Y))).Compose(inverse);
        var matrix = new SKMatrix((float)output.M11, (float)output.M12, (float)output.M13,
            (float)output.M21, (float)output.M22, (float)output.M23, (float)output.M31, (float)output.M32, (float)output.M33);
        surface.Canvas.Concat(in matrix);
        surface.Canvas.DrawImage(image, new SKRect((float)screenBounds.X, (float)screenBounds.Y, (float)screenBounds.Right, (float)screenBounds.Bottom),
            new SKSamplingOptions(SKFilterMode.Linear));
        using var result = surface.Snapshot(); using var png = result.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
}
