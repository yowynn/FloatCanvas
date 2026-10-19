using FloatCanvas.Core;
using System.Text.Json;

internal static class CanvasGridLockTests
{
    public static void Run(Action<bool, string> check)
    {
        void Near(double actual, double expected, string name) => check(Math.Abs(actual - expected) < 1e-7, $"{name}: {actual} / {expected}");
        var viewport = new RectD(-250, -180, 500, 500);
        var initial = CanvasCalibration.CreateInitial(viewport, 4, 5);
        check(initial.LockedGridCellSize is null && initial.GridColumns == 4 && initial.GridRows == 5, "new calibration keeps the original unlocked counts");
        var uninitialized = new SceneDocument { Viewport = viewport };
        uninitialized.Canvas.Projection.LockedGridCellSize = 40;
        string uninitializedBefore = JsonSerializer.Serialize(uninitialized);
        check(CanvasMapping.Create(uninitialized).GridCellSize == 40 && JsonSerializer.Serialize(uninitialized) == uninitializedBefore,
            "uninitialized mapping honors a persisted lock without modifying the document");
        var rotation = Affine2D.Rotation(10);
        PointD[] shape = [new(-100, -100), new(100, -100), new(200, 200), new(-200, 200)];
        var tilted = shape.Select(rotation.TransformPoint).ToArray();
        check(CanvasCalibration.TryCalibrate(initial, tilted, viewport, out var start, out _), "tilted perspective fixture accepted");
        var scene = new SceneDocument { Viewport = viewport, Canvas = new() { Projection = start } };
        var anchor = start.Origin + new PointD(2 * start.CellSize, 3 * start.CellSize);
        var rectangle = new CanvasElement { Kind = ElementKind.Rectangle, Width = 80, Height = 100,
            IsBillboard = true, BillboardAnchor = anchor, BillboardHeight = 50, Style = new() { StrokeWidth = 5 } };
        scene.Elements.Add(rectangle);
        string elementBefore = JsonSerializer.Serialize(rectangle);
        var matrixBefore = start.Matrix; double cellBefore = start.CellSize;
        start.LockedGridCellSize = start.CellSize;
        check(start.Matrix == matrixBefore && start.GridCellSize == cellBefore, "locking spacing does not recalibrate or move the visible grid");
        Near(CanvasMapping.Create(scene).ElementToScreen(rectangle).TransformPoint(new(40, 100)).DistanceTo(CanvasMapping.Create(scene).ToScreen(anchor)), 0,
            "rectangle foot begins at its ground anchor");

        PointD[][] changes =
        [
            [new(-105, -115), new(110, -105), new(200, 200), new(-200, 200)],
            [new(-100, -100), new(100, -100), new(220, 200), new(-200, 200)],
            shape.Select(p => p + new PointD(12, 8)).ToArray()
        ];
        bool sawFraction = false, sawOldDrift = false;
        foreach (var changed in changes)
        {
            var quad = changed.Select(rotation.TransformPoint).ToArray();
            check(CanvasCalibration.TryCalibrate(start, quad, viewport, out var locked, out _), "four-point drag accepts a locked grid");
            var unlockedInput = start.Clone(); unlockedInput.LockedGridCellSize = null;
            check(CanvasCalibration.TryCalibrate(unlockedInput, quad, viewport, out var unlocked, out _), "same four-point drag accepts the legacy grid");
            check(locked.Matrix == unlocked.Matrix && locked.CellSize == unlocked.CellSize && locked.Columns == unlocked.Columns && locked.Rows == unlocked.Rows,
                "grid lock never changes the calibration normalization or input counts");
            check(locked.Origin == start.Origin && locked.LockedGridCellSize == cellBefore, "drag retains fixed logical grid origin and spacing");
            scene.Canvas.Projection = locked; var mapping = CanvasMapping.Create(scene);
            var gridIntersection = mapping.ToScreen(mapping.GridOrigin + new PointD(2 * mapping.GridCellSize, 3 * mapping.GridCellSize));
            var ground = mapping.ToScreen(rectangle.BillboardAnchor);
            var foot = mapping.ElementToScreen(rectangle).TransformPoint(new(40, 100));
            Near(gridIntersection.DistanceTo(ground), 0, "tilted grid intersection and ground anchor remain coincident");
            Near(foot.DistanceTo(ground), 0, "billboard foot and anchor remain coincident throughout recalibration");
            Near(mapping.ToScreen(mapping.GridOrigin).DistanceTo(quad[0]), 0, "logical origin remains attached to the original first corner");
            Near(locked.GridColumns * locked.GridCellSize, locked.Columns * locked.CellSize, "fractional horizontal coverage spans the calibration frame");
            Near(locked.GridRows * locked.GridCellSize, locked.Rows * locked.CellSize, "fractional vertical coverage spans the calibration frame");
            sawFraction |= Math.Abs(locked.GridColumns - Math.Round(locked.GridColumns)) > 1e-4;
            scene.Canvas.Projection = unlocked; var oldMapping = CanvasMapping.Create(scene);
            Near(oldMapping.ElementToScreen(rectangle).TransformPoint(new(40, 100)).DistanceTo(foot), 0, "lock leaves the element projection unchanged");
            sawOldDrift |= oldMapping.ToScreen(oldMapping.GridOrigin + new PointD(2 * oldMapping.GridCellSize, 3 * oldMapping.GridCellSize)).DistanceTo(ground) > .1;
            check(unlocked.GridCellSize == unlocked.CellSize && unlocked.GridColumns == unlocked.Columns && unlocked.GridRows == unlocked.Rows,
                "unlocked mode retains legacy grid spacing and counts");
        }
        check(sawFraction && sawOldDrift, "regression fixture exposes old tilted-grid drift and fractional locked coverage");
        check(JsonSerializer.Serialize(rectangle) == elementBefore, "all calibration changes preserve logical layer geometry and placement data");
        scene.Canvas.Projection = start;
        var cloned = scene.Clone(); cloned.Canvas.Projection.LockedGridCellSize = null;
        check(scene.Canvas.Projection.LockedGridCellSize == cellBefore && cloned.Canvas.Projection.LockedGridCellSize is null, "document clone owns independent grid-lock state");
        var session = new EditorSession(scene);
        session.Execute("grid unlock", d => d.Canvas.Projection.LockedGridCellSize = null);
        check(session.Undo() && session.Document.Canvas.Projection.LockedGridCellSize == cellBefore, "undo restores locked grid size");
        check(session.Redo() && session.Document.Canvas.Projection.LockedGridCellSize is null, "redo restores unlocked grid");
        var json = JsonSerializer.Serialize(start);
        using var parsed = JsonDocument.Parse(json);
        check(parsed.RootElement.TryGetProperty("LockedGridCellSize", out _) && !parsed.RootElement.TryGetProperty("GridCellSize", out _) &&
            !parsed.RootElement.TryGetProperty("GridColumns", out _) && !parsed.RootElement.TryGetProperty("GridRows", out _), "only persisted grid-lock state is serialized");
    }
}
