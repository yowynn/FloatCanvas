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
        var capturePlane = RectD.FromPoints(mapping.ScreenToPlane.TransformPoint(new(220, 180)),
            mapping.ScreenToPlane.TransformPoint(new(370, 290)));
        check("perspective planar captures follow the plane rectangle and preserve screen origin", selection?.PlaneBounds == capturePlane &&
            selection.Points.Count == 4 && selection.Points[0].DistanceTo(mapping.ToScreen(new(capturePlane.Left, capturePlane.Top))) < 1e-7 &&
            selection.PlaneToScreen == mapping.PlaneToScreen && selection.Bounds == mapping.PlaneToScreen.TransformBounds(capturePlane));

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
        Pointer(controller, OverlayPointerKind.Down, new(330, 260), OverlayModifiers.Control);
        Pointer(controller, OverlayPointerKind.Up, new(380, 295), OverlayModifiers.Control);
        image = session.Document.Elements.Single(e => e.Kind == ElementKind.Image);
        check("projective image crop composes local translation on the correct side", Math.Abs(image.Width - 50) < 1e-7 && Math.Abs(image.Height - 35) < 1e-7 &&
            ElementGeometry.LocalToPlane(image).TransformPoint(default).DistanceTo(beforeCrop.TransformPoint(new(10, 10))) < 1e-7);
        check("projective image crop remains one reversible edit", session.Undo() && session.Document.Elements.Last().Width == 80 &&
            ElementGeometry.LocalToPlane(session.Document.Elements.Last()) == beforeCrop);

        image = session.Document.Elements.Last(); session.SetSelection([image.Id]); controller.Tool = EditorTool.Select;
        var planeImageBounds = ElementGeometry.Bounds(image);
        check("snapshot selection uses plane bounds with rotation handles 28 screen pixels from the projected frame", controller.SelectionScreenRotationAnchor is { } anchor &&
            controller.SelectionScreenRotationHandle is { } handle && Math.Abs(anchor.DistanceTo(handle) - 28) < 1e-7 &&
            controller.SelectionScreenCorners[0].DistanceTo(mapping.ToScreen(new(planeImageBounds.Left, planeImageBounds.Top))) < 1e-7);
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
        StrokeEdgeSelection(check);
        FilledArrowSelection(check);
        HorizonSelection(check);
    }

    private static void FilledArrowSelection(Action<string, bool> check)
    {
        foreach (bool projected in new[] { false, true })
        foreach (var style in new[] { ArrowHeadStyle.FilledTriangle, ArrowHeadStyle.Diamond })
        foreach (bool reverse in new[] { false, true })
        {
            var document = projected ? Document() : new SceneDocument { Viewport = new(0, 0, 800, 600) };
            var arrow = new CanvasElement { Kind = ElementKind.Arrow, Points = [new(100, 100), new(300, 100)],
                Style = new() { StrokeWidth = 20, ArrowHeadSize = 60,
                    StartArrowHead = reverse ? style : ArrowHeadStyle.None,
                    EndArrowHead = reverse ? ArrowHeadStyle.None : style } };
            document.Elements.Add(arrow);
            var session = new EditorSession(document);
            var controller = new EditorController(session) { Tool = EditorTool.Select };
            var mapping = CanvasMapping.Create(document);
            using var bitmap = new SKBitmap(800, 600);
            using var canvas = new SKCanvas(bitmap);
            using var renderer = new SceneRenderer();
            canvas.Clear(SKColors.Transparent);
            renderer.Draw(canvas, document, session.Assets, includeBackground: false, includeCanvasBackground: false);
            void Marquee(string name, RectD bounds, bool expected)
            {
                string label = $"{name} for {style}, reverse={reverse}, projection={projected}";
                bool painted = false;
                for (int y = (int)Math.Ceiling(bounds.Top); y < (int)Math.Floor(bounds.Bottom); y++)
                    for (int x = (int)Math.Ceiling(bounds.Left); x < (int)Math.Floor(bounds.Right); x++)
                        painted |= bitmap.GetPixel(x, y).Alpha > 127;
                check($"{label} render fixture", painted == expected);
                session.SetSelection([]);
                Pointer(controller, OverlayPointerKind.Down, new(reverse ? bounds.Left : bounds.Right, bounds.Bottom));
                check($"{label} starts a marquee", controller.Marquee is not null);
                Pointer(controller, OverlayPointerKind.Up, new(reverse ? bounds.Right : bounds.Left, bounds.Top));
                check(label, session.Selection.Contains(arrow.Id) == expected);
            }

            var tip = mapping.ToScreen(new(reverse ? 100 : 300, 100));
            Marquee("marquee rejects the transparent space beyond a filled arrow tip",
                new(tip.X + (reverse ? -15 : 2), tip.Y - 5, 13, 10), false);
            double wingX = style == ArrowHeadStyle.Diamond ? (reverse ? 120 : 260) : (reverse ? 150 : 230);
            Marquee("marquee retains the filled arrowhead outside its trimmed shaft",
                mapping.PlaneToScreen.TransformBounds(new(wingX, 120, 20, 25)), true);
        }
    }

    private static void StrokeEdgeSelection(Action<string, bool> check)
    {
        foreach (bool projected in new[] { false, true })
        {
            var document = projected ? Document() : new SceneDocument { Viewport = new(0, 0, 800, 600) };
            var line = new CanvasElement { Kind = ElementKind.Line, Points = [new(100, 100), new(300, 100)],
                Style = new() { StrokeWidth = 100 } };
            var rectangle = new CanvasElement { Kind = ElementKind.Rectangle, Width = 100, Height = 100,
                Transform = Affine2D.Translation(400, 300), Style = new() { StrokeWidth = 60 } };
            var dot = new CanvasElement { Kind = ElementKind.Freehand, Points = [new(500, 100)], Style = new() { StrokeWidth = 100 } };
            var rotated = new CanvasElement { Kind = ElementKind.Line, Points = [new(0, 0), new(100, 0)],
                Transform = Affine2D.Translation(100, 400).Compose(Affine2D.Rotation(30)).Compose(Affine2D.Scale(2, .5)),
                Style = new() { StrokeWidth = 100 } };
            var normal = new CanvasElement { Kind = ElementKind.Rectangle, Width = 40, Height = 40,
                Transform = Affine2D.Translation(600, 300), Style = new() { StrokeWidth = 2, Fill = 0xFF00FF00 } };
            var image = new CanvasElement { Kind = ElementKind.Image, Width = 40, Height = 40,
                Transform = Affine2D.Translation(600, 400), Style = new() { StrokeWidth = 100 } };
            var arrow = new CanvasElement { Kind = ElementKind.Arrow, Points = [new(600, 100), new(700, 100)],
                Style = new() { StrokeWidth = 20, ArrowHeadSize = 60, ArrowHeadStyle = ArrowHeadStyle.Open } };
            document.Elements.AddRange([line, rectangle, dot, rotated, normal, image, arrow]);
            var session = new EditorSession(document);
            var controller = new EditorController(session) { Tool = EditorTool.Select };
            var mapping = CanvasMapping.Create(document);
            using var bitmap = new SKBitmap(800, 600);
            using var canvas = new SKCanvas(bitmap);
            using var renderer = new SceneRenderer();
            canvas.Clear(SKColors.Transparent);
            renderer.Draw(canvas, document, session.Assets, includeBackground: false, includeCanvasBackground: false);
            void Marquee(string name, RectD planeRectangle, params CanvasElement[] expected)
            {
                session.SetSelection([]);
                var bounds = mapping.PlaneToScreen.TransformBounds(planeRectangle);
                bool painted = false;
                for (int y = (int)Math.Ceiling(bounds.Top); y < (int)Math.Floor(bounds.Bottom); y++)
                    for (int x = (int)Math.Ceiling(bounds.Left); x < (int)Math.Floor(bounds.Right); x++)
                        painted |= bitmap.GetPixel(x, y).Alpha > 127;
                check($"{name} render fixture with projection={projected}", painted == (expected.Length > 0));
                var start = expected.Length == 0 ? new PointD(bounds.Left, bounds.Top) : new(bounds.Right, bounds.Bottom);
                var end = expected.Length == 0 ? new PointD(bounds.Right, bounds.Bottom) : new(bounds.Left, bounds.Top);
                Pointer(controller, OverlayPointerKind.Down, start);
                check($"{name} starts a marquee with projection={projected}", controller.Marquee is not null);
                Pointer(controller, OverlayPointerKind.Up, end);
                check($"{name} with projection={projected}", session.Selection.SetEquals(expected.Select(e => e.Id)));
            }

            Marquee("marquee includes only the wide line's painted edge", new(150, 140, 50, 30), line);
            Marquee("marquee includes the full inward band of a closed shape stroke", new(465, 330, 60, 20), rectangle);
            Marquee("marquee excludes the former outside band of a closed shape stroke", new(515, 330, 40, 20));
            Marquee("marquee includes the painted edge of a single-point stroke", new(510, 140, 20, 30), dot);
            Marquee("marquee rejects empty corners outside a round stroke cap", new(45, 45, 12, 12));
            Marquee("marquee rejects space beyond a wide stroke", new(150, 160, 50, 25));
            Marquee("marquee includes a rotated nonuniform stroke edge", new(150, 455, 15, 35), rotated);
            Marquee("marquee rejects empty rotated stroke bounds", new(130, 485, 15, 15));
            Marquee("marquee keeps ordinary filled shape selection", new(610, 330, 20, 30), normal);
            Marquee("marquee does not invent an image stroke", new(650, 410, 20, 20));
            Marquee("marquee includes the painted outer edge of an open arrowhead", new(630, 130, 20, 25), arrow);

            document.Canvas.Grid.Visible = true;
            Marquee("grid visibility preserves stroke selection", new(150, 140, 50, 30), line);
        }
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
