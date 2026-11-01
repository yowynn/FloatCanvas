using FloatCanvas.Core;
using System.Text.Json;

internal static class CanvasViewMappingTests
{
    internal static void Run(Action<bool, string> check)
    {
        var placements = new[]
        {
            new LayerPlacement(Affine2D.Translation(170, 85), 1.7),
            new LayerPlacement(Affine2D.Translation(190, 90).Compose(Affine2D.Rotation(27)).Compose(Affine2D.Scale(1.8, .6)), 2.3),
            new LayerPlacement(Affine2D.Translation(420, 100).Compose(Affine2D.Rotation(-19)).Compose(Affine2D.Scale(-1.2, .8)), -.75)
        };
        foreach (bool perspective in new[] { false, true })
        foreach (var placement in placements) Verify(perspective, placement, check);
        bool rejected = false;
        try { _ = CanvasMapping.CreateView(new(), new(Affine2D.Scale(0, 1), 1)); } catch (ArgumentException) { rejected = true; }
        check(rejected, "view rejects a singular source placement instead of inventing inverse coordinates");
    }

    private static void Verify(bool perspective, LayerPlacement placement, Action<bool, string> check)
    {
        string label = $"view perspective={perspective} placement={placement}";
        var root = new SceneDocument { Viewport = new(-200, -100, 1600, 1200) };
        root.Canvas.Grid.FlatTranslation = new(53, -27);
        root.Canvas.Projection = new() { Enabled = perspective, Quad = [new(0, 0), new(1000, 0), new(1000, 800), new(0, 800)],
            Matrix = new(1.05, .04, 60, -.02, .98, 35, .00015, .0009, 1) };
        var source = new CanvasElement { Kind = ElementKind.Rectangle, Name = "Author rectangle", Width = 100, Height = 60,
            Transform = Affine2D.Translation(30, 40).Compose(Affine2D.Rotation(11)), Style = new() { Foreground = 0xff336699, StrokeWidth = 3 } };
        var billboard = new CanvasElement { Kind = ElementKind.Rectangle, Name = "Author billboard", Width = 100, Height = 60,
            IsBillboard = true, BillboardAnchor = new(70, 130), BillboardHeight = 23,
            Transform = Affine2D.Rotation(-17).Compose(Affine2D.Scale(.9, 1.2)), Style = new() { Foreground = 0xffaa5577, StrokeWidth = 4 } };
        var authored = new SceneDocument { Elements = [source, billboard] };
        string beforeRoot = Json(root), beforeSource = Json(authored);
        var baseMapping = CanvasMapping.Create(root); var view = CanvasMapping.CreateView(root, placement);
        PointD point = new(80, 160), expected = baseMapping.ToScreen(placement.PlaneTransform.TransformPoint(point));
        check(view.ToScreen(point).DistanceTo(expected) < 1e-7 && view.TryToPlane(expected, out var restored) && restored.DistanceTo(point) < 1e-7,
            label + " source-plane forward and inverse roundtrip include root flat translation or perspective");
        foreach (var element in authored.Elements)
        {
            var placed = Place(element, placement);
            check(Same(view.ElementToScreen(element), baseMapping.ElementToScreen(placed), element), label + " element projection equals authored placement into root");
            check(view.IsElementSafe(element) == baseMapping.IsElementSafe(placed) && Near(view.ProjectedBounds(element), baseMapping.ProjectedBounds(placed)),
                label + " safety and projected envelope equal the actual root geometry");
        }
        var rootBillboard = Place(billboard, placement);
        check(baseMapping.TryGetDepthScale(rootBillboard.BillboardAnchor, out double rootDepth) && view.TryGetDepthScale(billboard.BillboardAnchor, out double viewDepth) &&
            Math.Abs(viewDepth - rootDepth * placement.ElevationScale) < 1e-8, label + " logical height depth uses root anchor and independent elevation scale");
        var screen = view.ElementToScreen(billboard); var ground = view.ToScreen(billboard.BillboardAnchor);
        check(BillboardGeometry.TryResolveScreenPlacement(billboard, view, screen, ground, out var resolved, out var failure) && failure == BillboardPlacementFailure.None &&
            resolved.Anchor.DistanceTo(billboard.BillboardAnchor) < 1e-7 && Math.Abs(resolved.Height - billboard.BillboardHeight) < 1e-7 &&
            Same(Resolve(billboard, resolved), billboard), label + " screen placement returns source anchor height and linear transform");
        check(BillboardGeometry.TryResolveScreenBottom(billboard, view, screen, out var bottom, out failure) && failure == BillboardPlacementFailure.None &&
            Same(view.ElementToScreen(Resolve(billboard, bottom)), screen, billboard), label + " screen-bottom placement reconstructs the requested pixels in source coordinates");
        check(view.TryGetEqualDepthLine(ground, out var depthLine) && view.TryGetBillboardAnchor(depthLine, screen.TransformPoint(ElementGeometry.LocalBounds(billboard).Center), out var depthAnchor, out double depthHeight) &&
            depthAnchor.DistanceTo(billboard.BillboardAnchor) < 1e-7 && Math.Abs(depthHeight - billboard.BillboardHeight) < 1e-7,
            label + " equal-depth guide and height remain in source units");
        var created = source.Clone();
        check(BillboardGeometry.TryPlaceFromScreen(created, view, depthLine, screen) && Same(view.ElementToScreen(created), screen, billboard),
            label + " screen-first creation inverts both plane linear placement and height elevation");
        var grab = new PointD(84, 18); var pointer = screen.TransformPoint(grab);
        check(BillboardGeometry.TryMoveAnchor(billboard, view, grab, pointer, out var unchanged) && unchanged.DistanceTo(billboard.BillboardAnchor) < 1e-7,
            label + " grabbed source point never jumps at pointer down");
        var movedPointer = pointer + new PointD(21, -33);
        bool moved = BillboardGeometry.TryMoveAnchor(billboard, view, grab, movedPointer, out var anchor);
        var movedElement = billboard.Clone(); movedElement.BillboardAnchor = anchor;
        check(moved && view.ElementToScreen(movedElement).TransformPoint(grab).DistanceTo(movedPointer) < 1e-7 && movedElement.BillboardHeight == billboard.BillboardHeight,
            label + " dragging the same local point solves source anchor through root perspective");
        check(Json(root) == beforeRoot && Json(authored) == beforeSource, label + " mapping queries never change source geometry style canvas or identity");
    }

