using FloatCanvas.Core;
using System.Text.Json;

internal static class BillboardGeometryTests
{
    public static void Run(Action<bool, string> check)
    {
        void Near(double actual, double expected, string name) => check(Math.Abs(actual - expected) < 1e-7, $"{name}: {actual} / {expected}");
        var document = Scene(); var mapping = CanvasMapping.Create(document);
        var anchor = new PointD(280, 600);
        check(mapping.TryGetDepthScale(anchor, out double scale), "far billboard has a finite shared scale");
        Near(scale, .25, "global depth is one quarter at denominator four");
        check(BillboardGeometry.TryDepthScale(mapping.PlaneToScreen.ScaleHomogeneous(7), anchor, out double rescaled), "rescaled H accepted");
        Near(rescaled, scale, "depth scale independent of homogeneous matrix magnitude");
        check(mapping.TryGetEqualDepthLine(new(70, 150), out var line), "depth line selected from screen point");
        check(line.TryClip(document.Viewport, out var a, out var b) && Math.Abs(a.Y - 150) < 1e-7 && Math.Abs(b.Y - 150) < 1e-7, "line clipped to viewport");
        check(mapping.TryGetBillboardAnchor(line, new(70, 100), out var computed, out double height), "vertical line determines ground anchor");
        Near(computed.DistanceTo(anchor), 0, "anchor is inverse projection of ground intersection"); Near(height, 200, "height is logical distance");
        var element = new CanvasElement { Kind = ElementKind.Rectangle, Width = 400, Height = 400,
            Style = new() { Fill = 0xFF00FF00, StrokeWidth = 5 } };
        check(BillboardGeometry.TryPlaceFromScreen(element, mapping, line,
            Projective2D.FromAffine(Affine2D.Translation(20, 50).Compose(Affine2D.Scale(.25, .25)))), "logical draft placed on depth line");
        Near(element.Width, 400, "creation preserves logical width"); Near(element.Style.StrokeWidth, 5, "creation never backcalculates preset stroke");
        var screenBounds = mapping.ProjectedBounds(element, false);
        Near(screenBounds.Width, 100, "screen width projected from logical dimensions"); Near(screenBounds.Height, 100, "screen height projected uniformly");
        Near(screenBounds.Center.DistanceTo(new(70, 100)), 0, "screen center retained at creation");
        check(ElementGeometry.HitTestProjected(element, new(70, 100), mapping.PlaneToScreen, 0), "screen-facing fill hittest");
        check(!ElementGeometry.HitTestProjected(element, new(125, 100), mapping.PlaneToScreen, 0), "screen-facing hittest excludes outside");
        element.Style.Fill = 0;
        check(ElementGeometry.HitTestProjected(element, new(20.5, 100), mapping.PlaneToScreen, 0), "logical stroke 5 renders 1.25 physical pixels");
        check(ElementGeometry.HitTestProjected(element, new(21, 100), mapping.PlaneToScreen, 0), "inside stroke retains the full 1.25 physical pixels inward");
        check(!ElementGeometry.HitTestProjected(element, new(21.5, 100), mapping.PlaneToScreen, 0) &&
            !ElementGeometry.HitTestProjected(element, new(19.5, 100), mapping.PlaneToScreen, 0), "inside hit uses scaled width without extending outside the shape");
        element.Style.Fill = 0xFF00FF00;
        var original = element.Clone(); var grab = new PointD(350, 250); var pointer = mapping.ElementToScreen(element).TransformPoint(grab);
        check(BillboardGeometry.TryMoveAnchor(element, mapping, grab, pointer, out var unmoved), "grab inverse at pointer down");
        Near(unmoved.DistanceTo(element.BillboardAnchor), 0, "initial grab cannot jump anchor");
        var movedPointer = pointer + new PointD(10, -20);
        check(BillboardGeometry.TryMoveAnchor(element, mapping, grab, movedPointer, out var moved), "grab inverse after moving pointer");
        element.BillboardAnchor = moved;
        Near(mapping.ElementToScreen(element).TransformPoint(grab).DistanceTo(movedPointer), 0, "grabbed corner remains under pointer through depth changes");
        Near(element.Width, original.Width, "move preserves logical width"); Near(element.BillboardHeight, original.BillboardHeight, "move preserves height");
        element.BillboardHeight = -80;
        var center = mapping.ElementToScreen(element).TransformPoint(ElementGeometry.LocalBounds(element).Center);
        check(center.Y > mapping.ToScreen(element.BillboardAnchor).Y, "negative height places content below ground");
        document.Elements = [original]; document.Canvas.Projection.Enabled = false;
        var flat = CanvasMapping.Create(document).ProjectedBounds(original, false);
        Near(flat.Width, 400, "perspective off displays logical width"); Near(flat.Center.Y, original.BillboardAnchor.Y - original.BillboardHeight, "off uses logical height");
        check(original.IsBillboard && original.BillboardHeight == height, "perspective off retains data");
        var json = JsonSerializer.Serialize(original);
        check(JsonSerializer.Serialize(original.Clone()) == json, "clone preserves billboard fields exactly");
        check(!JsonSerializer.Deserialize<CanvasElement>("{\"Kind\":5,\"Width\":100,\"Height\":100}")!.IsBillboard, "old JSON defaults to planar");

        var session = new EditorSession(document); session.SetSelection([original.Id]); session.DuplicateSelection(11, 17);
        var copy = session.SelectedElements().Single();
        Near(copy.BillboardAnchor.DistanceTo(original.BillboardAnchor + new PointD(11, 17)), 0, "duplicate offsets ground anchor");
        Near(copy.BillboardHeight, original.BillboardHeight, "duplicate keeps height");
        check(session.Undo() && session.Redo() && session.SelectedElements().Single().BillboardAnchor == copy.BillboardAnchor, "undo redo retains billboard placement");
        var snapshot = session.CreateSelectionSnapshot(); var imported = new EditorSession();
        imported.InsertDocument(snapshot, session.Assets, Affine2D.Translation(200, 300));
        var inserted = imported.Document.Elements.Single();
        Near(inserted.BillboardAnchor.DistanceTo(snapshot.Elements[0].BillboardAnchor + new PointD(200, 300)), 0, "editable import moves anchor with content");
        Near(ElementGeometry.Bounds(snapshot.Elements[0]).Left, 0, "snapshot normalizes billboard content bounds");
        var capture = new SceneDocument { Elements = [original.Clone()] }.CreateCapture(new(70, 190, 450, 450), "fixture-background");
        check(capture.Elements.Single().IsBillboard && capture.Elements.Single().BillboardAnchor == original.BillboardAnchor - new PointD(70, 190), "logical frozen capture translates ground anchor with cloned content");
        var tilted = Scene();
        tilted.Canvas.Projection.Matrix = Projective2D.FromAffine(Affine2D.Rotation(23)).Compose(tilted.Canvas.Projection.Matrix);
        var tiltedMapping = CanvasMapping.Create(tilted);
        var screen = tiltedMapping.ToScreen(anchor);
        check(tiltedMapping.TryGetEqualDepthLine(screen, out var tiltedLine) && Math.Abs(tiltedLine.ScreenDirection.Y) > .2, "equal depth guide follows tilted canvas");
        check(tiltedMapping.TryGetBillboardAnchor(tiltedLine, screen + new PointD(30, -20), out var shifted, out _), "tilted line vertical intersection");
        tiltedMapping.TryGetDepthScale(shifted, out double shiftedScale); Near(shiftedScale, scale, "moving along tilted equal-depth line preserves scale");
        var vertical = new BillboardDepthLine(new(10, 10), new(0, 1), 1);
        check(!vertical.TryVerticalIntersection(10, out _), "parallel or coincident vertical depth line rejected");
        check(!mapping.TryGetDepthScale(new(0, -200), out _), "horizon anchor rejected");
        var flatMapping = CanvasMapping.Create(new());
        check(flatMapping.TryGetEqualDepthLine(new(30, 40), out var flatLine) && flatLine.ScreenDirection == new PointD(1, 0) && flatLine.Scale == 1, "no depth gradient uses horizontal unit-scale guide");
        var planar = new CanvasElement { Kind = ElementKind.Rectangle, Width = 60, Height = 20, Transform = Affine2D.Translation(40, 50).Compose(Affine2D.Rotation(30)) };
        var expectedAnchor = ElementGeometry.LocalToPlane(planar).TransformPoint(new(30, 10));
        check(BillboardGeometry.TrySetBillboard(planar, true, flatMapping) && planar.BillboardAnchor == expectedAnchor, "mode conversion retains logical center as anchor");
        check(BillboardGeometry.TrySetBillboard(planar, false, flatMapping) && !planar.IsBillboard && planar.Width == 60, "mode conversion retains editable dimensions");
    }

    public static SceneDocument Scene()
    {
        var matrix = new Projective2D(1, 0, 0, 0, 1, 0, 0, .005, 1);
        return new() { Viewport = new(0, 0, 180, 150), Canvas = new() { Projection = new() { Enabled = true, Columns = 100, Rows = 100, CellSize = 1,
            Matrix = matrix, Quad = [matrix.TransformPoint(new(0, 0)), matrix.TransformPoint(new(100, 0)), matrix.TransformPoint(new(100, 100)), matrix.TransformPoint(new(0, 100))] } } };
    }
}
