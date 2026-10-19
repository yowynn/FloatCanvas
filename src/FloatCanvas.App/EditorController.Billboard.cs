using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class EditorController
{
    private bool billboardMode;
    private BillboardDepthLine? billboardDepth, billboardPreviewDepth;
    private bool billboardDrawing;
    private bool billboardDraftValid = true;
    private Affine2D billboardDraftTransform;
    private Action<BillboardDepthLine, PointD>? pendingBillboardInsertion;
    private SceneDocument? pendingBillboardDocument;
    private long pendingBillboardRevision;
    private Projective2D pendingBillboardProjection;
    internal bool HasPendingBillboardInsertion => pendingBillboardInsertion is not null;
    private Dictionary<Guid, CanvasElement> billboardSources = [];
    private PointD? billboardMoveDelta;
    public bool BillboardMode
    {
        get => billboardMode && Session.Document.Canvas.Projection.Enabled;
        set { Cancel(); billboardMode = value && Session.Document.Canvas.Projection.Enabled; if (billboardMode) Message?.Invoke(L.Get("Billboard.ChooseDepth")); Changed?.Invoke(); }
    }
    internal BillboardDepthLine? ActiveBillboardDepthLine => billboardDepth;
    internal BillboardDepthLine? VisibleBillboardDepthLine => billboardDepth ?? billboardPreviewDepth;
    internal (PointD Center, PointD Anchor)? BillboardSelectionGuide
    {
        get
        {
            if (!Session.Document.Canvas.Projection.Enabled) return null;
            var elements = Session.SelectedElements().Where(e => e.Visible && !e.Locked).ToArray();
            if (elements is not [var item] || !item.IsBillboard) return null;
            return BillboardGuide(item);
        }
    }
    internal (PointD Center, PointD Anchor)? BillboardDrawingGuide
    {
        get
        {
            if (drawing is { IsBillboard: true } item) return BillboardGuide(item);
            if (CapturePreview is { } capture && billboardDepth is { } depth &&
                depth.TryVerticalIntersection(capture.Bounds.Center.X, out var ground)) return (capture.Bounds.Center, ground);
            if (gesture == "desktop-effect" && Marquee is { } region && billboardDepth is { } effectDepth)
            {
                var center = region.Center * effectDepth.Scale;
                if (effectDepth.TryVerticalIntersection(center.X, out var effectGround)) return (center, effectGround);
            }
            return null;
        }
    }
    private (PointD Center, PointD Anchor)? BillboardGuide(CanvasElement item)
    {
        var mapping = CanvasMapping.Create(Session.Document);
        if (!mapping.IsVisible(item.BillboardAnchor)) return null;
        var center = mapping.ElementToScreen(item).TransformPoint(ElementGeometry.LocalBounds(item).Center);
        return (center, mapping.ToScreen(item.BillboardAnchor));
    }
    internal void ClearBillboardDepth()
    {
        billboardDepth = billboardPreviewDepth = null;
        billboardDrawing = false;
    }
    internal bool BeginBillboardInsertion(Action<BillboardDepthLine, PointD> insert)
    {
        if (!BillboardMode) return false;
        Cancel(); pendingBillboardInsertion = insert;
        pendingBillboardDocument = Session.Document; pendingBillboardRevision = Session.SceneRevision;
        pendingBillboardProjection = Session.Document.Canvas.Projection.Matrix;
        Message?.Invoke(L.Get("Billboard.ChooseDepth")); Changed?.Invoke(); return true;
    }
    private bool HandleBillboardDepthInput(OverlayPointerEvent e)
    {
        if (pendingBillboardInsertion is not null && (!ReferenceEquals(pendingBillboardDocument, Session.Document) ||
            pendingBillboardRevision != Session.SceneRevision || pendingBillboardProjection != Session.Document.Canvas.Projection.Matrix))
        { Cancel(); return true; }
        if (!Session.Document.Canvas.Projection.Enabled)
        { billboardMode = false; ClearBillboardDepth(); pendingBillboardInsertion = null; return false; }
        if (!BillboardMode || gesture is not null || !(IsBillboardCreationTool || pendingBillboardInsertion is not null)) return false;
        var mapping = CanvasMapping.Create(Session.Document);
        bool selectedHit = pendingBillboardInsertion is null && billboardDepth is null &&
            (e.Modifiers & OverlayModifiers.Control) == 0 && Session.Selection.Count > 0 &&
            mapping.TryToPlane(pointerScreen, out var selectionPlane) && IsWithinCurrentSelection(selectionPlane);
        bool existingText = Tool == EditorTool.Text && HitTestScreen(pointerScreen, 4) is { Kind: ElementKind.Text } &&
            (e.Modifiers & OverlayModifiers.Control) == 0;
        if (selectedHit || existingText) { billboardPreviewDepth = null; return false; }
        if (billboardDepth is null)
        {
            billboardPreviewDepth = mapping.TryGetEqualDepthLine(pointerScreen, out var line) ? line : null;
            if (e.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick && e.Button == OverlayMouseButton.Left)
            {
                if (billboardPreviewDepth is { } valid)
                { billboardDepth = valid; Session.SetSelection([]); Message?.Invoke(L.Get("Billboard.DrawAfterDepth")); }
                else Message?.Invoke(L.Get("Billboard.InvalidDepth"));
                Changed?.Invoke(); return true;
            }
            if (e.Kind == OverlayPointerKind.Move) { Changed?.Invoke(); return true; }
            return e.Kind == OverlayPointerKind.Up;
        }
        if (pendingBillboardInsertion is { } insert &&
            e.Kind is OverlayPointerKind.Down or OverlayPointerKind.DoubleClick && e.Button == OverlayMouseButton.Left)
        {
            var fixedDepth = billboardDepth.Value;
            if (!mapping.TryGetBillboardAnchor(fixedDepth, pointerScreen, out _, out _))
            { Message?.Invoke(L.Get("Billboard.InvalidDepth")); return true; }
            pendingBillboardInsertion = null; ClearBillboardDepth(); insert(fixedDepth, pointerScreen); return true;
        }
        return false;
    }
    private PointD BillboardInput(PointD screen) => billboardDepth is { } line ? screen * (1 / line.Scale) : screen;
    private bool UsesBillboardDrawingInput => billboardDrawing || gesture == "desktop-effect" && billboardDepth is not null;
    private bool IsBillboardCreationTool => IsCreationTool(Tool) ||
        Tool is EditorTool.Blur or EditorTool.Pixelate && (Session.Document.Kind == DocumentKind.LiveOverlay ||
            !Session.SelectedElements().Any(e => e.Kind == ElementKind.Image) && Session.Document.Background is null);
    private void BeginBillboardDrawing()
    {
        billboardDrawing = BillboardMode && billboardDepth is not null;
        billboardDraftValid = true;
        if (billboardDrawing && drawing is not null) RefreshBillboardDrawing();
    }
    private void PrepareBillboardDrawing()
    {
        if (!billboardDrawing || drawing is null) return;
        drawing.IsBillboard = false; drawing.Transform = billboardDraftTransform; drawing.Visible = true;
    }
    private bool RefreshBillboardDrawing()
    {
        if (!billboardDrawing || drawing is null || billboardDepth is not { } line) return true;
        billboardDraftTransform = drawing.Transform;
        var mapping = CanvasMapping.Create(Session.Document);
        var screen = Projective2D.FromAffine(Affine2D.Scale(line.Scale, line.Scale)).Compose(ElementGeometry.LocalToPlane(drawing));
        bool valid = BillboardGeometry.TryPlaceFromScreen(drawing, mapping, line, screen);
        billboardDraftValid = valid; drawing.Visible = valid;
        if (!valid) Message?.Invoke(L.Get("Billboard.InvalidDepth"));
        return valid;
    }
    private static PointD ConstrainMove(PointD delta, OverlayModifiers modifiers) => (modifiers & OverlayModifiers.Shift) == 0 ? delta :
        Math.Abs(delta.X) >= Math.Abs(delta.Y) ? new(delta.X, 0) : new(0, delta.Y);
    private bool TryStartBillboardHeight()
    {
        if (BillboardSelectionGuide is not { } guide || pointerScreen.DistanceTo(guide.Center) >= 12) return false;
        billboardSources = Session.SelectedElements().ToDictionary(e => e.Id, e => e.Clone());
        transforms = billboardSources.ToDictionary(e => e.Key, e => e.Value.Transform);
        gesture = "billboard-height"; Session.BeginTransaction(L.Get("Editor.History.Transform")); return true;
    }
    private bool MoveBillboardSelection(PointD p, OverlayModifiers modifiers)
    {
        if (gesture == "billboard-height")
        {
            var mapping = CanvasMapping.Create(Session.Document);
            foreach (var item in Session.SelectedElements())
                if (billboardSources.TryGetValue(item.Id, out var source) && mapping.TryGetDepthScale(source.BillboardAnchor, out var scale))
                    item.BillboardHeight = source.BillboardHeight - (pointerScreen.Y - startScreen.Y) / scale;
            Session.NotifyChanged(); return true;
        }
        if (gesture is not ("move" or "rotate" or "resize") || billboardSources.Count == 0) return false;
        var selected = Session.SelectedElements().Where(e => !e.Locked).ToArray();
        bool mixed = selected.Any(e => !e.IsBillboard);
        var canvas = CanvasMapping.Create(Session.Document);
        billboardMoveDelta = null;
        if (gesture == "move")
        {
            var source = billboardSources.Values.FirstOrDefault(e => ElementGeometry.HitTestProjected(e, startScreen, canvas.PlaneToScreen, 4) ||
                ElementGeometry.PointInPolygon(startScreen, Corners(ElementGeometry.LocalBounds(e)).Select(canvas.ElementToScreen(e).TransformPoint).ToArray()));
            if (source is not null && canvas.ElementToScreen(source).TryInverse(out var inverse) &&
                inverse.TryTransformPoint(startScreen, out var grab) && BillboardGeometry.TryMoveAnchor(source, canvas, grab, pointerScreen, out var moved))
                billboardMoveDelta = ConstrainMove(moved - source.BillboardAnchor, modifiers);
            else billboardMoveDelta = ConstrainMove(p - start, modifiers);
            if (billboardSources.Values.Any(e => !canvas.TryGetDepthScale(e.BillboardAnchor + billboardMoveDelta.Value, out _))) return true;
        }
        if (selected.Length > 1 && (gesture == "resize" || gesture == "rotate" && !mixed))
        {
            // Validate the complete candidate group before changing any member. A screen
            // pointer can stay on the visible branch while a distant group anchor crosses
            // the horizon during rotation or scaling. Such a sample must leave the last
            // valid group intact, including surface members handled by the caller.
            var groupCenter = selectionBounds.Center;
            Affine2D layout, content;
            double heightScale = 1;
            if (gesture == "rotate")
            {
                double angle = (Math.Atan2(p.Y - groupCenter.Y, p.X - groupCenter.X) -
                    Math.Atan2(start.Y - groupCenter.Y, start.X - groupCenter.X)) * 180 / Math.PI;
                if ((modifiers & OverlayModifiers.Shift) != 0) angle = Math.Round(angle / 15) * 15;
                content = Affine2D.Rotation(angle); layout = Affine2D.Around(groupCenter, content);
            }
            else
            {
                var from = start - groupCenter; var to = p - groupCenter;
                double sx = Math.Abs(from.X) < 1 ? 1 : Math.Max(.02, to.X / from.X);
                double sy = Math.Abs(from.Y) < 1 ? 1 : Math.Max(.02, to.Y / from.Y);
                if (selected.All(ElementGeometry.PreservesAspect) ^ ((modifiers & OverlayModifiers.Shift) != 0)) sx = sy = UniformScale(from, to);
                content = Affine2D.Scale(sx, sy); layout = Affine2D.Around(groupCenter, content); heightScale = sy;
            }
            var candidates = new Dictionary<Guid, CanvasElement>();
            foreach (var source in billboardSources.Values)
            {
                var candidate = source.Clone();
                candidate.BillboardAnchor = layout.TransformPoint(source.BillboardAnchor);
                candidate.Transform = content.Compose(source.Transform);
                candidate.BillboardHeight = source.BillboardHeight * heightScale;
                var transform = candidate.Transform;
                if (!canvas.IsVisible(candidate.BillboardAnchor) || !canvas.TryGetDepthScale(candidate.BillboardAnchor, out _) ||
                    !double.IsFinite(candidate.BillboardHeight) ||
                    !new[] { transform.M11, transform.M12, transform.M21, transform.M22, transform.OffsetX, transform.OffsetY }.All(double.IsFinite) ||
                    !transform.TryInverse(out _) ||
                    !canvas.ElementToScreen(candidate).TryTransformBounds(ElementGeometry.StrokedLocalBounds(candidate), out _)) return true;
                candidates.Add(source.Id, candidate);
            }
            foreach (var item in selected.Where(e => e.IsBillboard))
            {
                var candidate = candidates[item.Id];
                item.BillboardAnchor = candidate.BillboardAnchor;
                item.Transform = candidate.Transform;
                item.BillboardHeight = candidate.BillboardHeight;
            }
            if (!mixed) { Session.NotifyChanged(); return true; }
            return false;
        }
        foreach (var item in selected.Where(e => e.IsBillboard))
        {
            if (!billboardSources.TryGetValue(item.Id, out var source)) continue;
            if (gesture == "move")
            {
                var candidate = source.BillboardAnchor + billboardMoveDelta!.Value;
                if (canvas.IsVisible(candidate) && canvas.TryGetDepthScale(candidate, out _)) item.BillboardAnchor = candidate;
            }
            else if (gesture == "rotate" && !mixed)
            {
                var center = canvas.ElementToScreen(source).TransformPoint(ElementGeometry.LocalBounds(source).Center);
                double angle = (Math.Atan2(pointerScreen.Y - center.Y, pointerScreen.X - center.X) -
                    Math.Atan2(startScreen.Y - center.Y, startScreen.X - center.X)) * 180 / Math.PI;
                if ((modifiers & OverlayModifiers.Shift) != 0) angle = Math.Round(angle / 15) * 15;
                item.Transform = Affine2D.Rotation(angle).Compose(source.Transform);
            }
            else if (gesture == "resize")
            {
                if (!canvas.ElementToScreen(source).TryInverse(out var inverse)) continue;
                var center = ElementGeometry.LocalBounds(source).Center;
                var from = inverse.TransformPoint(startScreen) - center;
                var to = inverse.TransformPoint(pointerScreen) - center;
                double sx = Math.Abs(from.X) < .001 ? 1 : Math.Max(.02, to.X / from.X);
                double sy = Math.Abs(from.Y) < .001 ? 1 : Math.Max(.02, to.Y / from.Y);
                if (ElementGeometry.PreservesAspect(source) ^ ((modifiers & OverlayModifiers.Shift) != 0)) sx = sy = UniformScale(from, to);
                if (source.Kind == ElementKind.Text && resizeTextRenderer is not null)
                    TextFrameLayout.Resize(item, source, sx, sy, resizeTextRenderer);
                else if (ElementGeometry.IsBoxShape(source.Kind))
                {
                    item.Width = source.Width; item.Height = source.Height; item.CornerRadius = source.CornerRadius; item.Transform = source.Transform;
                    ElementGeometry.ResizeShapeGeometry(item, sx, sy);
                }
                else item.Transform = source.Transform.Compose(Affine2D.Scale(sx, sy));
            }
        }
        if (!mixed) { Session.NotifyChanged(); return true; }
        return false;
    }
}
