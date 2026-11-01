using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class EditorController
{
    private SelectionTransformSnapshot? nodeTransform;
    private Dictionary<Guid, CanvasElement> nodeOriginalElements = [];
    private readonly Dictionary<Guid, Guid?> drawingParents = [];

    private void InsertWorldDrawing()
    {
        if (drawing is null) return;
        AppearanceMigration.NormalizeElement(drawing);
        if (screenBillboardDrawing) return;
        new SceneGraph(Session.Document).InsertElement(drawing, Session.EditScopeId);
        drawingParents[drawing.Id] = Session.EditScopeId;
    }
    private void BeginWorldEndpoint(CanvasElement source)
    {
        drawingParents[source.Id] = source.GroupId;
        drawing = new SceneGraph(Session.Document).ToWorld(source);
    }
    private void PublishDrawing()
    {
        if (drawing is null || !drawingParents.TryGetValue(drawing.Id, out var parent)) return;
        var graph = new SceneGraph(Session.Document); var source = graph.FindElement(drawing.Id);
        if (source is null) return;
        var local = graph.ToLocal(drawing, parent);
        // Visibility and locking are source properties; resolving a parent's state must not bake it into its child.
        local.Visible = source.Visible; local.Locked = source.Locked;
        CopyGeometry(source, local);
    }
    private void NotifyGeometryChanged()
    {
        if (screenBillboardDrawing) { Changed?.Invoke(); return; }
        PublishDrawing(); Session.NotifyChanged();
    }
    private void CommitPointerEdit()
    {
        if (!OwnsPointerTransaction) return;
        if (screenBillboardDrawing) PublishScreenBillboard(); else PublishDrawing();
        Session.Commit();
    }
    private bool HasNodeSelection
    {
        get { var containers = Session.Document.Groups.Select(group => group.Id).Concat(Session.Document.Instances.Select(instance => instance.Id)).ToHashSet(); return Session.Selection.Any(containers.Contains); }
    }
    private bool UsesHierarchicalSelection => viewPlacement is not null || HasNodeSelection || Session.TransformIndividually || Session.SelectedElements().Any(element => element.GroupId is not null) ||
        Session.Selection.Count > 1 && Session.SelectedElements().Any(element => element.IsBillboard);

    private void PrepareNodeTransform()
    {
        nodeTransform = null; nodeOriginalElements.Clear();
        if (!UsesHierarchicalSelection || gesture is not ("move" or "rotate" or "resize")) return;
        nodeTransform = Session.CaptureSelectionTransform();
        nodeOriginalElements = Session.SelectedWorldElements(editableOnly: true).ToDictionary(element => element.Id, element => element);
    }
    private bool WriteWorldElement(CanvasElement candidate)
    {
        var graph = new SceneGraph(Session.Document); var source = graph.FindElement(candidate.Id);
        if (source is null || graph.IsLocked(source.Id)) return false;
        if (!CurrentCanvasMapping.IsElementSafe(candidate)) return false;
        var local = graph.ToLocal(candidate, source.GroupId);
        CopyGeometry(source, local); return true;
    }
    private static void CopyGeometry(CanvasElement target, CanvasElement source)
    {
        target.Width = source.Width; target.Height = source.Height; target.CornerRadius = source.CornerRadius;
        target.Points = source.Points.Snapshot(); target.TextFrameFixed = source.TextFrameFixed; target.Style = source.Style;
        target.Text = source.Text; target.Number = source.Number; target.NumberTextSizeMode = source.NumberTextSizeMode;
        target.ConstrainAspect = source.ConstrainAspect; EditorSession.CopyPlacement(target, source);
    }
    private bool MoveNodeSelection(PointD point, OverlayModifiers modifiers)
    {
        if (nodeTransform is null || gesture is not ("move" or "rotate" or "resize")) return false;
        var mapping = CurrentCanvasMapping;
        bool directLeaf = Session.Selection.Count == 1 && !HasNodeSelection;
        if (directLeaf && nodeOriginalElements.Values.Single() is { IsBillboard: true } billboard)
        {
            var candidate = billboard.Clone();
            if (gesture == "move")
            {
                if (!mapping.ElementToScreen(billboard).TryInverse(out var inverse) ||
                    !BillboardGeometry.TryMoveAnchor(billboard, mapping, inverse.TransformPoint(startScreen), pointerScreen, out var anchor)) { RejectPointer(); return true; }
                candidate.BillboardAnchor = billboard.BillboardAnchor + ConstrainViewMove(anchor - billboard.BillboardAnchor, modifiers);
            }
            else if (gesture == "rotate")
            {
                var center = mapping.ElementToScreen(billboard).TransformPoint(ElementGeometry.LocalBounds(billboard).Center);
                double angle = (Math.Atan2(pointerScreen.Y - center.Y, pointerScreen.X - center.X) - Math.Atan2(startScreen.Y - center.Y, startScreen.X - center.X)) * 180 / Math.PI;
                if ((modifiers & OverlayModifiers.Shift) != 0) angle = Math.Round(angle / 15) * 15;
                candidate.Transform = ViewDelta(Affine2D.Rotation(angle), linearOnly: true).Compose(billboard.Transform);
            }
            else
            {
                if (!mapping.ElementToScreen(billboard).TryInverse(out var inverse)) { RejectPointer(); return true; }
                var center = ElementGeometry.LocalBounds(billboard).Center;
                var from = inverse.TransformPoint(startScreen) - center; var to = inverse.TransformPoint(pointerScreen) - center;
                double sx = Math.Abs(from.X) < .001 ? 1 : Math.Max(.02, to.X / from.X);
                double sy = Math.Abs(from.Y) < .001 ? 1 : Math.Max(.02, to.Y / from.Y);
                if (ElementGeometry.PreservesAspect(billboard) ^ ((modifiers & OverlayModifiers.Shift) != 0)) sx = sy = UniformScale(from, to);
                ResizeWorldLeaf(candidate, billboard, sx, sy);
            }
            if (!WriteWorldElement(candidate)) RejectPointer(); else NotifyGeometryChanged();
            return true;
        }
        if (directLeaf && gesture == "resize" && nodeOriginalElements.Values.Single() is { } single &&
            (ElementGeometry.IsBoxShape(single.Kind) || single.Kind == ElementKind.Text))
        {
            if (!ElementGeometry.LocalToPlane(single).TryInverse(out var inverse)) return true;
            var center = ElementGeometry.LocalBounds(single).Center;
            var from = inverse.TransformPoint(start) - center; var to = inverse.TransformPoint(point) - center;
            double sx = Math.Abs(from.X) < .001 ? 1 : Math.Max(.02, to.X / from.X);
            double sy = Math.Abs(from.Y) < .001 ? 1 : Math.Max(.02, to.Y / from.Y);
            if (ElementGeometry.PreservesAspect(single) ^ ((modifiers & OverlayModifiers.Shift) != 0)) sx = sy = UniformScale(from, to);
            var candidate = single.Clone(); ResizeWorldLeaf(candidate, single, sx, sy);
            if (!WriteWorldElement(candidate)) RejectPointer(); else NotifyGeometryChanged();
            return true;
        }
        var pivot = selectionBounds.Center; Affine2D delta; double elevation = 1;
        if (gesture == "move")
        {
            var displacement = ConstrainViewMove(point - start, modifiers);
            var grabbed = nodeOriginalElements.Values.FirstOrDefault(element => element.IsBillboard &&
                ElementGeometry.PointInPolygon(startScreen, Corners(ElementGeometry.LocalBounds(element)).Select(mapping.ElementToScreen(element).TransformPoint).ToArray()));
            if (grabbed is not null && mapping.ElementToScreen(grabbed).TryInverse(out var inverse) &&
                BillboardGeometry.TryMoveAnchor(grabbed, mapping, inverse.TransformPoint(startScreen), pointerScreen, out var moved))
                displacement = ConstrainViewMove(moved - grabbed.BillboardAnchor, modifiers);
            delta = Affine2D.Translation(displacement.X, displacement.Y);
        }
        else if (gesture == "rotate")
        {
            delta = RotationDelta(pivot, start, point, modifiers);
        }
        else
        {
            var from = start - pivot; var to = point - pivot;
            double sx = Math.Abs(from.X) < 1 ? 1 : Math.Max(.02, to.X / from.X);
            double sy = Math.Abs(from.Y) < 1 ? 1 : Math.Max(.02, to.Y / from.Y);
            if ((HasNodeSelection || nodeOriginalElements.Values.All(ElementGeometry.PreservesAspect)) ^ ((modifiers & OverlayModifiers.Shift) != 0)) sx = sy = UniformScale(from, to);
            delta = Affine2D.Around(pivot, Affine2D.Scale(sx, sy)); elevation = sy;
        }
        if (!Session.TryApplySelectionTransform(nodeTransform, delta, elevation, displayMapping: CurrentCanvasMapping)) RejectPointer();
        return true;
    }
    private Affine2D ViewDelta(Affine2D delta, bool linearOnly = false)
    {
        if (viewPlacement is not { } placement) return delta;
        var transform = linearOnly ? placement.PlaneTransform with { OffsetX = 0, OffsetY = 0 } : placement.PlaneTransform;
        return transform.Inverse().Compose(delta).Compose(transform);
    }
    internal void TransformSelectionInCanvas(Affine2D delta) => Session.Execute(L.Get("History.TransformElements"), _ =>
    {
        if (!Session.TryApplySelectionTransform(Session.CaptureSelectionTransform(), ViewDelta(delta), displayMapping: CurrentCanvasMapping))
            throw new InvalidOperationException(L.Get("Perspective.InputOutsidePlane"));
    });
    private Affine2D RotationDelta(PointD pivot, PointD first, PointD last, OverlayModifiers modifiers)
    {
        var transform = viewPlacement?.PlaneTransform ?? Affine2D.Identity;
        var center = transform.TransformPoint(pivot); var from = transform.TransformPoint(first) - center; var to = transform.TransformPoint(last) - center;
        double angle = (Math.Atan2(to.Y, to.X) - Math.Atan2(from.Y, from.X)) * 180 / Math.PI;
        if ((modifiers & OverlayModifiers.Shift) != 0) angle = Math.Round(angle / 15) * 15;
        return ViewDelta(Affine2D.Around(center, Affine2D.Rotation(angle)));
    }
    private PointD ConstrainViewMove(PointD delta, OverlayModifiers modifiers)
    {
        if (viewPlacement is not { } placement) return ConstrainMove(delta, modifiers);
        var linear = placement.PlaneTransform with { OffsetX = 0, OffsetY = 0 };
        return linear.Inverse().TransformPoint(ConstrainMove(linear.TransformPoint(delta), modifiers));
    }
    private void ResizeWorldLeaf(CanvasElement target, CanvasElement original, double sx, double sy)
    {
        if (original.Kind == ElementKind.Text)
            TextFrameLayout.Resize(target, original, sx, sy, resizeTextRenderer ??= new());
        else if (ElementGeometry.IsBoxShape(original.Kind)) ElementGeometry.ResizeShapeGeometry(target, sx, sy);
        else target.Transform = original.Transform.Compose(Affine2D.Scale(sx, sy));
    }
}
