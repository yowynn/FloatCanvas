namespace FloatCanvas.Core;

public static partial class ElementGeometry
{
    private static bool TryImageFrameBounds(CanvasElement element, bool includeStroke, out RectD bounds)
    {
        bounds = default;
        // Image effects can create or spread alpha into the captured PNG's transparent corners.
        // Keep the original contour for undo, but frame the complete editable raster meanwhile.
        if (element.Kind != ElementKind.Image || element.IsBillboard || element.Effects.Count > 0 || element.ImageFrame is not { Count: >= 3 } frame ||
            !(element.Width > 0) || !(element.Height > 0)) return false;
        var transform = LocalToPlane(element);
        double radius = includeStroke && (element.Style.Stroke >> 24) != 0 ? element.ImageBorderWidth : 0;
        double left = double.PositiveInfinity, top = double.PositiveInfinity;
        double right = double.NegativeInfinity, bottom = double.NegativeInfinity;
        foreach (var normalized in frame)
        {
            var point = new PointD(normalized.X * element.Width, normalized.Y * element.Height);
            if (!transform.TryTransformPoint(point, out var mapped) ||
                !TryDiskCoordinateBounds(transform, point, mapped.X, transform.M11, transform.M12, radius, out var minX, out var maxX) ||
                !TryDiskCoordinateBounds(transform, point, mapped.Y, transform.M21, transform.M22, radius, out var minY, out var maxY)) return false;
            left = Math.Min(left, minX); right = Math.Max(right, maxX);
            top = Math.Min(top, minY); bottom = Math.Max(bottom, maxY);
        }
        bounds = new(left, top, right - left, bottom - top);
        return double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height);
    }

    // A round outside stroke is the contour plus radius-r disks. On a finite projective
    // branch each coordinate is monotone along a segment, so vertex disks give its exact
    // envelope. Solve the tangent-circle quadratic around the mapped center to avoid
    // cancellation between large absolute coordinates.
    private static bool TryDiskCoordinateBounds(Projective2D transform, PointD point, double center,
        double axisX, double axisY, double radius, out double minimum, out double maximum)
    {
        minimum = maximum = center;
        if (radius == 0) return true;
        if (!double.IsFinite(radius) || radius < 0) return false;
        double denominator = transform.Denominator(point);
        double vx = transform.M31 / denominator, vy = transform.M32 / denominator;
        double ux = (axisX - center * transform.M31) / denominator;
        double uy = (axisY - center * transform.M32) / denominator;
        double squaredRadius = radius * radius;
        double coefficient = 1 - squaredRadius * (vx * vx + vy * vy);
        if (!(coefficient > 1e-12)) return false;
        double dot = ux * vx + uy * vy;
        double radicand = squaredRadius * dot * dot + coefficient * (ux * ux + uy * uy);
        double halfSpan = radius * Math.Sqrt(Math.Max(0, radicand));
        double shift = -squaredRadius * dot;
        minimum = center + (shift - halfSpan) / coefficient;
        maximum = center + (shift + halfSpan) / coefficient;
        return double.IsFinite(minimum) && double.IsFinite(maximum);
    }

    /// <summary>Retains the captured contour when the image body is cropped. Call before changing its dimensions.</summary>
    public static void CropImageFrame(CanvasElement image, RectD localCrop)
    {
        if (image.ImageFrame is not { Count: >= 3 } frame || image.Width <= 0 || image.Height <= 0 || localCrop.IsEmpty) return;
        var crop = new RectD(localCrop.Left / image.Width, localCrop.Top / image.Height,
            localCrop.Width / image.Width, localCrop.Height / image.Height);
        var polygon = frame.ToList();
        polygon = ClipImageFrame(polygon, p => p.X - crop.Left);
        polygon = ClipImageFrame(polygon, p => crop.Right - p.X);
        polygon = ClipImageFrame(polygon, p => p.Y - crop.Top);
        polygon = ClipImageFrame(polygon, p => crop.Bottom - p.Y);
        // A crop can merely touch the contour and leave duplicate vertices along one edge.
        // Retain the ordinary crop frame in that case so the remaining bitmap stays editable.
        // Extents also admit valid self-intersecting contours whose signed areas cancel.
        bool empty = polygon.Count < 3 || polygon.Max(p => p.X) - polygon.Min(p => p.X) <= crop.Width * 1e-10 ||
            polygon.Max(p => p.Y) - polygon.Min(p => p.Y) <= crop.Height * 1e-10;
        image.ImageFrame = empty ? null : polygon.Select(p =>
            new PointD(Math.Clamp((p.X - crop.Left) / crop.Width, 0, 1), Math.Clamp((p.Y - crop.Top) / crop.Height, 0, 1))).ToList();
    }

    private static List<PointD> ClipImageFrame(List<PointD> input, Func<PointD, double> side)
    {
        if (input.Count == 0) return input;
        var output = new List<PointD>();
        var previous = input[^1]; double before = side(previous);
        foreach (var point in input)
        {
            double after = side(point);
            if ((before >= 0) != (after >= 0)) output.Add(previous + (point - previous) * (before / (before - after)));
            if (after >= 0) output.Add(point);
            previous = point; before = after;
        }
        return output;
    }
}
