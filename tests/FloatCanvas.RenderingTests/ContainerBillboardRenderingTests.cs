using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class ContainerBillboardRenderingTests
{
    internal static void Run(Action<string, bool> check)
    {
        var a = new CanvasElement { Kind = ElementKind.Rectangle, Width = 120, Height = 80,
            Style = new() { Fill = 0xFFDD4411, Stroke = 0xFFFFFFFF, StrokeWidth = 4 } };
        var b = new CanvasElement { Kind = ElementKind.Ellipse, Width = 45, Height = 30, IsBillboard = true,
            BillboardAnchor = new(55, 55), BillboardHeight = 24, Style = new() { Fill = 0xFF3377CC, StrokeWidth = 3 } };
        var group = new CanvasGroup { Transform = Affine2D.Translation(220, 280), Billboard = new(new(60, 40), 40),
            OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [a.Id, b.Id] }; a.GroupId = b.GroupId = group.Id;
        var document = new SceneDocument { Viewport = new(0, 0, 600, 500), Elements = [a, b], Groups = [group], RootOrder = [group.Id] };
        document.Canvas.Grid.Visible = false;
        document.Canvas.Projection = new() { Enabled = true, Matrix = new(1, .1, 0, 0, 1, 0, 0, .001, 1),
            Quad = [new(0, 0), new(600, 0), new(600, 500), new(0, 500)] };
        using var renderer = new SceneRenderer();
        var mapping = CanvasMapping.Create(document); var anchor = new PointD(220, 280);
        mapping.TryGetDepthScale(anchor, out double scale); var screen = mapping.ToScreen(anchor);
        var outer = Projective2D.FromAffine(Affine2D.Translation(screen.X, screen.Y - 40 * scale)
            .Compose(Affine2D.Scale(scale, scale)).Compose(Affine2D.Translation(-60, -40)));
        var oracle = new SceneDocument { Viewport = document.Viewport };
        oracle.Canvas.Grid.Visible = false;
        foreach (var source in new[] { a, b })
        {
            var copy = source.Clone(); copy.GroupId = null; copy.IsBillboard = false; copy.Transform = Affine2D.Identity;
            copy.ProjectivePlacement = outer.Compose(ElementGeometry.LocalToPlane(source)); oracle.Elements.Add(copy);
        }
        using var actual = SKBitmap.Decode(renderer.ExportPng(document, new(), true));
        using var expected = SKBitmap.Decode(renderer.ExportPng(oracle, new(), true));
        check("whole card vector geometry and strokes match an independent flat-matrix oracle", actual.Pixels.SequenceEqual(expected.Pixels) && actual.Pixels.Any(pixel => pixel.Alpha > 0));
        string output = Environment.GetEnvironmentVariable("FLOATCANVAS_CONTAINER_EVIDENCE") ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(output); File.WriteAllBytes(Path.Combine(output, "whole-card-vector.png"), renderer.ExportPng(document, new(), true));
        b.BillboardHeight += 15;
        using var raised = SKBitmap.Decode(renderer.ExportPng(document, new(), true));
        check("inner elevation changes actual pixels without moving the outer anchor", !actual.Pixels.SequenceEqual(raised.Pixels) && group.Transform.OffsetY == 280);
        var clone = document.Clone(true);
        using var cloned = SKBitmap.Decode(renderer.ExportPng(clone, new(), true));
        check("clipboard-style new identities preserve card pixels", raised.Pixels.SequenceEqual(cloned.Pixels));
    }
}
