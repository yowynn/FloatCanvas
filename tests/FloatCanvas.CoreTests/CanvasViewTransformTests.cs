using FloatCanvas.Core;

internal static class CanvasViewTransformTests
{
    internal static void Run(Action<bool, string> check)
    {
        var viewport = new RectD(-1920, 40, 1920, 1080);
        var small = new RectD(20, 30, 40, 20);
        var first = CanvasViewTransform.Fit(small, viewport);
        check(first.Zoom == 1 && Near(first.ToView(small.Center), viewport.Center), "first entry centers small content without enlarging");
        var explicitFit = CanvasViewTransform.Fit(small, viewport, allowEnlarge: true);
        check(explicitFit.Zoom > 1 && Near(explicitFit.ToView(small.Center), viewport.Center), "explicit fit can enlarge small content");
        var large = CanvasViewTransform.Fit(new(0, 0, 3840, 2160), new(0, 0, 1920, 1080), padding: 0);
        check(large.Zoom == .5 && Near(large.ToView(new(3840, 2160)), new(1920, 1080)), "large content fits at exact half scale");
        var anchor = new PointD(-1000, 400);
        var zoomed = first.ZoomAt(4, anchor);
        check(Near(zoomed.ToCanvas(anchor), first.ToCanvas(anchor)), "pointer-centered zoom retains the grabbed canvas point");
        var moved = zoomed.Pan(new(-200, 75));
        check(Near(moved.ToView(new(12, 34)), zoomed.ToView(new(12, 34)) + new PointD(-200, 75)), "panning is measured in view pixels");
        var random = new Random(41003);
        for (int i = 0; i < 200; i++)
        {
            var view = new CanvasViewTransform(.01 + random.NextDouble() * 63.99, new(random.NextDouble() * 8000 - 4000, random.NextDouble() * 8000 - 4000));
            var point = new PointD(random.NextDouble() * 8000 - 4000, random.NextDouble() * 8000 - 4000);
            check(Near(view.ToCanvas(view.ToView(point)), point), "view round trip remains accurate across zoom and negative monitor origins");
        }
        var observed = zoomed.ToCanvas(viewport.Center);
        var restored = CanvasViewTransform.Restore(observed, zoomed.Zoom, new(0, 0, 3840, 2160));
        check(Near(restored.ToCanvas(new(1920, 1080)), observed) && restored.Zoom == 4, "reopening on a different viewport restores the observation center and scale");
        check(first.ZoomAt(1000, anchor).Zoom == 64 && first.ZoomAt(.0001, anchor).Zoom == .01, "zoom limits clamp valid requests");
        check(!default(CanvasViewTransform).IsValid && !new CanvasViewTransform(double.NaN, default).IsValid, "zero and nonfinite transforms cannot become active");
        check(Throws(() => first.ZoomAt(-1, anchor)) && Throws(() => first.Pan(new(double.PositiveInfinity, 0))), "invalid input is rejected without corrupting the old view");
        check(first.Zoom == 1 && small == new RectD(20, 30, 40, 20), "view operations leave prior values and authored bounds untouched");
        RectD[] screens = [new(-640, 0, 640, 360), new(0, 0, 640, 360)];
        check(CanvasEdgePan.Velocity(new(-1, 180), screens) == default && CanvasEdgePan.Velocity(new(1, 180), screens) == default,
            "shared monitor borders never trigger edge panning");
        check(CanvasEdgePan.Velocity(new(-639, 1), screens) is { X: < 0, Y: < 0 }, "outer corners pan along both axes with negative desktop origins");
        check(CanvasEdgePan.Velocity(new(639, 180), screens).X > CanvasEdgePan.Velocity(new(620, 180), screens).X &&
            CanvasEdgePan.Velocity(new(320, 180), screens) == default, "edge speed increases toward the border and stops outside its activation band");
        foreach (bool perspective in new[] { false, true })
        foreach (var root in new[] { RootCanvasKind.Main, RootCanvasKind.Foreground, RootCanvasKind.Background })
        {
            var document = new SceneDocument();
            document.Canvas.Projection = CanvasCalibration.CreateInitial(document.Viewport);
            document.Canvas.Projection.Enabled = perspective;
            if (perspective) document.Canvas.Projection.Matrix = new(1.1, .05, 40, .1, .9, 60, .0001, .0003, 1);
            var camera = new CanvasViewTransform(2.4, new(123, -40));
            var original = CanvasMapping.Create(document, root);
            var displayed = CanvasMapping.CreateViewing(document, camera, root);
            var billboard = new CanvasElement { Kind = ElementKind.Rectangle, Width = 60, Height = 40,
                IsBillboard = true, BillboardAnchor = new(300, 200), BillboardHeight = 80 };
            foreach (var point in new[] { new PointD(10, 20), new PointD(300, 200) })
            {
                check(Near(displayed.ToScreen(point), camera.ToView(original.ToScreen(point))), "editor camera composes after each root's own projection");
                check(displayed.TryToPlane(displayed.ToScreen(point), out var recovered) && Near(recovered, point), "displayed input inverts projection and camera together");
                check(Near(displayed.ElementToScreen(billboard).TransformPoint(point), camera.ToView(original.ElementToScreen(billboard).TransformPoint(point))), "billboard anchors, body and elevation share one post-projection camera");
            }
            var placement = new LayerPlacement(Affine2D.Translation(100, 40).Compose(Affine2D.Rotation(20)), 1.3);
            var local = CanvasMapping.CreateView(document, placement, root, camera);
            var oldLocal = CanvasMapping.CreateView(document, placement, root);
            check(Near(local.ElementToScreen(billboard).TransformPoint(default), camera.ToView(oldLocal.ElementToScreen(billboard).TransformPoint(default))),
                "editing an occurrence retains the source-to-host placement underneath the camera");
            var grabbed = new PointD(18, 12);
            check(BillboardGeometry.TryMoveAnchor(billboard, local, grabbed, local.ElementToScreen(billboard).TransformPoint(grabbed), out var recoveredAnchor) &&
                Near(recoveredAnchor, billboard.BillboardAnchor), "billboard grab inversion remains stable through host placement and camera");
        }
    }
    private static bool Near(PointD a, PointD b) => a.DistanceTo(b) < 1e-7;
    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (ArgumentException) { return true; }
    }
}
