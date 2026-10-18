using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;

internal static class PerspectiveInputTests
{
    internal static void Run(Action<string, bool> check)
    {
        var document = Document(); var session = new EditorSession(document);
        var controller = new EditorController(session) { Tool = EditorTool.Freehand, ScreenOrigin = new(-800, 50) };
        var mapping = CanvasMapping.Create(document);
        var from = new PointD(300, 280); var to = new PointD(380, 350);
        Pointer(controller, OverlayPointerKind.Down, mapping.ToScreen(from), OverlayModifiers.Control);
        Pointer(controller, OverlayPointerKind.Move, mapping.ToScreen(to), OverlayModifiers.Control);
        Pointer(controller, OverlayPointerKind.Up, mapping.ToScreen(to), OverlayModifiers.Control);
        var stroke = session.Document.Elements.Single();
        check("perspective pointer roundtrip stores freehand geometry on the plane", stroke.Transform.TransformPoint(stroke.Points[0]).DistanceTo(from) < 1e-7 &&
            stroke.Transform.TransformPoint(stroke.Points[^1]).DistanceTo(to) < 1e-7);

        controller.Tool = EditorTool.CaptureRegion; session.SetSelection([]);
        CaptureSelection? selection = null; controller.CaptureSelectionCompleted += value => selection = value;
        Pointer(controller, OverlayPointerKind.Down, new(220, 180), OverlayModifiers.Control);
        Pointer(controller, OverlayPointerKind.Up, new(370, 290), OverlayModifiers.Control);
        check("perspective capture gestures retain screen geometry and screen origin", selection?.Bounds == new RectD(220, 180, 150, 110));

        byte[] png = Checkerboard(80, 60);
        var raster = new CaptureRasterResult(png, new(320, 250, 80, 60), true);
        var image = SnapshotImageCommand.Insert(session, raster, mapping.PlaneToScreen);
        var composite = mapping.ElementToScreen(image);
        check("snapshot placement cancels the captured projection without raster unwarping", image.ProjectivePlacement is not null &&
            new[] { new PointD(0, 0), new PointD(80, 0), new PointD(80, 60), new PointD(0, 60) }.All(p =>
                composite.TransformPoint(p).DistanceTo(p + new PointD(320, 250)) < 1e-7) &&
            session.Assets.Get(image.AssetId!).Data.Span.SequenceEqual(png));

        var beforeCrop = ElementGeometry.LocalToPlane(image);
        controller.Tool = EditorTool.Crop;
        Pointer(controller, OverlayPointerKind.Down, new(330, 260));
        Pointer(controller, OverlayPointerKind.Up, new(380, 295));
        image = session.Document.Elements.Single(e => e.Kind == ElementKind.Image);
        check("projective image crop composes local translation on the correct side", Math.Abs(image.Width - 50) < 1e-7 && Math.Abs(image.Height - 35) < 1e-7 &&
            ElementGeometry.LocalToPlane(image).TransformPoint(default).DistanceTo(beforeCrop.TransformPoint(new(10, 10))) < 1e-7);
        check("projective image crop remains one reversible edit", session.Undo() && session.Document.Elements.Last().Width == 80 &&
            ElementGeometry.LocalToPlane(session.Document.Elements.Last()) == beforeCrop);

        image = session.Document.Elements.Last(); session.SetSelection([image.Id]); controller.Tool = EditorTool.Select;
        check("selection handles remain 28 screen pixels from the projected frame", controller.SelectionScreenRotationAnchor is { } anchor &&
            controller.SelectionScreenRotationHandle is { } handle && Math.Abs(anchor.DistanceTo(handle) - 28) < 1e-7 &&
            controller.SelectionScreenCorners[0].DistanceTo(new(320, 250)) < 1e-7);
        var copied = ObjectClipboardCommand.CreateSnapshot(session);
        var rasterCopy = ObjectClipboardCommand.CreateRasterSnapshot(session, copied);
        check("editable copy retains projective placement without copying canvas calibration", copied.Elements.Single().ProjectivePlacement == image.ProjectivePlacement &&
            !copied.Canvas.Projection.Enabled && rasterCopy.Canvas.Projection.Enabled && rasterCopy.Viewport.Contains(new(350, 270)));

        using var request = new ImageDropRequest(session, new(0, 0, 800, 600), CancellationToken.None);
        check("image drop captures the same inverse mapping as pointer input", request.TrySetDropPoint(mapping.ToScreen(from) + controller.ScreenOrigin, controller.ScreenOrigin) &&
            request.DropCenter!.Value.DistanceTo(from) < 1e-7);
        session.Document.Canvas.Projection.Enabled = false;
        check("image drop rejects a changed projection before asynchronous commit", !request.IsCurrent(request, session, null));

        var target = new EditorSession(Document());
        var selected = new CanvasElement { Kind = ElementKind.Rectangle, Width = 25, Height = 25,
            Transform = Affine2D.Translation(350, 300), Style = new() { Fill = 0xFF00FF00, StrokeWidth = 0 } };
        target.Document.Elements.Add(selected);
        var select = new EditorController(target) { Tool = EditorTool.Select };
        var screen = CanvasMapping.Create(target.Document).ProjectedBounds(selected).Inflate(4);
        Pointer(select, OverlayPointerKind.Down, new(screen.Left, screen.Top));
        Pointer(select, OverlayPointerKind.Up, new(screen.Right, screen.Bottom));
        check("screen rectangle selection tests projected geometry", target.Selection.SetEquals([selected.Id]));
        HorizonSelection(check);
    }

