using FloatCanvas.Core;

internal static class CanvasProjectionSafetyTests
{
    public static void Run(Action<bool, string> check)
    {
        var scene = new SceneDocument { Viewport = new(-250, -500, 500, 900) };
        var initial = CanvasCalibration.CreateInitial(scene.Viewport, 4, 5);
        PointD[] quad = [new(-100, -100), new(100, -100), new(200, 200), new(-200, 200)];
        check(CanvasCalibration.TryCalibrate(initial, quad, scene.Viewport, out var settings, out _), "strong trapezoid accepts a horizon inside the screen");
        scene.Canvas.Projection = settings;
        var mapping = CanvasMapping.Create(scene);
        check(CanvasCalibration.IsValidProjection(settings.Matrix) && !CanvasCalibration.IsStableViewport(settings.Matrix, scene.Viewport),
            "local projection validity is independent of the legacy full viewport guard");
        check(mapping.SafeScreenPolygon.Count >= 3 && mapping.SafeScreenPolygon.All(mapping.IsScreenSafe), "clipped viewport vertices are safe screen points");
        check(mapping.VisiblePlanePolygon.Count == mapping.SafeScreenPolygon.Count && mapping.VisiblePlanePolygon.All(mapping.IsVisible),
            "visible ground polygon is finite and on the drawable side");
        check(CanvasDepthOrder.ClipPlaneDepthRange(mapping, scene.Viewport, 0, double.PositiveInfinity).All(mapping.IsScreenSafe),
            "unbounded depth strips share the finite screen safety clip");
        for (int i = 0; i < mapping.VisiblePlanePolygon.Count; i++)
            check(mapping.ToScreen(mapping.VisiblePlanePolygon[i]).DistanceTo(mapping.SafeScreenPolygon[i]) < 1e-6, "safe polygon vertices roundtrip through the actual homography");
        var inverse = mapping.ScreenToPlane;
        double norm = Math.Sqrt(inverse.M31 * inverse.M31 + inverse.M32 * inverse.M32);
        var normal = new PointD(inverse.M31 / norm, inverse.M32 / norm);
        var horizon = normal * (-inverse.M33 / norm);
        check(!mapping.TryToPlane(horizon, out _) && !mapping.IsScreenSafe(horizon + normal * 5) && !mapping.IsScreenSafe(horizon - normal * 30),
            "infinity, the near-horizon margin and the opposite ground side reject input");
        var safePoint = horizon + normal * 20;
        check(mapping.TryToPlane(safePoint, out var ground) && mapping.IsVisible(ground) && mapping.ToScreen(ground).DistanceTo(safePoint) < 1e-6,
            "safe points near the horizon retain a finite forward inverse roundtrip");
        foreach (double factor in new[] { 1e-9, 1e9 })
        {
            var safety = new CanvasProjectionSafety(mapping.PlaneToScreen.ScaleHomogeneous(factor));
            check(safety.IsScreenSafe(safePoint) && !safety.IsScreenSafe(horizon + normal * 5), "horizon margin is invariant under homogeneous matrix scaling");
        }
        check(mapping.IsScreenSafe(new(2000, 1000)), "safe offscreen ground remains editable and is not limited to the viewport");
        var image = new CanvasElement { Kind = ElementKind.Image, Width = 40, Height = 40,
            ProjectivePlacement = mapping.ScreenToPlane.Compose(Projective2D.FromAffine(Affine2D.Translation(-20, -380))) };
        check(mapping.IsElementSafe(image), "complete planar image inside the domain is safe");
        image.ProjectivePlacement = mapping.ScreenToPlane.Compose(Projective2D.FromAffine(Affine2D.Translation(-20, -410)));
        check(!mapping.IsElementSafe(image), "planar image crossing the horizon is unsafe even with a finite center");
        var line = new CanvasElement { Kind = ElementKind.Line, Points = [new(-20, 0), new(20, 0)],
            ProjectivePlacement = mapping.ScreenToPlane.Compose(Projective2D.FromAffine(Affine2D.Translation(0, -388))), Style = new() { StrokeWidth = 8 } };
        check(mapping.IsElementSafe(line, false) && !mapping.IsElementSafe(line), "entire planar stroke support participates in the safety check");
        mapping.TryToPlane(new(0, -350), out var anchor);
        var billboard = new CanvasElement { Kind = ElementKind.Rectangle, Width = 80, Height = 2400, IsBillboard = true, BillboardAnchor = anchor,
            Style = new() { StrokeWidth = 0 } };
        check(mapping.IsElementSafe(billboard) && mapping.ProjectedBounds(billboard).Top < horizon.Y,
            "billboard top may extend above the horizon when its anchor and scale are safe");
        billboard.BillboardAnchor = mapping.ScreenToPlane.TransformPoint(horizon + normal * 5);
        check(!mapping.IsElementSafe(billboard) && !mapping.TryGetDepthScale(billboard.BillboardAnchor, out _), "unsafe billboard anchor rejects placement and scale together");
        check(!mapping.IsVisible(new(1e12, 1e12)), "finite coordinate guards bound the distant plane");
        var flat = CanvasMapping.Create(new SceneDocument { Viewport = new(-1200, -100, 2000, 1000) });
        check(flat.SafeScreenPolygon.SequenceEqual(flat.VisiblePlanePolygon) && flat.IsScreenSafe(new(-1300, -200)), "affine ground has no artificial horizon or viewport input restriction");
        var excessive = new CanvasProjectionSafety(Projective2D.FromAffine(Affine2D.Scale(1e9, 1e9)));
        check(!excessive.IsScreenSafe(new(10, 10)) && excessive.ClipScreen(new(0, 0, 1000, 800)).Count == 0,
            "excessive affine scale is excluded consistently from rendered and interactive domains");
        Insertion(scene, check);
    }

