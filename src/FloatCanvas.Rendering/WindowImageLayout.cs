using FloatCanvas.Core;

namespace FloatCanvas.Rendering;

/// <summary>One crop-to-body mapping shared by image ink, alpha support, editing and explicit freeze.</summary>
public readonly record struct WindowImageLayout(RectD SourcePixels, RectD Destination)
{
    public static bool TryCreate(int pixelWidth, int pixelHeight, double width, double height, RectD? normalizedCrop,
        bool contain, out WindowImageLayout layout, int contentWidth = 0, int contentHeight = 0)
    {
        layout = default;
        if (pixelWidth < 1 || pixelHeight < 1 || !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return false;
        if (contentWidth < 0 || contentHeight < 0 || (contentWidth == 0) != (contentHeight == 0)) return false;
        var crop = (normalizedCrop ?? new RectD(0, 0, 1, 1)).Normalize().Intersect(new(0, 0, 1, 1));
        if (!double.IsFinite(crop.X + crop.Y + crop.Width + crop.Height) || crop.IsEmpty) return false;
        var source = new RectD(crop.X * pixelWidth, crop.Y * pixelHeight, crop.Width * pixelWidth, crop.Height * pixelHeight);
        var destination = new RectD(0, 0, width, height);
        if (contain)
        {
            double aspectWidth = (contentWidth == 0 ? pixelWidth : contentWidth) * crop.Width;
            double aspectHeight = (contentHeight == 0 ? pixelHeight : contentHeight) * crop.Height;
            double scale = Math.Min(width / aspectWidth, height / aspectHeight);
            var fittedWidth = aspectWidth * scale; var fittedHeight = aspectHeight * scale;
            destination = new((width - fittedWidth) / 2, (height - fittedHeight) / 2, fittedWidth, fittedHeight);
        }
        layout = new(source, destination); return true;
    }
}
