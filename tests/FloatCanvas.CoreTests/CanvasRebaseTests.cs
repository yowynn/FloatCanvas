using FloatCanvas.Core;
using System.Text.Json;

internal static class CanvasRebaseTests
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (bool perspective in new[] { false, true })
        foreach (var origin in new[] { default(PointD), new PointD(37, -19) })
        {
            var header = new SubprojectCanvasSettings { Origin = origin };
            header.Canvas.Grid.FlatTranslation = new(23, -14);
            header.Canvas.Grid.Width = 3;
            header.Canvas.Projection = CanvasCalibration.CreateInitial(header.Viewport);
            header.Canvas.Projection.Enabled = perspective;
            if (perspective)
            {
                header.Canvas.Projection.Matrix = new(1.1, .1, 200, .05, .9, 100, .0002, .0007, 1);
                var b = CanvasProjectionGeometry.ResolvePlaneBounds(header.Canvas.Projection);
                header.Canvas.Projection.Quad = new[] { new PointD(b.Left, b.Top), new PointD(b.Right, b.Top), new PointD(b.Right, b.Bottom), new PointD(b.Left, b.Bottom) }
                    .Select(header.Canvas.Projection.Matrix.TransformPoint).ToList();
            }
            var group = new CanvasGroup { Transform = Affine2D.Translation(50, 30), ElevationScale = 1.2 };
            var leaf = new CanvasElement { Kind = ElementKind.Rectangle, Width = 40, Height = 20, GroupId = group.Id,
                Transform = Affine2D.Translation(20, 10), Locked = true, Visible = false };
            var billboard = new CanvasElement { Kind = ElementKind.Rectangle, Width = 30, Height = 70, IsBillboard = true,
                BillboardAnchor = new(300, 200), BillboardHeight = 25, Transform = Affine2D.Rotation(12) };
            var graph = new SceneNodeGraph { Groups = [group], Elements = [leaf, billboard] };
            var before = JsonSerializer.Serialize(graph); var beforeHeader = JsonSerializer.Serialize(header);
            var view = new CanvasViewTransform(.6, new(-120, 85));
            var result = CanvasRebase.Prepare(graph, header, view);
            check(before == JsonSerializer.Serialize(graph) && beforeHeader == JsonSerializer.Serialize(header), "rebase prepares a detached candidate without touching graph or header");
            var oldDoc = new SceneDocument(); header.ApplyTo(oldDoc);
            var newDoc = new SceneDocument(); result.Canvas.ApplyTo(newDoc);
            var oldMapping = CanvasMapping.CreateView(oldDoc, new(Affine2D.Translation(-origin.X, -origin.Y), 1));
            var newMapping = CanvasMapping.Create(newDoc);
            foreach (var id in new[] { leaf.Id, billboard.Id })
            {
                var oldLeaf = new SceneGraph(graph).ToWorld(graph.Elements.Single(item => item.Id == id));
                var newLeaf = new SceneGraph(result.Graph).ToWorld(result.Graph.Elements.Single(item => item.Id == id));
                foreach (var point in new[] { default(PointD), new PointD(10, 15), new PointD(30, 20) })
                {
                    var expected = view.ToView(oldMapping.ElementToScreen(oldLeaf).TransformPoint(point));
                    var actual = newMapping.ElementToScreen(newLeaf).TransformPoint(point);
                    check(expected.DistanceTo(actual) < 1e-7, "applying pan and uniform zoom preserves planar and billboard screen geometry including independent origin");
                }
            }
            check(result.Canvas.Canvas.Projection.Enabled == perspective && Math.Abs(result.Canvas.Canvas.Grid.Width - 1.8) < 1e-10,
                "rebase retains flat/perspective mode and scales entity grid stroke");
            check(result.Graph.Elements.Single(item => item.Id == leaf.Id).Transform == leaf.Transform &&
                result.Graph.Elements.Single(item => item.Id == leaf.Id).Locked && !result.Graph.Elements.Single(item => item.Id == leaf.Id).Visible,
                "nested geometry transforms only through its root and retains hidden/locked flags");
        }
    }
}
