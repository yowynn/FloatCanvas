using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;
using System.Text.Json;

internal static class GridLockRenderingTests
{
    public static void Run(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer(); var assets = new AssetLibrary();
        var scene = Scene();
        using var beforeLock = Pixels(renderer, scene, assets);
        scene.Canvas.Projection.LockedGridCellSize = scene.Canvas.Projection.CellSize;
        using (var afterLock = Pixels(renderer, scene, assets))
            check("locking the current grid preserves every rendered pixel", Difference(beforeLock, afterLock) == 0);

        foreach (bool widenBottom in new[] { false, true })
        {
            scene = Scene();
            var projection = scene.Canvas.Projection;
            projection.LockedGridCellSize = projection.CellSize;
            var anchor = projection.Origin + new PointD(4 * projection.CellSize, 4 * projection.CellSize);
            var board = Board(anchor);
            string boardJson = JsonSerializer.Serialize(board);
            scene.Elements = [board];
            var quad = projection.Quad.ToArray();
            if (widenBottom) { quad[2] += new PointD(40, 0); quad[3] += new PointD(-40, 0); }
            else { quad[0] += new PointD(0, -50); quad[1] += new PointD(0, -50); }
            string gesture = widenBottom ? "tilted bottom widening" : "tilted top dragging";
            check($"{gesture} remains a valid perspective calibration", CanvasCalibration.TryCalibrate(projection, quad, scene.Viewport, out var next, out _));
            scene.Canvas.Projection = next;
            var mapping = CanvasMapping.Create(scene);
            var projectedAnchor = mapping.ToScreen(anchor);
            var foot = mapping.ElementToScreen(board).TransformPoint(new(board.Width / 2, board.Height));
            var handle = CanvasGridGeometry.ReferenceCenter(next);
            check($"{gesture} keeps the locked grid centered on its current handle with fixed plane spacing",
                mapping.ToScreen(mapping.GridOrigin).DistanceTo(handle) < 1e-7 && Math.Abs(mapping.GridCellSize - projection.CellSize) < 1e-7);
            check($"{gesture} keeps the original logical billboard anchor and its rendered foot coincident", foot.DistanceTo(projectedAnchor) < 1e-7 && board.BillboardAnchor == anchor);
            check($"{gesture} actually changes calibration scale without changing the original object", Math.Abs(next.CellSize - projection.CellSize) > .01 && JsonSerializer.Serialize(board) == boardJson);

            // Sample both grid families away from the billboard, including fractional
            // positions along each line, so a single accidental crossing cannot pass.
            var samples = new[] { new PointD(-1.37, -1), new PointD(-1, -1.37), new PointD(1.37, 1), new PointD(1, 1.37) }
                .Select(p => mapping.ToScreen(mapping.GridOrigin + p * mapping.GridCellSize)).ToArray();
            board.Visible = false;
            using (var grid = Pixels(renderer, scene, assets))
                check($"{gesture} ordinary renderer draws both locked grid families", samples.All(p => Near(grid, p, color => color.Green > 140 && color.Alpha > 40)));
            scene.Canvas.Grid.Visible = false;
            using (var reference = ReferencePixels(renderer, scene, assets))
                check($"{gesture} calibration reference uses the same locked intersections", samples.All(p => Near(reference, p, color => color.Alpha > 40)));
            board.Visible = true;
            using var lockedBoard = Pixels(renderer, scene, assets);
            check($"{gesture} production renderer places the rectangle bottom at its anchor", Near(lockedBoard, projectedAnchor, color => color.Red > 200 && color.Alpha > 40));
            var lockedIntersection = mapping.ToScreen(mapping.GridOrigin + new PointD(mapping.GridCellSize, 0));
            var matrixBeforeUnlock = next.Matrix;
            double cellBeforeUnlock = next.CellSize;
            next.LockedGridCellSize = null;
            using (var unlockedBoard = Pixels(renderer, scene, assets))
                check($"{gesture} unlocking does not move or resize rendered objects", Difference(lockedBoard, unlockedBoard) == 0);
            var unlockedMapping = CanvasMapping.Create(scene);
            var unlockedIntersection = unlockedMapping.ToScreen(unlockedMapping.GridOrigin + new PointD(unlockedMapping.GridCellSize, 0));
            check($"{gesture} unlocked grid resumes the original calibration scale while retaining its center",
                unlockedIntersection.DistanceTo(lockedIntersection) > 1e-7 && Math.Abs(unlockedMapping.GridCellSize - mapping.GridCellSize) > .01 &&
                unlockedMapping.GridCellSize == next.CellSize && unlockedMapping.GridOrigin.DistanceTo(mapping.GridOrigin) < 1e-7 &&
                next.Matrix == matrixBeforeUnlock && next.CellSize == cellBeforeUnlock && JsonSerializer.Serialize(board) == boardJson);
        }
        CacheAndDepth(renderer, assets, check);
    }

