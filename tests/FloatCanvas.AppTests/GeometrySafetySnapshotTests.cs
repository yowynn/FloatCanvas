using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class GeometrySafetySnapshotTests
{
    internal static void Run(Action<string, bool> check)
    {
        // Warm the same input and allocation-counter paths before comparing sizes.
        _ = MoveAllocation(32);
        long small = MoveAllocation(32);
        long large = MoveAllocation(100_000);
        Console.WriteLine($"GEOMETRY_SNAPSHOT_ALLOCATION smallBytes={small} largeBytes={large} samples=4 points=100000");
        check("transform safety snapshots do not allocate a copy of a long unchanged stroke per sample", large - small < 128_000);
        EndpointRollback(check);
    }

    private static long MoveAllocation(int count)
    {
        var stroke = new CanvasElement { Kind = ElementKind.Freehand,
            Points = Enumerable.Range(0, count).Select(i => new PointD(100 + i % 200, 100 + i % 30)).ToList(),
            Style = new() { StrokeWidth = 4 } };
        var session = new EditorSession(new SceneDocument { Viewport = new(0, 0, 1000, 800), Elements = [stroke] });
        var originalPoints = stroke.Points;
        session.SetSelection([stroke.Id]);
        var editor = new EditorController(session) { Tool = EditorTool.Select };
        Send(editor, OverlayPointerKind.Down, new(115, 115));
        Send(editor, OverlayPointerKind.Move, new(135, 135));
        if (!session.IsTransactionActive) throw new InvalidOperationException("The allocation fixture must start a stroke transform.");
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 4; i++) Send(editor, OverlayPointerKind.Move, new(140 + i * 5, 140 + i * 5));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (editor.IsPointerBlocked || !ReferenceEquals(originalPoints, session.Document.Elements.Single().Points))
            throw new InvalidOperationException("The allocation fixture must keep the original safe point list.");
        editor.Cancel();
        return allocated;
    }

    private static void EndpointRollback(Action<string, bool> check)
    {
        var matrix = new Projective2D(1, 0, 0, 0, 1, 0, 0, .005, 1);
        var scene = new SceneDocument { Viewport = new(0, -400, 1000, 1000), Canvas = new() { Projection = new()
        { Enabled = true, Columns = 100, Rows = 100, CellSize = 1, Matrix = matrix,
            Quad = EditorController.Corners(new RectD(0, 0, 100, 100)).Select(matrix.TransformPoint).ToList() } } };
        var line = new CanvasElement { Kind = ElementKind.Line, Points = [new(0, 0), new(200, 100)],
            Transform = Affine2D.Translation(1000, 800), Style = new() { StrokeWidth = 40 } };
        scene.Elements.Add(line);
        var session = new EditorSession(scene); session.SetSelection([line.Id]);
        var editor = new EditorController(session) { Tool = EditorTool.Select };
        var mapping = CanvasMapping.Create(scene);
        var originalPoints = line.Points;
        string original = JsonSerializer.Serialize(scene);
        var down = mapping.ToScreen(new(1200, 900));
        var safe = mapping.ToScreen(new(1400, 2500));
        var unsafeExtent = mapping.ToScreen(new(1400, 3790));
        check("endpoint rollback fixture keeps the pointer inside safety while its stroke support can cross the threshold", mapping.IsScreenSafe(unsafeExtent));
        Send(editor, OverlayPointerKind.Down, down);
        Send(editor, OverlayPointerKind.Move, safe);
        var accepted = line.Points.ToArray();
        check("safe endpoint preview edits the original list in place", session.IsTransactionActive && ReferenceEquals(line.Points, originalPoints) &&
            line.Points[1].DistanceTo(new(400, 1700)) < 1e-6);
        Send(editor, OverlayPointerKind.Move, unsafeExtent);
        check("unsafe endpoint sample restores the previous point in the original list", editor.IsPointerBlocked && ReferenceEquals(line.Points, originalPoints) &&
            line.Points.SequenceEqual(accepted) && mapping.IsElementSafe(line));
        Send(editor, OverlayPointerKind.Up, unsafeExtent);
        check("unsafe endpoint release still restores the complete mouse-down transaction", !session.IsTransactionActive && session.UndoCount == 0 &&
            JsonSerializer.Serialize(session.Document) == original);
    }

    private static void Send(EditorController editor, OverlayPointerKind kind, PointD point) =>
        editor.Pointer(new(kind, new(point.X, point.Y), OverlayMouseButton.Left, OverlayModifiers.None));
}
