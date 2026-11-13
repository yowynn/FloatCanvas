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

/// <summary>A world-space placement; content geometry and style remain unchanged.</summary>
public readonly record struct BillboardPlacement(PointD Anchor, double Height, Affine2D Linear);

public enum BillboardPlacementFailure
{
    None, EmptyGeometry, NonAffineScreenTransform, UnsafeGround,
    GroundXNotRepresentable, InvalidScale, UnsafeContent
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
        if (mapping.ViewRoot is { } root)
        {
            if (!TryEqualDepthLine(root, screenPoint, out var rootLine)) return false;
            double viewScale = rootLine.Scale * mapping.ViewPlacement.ElevationScale;
            line = rootLine with { Scale = viewScale }; return double.IsFinite(viewScale) && viewScale != 0;
        }
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
        if (mapping.ViewRoot is { } root)
        {
            var rootLine = line with { Scale = line.Scale / mapping.ViewPlacement.ElevationScale };
            if (!TryAnchor(root, rootLine, screenCenter, out var rootAnchor, out double rootHeight)) return false;
            anchor = mapping.FromRootPoint(rootAnchor); height = rootHeight / mapping.ViewPlacement.ElevationScale;
            return double.IsFinite(anchor.X) && double.IsFinite(anchor.Y) && double.IsFinite(height);
        }
        if (!double.IsFinite(line.Scale) || line.Scale <= 0 || !line.TryVerticalIntersection(screenCenter.X, out var ground) ||
            !mapping.TryToPlane(ground, out anchor) || !mapping.TryGetDepthScale(anchor, out double actualScale) ||
            Math.Abs(actualScale / line.Scale - 1) > 1e-6) return false;
        height = (ground.Y - screenCenter.Y) / actualScale;
        return double.IsFinite(height);
    }

    /// <summary>Places content about its geometry center. The transform's linear part retains local rotation/resize.</summary>
    public static Projective2D LocalPlacement(CanvasElement element, PointD projectedAnchor, double scale)
    {
        if (element.BillboardSurface is { } surface) return surface.ToTarget(projectedAnchor, scale);
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
    /// Resolves a screen affine without changing content. This model places the content center
    /// directly above its ground point, so a different ground X cannot preserve the screen affine.
    /// The body may extend above the horizon; only the ground and finite screen envelope are guarded.
    /// </summary>
    public static bool TryResolveScreenPlacement(CanvasElement content, CanvasMapping mapping,
        Projective2D localToScreen, PointD groundScreen, out BillboardPlacement placement,
        out BillboardPlacementFailure failure)
    {
        placement = default;
        return TryScreenContent(content, localToScreen, out var screen, out var local, out var envelope, out failure) &&
            TryResolvePlacement(mapping, screen, local, envelope, groundScreen, out placement, out failure);
    }

    /// <summary>
    /// Uses the transformed geometry frame's bottom midpoint, rather than an alpha/ink bound.
    /// A degenerate stroke uses its stroked envelope's bottom, retaining the geometry center X.
    /// </summary>
    public static bool TryResolveScreenBottom(CanvasElement content, CanvasMapping mapping,
        Projective2D localToScreen, out BillboardPlacement placement, out BillboardPlacementFailure failure)
    {
        placement = default;
        if (!TryScreenContent(content, localToScreen, out var screen, out var local, out var envelope, out failure)) return false;
        var center = screen.TransformPoint(local.Center);
        var frame = screen.TransformBounds(local.IsEmpty ? envelope : local);
        return TryResolvePlacement(mapping, screen, local, envelope, new(center.X, frame.Bottom), out placement, out failure);
    }

    private static bool TryScreenContent(CanvasElement content, Projective2D transform, out Affine2D screen,
        out RectD local, out RectD envelope, out BillboardPlacementFailure failure)
    {
        screen = default; local = ElementGeometry.LocalBounds(content); envelope = default;
        failure = BillboardPlacementFailure.EmptyGeometry;
        if (ElementGeometry.UsesPoints(content.Kind) && content.Points.Count == 0 || !FiniteBounds(local)) return false;
        envelope = ElementGeometry.StrokedLocalBounds(content);
        if (!FiniteBounds(envelope) || local.IsEmpty && (!ElementGeometry.UsesPoints(content.Kind) || envelope.IsEmpty)) return false;
        failure = BillboardPlacementFailure.NonAffineScreenTransform;
        if (!transform.IsFinite || transform.M33 == 0) return false;
        double factor = 1 / transform.M33;
        // Accept roundoff from H * H^-1, but reject a genuine projective denominator
        // over this content's domain. The check is invariant under homogeneous rescaling.
        double denominatorVariation = Math.Abs(transform.M31 * factor) * Math.Max(Math.Abs(envelope.Left), Math.Abs(envelope.Right)) +
            Math.Abs(transform.M32 * factor) * Math.Max(Math.Abs(envelope.Top), Math.Abs(envelope.Bottom));
        screen = new(transform.M11 * factor, transform.M12 * factor, transform.M21 * factor,
            transform.M22 * factor, transform.M13 * factor, transform.M23 * factor);
        if (denominatorVariation > 1e-12 || !LayerPlacement.Finite(screen) || !screen.TryInverse(out _)) return false;
        var bounds = screen.TransformBounds(envelope);
        failure = BillboardPlacementFailure.UnsafeContent;
        if (!FiniteBounds(bounds) || Math.Abs(bounds.Left) > CanvasProjectionSafety.MaximumCoordinate ||
            Math.Abs(bounds.Right) > CanvasProjectionSafety.MaximumCoordinate || Math.Abs(bounds.Top) > CanvasProjectionSafety.MaximumCoordinate ||
            Math.Abs(bounds.Bottom) > CanvasProjectionSafety.MaximumCoordinate) return false;
        // A tiny residual denominator must also be negligible in physical pixels.
        PointD[] corners = [new(envelope.Left, envelope.Top), new(envelope.Right, envelope.Top),
            new(envelope.Right, envelope.Bottom), new(envelope.Left, envelope.Bottom)];
        failure = BillboardPlacementFailure.NonAffineScreenTransform;
        foreach (var point in corners)
            if (!transform.TryTransformPoint(point, out var projected) || projected.DistanceTo(screen.TransformPoint(point)) > 1e-7) return false;
        failure = BillboardPlacementFailure.None; return true;
    }

    private static bool TryResolvePlacement(CanvasMapping mapping, Affine2D screen, RectD local, RectD envelope,
        PointD groundScreen, out BillboardPlacement placement, out BillboardPlacementFailure failure)
    {
        placement = default;
        if (mapping.ViewRoot is { } root)
        {
            if (!TryResolvePlacement(root, screen, local, envelope, groundScreen, out var rootPlacement, out failure)) return false;
            if (mapping.TryFromRootPlacement(rootPlacement, out placement)) return true;
            failure = BillboardPlacementFailure.InvalidScale; return false;
        }
        var center = screen.TransformPoint(local.Center);
        failure = BillboardPlacementFailure.GroundXNotRepresentable;
        if (!double.IsFinite(groundScreen.X) || Math.Abs(groundScreen.X - center.X) > 1e-7) return false;
        // Snap rounding at the frame midpoint to the model's exact center-X invariant.
        groundScreen = new(center.X, groundScreen.Y);
        failure = BillboardPlacementFailure.UnsafeGround;
        if (!mapping.TryToPlane(groundScreen, out var anchor)) return false;
        failure = BillboardPlacementFailure.InvalidScale;
        if (!mapping.TryGetDepthScale(anchor, out double scale) || !TryScreenLinear(screen, scale, out var linear)) return false;
        double height = (groundScreen.Y - center.Y) / scale;
        if (!double.IsFinite(height)) return false;
        // Validate the actual reconstructed envelope as well as the requested screen affine.
        var actual = Affine2D.Translation(mapping.ToScreen(anchor).X, mapping.ToScreen(anchor).Y - height * scale)
            .Compose(Affine2D.Scale(scale, scale)).Compose(linear).Compose(Affine2D.Translation(-local.Center.X, -local.Center.Y));
        var bounds = actual.TransformBounds(envelope);
        failure = BillboardPlacementFailure.UnsafeContent;
        if (!FiniteBounds(bounds) || Math.Abs(bounds.Left) > CanvasProjectionSafety.MaximumCoordinate ||
            Math.Abs(bounds.Right) > CanvasProjectionSafety.MaximumCoordinate || Math.Abs(bounds.Top) > CanvasProjectionSafety.MaximumCoordinate ||
            Math.Abs(bounds.Bottom) > CanvasProjectionSafety.MaximumCoordinate) return false;
        placement = new(anchor, height, linear); failure = BillboardPlacementFailure.None; return true;
    }

    private static bool TryScreenLinear(Affine2D screen, double scale, out Affine2D linear)
    {
        linear = new(screen.M11 / scale, screen.M12 / scale, screen.M21 / scale, screen.M22 / scale, 0, 0);
        return LayerPlacement.Finite(linear) && linear.TryInverse(out _);
    }

    private static bool FiniteBounds(RectD bounds) => double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top) &&
        double.IsFinite(bounds.Right) && double.IsFinite(bounds.Bottom) && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height);

    /// <summary>
    /// Converts an already logical draft to billboard placement using its actual screen transform.
    /// Geometry/style are untouched. Screen matrix must be affine and contain the selected depth scale.
    /// </summary>
    public static bool TryPlaceFromScreen(CanvasElement element, CanvasMapping mapping, BillboardDepthLine line, Projective2D localToScreen)
    {
        if (mapping.ViewRoot is { } root)
        {
            var placed = mapping.ToRootElement(element);
            if (!TryPlaceFromScreen(placed, root, line with { Scale = line.Scale / mapping.ViewPlacement.ElevationScale }, localToScreen) ||
                !mapping.TryFromRootPlacement(new(placed.BillboardAnchor, placed.BillboardHeight, placed.Transform), out var source)) return false;
            element.Transform = source.Linear; element.ProjectivePlacement = null; element.IsBillboard = true;
            element.BillboardAnchor = source.Anchor; element.BillboardHeight = source.Height; return true;
        }
        if (!localToScreen.IsFinite || Math.Abs(localToScreen.M31) > 1e-10 || Math.Abs(localToScreen.M32) > 1e-10 ||
            Math.Abs(localToScreen.M33) < 1e-10 || !localToScreen.TryTransformPoint(ElementGeometry.LocalBounds(element).Center, out var center) ||
            !TryAnchor(mapping, line, center, out var anchor, out double height)) return false;
        double factor = 1 / localToScreen.M33;
        var screen = new Affine2D(localToScreen.M11 * factor, localToScreen.M12 * factor,
            localToScreen.M21 * factor, localToScreen.M22 * factor, 0, 0);
        if (!TryScreenLinear(screen, line.Scale, out var linear)) return false;
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
        if (mapping.ViewRoot is { } root)
        {
            if (!TryMoveAnchor(mapping.ToRootElement(element), root, localGrab, screenPointer, out var rootAnchor)) return false;
            anchor = mapping.FromRootPoint(rootAnchor); return double.IsFinite(anchor.X) && double.IsFinite(anchor.Y);
        }
        if (!element.IsBillboard || !mapping.TryGetDepthScale(element.BillboardAnchor, out double scale)) return false;
        var offset = element.BillboardSurface is { } surface ? surface.ContentPlacement.TransformPoint(localGrab) :
            Linear(element.Transform).TransformPoint(localGrab - ElementGeometry.LocalBounds(element).Center);
        offset = new(offset.X, offset.Y - element.BillboardHeight);
        var matrix = mapping.PlaneToScreen;
        double numerator = scale * matrix.Denominator(element.BillboardAnchor);
        var grabbedProjection = matrix with { M13 = matrix.M13 + numerator * offset.X, M23 = matrix.M23 + numerator * offset.Y };
        return grabbedProjection.TryInverse(out var inverse) && inverse.TryTransformPoint(screenPointer, out anchor) &&
            mapping.TryGetDepthScale(anchor, out _);
    }
}
