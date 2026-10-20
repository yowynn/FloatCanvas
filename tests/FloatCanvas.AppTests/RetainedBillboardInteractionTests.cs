using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;

internal static class RetainedBillboardInteractionTests
{
    internal static void Run(Action<string, bool> check)
    {
        foreach (bool depth in new[] { false, true }) RunRetainedBoard(depth, check);
    }

    private static void RunRetainedBoard(bool depth, Action<string, bool> check)
    {
        var original = Projective2D.FromAffine(Affine2D.Translation(-2000, -3800));
        var scene = new SceneDocument { Viewport = new(0, -400, 240, 1000) };
        scene.Canvas.DepthPriorityOcclusion = depth;
        scene.Canvas.Projection = CanvasCalibration.CreateInitial(scene.Viewport);
        scene.Canvas.Projection.Enabled = true;
        SetProjection(scene, original);
        var board = new CanvasElement { Kind = ElementKind.Rectangle, IsBillboard = true,
            BillboardAnchor = new(2100, 4000), Width = 400, Height = 600,
            Style = new() { StrokeWidth = 0, Fill = 0xFF0055CC } };
        scene.Elements.Add(board);
        var session = new EditorSession(scene);
        var editor = new EditorController(session) { Tool = EditorTool.Select };
        using var renderer = new SceneRenderer();
        var assets = new AssetLibrary();
        string elementsBefore = JsonSerializer.Serialize(scene.Elements);
        string name = $"retained visible unlocked billboard (depth {depth})";
        var originalCenter = new PointD(100, 200);
        Click(editor, originalCenter);
        check(name + " begins visible, selectable and with a finite selection frame", session.Selection.SetEquals([board.Id]) &&
            editor.SelectionScreenCorners.Count == 4 && Pixel(renderer, scene, assets, originalCenter).Alpha == 255);

        SetProjection(scene, new(1, 0, 0, 0, 1, 0, 0, .005, 1));
        var mapping = CanvasMapping.Create(scene);
        var ghostPoint = new PointD(100, 180);
        check(name + " fixture has a safe pointer over an unsafe board's raw finite footprint", !mapping.IsElementSafe(board) &&
            mapping.IsScreenSafe(ghostPoint) && ElementGeometry.HitTestProjected(board, ghostPoint, mapping.PlaneToScreen));
        using (var pixels = SKBitmap.Decode(renderer.ExportPng(scene, assets, true)))
            check(name + " is entirely hidden without removing its logical content", pixels.Pixels.All(pixel => pixel.Alpha == 0) &&
                JsonSerializer.Serialize(scene.Elements) == elementsBefore);
        check(name + " retained selection has no ghost frame or rotation handles", editor.SelectionScreenCorners.Count == 0 &&
            editor.SelectionScreenRotationHandle is null && session.Selection.SetEquals([board.Id]));
        session.SetSelection([]);
        Click(editor, ghostPoint);
        check(name + " cannot be clicked through its hidden raw footprint", session.Selection.Count == 0 && !session.IsTransactionActive);
        session.SetSelection([board.Id]);
        check(name + " remains selectable by its layer ID while its screen adorners stay hidden", session.Selection.SetEquals([board.Id]) &&
            editor.SelectionScreenCorners.Count == 0);
        var plane = new CanvasElement { Kind = ElementKind.Rectangle, Width = 20, Height = 20,
            Transform = Affine2D.Translation(100, 0), Style = new() { StrokeWidth = 0 } };
        scene.Elements.Add(plane); session.SetSelection([board.Id, plane.Id]);
        check(name + " does not enlarge a mixed selection frame around its hidden footprint", editor.SelectionScreenCorners.SequenceEqual(
            EditorController.Corners(mapping.ProjectedBounds(plane))));
        scene.Elements.Remove(plane); session.SetSelection([board.Id]);

        SetProjection(scene, original);
        check(name + " restored perspective recovers the original frame and visible pixels", editor.SelectionScreenCorners.Count == 4 &&
            Pixel(renderer, scene, assets, originalCenter).Alpha == 255 && JsonSerializer.Serialize(scene.Elements) == elementsBefore);
        session.SetSelection([]);
        Click(editor, originalCenter);
        check(name + " restored perspective recovers ordinary pointer selection", session.Selection.SetEquals([board.Id]) && !session.IsTransactionActive);

        board.Kind = ElementKind.Line; board.Points = [new(0, 0), new(400, 600)]; board.Style.StrokeWidth = 10;
        SetProjection(scene, new(1, 0, 0, 0, 1, 0, 0, .005, 1));
        check(name + " retained line also hides its endpoint handles", editor.SelectionScreenCorners.Count == 0 && editor.SelectionScreenEndpoints.Count == 0);
        SetProjection(scene, original);
        check(name + " restored line recovers its endpoint handles", editor.SelectionScreenCorners.Count == 4 && editor.SelectionScreenEndpoints.Count == 2);
    }

    private static void SetProjection(SceneDocument scene, Projective2D matrix)
    {
        var projection = scene.Canvas.Projection;
        projection.Matrix = matrix;
        projection.Quad = EditorController.Corners(new RectD(projection.Origin.X, projection.Origin.Y,
            projection.Columns * projection.CellSize, projection.Rows * projection.CellSize)).Select(matrix.TransformPoint).ToList();
    }

    private static void Click(EditorController editor, PointD point)
    {
        editor.Pointer(new(OverlayPointerKind.Down, new(point.X, point.Y), OverlayMouseButton.Left, OverlayModifiers.None));
        editor.Pointer(new(OverlayPointerKind.Up, new(point.X, point.Y), OverlayMouseButton.Left, OverlayModifiers.None));
    }

    private static SKColor Pixel(SceneRenderer renderer, SceneDocument scene, AssetLibrary assets, PointD point)
    {
        using var pixels = SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
        return pixels.GetPixel((int)(point.X - scene.Viewport.Left), (int)(point.Y - scene.Viewport.Top));
    }
}