    private static void Insertion(SceneDocument source, Action<bool, string> check)
    {
        var scene = source.Clone(); scene.Elements.Clear();
        var rectangle = new CanvasElement { Kind = ElementKind.Rectangle, Width = 24, Height = 18,
            Transform = Affine2D.Translation(scene.Canvas.Projection.Origin.X + 20, scene.Canvas.Projection.Origin.Y + 20) };
        scene.Elements.Add(rectangle);
        var session = new EditorSession(scene); session.SetSelection([rectangle.Id]);
        string before = System.Text.Json.JsonSerializer.Serialize(scene);
        bool duplicateRejected = false;
        try { session.DuplicateSelection(0, -1_000_000_000); } catch (InvalidOperationException) { duplicateRejected = true; }
        check(duplicateRejected && System.Text.Json.JsonSerializer.Serialize(session.Document) == before && session.Selection.SetEquals([rectangle.Id]) &&
            session.UndoCount == 0 && !session.IsTransactionActive, "duplicate validates the complete offset candidates before changing selection scene or history");
        var assets = new AssetLibrary(); string assetId = assets.Add([1, 2, 3]);
        var import = new SceneDocument { Elements = [new CanvasElement { Kind = ElementKind.Image, Width = 100, Height = 60, AssetId = assetId }] };
        bool insertionRejected = false;
        try { session.InsertDocument(import, assets, Affine2D.Translation(-1_000_000_000, 20), true); } catch (InvalidOperationException) { insertionRejected = true; }
        check(insertionRejected && System.Text.Json.JsonSerializer.Serialize(session.Document) == before && session.Selection.SetEquals([rectangle.Id]) &&
            session.UndoCount == 0 && !session.IsTransactionActive && session.Assets.Count == 0,
            "document insertion rejects unsafe candidates before mutating scene groups selection assets or history");
        session.DuplicateSelection(5, 5);
        check(session.Document.Elements.Count == 2 && session.UndoCount == 1, "safe duplicate remains a single normal undo operation");
    }
}
