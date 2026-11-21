using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FloatCanvas.App;

/// <summary>Paints opaque rounded surfaces continuously through the antialiased inner edge.</summary>
public class RoundedSurfaceBorder : Border
{
    protected override void OnRender(DrawingContext drawing)
    {
        var corners = CornerRadius;
        double radius = corners.TopLeft;
        var border = BorderThickness;
        double width = border.Left;
        if (radius == 0 || corners.TopRight != radius || corners.BottomRight != radius || corners.BottomLeft != radius ||
            width == 0 || border.Top != width || border.Right != width || border.Bottom != width ||
            BorderBrush is not SolidColorBrush { Color.A: 255, Opacity: 1 } edge ||
            Background is not SolidColorBrush { Color.A: 255, Opacity: 1 } fill)
        {
            base.OnRender(drawing);
            return;
        }

        double x = width, y = width;
        if (UseLayoutRounding)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            x = Math.Round(width * dpi.DpiScaleX) / dpi.DpiScaleX;
            y = Math.Round(width * dpi.DpiScaleY) / dpi.DpiScaleY;
        }

        // Border's radius describes the stroke centerline. Fill its outer silhouette
        // once so that the antialiased inner fill blends against the edge color.
        var bounds = new Rect(RenderSize);
        drawing.DrawRoundedRectangle(edge, null, bounds, radius + x / 2, radius + y / 2);
        bounds.Inflate(-x, -y);
        if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
            drawing.DrawRoundedRectangle(fill, null, bounds, Math.Max(0, radius - x / 2), Math.Max(0, radius - y / 2));
    }
}
