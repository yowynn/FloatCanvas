using FloatCanvas.Core;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

internal sealed partial class EditorController
{
    private bool OpenSelectionTarget(PointD point)
    {
        Cancel();
        var hit=HitTestScreen(point,4);
        if(hit is null) return false;
        var node=ResolveHitSelectableNode(hit.Id);
        if(node is {} group && Session.Document.Groups.Any(item=>item.Id==group)) Session.EnterScope(group);
        else if(node is {} instance && Session.Document.Instances.Any(item=>item.Id==instance)) PrefabEditRequested?.Invoke(instance);
        else { Session.SetSelection([hit.Id]); PropertiesRequested?.Invoke(point); }
        return true;
    }
    internal void SelectContextTarget(PointD screen)
    {
        if(HitTestScreen(screen,7) is {} hit && ResolveHitSelectableNode(hit.Id) is {} node)
        {
            if (Session.Selection.Contains(node) ||
                ResolveHitSelectableNode(hit.Id, individual: true) is { } leaf && Session.Selection.Contains(leaf)) return;
            Session.SetSelection([node]);
        }
        else Session.SetSelection([]);
    }
    private PointD ResizePivot(RectD bounds, PointD grab) => centeredGesture ? bounds.Center :
        new(grab.X >= bounds.Center.X ? bounds.X : bounds.Right, grab.Y >= bounds.Center.Y ? bounds.Y : bounds.Bottom);

    // Keep the frame that was hit on pointer-down. Its axes, grab offset and fixed
    // corner must not drift as subsequent previews change the element or the view.
    private sealed record SingleLayerResize(CanvasElement Source, bool AfterProjection, Projective2D ScreenToFrame, PointD Grab, PointD Pivot)
    {
        internal bool TryScale(PointD pointer, bool uniform, out Affine2D scale)
        {
            scale = Affine2D.Identity;
            if (!ScreenToFrame.TryTransformPoint(pointer, out var position)) return false;
            if (position == Grab) return true;
            var from = Grab - Pivot; var to = position - Pivot;
            if (uniform)
            {
                double factor = UniformScale(from, to);
                scale = Affine2D.Scale(factor, factor);
            }
            else
            {
                double x = Math.Abs(from.X) < .001 ? 1 : Math.Max(.02, to.X / from.X);
                double y = Math.Abs(from.Y) < .001 ? 1 : Math.Max(.02, to.Y / from.Y);
                scale = Affine2D.Scale(x, y);
            }
            return double.IsFinite(scale.M11) && double.IsFinite(scale.M22);
        }
    }

    private SingleLayerResize? BeginSingleLayerResize(CanvasElement source)
    {
        var frame = SingleSelectionFrame(source, CurrentCanvasMapping);
        var screen = MapCorners(frame.Bounds, frame.ToScreen);
        if (screen.Count != 4 || !frame.ToScreen.TryInverse(out var inverse) || !inverse.TryTransformPoint(startScreen, out var grab)) return null;
        int corner = Enumerable.Range(0, 4).MinBy(index => screen[index].DistanceTo(startScreen));
        var pivot = centeredGesture ? frame.Bounds.Center : Corners(frame.Bounds)[(corner + 2) % 4];
        return new(source.Clone(), frame.AfterProjection, inverse, grab, pivot);
    }

