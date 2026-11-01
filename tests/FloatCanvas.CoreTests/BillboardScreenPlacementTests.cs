using System.Text.Json;
using FloatCanvas.Core;

internal static class BillboardScreenPlacementTests
{
    internal static void Run(Action<bool, string> check)
    {
        var matrix = new Projective2D(1, 0, 0, 0, -.4, 700, 0, -.002, 1);
        var scene = Scene(matrix); var mapping = CanvasMapping.Create(scene);
        var content = new CanvasElement { Kind = ElementKind.Rectangle, Width = 200, Height = 300,
            Transform = Affine2D.Translation(777, -888), Style = new() { Fill = 0xFF408020, StrokeWidth = 7 } };
        var screen = Projective2D.FromAffine(Affine2D.Translation(100, 100));
        string original = Json(content);
        check(!mapping.TryToPlane(new(100, 100), out _), "screen-first start point is above the ground horizon");
        check(BillboardGeometry.TryResolveScreenBottom(content, mapping, screen, out var placement, out var failure) &&
            failure == BillboardPlacementFailure.None, "screen-first solves a body above the horizon from its safe bottom");
        NearPoint(placement.Anchor, new(500, -750), "screen-first numerical ground anchor");
        Near(placement.Height, 375, "screen-first numerical height");
        Near(placement.Linear.M11, 2.5, "screen-first numerical inverse depth compensation");
        CheckScreen(content, placement, mapping, screen, "screen-first numerical screen affine");
        check(Json(content) == original, "successful screen-first solver does not modify source content or style");
        check(mapping.IsElementSafe(Placed(content, placement)), "screen-first body above horizon satisfies normal billboard safety");

        check(!BillboardGeometry.TryResolveScreenPlacement(content, mapping, screen, new(200, 209), out _, out failure) &&
            failure == BillboardPlacementFailure.UnsafeGround, "screen-first ground inside ten-pixel horizon margin rejected");
        check(BillboardGeometry.TryResolveScreenPlacement(content, mapping, screen, new(200, 210), out var boundary, out _),
            "screen-first ground at exact horizon safety margin accepted");
        CheckScreen(content, boundary, mapping, screen, "horizon-margin placement retains the screen body");
        check(BillboardGeometry.TryResolveScreenPlacement(content, mapping, screen, new(200, 220), out var negative, out _) && negative.Height < 0,
            "explicit screen ground above content center retains signed negative height");
        CheckScreen(content, negative, mapping, screen, "negative-height explicit placement keeps content fixed");
        check(!BillboardGeometry.TryResolveScreenPlacement(content, mapping, screen, new(201, 400), out _, out failure) &&
            failure == BillboardPlacementFailure.GroundXNotRepresentable, "arbitrary horizontal reanchor cannot pretend to preserve screen pixels");
        check(!BillboardGeometry.TryResolveScreenPlacement(content, mapping, screen, new(200, double.NaN), out _, out failure) &&
            failure == BillboardPlacementFailure.UnsafeGround && Json(content) == original,
            "nonfinite ground is rejected without changing content");

        foreach (double magnitude in new[] { 1e-20, 7, 1e20 })
        {
            check(BillboardGeometry.TryResolveScreenBottom(content, mapping, screen.ScaleHomogeneous(magnitude), out var homogeneous, out _),
                $"screen affine homogeneous magnitude {magnitude} accepted");
            NearPoint(homogeneous.Anchor, placement.Anchor, "screen homogeneous magnitude preserves anchor");
            Near(homogeneous.Height, placement.Height, "screen homogeneous magnitude preserves height");
            CheckScreen(content, homogeneous, mapping, screen, "homogeneous screen matrix retains placement");
            var scaledMapping = CanvasMapping.Create(Scene(matrix.ScaleHomogeneous(magnitude)));
            check(BillboardGeometry.TryResolveScreenBottom(content, scaledMapping, screen, out var projected, out _),
                $"canvas homogeneous magnitude {magnitude} accepted");
            NearPoint(projected.Anchor, placement.Anchor, "canvas homogeneous magnitude preserves anchor");
            Near(projected.Height, placement.Height, "canvas homogeneous magnitude preserves height");
        }

        var small = content.Clone(); small.Width = 80; small.Height = 40;
        foreach (var affine in new[]
        {
            Affine2D.Translation(100, 300),
            Affine2D.Translation(350, 450).Compose(Affine2D.Rotation(31)),
            Affine2D.Translation(350, 450).Compose(Affine2D.Rotation(90)),
            Affine2D.Translation(350, 450).Compose(Affine2D.Scale(-2, .7)),
            new Affine2D(1.1, .6, -.2, 1.7, 300, 450)
        })
        {
            var target = Projective2D.FromAffine(affine);
            check(BillboardGeometry.TryResolveScreenBottom(small, mapping, target, out var pose, out _),
                "rotated, mirrored or sheared screen affine resolves");
            var expectedBottom = affine.TransformBounds(ElementGeometry.LocalBounds(small));
            NearPoint(mapping.ToScreen(pose.Anchor), new(expectedBottom.Center.X, expectedBottom.Bottom), "geometry frame bottom midpoint is the default ground");
            CheckScreen(small, pose, mapping, target, "general affine exact screen placement");
        }

        var verticalMapping = CanvasMapping.Create(Scene(new(1, 0, 0, 0, 1, 0, .001, 0, 1)));
        var verticalScreen = Projective2D.FromAffine(Affine2D.Translation(100, 300));
        check(!verticalMapping.TryGetEqualDepthLine(new(140, 340), out _), "legacy near-vertical depth line remains rejected");
        check(BillboardGeometry.TryResolveScreenBottom(small, verticalMapping, verticalScreen, out var vertical, out _),
            "screen-first point ground does not inherit vertical depth-line restriction");
        CheckScreen(small, vertical, verticalMapping, verticalScreen, "vertical-depth screen placement remains exact");

        var affineMapping = CanvasMapping.Create(Scene(Projective2D.FromAffine(Affine2D.Scale(2, 8))));
        check(BillboardGeometry.TryResolveScreenBottom(small, affineMapping, verticalScreen, out var affinePose, out _),
            "anisotropic affine canvas uses existing uniform depth-scale contract");
        Near(affinePose.Linear.M11, .25, "anisotropic canvas area-equivalent scale is four");
        CheckScreen(small, affinePose, affineMapping, verticalScreen, "anisotropic canvas keeps screen affine");

        scene.Canvas.Grid.PlaneCellSize = 31; scene.Canvas.Grid.PlaneOrigin = new(-345, 678);
        check(BillboardGeometry.TryResolveScreenBottom(content, CanvasMapping.Create(scene), screen, out var gridPose, out _) && gridPose == placement,
            "grid density and origin have no effect on screen-first placement");
        var outside = Projective2D.FromAffine(Affine2D.Translation(-800, 350));
        check(BillboardGeometry.TryResolveScreenBottom(small, mapping, outside, out var outsidePose, out _) &&
            !scene.Viewport.Contains(mapping.ToScreen(outsidePose.Anchor)), "safe ground outside viewport is not silently clamped");
        CheckScreen(small, outsidePose, mapping, outside, "off-viewport screen affine retained");

        foreach (var stroke in new[]
        {
            new CanvasElement { Kind = ElementKind.Line, Points = [new(10, 10), new(90, 10)], Style = new() { StrokeWidth = 6 } },
            new CanvasElement { Kind = ElementKind.Freehand, Points = [new(10, 10)], Style = new() { StrokeWidth = 8 } },
            new CanvasElement { Kind = ElementKind.Arrow, Points = [new(10, 10), new(90, 10)], Style = new() { StrokeWidth = 3 } }
        })
        {
            string before = Json(stroke);
            check(BillboardGeometry.TryResolveScreenBottom(stroke, mapping, verticalScreen, out var pose, out _),
                "degenerate point or horizontal stroke has a finite support ground");
            var local = ElementGeometry.LocalBounds(stroke);
            var expected = verticalScreen.TransformBounds(ElementGeometry.StrokedLocalBounds(stroke));
            NearPoint(mapping.ToScreen(pose.Anchor), new(verticalScreen.TransformPoint(local.Center).X, expected.Bottom),
                "degenerate stroke uses support bottom and original center X");
            CheckScreen(stroke, pose, mapping, verticalScreen, "degenerate stroke screen samples retained");
            check(Json(stroke) == before && mapping.IsElementSafe(Placed(stroke, pose)), "degenerate stroke remains unchanged and safely renderable");
        }
        var absent = new CanvasElement { Kind = ElementKind.Freehand, Points = [] };
        check(!BillboardGeometry.TryResolveScreenBottom(absent, mapping, verticalScreen, out _, out failure) && failure == BillboardPlacementFailure.EmptyGeometry,
            "missing stroke points are not a default-sized phantom body");
        var unpaintedPoint = new CanvasElement { Kind = ElementKind.Freehand, Points = [new(10, 10)], Style = new() { StrokeWidth = 0 } };
        check(!BillboardGeometry.TryResolveScreenBottom(unpaintedPoint, mapping, verticalScreen, out _, out failure) && failure == BillboardPlacementFailure.EmptyGeometry,
            "zero-area unpainted point has no grounding envelope");
        var empty = new CanvasElement { Kind = ElementKind.Rectangle, Width = 0, Height = 10 };
        check(!BillboardGeometry.TryResolveScreenBottom(empty, mapping, verticalScreen, out _, out failure) && failure == BillboardPlacementFailure.EmptyGeometry,
            "empty non-stroke geometry rejected");
        foreach (var invalid in new[] { screen with { M31 = .001 }, screen with { M11 = double.NaN }, screen with { M11 = 0 }, screen with { M33 = 0 } })
            check(!BillboardGeometry.TryResolveScreenBottom(content, mapping, invalid, out _, out failure) && failure == BillboardPlacementFailure.NonAffineScreenTransform,
                "projective, nonfinite or noninvertible screen transform rejected");
        var excessive = Projective2D.FromAffine(Affine2D.Translation(CanvasProjectionSafety.MaximumCoordinate + 1, 350));
        check(!BillboardGeometry.TryResolveScreenBottom(small, mapping, excessive, out _, out failure) && failure == BillboardPlacementFailure.UnsafeContent,
            "finite but excessive screen content is rejected");

        var sourceImage = new CanvasElement { Kind = ElementKind.Image, Width = 80, Height = 40,
            AssetId = "unchanged-image", ImageBorderWidth = 12,
            ProjectivePlacement = mapping.ScreenToPlane.Compose(verticalScreen) };
        var composed = mapping.PlaneToScreen.Compose(sourceImage.ProjectivePlacement.Value);
        check(BillboardGeometry.TryResolveScreenBottom(sourceImage, mapping, composed, out var imagePose, out _),
            "composed screen affine accepted even when source image has projective placement");
        CheckScreen(sourceImage, imagePose, mapping, verticalScreen, "cancelled projective image retains screen frame");
        Near(mapping.ToScreen(imagePose.Anchor).Y, 340, "nondegenerate image ground uses body frame rather than border padding");

        var group = new CanvasGroup { Transform = Affine2D.Translation(50, -20).Compose(Affine2D.Rotation(17)).Compose(Affine2D.Scale(1.4, .8)), ElevationScale = 2.7 };
        var nested = new CanvasGroup { ParentId = group.Id, Transform = Affine2D.Translation(-25, 18), ElevationScale = .4 };
        var owner = Scene(matrix); owner.Groups = [group, nested];
        var graph = new SceneGraph(owner); var world = Placed(content, placement);
        var localElement = graph.InsertElement(world, nested.Id); var restored = graph.ToWorld(localElement);
        NearPoint(restored.BillboardAnchor, placement.Anchor, "nested group writes world anchor back through local placement once");
        Near(restored.BillboardHeight, placement.Height, "nested group writes height through elevation scale once");
        CheckScreen(restored, new(restored.BillboardAnchor, restored.BillboardHeight, restored.Transform), CanvasMapping.Create(owner), screen,
            "nested placement roundtrip retains requested screen affine");

        void Near(double actual, double expected, string name) => check(Math.Abs(actual - expected) <= 1e-6, $"{name}: {actual} / {expected}");
        void NearPoint(PointD actual, PointD expected, string name) => check(actual.DistanceTo(expected) <= 1e-6, $"{name}: {actual} / {expected}");
        void CheckScreen(CanvasElement body, BillboardPlacement pose, CanvasMapping projection, Projective2D target, string name)
        {
            var actual = projection.ElementToScreen(Placed(body, pose)); var bounds = ElementGeometry.LocalBounds(body);
            foreach (var point in new[] { new PointD(bounds.Left, bounds.Top), new(bounds.Right, bounds.Top), new(bounds.Right, bounds.Bottom), new(bounds.Left, bounds.Bottom), bounds.Center })
                NearPoint(actual.TransformPoint(point), target.TransformPoint(point), name);
        }
    }

    private static CanvasElement Placed(CanvasElement content, BillboardPlacement pose)
    {
        var placed = content.Clone(); placed.IsBillboard = true; placed.BillboardAnchor = pose.Anchor;
        placed.BillboardHeight = pose.Height; placed.Transform = pose.Linear; placed.ProjectivePlacement = null; return placed;
    }

    private static SceneDocument Scene(Projective2D matrix) => new()
    {
        Viewport = new(0, 0, 800, 600), Canvas = new() { Projection = new() { Enabled = true, Matrix = matrix,
            Quad = [new(0, 300), new(400, 300), new(400, 600), new(0, 600)] } }
    };
    private static string Json(CanvasElement element) => JsonSerializer.Serialize(element);
}