    private static void CacheAndDepth(SceneRenderer renderer, AssetLibrary assets, Action<string, bool> check)
    {
        var scene = Scene();
        var previous = scene.Canvas.Projection;
        previous.LockedGridCellSize = previous.CellSize;
        var quad = previous.Quad.ToArray(); quad[2] += new PointD(40, 0); quad[3] += new PointD(-40, 0);
        if (!CanvasCalibration.TryCalibrate(previous, quad, scene.Viewport, out var next, out var error)) throw new InvalidOperationException(error);
        scene.Canvas.Projection = next;
        using var surface = SKSurface.Create(new SKImageInfo(1000, 800, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var cache = new OverlaySceneCache(renderer);
        SKBitmap Cached()
        {
            surface.Canvas.Clear(SKColors.Transparent); cache.DrawOnClearedRasterTarget(surface, scene, assets, 1, 0);
            using var snapshot = surface.Snapshot(); return SKBitmap.FromImage(snapshot);
        }
        using var locked = Cached(); long builds = cache.BuildCount;
        using (var unchanged = Cached())
            check("unchanged locked grid reuses its raster cache", cache.BuildCount == builds && Difference(locked, unchanged) == 0);
        next.LockedGridCellSize = null;
        using (var unlocked = Cached())
        using (var direct = Pixels(renderer, scene, assets))
            check("unlocking invalidates cached grid without a revision and matches direct rendering", cache.BuildCount > builds && Difference(unlocked, direct) <= 2 && Difference(locked, unlocked) > 100);

        next.LockedGridCellSize = previous.CellSize;
        scene.Elements = [Board(previous.Origin + new PointD(4 * previous.CellSize, 4 * previous.CellSize))];
        scene.Canvas.DepthPriorityOcclusion = true;
        var mapping = CanvasMapping.Create(scene);
        var samples = new[] { new PointD(-1.37, -1), new PointD(-1, -1.37), new PointD(1.37, 1), new PointD(1, 1.37) }
            .Select(p => mapping.ToScreen(mapping.GridOrigin + p * mapping.GridCellSize)).ToArray();
        using (var depth = Pixels(renderer, scene, assets))
            check("depth plane composition retains locked grid positions outside billboard occlusion", samples.All(p => Near(depth, p, color => color.Green > 140 && color.Alpha > 40)));
        scene.Canvas.Grid.Visible = false;
        using (var reference = ReferencePixels(renderer, scene, assets))
            check("depth scene calibration reference retains the same locked grid", samples.All(p => Near(reference, p, color => color.Alpha > 40)));
    }

    public static void WriteEvidence(string directory)
    {
        Directory.CreateDirectory(directory);
        using var renderer = new SceneRenderer(); var assets = new AssetLibrary();
        var scene = Scene(); var projection = scene.Canvas.Projection;
        projection.LockedGridCellSize = projection.CellSize;
        var anchor = projection.Origin + new PointD(4 * projection.CellSize, 4 * projection.CellSize);
        var board = Board(anchor); scene.Elements = [board];
        File.WriteAllBytes(Path.Combine(directory, "locked-before.png"), renderer.ExportPng(scene, assets, true));
        var quad = projection.Quad.ToArray(); quad[2] += new PointD(40, 0); quad[3] += new PointD(-40, 0);
        if (!CanvasCalibration.TryCalibrate(projection, quad, scene.Viewport, out var next, out var error)) throw new InvalidOperationException(error);
        scene.Canvas.Projection = next;
        File.WriteAllBytes(Path.Combine(directory, "locked-after.png"), renderer.ExportPng(scene, assets, true));
        var locked = Coordinates(scene, board);
        scene.Canvas.Grid.Visible = false;
        using (var reference = ReferencePixels(renderer, scene, assets))
        using (var image = SKImage.FromBitmap(reference))
        using (var png = image.Encode(SKEncodedImageFormat.Png, 100))
            File.WriteAllBytes(Path.Combine(directory, "locked-reference.png"), png.ToArray());
        scene.Canvas.Grid.Visible = true; next.LockedGridCellSize = null;
        File.WriteAllBytes(Path.Combine(directory, "unlocked-after.png"), renderer.ExportPng(scene, assets, true));
        File.WriteAllText(Path.Combine(directory, "coordinates.json"), JsonSerializer.Serialize(new { BeforeCell = projection.CellSize, AfterCell = next.CellSize, Locked = locked, Unlocked = Coordinates(scene, board) }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static object Coordinates(SceneDocument scene, CanvasElement board)
    {
        var mapping = CanvasMapping.Create(scene);
        var center = mapping.ToScreen(mapping.GridOrigin);
        var handle = CanvasGridGeometry.ReferenceCenter(scene.Canvas.Projection);
        var intersection = mapping.ToScreen(mapping.GridOrigin + new PointD(mapping.GridCellSize, 0));
        var anchor = mapping.ToScreen(board.BillboardAnchor);
        var foot = mapping.ElementToScreen(board).TransformPoint(new(board.Width / 2, board.Height));
        return new { Center = center, Handle = handle, GridOrigin = mapping.GridOrigin, GridCellSize = mapping.GridCellSize,
            Intersection = intersection, Anchor = anchor, Foot = foot, GridCenterError = center.DistanceTo(handle), FootAnchorError = foot.DistanceTo(anchor) };
    }

    private static SceneDocument Scene()
    {
        var scene = new SceneDocument { Viewport = new(0, 0, 1000, 800) };
        PointD[] quad = [new(250, 180), new(750, 120), new(900, 650), new(100, 650)];
        if (!CanvasCalibration.TryCalibrate(CanvasCalibration.CreateInitial(scene.Viewport), quad, scene.Viewport, out var projection, out var error)) throw new InvalidOperationException(error);
        scene.Canvas.Projection = projection;
        scene.Canvas.Grid = new() { Visible = true, Width = 2, Color = 0xFF00AA00 };
        return scene;
    }

    private static CanvasElement Board(PointD anchor) => new()
    {
        Kind = ElementKind.Rectangle, Width = 200, Height = 100, IsBillboard = true, BillboardAnchor = anchor, BillboardHeight = 50,
        Style = new() { Stroke = 0xFFFF0000, StrokeWidth = 3, Fill = 0 }
    };
    private static SKBitmap Pixels(SceneRenderer renderer, SceneDocument scene, AssetLibrary assets) => SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
    private static SKBitmap ReferencePixels(SceneRenderer renderer, SceneDocument scene, AssetLibrary assets)
    {
        using var surface = SKSurface.Create(new SKImageInfo(1000, 800, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent); renderer.Draw(surface.Canvas, scene, assets, calibrationReference: true);
        using var snapshot = surface.Snapshot(); return SKBitmap.FromImage(snapshot);
    }
    private static bool Near(SKBitmap bitmap, PointD point, Func<SKColor, bool> match)
    {
        int x = (int)Math.Round(point.X), y = (int)Math.Round(point.Y);
        return Enumerable.Range(Math.Max(0, x - 2), Math.Min(bitmap.Width - 1, x + 2) - Math.Max(0, x - 2) + 1)
            .Any(px => Enumerable.Range(Math.Max(0, y - 2), Math.Min(bitmap.Height - 1, y + 2) - Math.Max(0, y - 2) + 1).Any(py => match(bitmap.GetPixel(px, py))));
    }
    private static int Difference(SKBitmap a, SKBitmap b)
    {
        var aa = a.Bytes; var bb = b.Bytes; int maximum = 0;
        for (int i = 0; i < aa.Length; i++) maximum = Math.Max(maximum, Math.Abs(aa[i] - bb[i]));
        return maximum;
    }
}
