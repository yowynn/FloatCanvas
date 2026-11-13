using FloatCanvas.Core;
using System.Text.Json;

internal static class BillboardLogicalPlacementTests
{
    internal static void Run(Action<bool, string> check)
    {
        var scene = new SceneDocument { Viewport = new(0, 0, 800, 700) };
        scene.Canvas.Projection = new() { Enabled = true, Matrix = new(1, 0, 0, 0, -.4, 700, 0, -.002, 1),
            Quad = [new(0, 0), new(800, 0), new(800, 700), new(0, 700)] };
        var content = new CanvasElement { Kind = ElementKind.RoundedRectangle, Width = 160, Height = 100, CornerRadius = 12,
            Style = new() { StrokeWidth = 4, FontSize = 24, StrokePattern = StrokePattern.Dashed } };
        string original = JsonSerializer.Serialize(content);
        foreach (var matrix in new[] { scene.Canvas.Projection.Matrix, scene.Canvas.Projection.Matrix.ScaleHomogeneous(17),
            Projective2D.FromAffine(Affine2D.Rotation(18)).Compose(scene.Canvas.Projection.Matrix),
            Projective2D.FromAffine(Affine2D.Scale(1.3, .8)), new Projective2D(1, 0, 0, 0, 1, 0, 0, 1e-14, 1) })
        {
            scene.Canvas.Projection.Matrix = matrix;
            foreach (var camera in new[] { CanvasViewTransform.Identity, new CanvasViewTransform(2, new(130, -60), new(20, 30)) })
            {
                var mapping = CanvasMapping.CreateViewing(scene, camera);
                var reference = camera.ToView(new(300, 500));
                check(BillboardCreationGeometry.TryPlaceLogical(content, mapping, new(80, 50), reference, out var item, out _), "logical placement solves projection/camera variant");
                var box = mapping.ProjectedBounds(item, false);
                check(box.Center.DistanceTo(reference) < .01 && new PointD(box.Center.X, box.Bottom).DistanceTo(mapping.ToScreen(item.BillboardAnchor)) < .01,
                    "joint solution reprojects center and bottom to within 0.01 DIP");
                check(item.Width == 160 && item.Height == 100 && item.Transform == Affine2D.Identity && item.CornerRadius == 12 && item.Style.StrokeWidth == 4,
                    "camera and perspective do not rewrite logical geometry or style");
            }
        }
        scene.Canvas.Projection.Matrix = new(1, 0, 0, 0, -.4, 700, 0, -.002, 1);
        var map = CanvasMapping.Create(scene);
        check(!BillboardCreationGeometry.TryPlaceLogical(content, map, new(80, 50), new(300, 100), out _, out var failure) && failure != BillboardPlacementFailure.None,
            "above-horizon center without a positive solution is rejected");
        var huge = content.Clone(); huge.Height = 2000;
        check(!BillboardCreationGeometry.TryPlaceLogical(huge, map, default, new(300, 500), out _, out _), "oversized top-left placement rejects a nonpositive solution");
        var root = CanvasMapping.CreateView(scene, new LayerPlacement(Affine2D.Translation(20, 30).Compose(Affine2D.Scale(2, 3)), 2),
            view: new CanvasViewTransform(1.5, new(40, 50)));
        check(BillboardCreationGeometry.TryPlaceLogical(content, root, new(80, 50), new(400, 600), out var nested, out _) &&
            root.ElementToScreen(nested).TransformPoint(new(80, 50)).DistanceTo(new(400, 600)) < .01 && nested.Transform == Affine2D.Identity,
            "shared preview placement applies parent conversion once");
        foreach (var points in new PointD[][] { [new(0, 0), new(160, 0)], [new(0, 0), new(0, 100)], [new(0, 0)] })
        {
            var stroke = new CanvasElement { Kind = ElementKind.Freehand, Points = new(points), Style = new() { StrokeWidth = 4 } };
            var screen = Projective2D.FromAffine(Affine2D.Translation(300, 350));
            check(BillboardCreationGeometry.TryFromScreenContour(stroke, map, screen, out var placed, out _), "degenerate stroke contour produces a safe logical placement");
            var projected = map.ElementToScreen(placed);
            check(points.Select((point, i) => screen.TransformPoint(point).DistanceTo(projected.TransformPoint(placed.Points[i]))).All(error => error < .01),
                "degenerate stroke points preserve the drawn screen contour");
            var envelope = map.ProjectedBounds(placed, true);
            check(Math.Abs(envelope.Bottom - map.ToScreen(placed.BillboardAnchor).Y) < .01 && placed.Style.StrokeWidth == 4 && placed.Transform == Affine2D.Identity,
                "degenerate stroke ground uses the actual logical pen envelope");
        }
        check(BillboardCreationGeometry.TryFromScreenContour(content, map, Projective2D.FromAffine(Affine2D.Translation(200, 350)), out var drag, out _) &&
            drag.CornerRadius == 12 && drag.Style.StrokeWidth == 4 && drag.Style.FontSize == 24 && drag.Style.StrokePattern == StrokePattern.Dashed,
            "contour fitting leaves all preset style lengths unscaled");
        double before = map.ElementToScreen(drag).M11 * drag.Style.StrokeWidth;
        ElementGeometry.ResizeShapeGeometry(drag, 1.5, .75);
        check(Math.Abs(map.ElementToScreen(drag).M11 * drag.Style.StrokeWidth - before) < 1e-7, "first dimension edit cannot reveal a hidden stroke compensation");
        check(JsonSerializer.Serialize(content) == original, "new creation never mutates its authored input");
    }
}
