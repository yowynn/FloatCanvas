using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Prepares owned world geometry from native source dimensions. No document, asset or history is modified.</summary>
internal static class WindowSourceInsertion
{
    internal static CanvasElement Prepare(EditorSession target, WindowSourceDescriptor descriptor, int contentWidth, int contentHeight,
        RectD? crop, RectD placement, bool billboard = false, BillboardDepthLine? depth = null, PointD? screenCenter = null, double opacity = 1,
        PointD? planeCenter = null)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(descriptor);
        var source = descriptor.Clone();
        if (!source.IsValid()) throw new ArgumentException("Invalid window source descriptor.", nameof(descriptor));
        if (contentWidth <= 0) throw new ArgumentOutOfRangeException(nameof(contentWidth));
        if (contentHeight <= 0) throw new ArgumentOutOfRangeException(nameof(contentHeight));
        if (!double.IsFinite(opacity) || opacity is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(opacity));
        if (planeCenter is { } point && (!double.IsFinite(point.X) || !double.IsFinite(point.Y))) throw new ArgumentOutOfRangeException(nameof(planeCenter));
        if (!FiniteRectangle(placement)) throw new ArgumentOutOfRangeException(nameof(placement));
        var sourceCrop = crop ?? new RectD(0, 0, 1, 1);
        if (!double.IsFinite(sourceCrop.X) || !double.IsFinite(sourceCrop.Y) || !double.IsFinite(sourceCrop.Width) || !double.IsFinite(sourceCrop.Height) ||
            sourceCrop.X < 0 || sourceCrop.Y < 0 || sourceCrop.Width <= 0 || sourceCrop.Height <= 0 || sourceCrop.Right > 1 || sourceCrop.Bottom > 1)
            throw new ArgumentOutOfRangeException(nameof(crop));
        if (target.IsTransactionActive) throw new InvalidOperationException(L.Get("Text.ActionInProgress"));
        if (!target.CanInsertInEditScope) throw new InvalidOperationException(L.Get("Inspector.LockedSuffix"));

        // A saved/downsampled PNG is a fallback only. It cannot determine authored geometry or source crop ratio.
        double croppedWidth = contentWidth * sourceCrop.Width, croppedHeight = contentHeight * sourceCrop.Height;
        double fit = Math.Min(1, Math.Min(placement.Width * .8 / croppedWidth, placement.Height * .8 / croppedHeight));
        double width = croppedWidth * fit, height = croppedHeight * fit;
        var mapping = CanvasMapping.Create(target.Document, target.ActiveRootCanvas);
        CanvasElement image;
        if (billboard)
        {
            var center = screenCenter ?? placement.Center;
            if (!double.IsFinite(center.X) || !double.IsFinite(center.Y)) throw new ArgumentOutOfRangeException(nameof(screenCenter));
            var bounds = new RectD(center.X - width / 2, center.Y - height / 2, width, height);
            // The existing M5 helper is pure: no PNG is decoded or attached until the caller commits.
            image = SnapshotImageCommand.PrepareBillboardElement(new([], bounds, true), mapping, depth);
        }
        else
        {
            var center = planeCenter ?? placement.Center;
            image = new CanvasElement { Kind = ElementKind.Image, Width = width, Height = height,
                Transform = Affine2D.Translation(center.X - width / 2, center.Y - height / 2), Style = new() { StrokeWidth = 0 } };
        }
        image.WindowSource = source; image.ImageCrop = crop; image.Style.Opacity = opacity;
        if (!mapping.IsElementSafe(image)) throw new InvalidOperationException(L.Get("Perspective.InputOutsidePlane"));
        return image;
    }

    private static bool FiniteRectangle(RectD rectangle) => double.IsFinite(rectangle.X) && double.IsFinite(rectangle.Y) &&
        double.IsFinite(rectangle.Width) && double.IsFinite(rectangle.Height) && double.IsFinite(rectangle.Right) && double.IsFinite(rectangle.Bottom) &&
        rectangle.Width > 0 && rectangle.Height > 0;
}
