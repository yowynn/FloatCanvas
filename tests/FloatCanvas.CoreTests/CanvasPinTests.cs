using FloatCanvas.Core;
using System.Text.Json;

internal static class CanvasPinTests
{
    internal static void Run(Action<bool, string> check)
    {
        void Near(PointD a, PointD b, string text) => check(a.DistanceTo(b) < 1e-6, text);
        var viewport = new RectD(0, 0, 1500, 1000);
        var initial = CanvasCalibration.CreateInitial(viewport, 9, 6);
        PointD[] rectangle = [new(0, 0), new(900, 0), new(900, 600), new(0, 600)];
        initial.ReferenceBounds = new(0, 0, 900, 600);
        check(CanvasCalibration.TryCalibrate(initial, rectangle, viewport, out var value, out _), "9x6 rectangle calibrates");
        Near(value.Matrix.TransformPoint(new(100, 500)), new(100, 500), "9x6 preserves layer rendering");
        var draft = value.Clone(); draft.Rows = 9;
        check(CanvasCalibration.TryCalibrate(draft, rectangle, viewport, out value, out _), "9x9 fixed frame calibrates");
        check(Math.Abs(value.CellSize - 100) < 1e-7, "900x600 9x9 uses logical 100x100 cells");
        Near(value.Matrix.TransformPoint(new(100, 500)), new(100, 433.333333333333), "100 logical height becomes 66.6667 screen height");
        var document = new SceneDocument { Viewport = viewport, Canvas = new() { Projection = value } };
        var mapping = CanvasMapping.Create(document);
        check(mapping.GridOrigin == value.PinPoint, "grid phase is bound to pin rather than center");
        for (int i = 0; i <= 9; i++)
        {
            Near(mapping.ToScreen(value.PinPoint + new PointD(i * 100, 0)), new(i * 100, 600), "all bottom edge intersections are complete cells");
            Near(mapping.ToScreen(value.PinPoint + new PointD(0, -i * 100)), new(0, 600 - i * 600.0 / 9), "all left edge intersections are complete cells");
        }
        PointD[] quad = [new(150, 180), new(750, 120), new(900, 650), new(100, 650)];
        foreach (var location in Enum.GetValues<CanvasPinLocation>())
        {
            check(CanvasCalibration.TryChangePin(value, location, viewport, out var pin, out _), "all nine pin positions can be selected");
            var P = pin.PinPoint;
            check(CanvasCalibration.TryCalibrate(pin, quad, viewport, out var tilted, out _), "tilted frame accepts each pin");
            CanvasCalibration.TryGridToScreen(9, 9, quad, out var grid);
            check(tilted.PinPoint == P, "corner adjustment preserves the material point");
            Near(tilted.Matrix.TransformPoint(P), grid.TransformPoint(tilted.PinGridPoint), "pin remains at its projective nine-grid position");
            var change = tilted.Clone(); change.Columns = 7; change.Rows = 11;
            check(CanvasCalibration.TryCalibrate(change, quad, viewport, out var counts, out _), "count adjustment accepts each pin");
            check(counts.ReferenceBounds == pin.ReferenceBounds, "count adjustment retains the fixed default reference");
            CanvasCalibration.TryGridToScreen(7, 11, quad, out grid);
            P = counts.PinPoint;
            Near(counts.Matrix.TransformPoint(P), grid.TransformPoint(counts.PinGridPoint), "pin remains bound after changing counts");
            Near(counts.Matrix.Inverse().TransformPoint(counts.Matrix.TransformPoint(P)), P, "inverse input returns the same material point");
            document.Canvas.Projection = counts;
            var screen = CanvasMapping.Create(document).ToScreen(P);
            CanvasCalibration.SetEnabled(document.Canvas, false);
            Near(CanvasMapping.Create(document).ToScreen(P), screen, "mode switch preserves screen pin");
            CanvasCalibration.SetEnabled(document.Canvas, true);
            Near(CanvasMapping.Create(document).ToScreen(P), screen, "mode roundtrip preserves screen pin");
        }
        // Re-selecting bottom left must recover the original material corner, not capture the current inverse image.
        var start = initial.Clone();
        CanvasCalibration.TryChangePin(start, CanvasPinLocation.TopLeft, viewport, out var top, out _);
        CanvasCalibration.TryCalibrate(top, quad, viewport, out top, out _);
        CanvasCalibration.TryChangePin(top, CanvasPinLocation.BottomLeft, viewport, out var bottom, out _);
        Near(bottom.Matrix.TransformPoint(initial.PinPoint), quad[3], "switching back recovers original bottom-left content");
        foreach (var location in Enum.GetValues<CanvasPinLocation>())
        {
            CanvasCalibration.TryCalibrate(initial, rectangle, viewport, out var defaultBefore, out _);
            CanvasCalibration.TryChangePin(defaultBefore, location, viewport, out var defaultAfter, out _);
            Near(defaultAfter.Matrix.TransformPoint(new(123, 321)), defaultBefore.Matrix.TransformPoint(new(123, 321)), "default frame pin selection does not move layers");
            var direct = initial.Clone(); direct.Columns = 7; direct.Rows = 11; direct.PinLocation = location;
            CanvasCalibration.TryCalibrate(direct, quad, viewport, out direct, out _);
            var indirect = initial.Clone();
            foreach (var intermediate in new[] { CanvasPinLocation.Center, CanvasPinLocation.Right, CanvasPinLocation.TopLeft, location })
            {
                CanvasCalibration.TryChangePin(indirect, intermediate, viewport, out indirect, out _);
                indirect.Columns = 13; indirect.Rows = 5;
                CanvasCalibration.TryCalibrate(indirect, rectangle, viewport, out indirect, out _);
            }
            indirect.Columns = 7; indirect.Rows = 11;
            CanvasCalibration.TryCalibrate(indirect, quad, viewport, out indirect, out _);
            foreach (var point in new[] { new PointD(), new PointD(100, 200), initial.PinPoint })
                Near(direct.Matrix.TransformPoint(point), indirect.Matrix.TransformPoint(point), "final parameters are independent of calibration history");
        }
        var locked = value.Clone(); locked.LockedGridCellSize = value.CellSize;
        check(CanvasCalibration.TryCalibrate(locked, quad, viewport, out var lockedTilt, out _), "locked grid accepts free corner adjustment");
        check(lockedTilt.LockedGridCellSize == locked.LockedGridCellSize && lockedTilt.GridCellSize == locked.GridCellSize,
            "corner adjustment preserves the locked logical cell size");
        document.Canvas.Projection = lockedTilt;
        mapping = CanvasMapping.Create(document);
        foreach (var gridPoint in new[] { new PointD(1, -1), new PointD(2, -3), new PointD(4, -2) })
        {
            var point = locked.PinPoint + gridPoint * locked.GridCellSize;
            var phase = (point - mapping.GridOrigin) * (1 / mapping.GridCellSize);
            Near(phase, gridPoint, "objects retain their grid indices while locked");
        }
        check(Math.Abs(lockedTilt.GridColumns - lockedTilt.Columns * lockedTilt.CellSize / locked.GridCellSize) < 1e-8 &&
            Math.Abs(lockedTilt.GridColumns - lockedTilt.Columns) > .01, "locked grid reports exact changing coverage rather than rounded counts");
        var legacy = value.Clone(); legacy.PinPlanePoint = null; legacy.AlgorithmVersion = 1; legacy.LockedGridCellSize = 73;
        var matrix = legacy.Matrix;
        CanvasCalibration.UpgradeLegacy(legacy);
        check(legacy.Matrix == matrix && legacy.LockedGridCellSize == 73 && legacy.PinPlanePoint is not null, "legacy upgrade preserves lock without recalibrating content");
        check(JsonSerializer.Serialize(legacy).Contains("LockedGridCellSize"), "new saved settings retain enabled lock");
        var session = new EditorSession(document);
        var before = JsonSerializer.Serialize(document.Canvas);
        session.ExecuteSettings("pin", d => { CanvasCalibration.TryChangePin(d.Canvas.Projection, CanvasPinLocation.TopRight, viewport, out var p, out _); d.Canvas.Projection = p; });
        check(session.Undo() && JsonSerializer.Serialize(session.Document.Canvas) == before, "pin change is fully undoable");
        check(session.Redo(), "pin change can be redone");
        var invalid = value.Clone(); invalid.PinLocation = (CanvasPinLocation)99;
        check(!CanvasCalibration.TryCalibrate(invalid, rectangle, viewport, out _, out _), "invalid pin rejected");
        document.Canvas.Projection = bottom;
        document.Canvas.Projection.Enabled = false;
        document.Canvas.Grid.FlatOrigin = new(750, 500);
        document.Canvas.Grid.FlatTranslation = new(123, -57);
        foreach (var location in Enum.GetValues<CanvasPinLocation>())
        {
            document.Canvas.Projection.PinLocation = location;
            var flat = CanvasMapping.Create(document);
            Near(flat.ToScreen(flat.GridOrigin), new(750, 500), "flat grid intersection stays at the screen center independently of perspective pin and layer translation");
        }
    }
}