    private static CanvasElement Place(CanvasElement source, LayerPlacement placement) { var clone = source.Clone(); SceneGraph.ApplyPlacement(clone, placement); return clone; }
    private static CanvasElement Resolve(CanvasElement source, BillboardPlacement placement)
    { var result = source.Clone(); result.IsBillboard = true; result.BillboardAnchor = placement.Anchor; result.BillboardHeight = placement.Height; result.Transform = placement.Linear; result.ProjectivePlacement = null; return result; }
    private static bool Same(CanvasElement first, CanvasElement second) => Same(Projective2D.FromAffine(first.Transform), Projective2D.FromAffine(second.Transform), first);
    private static bool Same(Projective2D first, Projective2D second, CanvasElement geometry) => new[] { new PointD(0, 0), new PointD(geometry.Width, 0), new PointD(0, geometry.Height), new PointD(geometry.Width, geometry.Height) }
        .All(point => first.TransformPoint(point).DistanceTo(second.TransformPoint(point)) < 1e-7);
    private static bool Near(RectD first, RectD second) => Math.Abs(first.Left - second.Left) + Math.Abs(first.Top - second.Top) + Math.Abs(first.Width - second.Width) + Math.Abs(first.Height - second.Height) < 1e-7;
    private static string Json(object value) => JsonSerializer.Serialize(value);
}
