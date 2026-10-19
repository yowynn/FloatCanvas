namespace FloatCanvas.Core;

/// <summary>A transient screen-space line sharing one canvas depth. It is never persisted in a scene.</summary>
public readonly record struct BillboardDepthLine(PointD ScreenPoint, PointD ScreenDirection, double Scale)
{
    public bool TryVerticalIntersection(double screenX, out PointD point)
    {
        point = default;
        // Near-parallel lines amplify subpixel pointer noise into unbounded anchor motion.
        if (!double.IsFinite(screenX) || Math.Abs(ScreenDirection.X) < 1e-4) return false;
        point = new(screenX, ScreenPoint.Y + (screenX - ScreenPoint.X) * ScreenDirection.Y / ScreenDirection.X);
        return double.IsFinite(point.Y);
    }

    public bool TryClip(RectD viewport, out PointD first, out PointD last)
    {
        first = last = default;
        double minimum = double.NegativeInfinity, maximum = double.PositiveInfinity;
        bool Clip(double origin, double direction, double low, double high)
        {
            if (Math.Abs(direction) < 1e-12) return origin >= low && origin <= high;
            double a = (low - origin) / direction, b = (high - origin) / direction;
            minimum = Math.Max(minimum, Math.Min(a, b)); maximum = Math.Min(maximum, Math.Max(a, b));
            return maximum >= minimum;
        }
        if (!Clip(ScreenPoint.X, ScreenDirection.X, viewport.Left, viewport.Right) ||
            !Clip(ScreenPoint.Y, ScreenDirection.Y, viewport.Top, viewport.Bottom) ||
            !double.IsFinite(minimum) || !double.IsFinite(maximum)) return false;
        first = ScreenPoint + ScreenDirection * minimum; last = ScreenPoint + ScreenDirection * maximum;
        return true;
    }
}

/// <summary>One geometry contract for billboard drawing, hit testing, editing and export.</summary>
public static class BillboardGeometry
{
    private static Affine2D Linear(Affine2D value) => value with { OffsetX = 0, OffsetY = 0 };

    /// <summary>
    /// The shared scale is the projected length of one plane unit tangent to an equal-depth line.
    /// Along that tangent the denominator is constant, so the scale varies exactly as 1/depth.
    /// Unlike a raw homogeneous denominator this definition is invariant under matrix rescaling.
    /// Affine calibration has no depth gradient and uses its area-equivalent uniform scale.
    /// </summary>
    public static bool TryDepthScale(Projective2D planeToScreen, PointD anchor, out double scale)
    {
        scale = 0;
        if (!planeToScreen.IsFinite || !planeToScreen.TryTransformPoint(anchor, out _) || planeToScreen.Denominator(anchor) <= 0) return false;
        double length = Math.Sqrt(planeToScreen.M31 * planeToScreen.M31 + planeToScreen.M32 * planeToScreen.M32);
        if (length > 1e-12 * Math.Abs(planeToScreen.M33))
        {
            double x = -planeToScreen.M32 / length, y = planeToScreen.M31 / length;
            double a = planeToScreen.M11 * x + planeToScreen.M12 * y;
            double b = planeToScreen.M21 * x + planeToScreen.M22 * y;
            scale = Math.Sqrt(a * a + b * b) / planeToScreen.Denominator(anchor);
        }
        else
        {
            var jacobian = planeToScreen.Jacobian(anchor);
            scale = Math.Sqrt(Math.Abs(jacobian.M11 * jacobian.M22 - jacobian.M12 * jacobian.M21));
        }
        return double.IsFinite(scale) && scale is >= 1e-8 and <= 1e8;
    }

    public static bool TryEqualDepthLine(CanvasMapping mapping, PointD screenPoint, out BillboardDepthLine line)
    {
        line = default;
        if (!mapping.TryToPlane(screenPoint, out var anchor) || !mapping.TryGetDepthScale(anchor, out double scale)) return false;
        var inverse = mapping.ScreenToPlane;
        double length = Math.Sqrt(inverse.M31 * inverse.M31 + inverse.M32 * inverse.M32);
        var direction = length > 1e-12 * Math.Abs(inverse.M33)
            ? new PointD(-inverse.M32 / length, inverse.M31 / length) : new PointD(1, 0);
        // Choose a consistent left-to-right orientation for guides and diagnostics.
        if (direction.X < 0) direction *= -1;
        line = new(screenPoint, direction, scale);
        return Math.Abs(direction.X) >= 1e-4;
    }

    public static bool TryAnchor(CanvasMapping mapping, BillboardDepthLine line, PointD screenCenter, out PointD anchor, out double height)
    {
        anchor = default; height = 0;
        if (!double.IsFinite(line.Scale) || line.Scale <= 0 || !line.TryVerticalIntersection(screenCenter.X, out var ground) ||
            !mapping.TryToPlane(ground, out anchor) || !mapping.TryGetDepthScale(anchor, out double actualScale) ||
            Math.Abs(actualScale / line.Scale - 1) > 1e-6) return false;
        height = (ground.Y - screenCenter.Y) / actualScale;
        return double.IsFinite(height);
    }

