using FloatCanvas.Core;

namespace FloatCanvas.App;

/// <summary>Resizes a presentation frame while keeping its font tied to the vertical scale.</summary>
internal static class PresentationFrameLayout
{
    internal static void Resize(CanvasElement target, CanvasElement source, double x, double y)
    {
        var center = ElementGeometry.LocalToPlane(source).TransformPoint(new(source.Width / 2, source.Height / 2));
        target.Width = Math.Max(.01, source.Width * x);
        target.Height = Math.Max(.01, source.Height * y);
        target.Style.FontSize = Math.Clamp(source.Style.FontSize * y, 1, 4096);
        target.Transform = source.Transform;
        var moved = ElementGeometry.LocalToPlane(target).TransformPoint(new(target.Width / 2, target.Height / 2));
        target.Transform = Affine2D.Translation(center.X - moved.X, center.Y - moved.Y).Compose(target.Transform);
    }
}
