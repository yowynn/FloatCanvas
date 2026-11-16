using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class EditorController
{
    private SelectionTransformSnapshot? nodeTransform;
    private Dictionary<Guid, CanvasElement> nodeOriginalElements = [];
    private Dictionary<Guid, CanvasElement> nodeOriginalAuthoredElements = [];
    private CanvasMapping? nodeStartMapping;
    private readonly Dictionary<Guid, Guid?> drawingParents = [];

    private void InsertWorldDrawing()
    {
        if (drawing is null) return;
        AppearanceMigration.NormalizeElement(drawing);
        if (screenBillboardDrawing) return;
        new SceneGraph(Session.Document).InsertElement(drawing, Session.EditScopeId, rootCanvas: Session.ActiveRootCanvas);
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
        bool screenDraft = screenBillboardDrawing;
        if (screenDraft) PublishScreenBillboard(); else PublishDrawing();
        publishingScreenBillboard = screenDraft;
        try { Session.Commit(); }
        finally { publishingScreenBillboard = false; }
        if (screenDraft)
        {
            ClearBillboardDepth();
            ScreenBillboardCommitted?.Invoke();
        }
    }
    private bool HasNodeSelection
    {
        get { var containers = Session.Document.Groups.Select(group => group.Id).Concat(Session.Document.Instances.Select(instance => instance.Id)).ToHashSet(); return Session.Selection.Any(containers.Contains); }
    }
    private bool UsesHierarchicalSelection => viewPlacement is not null || HasNodeSelection || Session.TransformIndividually || Session.SelectedElements().Any(element => element.GroupId is not null) ||
        Session.Selection.Count > 1 && Session.SelectedElements().Any(element => element.IsBillboard);

    private void PrepareNodeTransform()
    {
        nodeTransform = null; nodeOriginalElements.Clear(); nodeOriginalAuthoredElements.Clear();
        if (gesture != "move" && (!UsesHierarchicalSelection || gesture is not ("rotate" or "resize"))) return;
        nodeTransform = Session.CaptureSelectionTransform();
        nodeOriginalElements = SelectedEditingWorldElements(editableOnly: true).ToDictionary(element => element.Id, element => element);
        nodeOriginalAuthoredElements = Session.Document.Elements.Where(element => nodeOriginalElements.ContainsKey(element.Id)).ToDictionary(element => element.Id, element => element.Clone());
        nodeStartMapping = CurrentCanvasMapping;
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
        if (directLeaf && nodeOriginalElements.Values.Single() is { BillboardSurface: not null } surfaceMember &&
            nodeOriginalAuthoredElements.TryGetValue(surfaceMember.Id, out var authored))
            return MoveSurfaceMember(surfaceMember, authored, modifiers);
        if (directLeaf && nodeOriginalElements.Values.Single() is { IsBillboard: true } billboard)
        {
            var candidate = billboard.Clone();
            if (gesture == "move")
            {
                if (!(nodeStartMapping ?? mapping).ElementToScreen(billboard).TryInverse(out var inverse) ||
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
                var center = ResizePivot(ElementGeometry.LocalBounds(billboard),inverse.TransformPoint(startScreen));
                var from = inverse.TransformPoint(startScreen) - center; var to = inverse.TransformPoint(pointerScreen) - center;
                double sx = Math.Abs(from.X) < .001 ? 1 : Math.Max(.02, to.X / from.X);
                double sy = Math.Abs(from.Y) < .001 ? 1 : Math.Max(.02, to.Y / from.Y);
                if (ElementGeometry.PreservesAspect(billboard) ^ ((modifiers & OverlayModifiers.Shift) != 0)) sx = sy = UniformScale(from, to);
                ResizeWorldLeaf(candidate, billboard, sx, sy);
                PreserveResizePivot(billboard,candidate,center);
            }
            if (!WriteWorldElement(candidate)) RejectPointer(); else NotifyGeometryChanged();
            return true;
        }
        if (directLeaf && gesture == "resize" && nodeOriginalElements.Values.Single() is { } single &&
            (ElementGeometry.IsBoxShape(single.Kind) || single.Kind is ElementKind.Text or ElementKind.Presentation))
        {
            if (!ElementGeometry.LocalToPlane(single).TryInverse(out var inverse)) return true;
            var center = ResizePivot(ElementGeometry.LocalBounds(single),inverse.TransformPoint(start));
            var from = inverse.TransformPoint(start) - center; var to = inverse.TransformPoint(point) - center;
            double sx = Math.Abs(from.X) < .001 ? 1 : Math.Max(.02, to.X / from.X);
            double sy = Math.Abs(from.Y) < .001 ? 1 : Math.Max(.02, to.Y / from.Y);
            if (ElementGeometry.PreservesAspect(single) ^ ((modifiers & OverlayModifiers.Shift) != 0)) sx = sy = UniformScale(from, to);
            var candidate = single.Clone(); ResizeWorldLeaf(candidate, single, sx, sy);
            PreserveResizePivot(single,candidate,center);
            if (!WriteWorldElement(candidate)) RejectPointer(); else NotifyGeometryChanged();
            return true;
        }
        var pivot = gesture == "resize" ? ResizePivot(selectionBounds,start) : selectionBounds.Center; Affine2D delta; double elevation = 1;
        if (gesture == "move")
        {
            var displacement = ConstrainViewMove(point - start, modifiers);
            var grabbed = nodeOriginalElements.Values.FirstOrDefault(element => element.IsBillboard &&
                ElementGeometry.PointInPolygon(startScreen, Corners(ElementGeometry.LocalBounds(element)).Select((nodeStartMapping ?? mapping).ElementToScreen(element).TransformPoint).ToArray()));
            if (grabbed is not null && (nodeStartMapping ?? mapping).ElementToScreen(grabbed).TryInverse(out var inverse) &&
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
    private bool MoveSurfaceMember(CanvasElement displayed, CanvasElement authored, OverlayModifiers modifiers)
    {
        var mapping = nodeStartMapping ?? CurrentCanvasMapping;
        if (!mapping.ElementToScreen(displayed).TryInverse(out var inverse)) { RejectPointer(); return true; }
        var first = inverse.TransformPoint(startScreen); var last = inverse.TransformPoint(pointerScreen);
        var candidate = authored.Clone(); var local = ElementGeometry.LocalToPlane(authored);
        if (gesture == "move")
        {
            var shift = local.TransformPoint(last) - local.TransformPoint(first);
            if ((modifiers & OverlayModifiers.Shift) != 0) shift = Math.Abs(shift.X) >= Math.Abs(shift.Y) ? new(shift.X, 0) : new(0, shift.Y);
            BillboardGeometry.ApplyPlaneTransform(candidate, Affine2D.Translation(shift.X, shift.Y));
        }
        else
        {
            var center = gesture == "resize" ? ResizePivot(ElementGeometry.LocalBounds(authored),first) : ElementGeometry.LocalBounds(authored).Center;
            var from = first - center; var to = last - center;
            if (gesture == "rotate")
            {
                double angle = (Math.Atan2(to.Y, to.X) - Math.Atan2(from.Y, from.X)) * 180 / Math.PI;
                if ((modifiers & OverlayModifiers.Shift) != 0) angle = Math.Round(angle / 15) * 15;
                if (authored.IsBillboard) candidate.Transform = Affine2D.Rotation(angle).Compose(authored.Transform);
                else BillboardGeometry.ApplyPlaneTransform(candidate, Affine2D.Around(local.TransformPoint(center), Affine2D.Rotation(angle)));
            }
            else
            {
                double sx = Math.Abs(from.X) < .001 ? 1 : Math.Max(.02, to.X / from.X);
                double sy = Math.Abs(from.Y) < .001 ? 1 : Math.Max(.02, to.Y / from.Y);
                if (ElementGeometry.PreservesAspect(authored) ^ ((modifiers & OverlayModifiers.Shift) != 0)) sx = sy = UniformScale(from, to);
                ResizeWorldLeaf(candidate, authored, sx, sy);
                PreserveResizePivot(authored,candidate,center);
            }
        }
        var graph = new SceneGraph(Session.Document);
        if (!CurrentCanvasMapping.IsElementSafe(graph.ScopeContainerPlacement(candidate.GroupId).Resolve(candidate))) RejectPointer();
        else { CopyGeometry(graph.FindElement(candidate.Id)!, candidate); Session.NotifyChanged(); }
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
        else if (original.Kind == ElementKind.Presentation) PresentationFrameLayout.Resize(target, original, sx, sy);
        else if (ElementGeometry.IsBoxShape(original.Kind)) ElementGeometry.ResizeShapeGeometry(target, sx, sy);
        else target.Transform = original.Transform.Compose(Affine2D.Scale(sx, sy));
    }
}
