using FloatCanvas.Core;
using System.Text.Json;

internal static class CanvasProjectionTests
{
    public static void Run(Action<bool, string> check)
    {
        void Near(double actual, double expected, string message) => check(Math.Abs(actual - expected) < 1e-7, $"{message}: {actual} / {expected}");
        var initial = CanvasCalibration.CreateInitial(new(-400, 100, 1200, 800), 8, 6);
        Near(initial.CellSize, 80, "initial square cells fit the centered 60 percent region");
        check(initial.Origin == new PointD(-120, 260) && initial.Quad.Count == 4 && !initial.Enabled, "initial reference uses negative desktop coordinates");
        check(CanvasCalibration.TryCalibrate(initial, initial.Quad, new(-400, 100, 1200, 800), out var identity, out _), "identity calibration accepted");
        Near(identity.Matrix.TransformPoint(new(-300, 170)).DistanceTo(new(-300, 170)), 0, "identity calibration retains screen plane");
        var document = new SceneDocument { Viewport = new(-250, -180, 500, 500) };
        string before = JsonSerializer.Serialize(document);
        _ = CanvasMapping.Create(document);
        check(before == JsonSerializer.Serialize(document), "default mapping initialization is read-only");
        var reference = CanvasCalibration.CreateInitial(document.Viewport, 4, 5);
        PointD[] quad = [new(-100, -100), new(100, -100), new(200, 200), new(-200, 200)];
        check(CanvasCalibration.TryCalibrate(reference, quad, document.Viewport, out var calibrated, out _), "trapezoid calibration accepted");
        Near(calibrated.CellSize, 100, "equal-depth normalization retains lower edge 1:1 for nonsquare counts");
        document.Canvas.Projection = calibrated;
        var mapping = CanvasMapping.Create(document);
        PointD[] planeCorners = [calibrated.Origin, calibrated.Origin + new PointD(400, 0), calibrated.Origin + new PointD(400, 500), calibrated.Origin + new PointD(0, 500)];
        for (int i = 0; i < 4; i++) Near(mapping.ToScreen(planeCorners[i]).DistanceTo(quad[i]), 0, "four-point correspondence");
        for (int row = -1; row <= 6; row++)
            for (int column = -1; column <= 5; column++)
            {
                var plane = calibrated.Origin + new PointD(column * 60, row * 60);
                var screen = mapping.ToScreen(plane);
                check(mapping.TryToPlane(screen, out var restored), "inverse accepts points outside reference grid");
                Near(restored.DistanceTo(plane), 0, "projective forward/inverse roundtrip");
            }
        var lower = calibrated.Origin + new PointD(100, 500);
        Near(mapping.ToScreen(lower + new PointD(50, 0)).DistanceTo(mapping.ToScreen(lower)), 50, "near edge fixed lengths are one-to-one");
        Near(mapping.ToScreen(calibrated.Origin + new PointD(50, 0)).DistanceTo(mapping.ToScreen(calibrated.Origin)), 25, "far edge fixed lengths shrink");
        check(mapping.ToScreen(lower + new PointD(50, 50)).DistanceTo(mapping.ToScreen(lower + new PointD(0, 50))) > 50, "nearward extension grows outside calibration box");
        check(!CanvasCalibration.TryCalibrate(reference, [quad[0], quad[2], quad[1], quad[3]], document.Viewport, out _, out _), "crossed quad rejected");
        check(!CanvasCalibration.TryCalibrate(reference, [quad[0], quad[1], new(0, 0), quad[3]], document.Viewport, out _, out _), "concave quad rejected");
        check(!CanvasCalibration.TryCalibrate(reference, quad, new(-250, -500, 500, 900), out _, out _), "horizon entering viewport rejected");
        check(!new Projective2D(1, 0, 0, 0, 1, 0, 0, 0, 0).TryInverse(out _), "singular homography rejected");
        var pole = new Projective2D(1, 0, 0, 0, 1, 0, 1, 0, 1);
        check(!pole.TryTransformPoint(new(-1, 3), out _) && !pole.TryTransformBounds(new(-2, 0, 4, 4), out _), "points and bounds crossing infinity are rejected");
        foreach (double threshold in new[] { .005, .02 })
        {
            CanvasProjectionSettings At(double depth)
            {
                var grid = new Projective2D(100, 0, 0, 0, 100, 0, depth / 8, 0, 1);
                PointD[] points = [grid.TransformPoint(new(0, 0)), grid.TransformPoint(new(8, 0)), grid.TransformPoint(new(8, 6)), grid.TransformPoint(new(0, 6))];
                check(CanvasCalibration.TryCalibrate(CanvasCalibration.CreateInitial(new(0, 0, 1000, 800)), points, new(0, 0, 1000, 800), out var value, out _), "near-affine calibration remains finite");
                return value;
            }
            check(Math.Abs(At(threshold - 1e-7).CellSize - At(threshold + 1e-7).CellSize) < .001, "scale continuous across smoothstep boundary");
        }
        var placement = new Projective2D(1, .1, 20, .05, 1, 30, .001, .0005, 1);
        var element = new CanvasElement { Kind = ElementKind.Rectangle, Width = 50, Height = 30, ProjectivePlacement = placement,
            Transform = Affine2D.Translation(25, 15), Style = new() { Fill = 0xFF00FF00, StrokeWidth = 0 } };
        var localPoint = new PointD(20, 10);
        var planePoint = element.Transform.TransformPoint(placement.TransformPoint(localPoint));
        Near(ElementGeometry.LocalToPlane(element).TransformPoint(localPoint).DistanceTo(planePoint), 0, "A after P composition order");
        check(ElementGeometry.HitTest(element, planePoint, 0), "plane hit includes projective placement");
        check(ElementGeometry.HitTestProjected(element, mapping.ToScreen(planePoint), mapping.PlaneToScreen, 0), "screen hit includes H A P");
        check(!ElementGeometry.HitTestProjected(element, mapping.ToScreen(ElementGeometry.LocalToPlane(element).TransformPoint(new(70, 10))), mapping.PlaneToScreen, 0), "projected fill excludes outside point");
        var edge = mapping.ElementToScreen(element).TransformPoint(new(25, 0));
        check(ElementGeometry.HitTestProjected(element, edge + new PointD(0, -2), mapping.PlaneToScreen, 4), "projected edge tolerance stays in screen pixels");
        var line = new CanvasElement { Kind = ElementKind.Line, Points = [new(0, 0), new(50, 0)], Transform = Affine2D.Translation(0, 30), ProjectivePlacement = placement, Style = new() { StrokeWidth = 4 } };
        check(ElementGeometry.HitTestProjected(line, mapping.ElementToScreen(line).TransformPoint(new(25, 1)), mapping.PlaneToScreen, 0), "projected stroke retains local width");
        document.Elements.Add(element); document.Background = element.Clone(true); document.Background.Kind = ElementKind.Image;
        var clone = document.Clone(); clone.Canvas.Projection.Quad[0] = new(999, 999); clone.Canvas.Grid.Visible = true;
        check(document.Canvas.Projection.Quad[0] == quad[0] && !document.Canvas.Grid.Visible && clone.Background!.ProjectivePlacement == placement,
            "document clone owns canvas metadata and preserves background placement");
        var session = new EditorSession(document);
        session.Execute("canvas", d => { d.Canvas.BackgroundColor = 0xFF123456; d.Canvas.Grid.Visible = true; });
        check(session.Undo() && session.Document.Canvas.BackgroundColor == 0 && !session.Document.Canvas.Grid.Visible, "canvas settings undo atomically");
        check(session.Redo() && session.Document.Canvas.BackgroundColor == 0xFF123456 && session.Document.Canvas.Grid.Visible, "canvas settings redo atomically");
        check(!JsonSerializer.Serialize(document).Contains("IsFinite"), "computed projective properties excluded from JSON");
    }
}
