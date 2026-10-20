using FloatCanvas.Core;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

internal sealed partial class EditorController
{
    private void AddCapturePoint(PointD point)
    {
        if (capturePoints.Count < MaximumCapturePoints &&
            (capturePoints.Count == 0 || capturePoints[^1].DistanceTo(point) >= .75)) capturePoints.Add(point);
    }

    private void UpdateCapturePreview(PointD point, OverlayModifiers modifiers, bool includeHover = true)
    {
        IReadOnlyList<PointD> contour = Array.Empty<PointD>();
        RectD? planeBounds = null;
        Projective2D? projection = null;
        RectD bounds;
        if (CaptureShape is CaptureShape.Rectangle or CaptureShape.Ellipse)
        {
            var mapping = CanvasMapping.Create(Session.Document);
            if (ActiveBillboardDepthLine is null && mapping.PlaneToScreen != Projective2D.Identity)
            {
                if (!mapping.TryToPlane(start, out var planeStart) || !mapping.TryToPlane(point, out var planeEnd))
                { CapturePreview = null; RejectPointer(); Changed?.Invoke(); return; }
                if (ConstrainBox(modifiers)) planeEnd = SquareEnd(planeStart, planeEnd);
                var box = RectD.FromPoints(planeStart, planeEnd);
                if (!mapping.PlaneToScreen.TryTransformBounds(box, out _))
                { CapturePreview = null; RejectPointer(); Changed?.Invoke(); return; }
                var points = CaptureShape == CaptureShape.Rectangle ? Corners(box)
                    : Enumerable.Range(0, 128).Select(i =>
                    {
                        double angle = i * Math.PI * 2 / 128;
                        return new PointD(box.Center.X + Math.Cos(angle) * box.Width / 2,
                            box.Center.Y + Math.Sin(angle) * box.Height / 2);
                    }).ToArray();
                contour = Array.AsReadOnly(points.Select(mapping.ToScreen).ToArray());
                bounds = CaptureBounds(contour);
                planeBounds = box; projection = mapping.PlaneToScreen;
            }
            else
            {
                if (ConstrainBox(modifiers)) point=SquareEnd(start,point);
                bounds = RectD.FromPoints(start, point);
            }
        }
        else
        {
            if (CaptureShape == CaptureShape.FreehandLasso) UpdateStroke(capturePoints,point,modifiers);
            var points = capturePoints.ToList();
            if(CaptureShape==CaptureShape.PolygonLasso&&points.Count>0&&(modifiers&OverlayModifiers.Shift)!=0)point=EightDirectionEnd(points[^1],point);
            if (CaptureShape == CaptureShape.PolygonLasso && includeHover &&
                (points.Count == 0 || points[^1] != point)) points.Add(point);
            contour = Array.AsReadOnly(points.ToArray());
            bounds = CaptureBounds(points);
        }
        var safeMapping = CanvasMapping.Create(Session.Document);
        if (ActiveBillboardDepthLine is null &&
            !(contour.Count > 0 ? contour : Corners(bounds)).All(safeMapping.IsScreenSafe))
        { CapturePreview = null; RejectPointer(); Changed?.Invoke(); return; }
        SetPointerBlocked(false);
        CapturePreview = new(CaptureShape, CaptureSource, bounds, contour,
            CaptureBorderWidth * (ActiveBillboardDepthLine?.Scale ?? 1), CaptureBorderColor)
        { PlaneBounds = planeBounds, PlaneToScreen = projection, BillboardDepthLine = ActiveBillboardDepthLine };
        Changed?.Invoke();
    }

    private void CompleteCaptureSelection()
    {
        if (gesture == "capture-polygon")
            UpdateCapturePreview(capturePoints.LastOrDefault(), OverlayModifiers.None, includeHover: false);
        var selection = CapturePreview;
        bool invalidRegion = IsPointerBlocked;
        bool valid = !invalidRegion && selection is { Bounds.Width: >= 2, Bounds.Height: >= 2 } &&
            (selection.Shape is CaptureShape.Rectangle or CaptureShape.Ellipse || HasCaptureArea(selection.Points));
        // Finishing and cancellation both discard only transient geometry, without a document transaction.
        gesture = null; CapturePreview = null; capturePoints.Clear(); lastGestureMove = null;
        straightStrokeStart=null;gestureModifiers=OverlayModifiers.None;modifierTime=null;
        deselectOnClick = gestureMoved = false;
        ClearBillboardDepth();
        Changed?.Invoke();
        if (invalidRegion) RejectPointer(true);
        if (valid) CaptureSelectionCompleted?.Invoke(selection!);
    }

    private static RectD CaptureBounds(IReadOnlyList<PointD> points) => points.Count == 0 ? default :
        new(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X) - points.Min(p => p.X), points.Max(p => p.Y) - points.Min(p => p.Y));

    private static bool HasCaptureArea(IReadOnlyList<PointD> points)
    {
        if (points.Count < 3) return false;
        var first = points[0];
        var edge = points.Skip(1).FirstOrDefault(point => point.DistanceTo(first) >= .75) - first;
        // Signed polygon area would reject valid self-intersecting contours. Any non-collinear
        // vertex is enough here; the renderer applies the even-odd mask and checks visible pixels.
        return points.Any(point => Math.Abs(edge.X * (point.Y - first.Y) - edge.Y * (point.X - first.X)) >= 1);
    }

}
