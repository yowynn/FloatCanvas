using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class CanvasToolInteractionTests
{
    internal static void Run(Action<string, bool> check)
    {
        check("Canvas appends to the existing serialized tool values", (int)EditorTool.Stamp == 21 && (int)EditorTool.Canvas == 22);
        check("C selects Canvas and Ctrl+C remains the copy command", ToolShortcutCatalog.TryGetTool(0x43, out var tool) &&
            tool == EditorTool.Canvas && EditorShortcutPolicy.IsCommand(0x43, OverlayModifiers.Control) &&
            !EditorShortcutPolicy.AllowsRepeat(0x43, OverlayModifiers.None));
        check("Canvas is not a creation or default-size drawing tool", !EditorController.IsCreationTool(EditorTool.Canvas) &&
            !EditorController.UsesDefaultSize(EditorTool.Canvas));
        var reference = new RectD(-200, 100, 1000, 800);
        var session = Scene(reference);
        var editor = new EditorController(session) { Tool = EditorTool.Canvas };
        editor.Pointer(new(OverlayPointerKind.Down, new(100, 200), OverlayMouseButton.Left, OverlayModifiers.None));
        editor.Pointer(new(OverlayPointerKind.Move, new(200, 300), OverlayMouseButton.Left, OverlayModifiers.None));
        editor.Pointer(new(OverlayPointerKind.Up, new(200, 300), OverlayMouseButton.Left, OverlayModifiers.None));
        check("ordinary drawing pointer routing cannot create content for Canvas", session.Document.Elements.Count == 0 && !session.IsTransactionActive && session.UndoCount == 0);

        var controller = new CanvasGridScaleController();
        var center = reference.Center; var start = center + new PointD(200, 0);
        string original = JsonSerializer.Serialize(session.Document);
        check("flat grid accepts a blank-area drag", controller.Down(session, start, reference));
        controller.Move(center + new PointD(300, 0));
        double enlarged = 50 * 332d / 232d;
        check("radial outward motion enlarges exact square cells without moving their origin", Near(session.Document.Canvas.Grid.FlatCellSize!.Value, enlarged) &&
            session.Document.Canvas.Grid.FlatOrigin == center && session.Document.Canvas.Projection.Matrix == Projective2D.Identity &&
            session.Document.Canvas.Projection.CellSize == session.Document.Canvas.Grid.FlatCellSize && session.IsTransactionActive && session.UndoCount == 0);
        check("grid resizing leaves the committed recovery snapshot unchanged until release", JsonSerializer.Serialize(session.CreateCommittedSnapshot()) == original);
        controller.Move(center + new PointD(100, 0));
        check("radial inward motion shrinks from the original radius without cumulative drift", Near(session.Document.Canvas.Grid.FlatCellSize!.Value, 50 * 132d / 232d));
        controller.Move(center + new PointD(300, 0)); controller.Up();
        string resized = JsonSerializer.Serialize(session.Document);
        check("many grid previews commit as exactly one undo", !controller.IsDragging && !session.IsTransactionActive && session.UndoCount == 1);
        check("grid drag undo and redo restore exact sizes and count pairs", session.Undo() && JsonSerializer.Serialize(session.Document) == original &&
            session.Redo() && JsonSerializer.Serialize(session.Document) == resized);

        session = Scene(reference); original = JsonSerializer.Serialize(session.Document);
        check("a drag may start exactly at the grid origin", controller.Down(session, center, reference));
        controller.Move(center);
        check("pressing and staying at the center never jumps the grid size", session.Document.Canvas.Grid.FlatCellSize == 50);
        controller.Move(center + new PointD(32, 0));
        check("center-origin dragging grows smoothly with a finite buffered ratio", session.Document.Canvas.Grid.FlatCellSize == 100);
        controller.Cancel();
        check("cancel restores the entire pre-drag canvas and creates no history", JsonSerializer.Serialize(session.Document) == original &&
            !session.IsTransactionActive && session.UndoCount == 0);
        controller.Down(session, start, reference); controller.Move(start); controller.Up();
        check("a click without radial movement creates no undo entry", session.UndoCount == 0 && JsonSerializer.Serialize(session.Document) == original);
        controller.Down(session, start, reference); controller.Move(center + new PointD(300, 0)); controller.Move(start); controller.Up();
        check("returning to the start radius restores the exact reference without an empty undo", session.UndoCount == 0 && JsonSerializer.Serialize(session.Document) == original);

        var tiny = Scene(reference);
        tiny.Document.Canvas.Projection = CanvasGridGeometry.CreateFlatReference(reference, 128, 128, .1);
        tiny.Document.Canvas.Grid.FlatCellSize = .1;
        string tinyOriginal = JsonSerializer.Serialize(tiny.Document);
        controller.Down(tiny, center, reference); controller.Move(center); controller.Up();
        check("clicking a legacy sub-unit grid at the origin never clamps or changes it", JsonSerializer.Serialize(tiny.Document) == tinyOriginal && tiny.UndoCount == 0);
        controller.Down(tiny, center, reference); controller.Move(center + new PointD(300, 0)); controller.Move(center); controller.Up();
        check("a legacy sub-unit grid returns exactly after an outward and back gesture", JsonSerializer.Serialize(tiny.Document) == tinyOriginal && tiny.UndoCount == 0);

        session.Document.Canvas.Projection.LockedGridCellSize = 50;
        controller.Down(session, start, reference); controller.Move(center + new PointD(300, 0)); controller.Up();
        check("flat size changes preserve grid-lock intent using the new exact size", session.Document.Canvas.Projection.LockedGridCellSize == session.Document.Canvas.Grid.FlatCellSize);

        session = Scene(reference);
        var shape = new CanvasElement { Kind = ElementKind.Rectangle, Width = 80, Height = 80, Locked = true,
            Transform = Affine2D.Translation(start.X - 40, start.Y - 40), Style = new ElementStyle { Fill = 0xffffffff } };
        session.Document.Elements.Add(shape);
        check("a visible locked object is not blank space for flat-grid dragging", !controller.Down(session, start, reference));
        shape.Visible = false;
        check("hidden layers do not block a blank-space grid drag", controller.Down(session, start, reference)); controller.Cancel();
        session.BeginTransaction("unrelated");
        check("flat-grid dragging cannot steal an existing document transaction", !controller.Down(session, start, reference) && session.IsTransactionActive);
        session.Cancel();
        controller.Down(session, start, reference); session.Commit(); session.BeginTransaction("later"); session.Document.Canvas.BackgroundColor = 0xff123456;
        check("late pointer samples cannot roll back a later transaction", !controller.Move(center) && !controller.IsDragging &&
            session.IsTransactionActive && session.Document.Canvas.BackgroundColor == 0xff123456);
        session.Cancel();
        session.Document.Canvas.Projection.Enabled = true;
        check("flat drag is unavailable while perspective is enabled", !controller.Down(session, start, reference));
        session.Document.Canvas.Projection.Enabled = false;
        check("nonfinite drag samples cannot start document mutations", !controller.Down(session, new(double.NaN, 0), reference) && !session.IsTransactionActive);

        session = Scene(reference); original = JsonSerializer.Serialize(session.Document);
        controller.Down(session, start, reference); controller.Move(new(double.PositiveInfinity, 0)); controller.Up();
        check("nonfinite move samples leave the exact original grid intact", JsonSerializer.Serialize(session.Document) == original && session.UndoCount == 0);
        controller.Down(session, start, reference); controller.Move(new(1e15, 1e15)); controller.Up();
        check("extreme pointer motion clamps to the editable maximum without invalid geometry", session.Document.Canvas.Grid.FlatCellSize == CanvasGridGeometry.MaximumCellSize &&
            session.Document.Canvas.Projection.Quad.All(point => double.IsFinite(point.X) && double.IsFinite(point.Y)));

        var saved = Scene(reference).Document.Canvas;
        CanvasGridScaleController.ApplyGridSize(saved, reference, 73.125);
        var counts = (saved.Projection.Columns, saved.Projection.Rows);
        string geometry = JsonSerializer.Serialize(saved.Projection.Quad);
        for (int i = 0; i < 8; i++) saved.Projection.Enabled = !saved.Projection.Enabled;
        check("switching modes preserves the exact flat size, reference geometry, and count pair", saved.Grid.FlatCellSize == 73.125 &&
            counts == (saved.Projection.Columns, saved.Projection.Rows) && JsonSerializer.Serialize(saved.Projection.Quad) == geometry);
        var reset = CanvasGridGeometry.CreateFlatReference(reference, counts.Columns, counts.Rows, saved.Grid.FlatCellSize!.Value);
        check("flat spacing and immediately reset perspective spacing are identical", reset.CellSize == saved.Grid.FlatCellSize &&
            reset.Matrix == Projective2D.Identity && CanvasGridGeometry.ReferenceCenter(reset) == saved.Grid.FlatOrigin);

        var dormant = Scene(reference).Document.Canvas;
        var oldProjection = dormant.Projection;
        PointD[] tilted = [oldProjection.Quad[0] + new PointD(50, 15), oldProjection.Quad[1] + new PointD(-30, 5),
            oldProjection.Quad[2], oldProjection.Quad[3]];
        check("dormant snapshot fixture has a valid projection with independent flat spacing", CanvasCalibration.TryCalibrate(
            oldProjection, tilted, reference, out var tiltedProjection, out _));
        dormant.Projection = tiltedProjection; dormant.Projection.Enabled = false;
        dormant.Grid.FlatCellSize = 73.1;
        var dormantCounts = (dormant.Projection.Columns, dormant.Projection.Rows);
        string dormantGeometry = JsonSerializer.Serialize(dormant.Projection.Quad);
        var dormantMatrix = dormant.Projection.Matrix; double dormantPlaneCell = dormant.Projection.CellSize;
        dormant.Projection.Enabled = true;
        check("enabling a dormant perspective preserves its count pair geometry and independently saved flat size",
            dormant.Grid.FlatCellSize == 73.1 && dormant.Projection.CellSize == dormantPlaneCell &&
            dormant.Projection.Matrix == dormantMatrix && JsonSerializer.Serialize(dormant.Projection.Quad) == dormantGeometry &&
            dormantCounts == (dormant.Projection.Columns, dormant.Projection.Rows));
        var restoredFlat = DesktopSession.CreateResetCanvasProjection(dormant, reference);
        var fittedReset = CanvasCalibration.CreateInitial(reference, dormantCounts.Columns, dormantCounts.Rows);
        check("reset fits its target reference without mutating the independent saved flat spacing",
            dormantPlaneCell != 73.1 && dormant.Grid.FlatCellSize == 73.1 && restoredFlat.CellSize == fittedReset.CellSize && restoredFlat.Enabled &&
            restoredFlat.Quad.SequenceEqual(fittedReset.Quad) &&
            restoredFlat.Matrix == Projective2D.Identity && CanvasGridGeometry.ReferenceCenter(restoredFlat).DistanceTo(dormant.Grid.FlatOrigin!.Value) < 1e-10 &&
            dormantCounts == (restoredFlat.Columns, restoredFlat.Rows));
        var flatDocument = new SceneDocument { Viewport = reference, Canvas = dormant.Clone() };
        flatDocument.Canvas.Projection.Enabled = false;
        flatDocument.Canvas.Grid.FlatCellSize = restoredFlat.CellSize;
        var resetDocument = flatDocument.Clone(); resetDocument.Canvas.Projection = restoredFlat;
        var flatMapping = CanvasMapping.Create(flatDocument); var resetMapping = CanvasMapping.Create(resetDocument);
        check("flat and reset perspective lattices coincide after applying the reset's newly fitted spacing",
            flatMapping.GridOrigin.DistanceTo(resetMapping.GridOrigin) < 1e-10 && flatMapping.GridCellSize == resetMapping.GridCellSize &&
            flatMapping.ToScreen(flatMapping.GridOrigin + new PointD(restoredFlat.CellSize, restoredFlat.CellSize)).DistanceTo(
                resetMapping.ToScreen(resetMapping.GridOrigin + new PointD(restoredFlat.CellSize, restoredFlat.CellSize))) < 1e-10);
    }

    private static EditorSession Scene(RectD reference)
    {
        var scene = new SceneDocument { Viewport = reference };
        CanvasGridScaleController.ApplyGridSize(scene.Canvas, reference, 50);
        return new(scene);
    }

    private static bool Near(double left, double right) => Math.Abs(left - right) < 1e-10;
}
