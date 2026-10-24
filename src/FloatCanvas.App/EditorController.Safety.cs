using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class EditorController
{
    private bool strokeInterrupted;
    private RectD? strokeLocalBounds;
    private readonly HashSet<Guid> strokeFragmentIds = [];
    private bool IsPlanarStrokeDrawing => gesture == "draw" && !billboardDrawing &&
        drawing is { Kind: ElementKind.Freehand or ElementKind.Highlighter };

    private void SetPointerBlocked(bool blocked)
    {
        if (IsPointerBlocked == blocked) return;
        IsPointerBlocked = blocked;
        Changed?.Invoke();
    }

    private void RejectPointer(bool message = false)
    {
        SetPointerBlocked(true);
        if (message) Message?.Invoke(L.Get("Perspective.InputOutsidePlane"));
    }

    private bool IsDrawingSafe() => drawing is null ||
        CanvasMapping.Create(Session.Document).IsElementSafe(drawing);

    // A billboard's upper half can be above the ground horizon. Ground inversion
    // is therefore optional for selecting and operating its screen-facing content.
    private bool TryBillboardSelectionInput(out PointD plane)
    {
        var source = billboardSources.Values.FirstOrDefault();
        if (source is null && gesture == "endpoint" && drawing is { IsBillboard: true }) source = drawing;
        if (source is null && gesture is "image-crop" or "effect" && effectTarget is { IsBillboard: true }) source = effectTarget;
        if (source is null && gesture is null)
        {
            source = HitTestScreen(pointerScreen, 7);
            if (source?.IsBillboard != true)
            {
                source = null;
                var selected = Session.SelectedElements().Where(e => e.IsBillboard && e.Visible && !e.Locked).ToArray();
                if (selected.Length > 0 && IsWithinCurrentSelection()) source = selected[0];
            }
        }
        plane = source?.BillboardAnchor ?? default;
        return source is not null;
    }

    private void MoveSafely(PointD point, OverlayModifiers modifiers)
    {
        SetPointerBlocked(false);
        if (IsPlanarStrokeDrawing) { MoveStrokeSafely(point, modifiers); return; }
        var changed = Session.IsTransactionActive && gesture is "draw" or "endpoint" or "move" or "rotate" or "resize" or "billboard-height"
            ? Session.Document.Elements.Where(e => e == drawing || transforms.ContainsKey(e.Id)).Select(e =>
                (Item: e, State: new GeometrySnapshot(e, gesture == "draw", gesture == "endpoint" && e == drawing ? endpoint : -1))).ToArray()
            : [];
        Move(point, modifiers);
        var mapping = CanvasMapping.Create(Session.Document);
        if (!IsPointerBlocked && changed.All(pair => mapping.IsElementSafe(pair.Item))) return;
        foreach (var (item, state) in changed) state.Restore(item);
        if (changed.Length > 0) Session.NotifyChanged();
        RejectPointer();
    }

    // Transform gestures leave the point list untouched. Retain that list rather than
    // copying a potentially large stroke on every pointer sample. Endpoint adjustment
    // mutates one point in place; drawing is the only path requiring a list copy.
    private readonly struct GeometrySnapshot
    {
        private readonly Affine2D transform;
        private readonly Projective2D? placement;
        private readonly PointCollection points;
        private readonly double width, height, billboardHeight;
        private readonly double? cornerRadius;
        private readonly ElementStyle style;
        private readonly bool textFrameFixed, isBillboard, visible;
        private readonly PointD billboardAnchor, endpointPoint;
        private readonly int endpointIndex;

        internal GeometrySnapshot(CanvasElement element, bool copyPoints, int endpointIndex)
        {
            transform = element.Transform; placement = element.ProjectivePlacement;
            points = copyPoints ? [.. element.Points] : element.Points;
            width = element.Width; height = element.Height; cornerRadius = element.CornerRadius;
            style = element.Style.Clone(); textFrameFixed = element.TextFrameFixed;
            isBillboard = element.IsBillboard; billboardAnchor = element.BillboardAnchor;
            billboardHeight = element.BillboardHeight; visible = element.Visible;
            this.endpointIndex = endpointIndex;
            endpointPoint = endpointIndex >= 0 ? element.Points[endpointIndex] : default;
        }

        internal void Restore(CanvasElement item)
        {
            item.Transform = transform; item.ProjectivePlacement = placement;
            item.Points = points;
            if (endpointIndex >= 0) item.Points[endpointIndex] = endpointPoint;
            item.Width = width; item.Height = height; item.CornerRadius = cornerRadius;
            item.Style = style; item.TextFrameFixed = textFrameFixed; item.IsBillboard = isBillboard;
            item.BillboardAnchor = billboardAnchor; item.BillboardHeight = billboardHeight;
            item.Visible = visible;
        }
    }

    private void MoveStrokeSafely(PointD point, OverlayModifiers modifiers)
    {
        if (drawing is null) return;
        gestureModifiers = modifiers;
        var local = point - start;
        if (!IsStrokeCandidateSafe(point, out var bounds))
        { strokeInterrupted = true; RejectPointer(); return; }
        if (strokeInterrupted)
        {
            strokeFragmentIds.Add(drawing.Id);
            drawing = drawing.Clone(newId: true); drawing.Points = [local];
            Session.Document.Elements.Add(drawing); strokeFragmentIds.Add(drawing.Id);
            straightStrokeStart = (modifiers & OverlayModifiers.Shift) != 0 ? 0 : null;
            strokeInterrupted = false;
        }
        else UpdateStroke(drawing.Points, local, modifiers);
        strokeLocalBounds = bounds;
        Session.NotifyChanged();
    }

    private bool IsStrokeCandidateSafe(PointD point, out RectD bounds)
    {
        bounds = default;
        if (drawing is null) return false;
        var local = point - start;
        // Check the same complete stroked envelope before both pointer updates and
        // modifier-boundary appends. Cache accepted bounds instead of rescanning a long stroke.
        bounds = strokeInterrupted ? new RectD(local.X, local.Y, 0, 0) :
            RectD.Union(strokeLocalBounds ?? ElementGeometry.LocalBounds(drawing), new(local.X, local.Y, 0, 0));
        return CanvasMapping.Create(Session.Document).IsElementSafe(new CanvasElement
        { Kind = drawing.Kind, Transform = drawing.Transform, Style = drawing.Style, Points = Corners(bounds).ToList() });
    }

    private void FinishSafeStroke()
    {
        Session.Commit();
        Cancel();
    }
}
