namespace FloatCanvas.Core;

/// <summary>New authored billboards. Existing placements and appearance-preserving conversions are never normalized here.</summary>
public static class BillboardCreationGeometry
{
    /// <summary>Retains logical geometry/style and solves the ground point with the given local reference under the pointer.</summary>
    public static bool TryPlaceLogical(CanvasElement content, CanvasMapping mapping, PointD localReference, PointD screenReference,
        out CanvasElement candidate, out BillboardPlacementFailure failure)
    {
        if (mapping.IsSurfaceView)
            return TryPlaceLogical(content, CanvasMapping.Create(new SceneDocument()), localReference, mapping.ScreenToPlane.TransformPoint(screenReference), out candidate, out failure);
        candidate = content.Clone();
        candidate.IsBillboard = true; candidate.ProjectivePlacement = null;
        candidate.Transform = content.Transform with { OffsetX = 0, OffsetY = 0 };
        if (mapping.ViewRoot is { } root)
        {
            if (!TryPlaceLogical(mapping.ToRootElement(candidate), root, localReference, screenReference, out var placed, out failure)) return false;
            if (!mapping.TryFromRootPlacement(new(placed.BillboardAnchor, placed.BillboardHeight, placed.Transform), out var source))
            { failure = BillboardPlacementFailure.InvalidScale; return false; }
            candidate.BillboardAnchor = source.Anchor; candidate.BillboardHeight = source.Height; candidate.Transform = source.Linear;
            return true;
        }
        failure = BillboardPlacementFailure.EmptyGeometry;
        var local = ElementGeometry.LocalBounds(candidate);
        var envelope = local.IsEmpty || candidate.Kind == ElementKind.Symbol ? ElementGeometry.StrokedLocalBounds(candidate) : local;
        if (envelope.IsEmpty || !Finite(envelope) || ElementGeometry.UsesPoints(content.Kind) && content.Points.Count == 0) return false;
        var linear = candidate.Transform;
        failure = BillboardPlacementFailure.InvalidScale;
        if (!LayerPlacement.Finite(linear) || !linear.TryInverse(out _) || !TryDepthNumerator(mapping.PlaneToScreen, out double numerator)) return false;
        var center = linear.TransformPoint(local.Center);
        double height = linear.TransformBounds(envelope).Bottom - center.Y;
        var offset = linear.TransformPoint(localReference) - center - new PointD(0, height);
        // s = numerator / H.denominator. Incorporating the reference offset in
        // H's numerators solves position and scale together, without iteration.
        var projection = mapping.PlaneToScreen;
        var referenceProjection = projection with { M13 = projection.M13 + numerator * offset.X, M23 = projection.M23 + numerator * offset.Y };
        failure = BillboardPlacementFailure.UnsafeGround;
        if (!referenceProjection.TryInverse(out var inverse) || !inverse.TryTransformPoint(screenReference, out var anchor) ||
            !mapping.TryGetDepthScale(anchor, out _)) return false;
        candidate.BillboardAnchor = anchor; candidate.BillboardHeight = height;
        failure = BillboardPlacementFailure.UnsafeContent;
        if (!mapping.IsElementSafe(candidate) || !mapping.ElementToScreen(candidate).TryTransformPoint(localReference, out var actual) ||
            actual.DistanceTo(screenReference) > .01) return false;
        failure = BillboardPlacementFailure.None;
        return true;
    }

    /// <summary>Fits a drawn screen contour by changing geometry only. Logical style lengths are not depth-compensated.</summary>
    public static bool TryFromScreenContour(CanvasElement content, CanvasMapping mapping, Projective2D localToScreen,
        out CanvasElement candidate, out BillboardPlacementFailure failure)
    {
        if (mapping.IsSurfaceView)
            return TryFromScreenContour(content, CanvasMapping.Create(new SceneDocument()), mapping.ScreenToPlane.Compose(localToScreen), out candidate, out failure);
        candidate = content.Clone();
        if (!BillboardGeometry.TryResolveScreenBottom(content, mapping, localToScreen, out var placement, out failure)) return false;
        var local = ElementGeometry.LocalBounds(content);
        if (local.IsEmpty)
        {
            // Only the geometric contour is inverse-scaled. The remaining logical
            // stroke/arrow extent below it determines the ground, including dots.
            double pad = ElementGeometry.StrokedLocalBounds(content).Bottom - local.Bottom;
            var screenBounds = localToScreen.TransformBounds(local);
            var support = new CanvasElement { Kind = ElementKind.Rectangle, Width = 1, Height = 2 * pad, Style = new() { StrokeWidth = 0 } };
            if (pad > 0 && (!TryPlaceLogical(support, mapping, new(.5, pad), new(screenBounds.Center.X, screenBounds.Bottom), out var degenerate, out failure) ||
                !BillboardGeometry.TryResolveScreenPlacement(content, mapping, localToScreen, mapping.ToScreen(degenerate.BillboardAnchor), out placement, out failure))) return false;
        }
        failure = BillboardPlacementFailure.InvalidScale;
        if (!mapping.TryGetDepthScale(placement.Anchor, out double depth)) return false;
        double denominator = localToScreen.M33;
        double x = Math.Sqrt(localToScreen.M11 * localToScreen.M11 + localToScreen.M21 * localToScreen.M21) / Math.Abs(denominator) / depth;
        double y = Math.Sqrt(localToScreen.M12 * localToScreen.M12 + localToScreen.M22 * localToScreen.M22) / Math.Abs(denominator) / depth;
        if (!double.IsFinite(x) || !double.IsFinite(y) || x <= 0 || y <= 0) return false;
        candidate.Width *= x; candidate.Height *= y;
        candidate.Points = new(content.Points.Select(point => new PointD(point.X * x, point.Y * y)));
        candidate.Transform = placement.Linear.Compose(Affine2D.Scale(1 / x, 1 / y));
        candidate.ProjectivePlacement = null; candidate.IsBillboard = true;
        candidate.BillboardAnchor = placement.Anchor; candidate.BillboardHeight = placement.Height;
        failure = BillboardPlacementFailure.UnsafeContent;
        if (!mapping.IsElementSafe(candidate)) return false;
        var actual = mapping.ElementToScreen(candidate);
        foreach (var point in new[] { new PointD(local.Left, local.Top), new PointD(local.Right, local.Bottom), local.Center })
            if (!actual.TryTransformPoint(new(point.X * x, point.Y * y), out var projected) ||
                !localToScreen.TryTransformPoint(point, out var expected) || projected.DistanceTo(expected) > .01) return false;
        failure = BillboardPlacementFailure.None;
        return true;
    }

    private static bool TryDepthNumerator(Projective2D matrix, out double numerator)
    {
        numerator = 0;
        double gradient = Math.Sqrt(matrix.M31 * matrix.M31 + matrix.M32 * matrix.M32);
        if (gradient > 1e-12 * Math.Abs(matrix.M33))
        {
            double x = -matrix.M32 / gradient, y = matrix.M31 / gradient;
            double a = matrix.M11 * x + matrix.M12 * y, b = matrix.M21 * x + matrix.M22 * y;
            numerator = Math.Sqrt(a * a + b * b);
        }
        else if (BillboardGeometry.TryDepthScale(matrix, default, out double scale)) numerator = scale * matrix.M33;
        return double.IsFinite(numerator) && numerator > 0;
    }
    private static bool Finite(RectD value) => double.IsFinite(value.Left) && double.IsFinite(value.Top) && double.IsFinite(value.Right) && double.IsFinite(value.Bottom);
}