    private bool TryResizeLeaf(CanvasElement target, CanvasElement source, OverlayModifiers modifiers)
    {
        bool uniform = ElementGeometry.PreservesAspect(source) ^ ((modifiers & OverlayModifiers.Shift) != 0);
        if (singleResize is not { } drag || !drag.TryScale(pointerScreen, uniform, out var scale)) return false;
        if (scale == Affine2D.Identity)
        {
            EditorSession.CopyPlacement(target, source);
            target.Width = source.Width; target.Height = source.Height; target.CornerRadius = source.CornerRadius;
            target.TextFrameFixed = source.TextFrameFixed; target.TextAutoWidth = source.TextAutoWidth;
            target.Style.FontSize = source.Style.FontSize;
            return true;
        }
        var aboutPivot = Affine2D.Around(drag.Pivot, scale);
        bool text = source.Kind == ElementKind.Text, presentation = source.Kind == ElementKind.Presentation;
        bool resizeShape = ElementGeometry.IsBoxShape(source.Kind) && source.ProjectivePlacement is null;
        if (!source.IsBillboard && !text && !presentation && !resizeShape)
        {
            // Keep existing rotation, reflection and any authored shear. For a
            // projective leaf the local resize precedes both stored placements.
            if (!drag.AfterProjection && source.ProjectivePlacement is { } placement)
                target.ProjectivePlacement = placement.Compose(Projective2D.FromAffine(aboutPivot));
            else target.Transform = source.Transform.Compose(aboutPivot);
            return true;
        }

        target.Width = source.Width; target.Height = source.Height; target.CornerRadius = source.CornerRadius;
        target.Transform = source.Transform; target.BillboardAnchor = source.BillboardAnchor;
        // Layout needs fresh dimensions and font metrics. Its provisional center
        // is aligned in the original projective placement after reflow.
        target.ProjectivePlacement = text || presentation ? null : source.ProjectivePlacement;
        if (text) TextFrameLayout.Resize(target, source, scale.M11, scale.M22, resizeTextRenderer ??= new());
        else if (presentation) PresentationFrameLayout.Resize(target, source, scale.M11, scale.M22);
        else if (resizeShape) ElementGeometry.ResizeShapeGeometry(target, scale.M11, scale.M22);
        else target.Transform = source.Transform.Compose(scale);

        var pivot = drag.Pivot;
        var resizedPivot = text || presentation || resizeShape
            ? new PointD(source.Width > 0 ? (pivot.X / source.Width) * target.Width : 0,
                source.Height > 0 ? (pivot.Y / source.Height) * target.Height : 0) : pivot;
        if (source.IsBillboard)
        {
            if (centeredGesture) return true;
            if (drag.Source.BillboardSurface is not null)
            {
                // The parent card already provides the projection and depth.
                target.BillboardAnchor += ElementGeometry.LocalToPlane(source).TransformPoint(pivot) - ElementGeometry.LocalToPlane(target).TransformPoint(resizedPivot);
                return true;
            }
            // Moving the ground anchor also changes depth scale. Solve for the
            // resized center, then compensate depth to keep both handles in place.
            var mapping = CurrentCanvasMapping;
            var center = ElementGeometry.LocalBounds(target).Center;
            var atOriginalDepth = mapping.ElementToScreen(target);
            var screenCenter = mapping.ElementToScreen(source).TransformPoint(pivot)
                + (atOriginalDepth.TransformPoint(center) - atOriginalDepth.TransformPoint(resizedPivot));
            if (!BillboardGeometry.TryMoveAnchor(target, mapping, center, screenCenter, out var anchor) ||
                !mapping.TryGetDepthScale(source.BillboardAnchor, out double before) ||
                !mapping.TryGetDepthScale(anchor, out double after)) return false;
            target.BillboardAnchor = anchor;
            target.Transform = Affine2D.Scale(before / after, before / after).Compose(target.Transform);
        }
        else if (source.ProjectivePlacement is { } projective)
        {
            // Reflow changed the text frame itself. Align corresponding points
            // in its own plane before projecting the new width and height.
            target.Transform = source.Transform;
            target.ProjectivePlacement = projective.Compose(Projective2D.FromAffine(Affine2D.Translation(pivot.X - resizedPivot.X, pivot.Y - resizedPivot.Y)));
        }
        else
        {
            var delta = source.Transform.TransformPoint(pivot) - target.Transform.TransformPoint(resizedPivot);
            target.Transform = Affine2D.Translation(delta.X, delta.Y).Compose(target.Transform);
        }
        return true;
    }
}
