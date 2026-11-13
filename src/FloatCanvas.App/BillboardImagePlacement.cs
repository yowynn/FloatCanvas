using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>New bitmap/source dimensions are logical pixels, independent of the observer camera and click depth.</summary>
internal static class BillboardImagePlacement
{
    internal static RectD LogicalBounds(CanvasSettings settings, RectD reference)
    {
        var projection = settings.Projection;
        if (projection.Quad.Count == 0)
            projection = CanvasCalibration.CreateInitial(reference, projection.Columns, projection.Rows);
        return CanvasProjectionGeometry.ResolvePlaneBounds(projection);
    }

    internal static CanvasElement Prepare(EditorSession target, CanvasMapping mapping, double sourceWidth, double sourceHeight, PointD center,
        RectD? logicalCanvasBounds = null)
    {
        var bounds = logicalCanvasBounds ?? LogicalBounds(target.Document.Canvas, target.Document.Viewport);
        double fit = Math.Min(1, Math.Min(bounds.Width * .8 / sourceWidth, bounds.Height * .8 / sourceHeight));
        var image = new CanvasElement { Kind = ElementKind.Image, Width = sourceWidth * fit, Height = sourceHeight * fit,
            Style = new() { StrokeWidth = 0 } };
        if (!BillboardCreationGeometry.TryPlaceLogical(image, mapping, new(image.Width / 2, image.Height / 2), center, out var placed, out _))
            throw new InvalidOperationException(L.Get("Billboard.InvalidLogicalPlacement"));
        return placed;
    }
}