    /// <summary>Places content about its geometry center. The transform's linear part retains local rotation/resize.</summary>
    public static Projective2D LocalPlacement(CanvasElement element, PointD projectedAnchor, double scale)
    {
        var center = ElementGeometry.LocalBounds(element).Center;
        return Projective2D.FromAffine(Affine2D.Translation(projectedAnchor.X, projectedAnchor.Y - element.BillboardHeight * scale)
            .Compose(Affine2D.Scale(scale, scale)).Compose(Linear(element.Transform))
            .Compose(Affine2D.Translation(-center.X, -center.Y)));
    }

    public static Projective2D ElementToTarget(CanvasElement element, Projective2D planeToTarget)
    {
        if (!element.IsBillboard) return planeToTarget.Compose(ElementGeometry.LocalToPlane(element));
        if (!TryDepthScale(planeToTarget, element.BillboardAnchor, out double scale) ||
            !planeToTarget.TryTransformPoint(element.BillboardAnchor, out var anchor))
            return Projective2D.FromAffine(Affine2D.Scale(0, 0));
        return LocalPlacement(element, anchor, scale);
    }

    /// <summary>
    /// Converts an already logical draft to billboard placement using its actual screen transform.
    /// Geometry/style are untouched. Screen matrix must be affine and contain the selected depth scale.
    /// </summary>
    public static bool TryPlaceFromScreen(CanvasElement element, CanvasMapping mapping, BillboardDepthLine line, Projective2D localToScreen)
    {
        if (!localToScreen.IsFinite || Math.Abs(localToScreen.M31) > 1e-10 || Math.Abs(localToScreen.M32) > 1e-10 ||
            Math.Abs(localToScreen.M33) < 1e-10 || !localToScreen.TryTransformPoint(ElementGeometry.LocalBounds(element).Center, out var center) ||
            !TryAnchor(mapping, line, center, out var anchor, out double height)) return false;
        double factor = 1 / (localToScreen.M33 * line.Scale);
        var linear = new Affine2D(localToScreen.M11 * factor, localToScreen.M12 * factor,
            localToScreen.M21 * factor, localToScreen.M22 * factor, 0, 0);
        if (!linear.TryInverse(out _)) return false;
        element.Transform = linear; element.ProjectivePlacement = null;
        element.IsBillboard = true; element.BillboardAnchor = anchor; element.BillboardHeight = height;
        return true;
    }

    /// <summary>Changes placement without baking/resampling content. A planar center becomes its ground anchor.</summary>
    public static bool TrySetBillboard(CanvasElement element, bool enabled, CanvasMapping mapping)
    {
        if (element.IsBillboard == enabled) return true;
        if (enabled)
        {
            if (!ElementGeometry.LocalToPlane(element).TryTransformPoint(ElementGeometry.LocalBounds(element).Center, out var anchor) ||
                !mapping.TryGetDepthScale(anchor, out _)) return false;
            element.BillboardAnchor = anchor; element.BillboardHeight = 0;
            element.Transform = Linear(element.Transform); element.ProjectivePlacement = null; element.IsBillboard = true;
        }
        else
        {
            var center = ElementGeometry.LocalBounds(element).Center;
            element.Transform = Affine2D.Translation(element.BillboardAnchor.X, element.BillboardAnchor.Y)
                .Compose(Linear(element.Transform)).Compose(Affine2D.Translation(-center.X, -center.Y));
            element.ProjectivePlacement = null; element.IsBillboard = false;
        }
        return true;
    }

    /// <summary>Moves/repositions imported logical content and its anchor together.</summary>
    public static void ApplyPlaneTransform(CanvasElement element, Affine2D transform)
    {
        if (element.IsBillboard)
        {
            element.BillboardAnchor = transform.TransformPoint(element.BillboardAnchor);
            element.Transform = Linear(transform).Compose(Linear(element.Transform));
        }
        else element.Transform = transform.Compose(element.Transform);
    }

    /// <summary>Solves the anchor position while keeping the originally grabbed local point under the screen pointer.</summary>
    public static bool TryMoveAnchor(CanvasElement element, CanvasMapping mapping, PointD localGrab, PointD screenPointer, out PointD anchor)
    {
        anchor = default;
        if (!element.IsBillboard || !mapping.TryGetDepthScale(element.BillboardAnchor, out double scale)) return false;
        var offset = Linear(element.Transform).TransformPoint(localGrab - ElementGeometry.LocalBounds(element).Center);
        offset = new(offset.X, offset.Y - element.BillboardHeight);
        var matrix = mapping.PlaneToScreen;
        double numerator = scale * matrix.Denominator(element.BillboardAnchor);
        var grabbedProjection = matrix with { M13 = matrix.M13 + numerator * offset.X, M23 = matrix.M23 + numerator * offset.Y };
        return grabbedProjection.TryInverse(out var inverse) && inverse.TryTransformPoint(screenPointer, out anchor) &&
            mapping.TryGetDepthScale(anchor, out _);
    }
}