    private static void HorizonSelection(Action<string, bool> check)
    {
        var document = new SceneDocument { Viewport = new(0, 0, 240, 180) };
        document.Canvas.Projection.Enabled = true;
        document.Canvas.Projection.Quad = [new(0, 0), new(200, 0), new(200, 160), new(0, 160)];
        document.Canvas.Projection.Matrix = new(1, 0, 0, 0, 1, 0, 0, .002, 1);
        var crossing = new CanvasElement { Kind = ElementKind.Rectangle, Width = 40, Height = 200,
            Transform = Affine2D.Translation(20, -600), Style = new() { Fill = 0xFF00FF00, StrokeWidth = 0 } };
        document.Elements.Add(crossing);
        var session = new EditorSession(document);
        var controller = new EditorController(session) { Tool = EditorTool.Select };
        session.SetSelection([crossing.Id]);
        check("horizon-crossing selection suppresses invalid frame handles", controller.SelectionScreenCorners.Count == 0 &&
            controller.SelectionScreenRotationHandle is null);
        session.SetSelection([]);
        Pointer(controller, OverlayPointerKind.Down, new(14, 80));
        Pointer(controller, OverlayPointerKind.Up, new(18, 100));
        check("screen marquee rejects the phantom chord between opposite projective branches", session.Selection.Count == 0);
        crossing.Height = 700;
        Pointer(controller, OverlayPointerKind.Down, new(25, 20));
        Pointer(controller, OverlayPointerKind.Up, new(35, 40));
        check("screen marquee retains visible geometry when its object crosses the horizon", session.Selection.SetEquals([crossing.Id]));
        session.SetSelection([]);
        crossing = session.Document.Elements.Single();
        crossing.Kind = ElementKind.Line; crossing.Points = [new(0, 0), new(0, 700)]; crossing.Style.StrokeWidth = 2;
        Pointer(controller, OverlayPointerKind.Down, new(100, 10));
        Pointer(controller, OverlayPointerKind.Up, new(15, 40));
        check("screen marquee clips a horizon-crossing open line without losing its visible branch", session.Selection.SetEquals([crossing.Id]));
    }

    private static SceneDocument Document()
    {
        var document = new SceneDocument { Viewport = new(0, 0, 800, 600) };
        var projection = CanvasCalibration.CreateInitial(document.Viewport);
        var corners = projection.Quad.ToArray(); corners[0] += new PointD(60, 0); corners[1] -= new PointD(60, 0);
        if (!CanvasCalibration.TryCalibrate(projection, corners, document.Viewport, out var calibrated, out var error)) throw new InvalidOperationException(error);
        document.Canvas.Projection = calibrated; return document;
    }
    private static void Pointer(EditorController controller, OverlayPointerKind kind, PointD screen, OverlayModifiers modifiers = OverlayModifiers.None) =>
        controller.Pointer(new(kind, new(screen.X + controller.ScreenOrigin.X, screen.Y + controller.ScreenOrigin.Y), OverlayMouseButton.Left, modifiers));
    private static byte[] Checkerboard(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) bitmap.SetPixel(x, y, (x + y) % 2 == 0 ? SKColors.White : SKColors.Black);
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
    }
}
