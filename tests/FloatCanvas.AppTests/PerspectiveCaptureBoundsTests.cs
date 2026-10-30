using System.IO;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using SkiaSharp;

/// <summary>Capture gestures and inserted image frames must describe the same canvas-plane region.</summary>
internal static class PerspectiveCaptureBoundsTests
{
    internal static void Run(Action<string, bool> check)
    {
        foreach (var shape in Enum.GetValues<CaptureShape>())
        {
            var source = Document();
            source.Elements.Add(new() { Kind = ElementKind.Rectangle, Width = 1400, Height = 1400,
                Style = new() { Fill = 0xFF1489C9, StrokeWidth = 0 } });
            var mapping = CanvasMapping.Create(source);
            var controller = new EditorController(new(source)) { Tool = EditorTool.CaptureRegion,
                CaptureShape = shape, CaptureSource = CaptureSource.CanvasOnly, CaptureBorderWidth = 0 };
            var selection = Gesture(controller, mapping, shape);
            check(shape + " perspective capture supplies its intended outline", selection is not null &&
                selection.Points.Count >= 4 && selection.BillboardDepthLine is null &&
                (selection.PlaneBounds is not null) == (shape is CaptureShape.Rectangle or CaptureShape.Ellipse));

            var raster = SnapshotImageCommand.Render(source, [], selection!, null, [source.Viewport]);
            var target = new EditorSession(Document());
            var image = SnapshotImageCommand.Insert(target, raster, mapping.PlaneToScreen);
            var select = new EditorController(target) { Tool = EditorTool.Select };
            var expected = selection!.PlaneBounds ?? Bounds(selection.Points.Select(mapping.ScreenToPlane.TransformPoint));
            var actual = PlaneFrame(select, mapping);
            Console.WriteLine($"CAPTURE_FRAME shape={shape} expected={expected} actual={actual} maximumError={Error(actual, expected):R}");
            ExportPreview(target, select, shape);
            check(shape + " unframed perspective capture selection fits its minimum plane envelope", Error(actual, expected) < 1e-6);
            check(shape + " perspective capture keeps the encoded source pixels", target.Assets.Get(image.AssetId!).Data.Span.SequenceEqual(raster.PngBytes));
            Borders(target, select, mapping, selection, expected, check);
            Copies(target, expected, check);
            if (shape == CaptureShape.Rectangle)
            {
                EffectsAndReplacement(target, select, mapping, expected, check);
                Crop(target, select, mapping, expected, check);
                LegacyAndBillboard(raster, mapping, check);
            }
        }
    }

    private static void EffectsAndReplacement(EditorSession target, EditorController select, CanvasMapping mapping,
        RectD body, Action<string, bool> check)
    {
        var original = target.Document.Elements.Single();
        string originalAsset = original.AssetId!;
        var contour = original.ImageFrame!.ToArray();
        var rasterFrame = ElementGeometry.LocalToPlane(original).TransformBounds(ElementGeometry.StrokedLocalBounds(original));
        foreach (var kind in Enum.GetValues<ImageEffectKind>())
        {
            target.UpdateSelection(image => image.Effects = [new() { Kind = kind, Bounds = new(0, 0, 1, 1), Color = 0xFFFF0000 }]);
            check(kind + " applied to a perspective capture leaves room for pixels outside its original contour",
                Error(PlaneFrame(select, mapping), rasterFrame) < 1e-6 &&
                target.Document.Elements.Single().ImageFrame?.SequenceEqual(contour) == true);
            check(kind + " undo restores the tight original capture frame", target.Undo() && Error(PlaneFrame(select, mapping), body) < 1e-6);
        }
        using var replacement = new SKBitmap(32, 24);
        replacement.Erase(SKColors.Red);
        using var replacementImage = SKImage.FromBitmap(replacement);
        using var encoded = replacementImage.Encode(SKEncodedImageFormat.Png, 100);
        ImageImportCommand.Insert(target, [AssetImport.Normalize(encoded.ToArray())], target.Document.Viewport, replace: true);
        var replaced = target.Document.Elements.Single();
        check("replacing a perspective capture clears its source-specific contour and frames the complete replacement",
            replaced.ImageFrame is null && replaced.AssetId != originalAsset && Error(PlaneFrame(select, mapping), rasterFrame) < 1e-6);
        check("undo replacement restores the exact captured source and tight contour", target.Undo() &&
            target.Document.Elements.Single().AssetId == originalAsset &&
            target.Document.Elements.Single().ImageFrame?.SequenceEqual(contour) == true && Error(PlaneFrame(select, mapping), body) < 1e-6);
    }

    private static CaptureSelection? Gesture(EditorController controller, CanvasMapping mapping, CaptureShape shape)
    {
        CaptureSelection? selection = null;
        controller.CaptureSelectionCompleted += value => selection = value;
        if (shape is CaptureShape.Rectangle or CaptureShape.Ellipse)
        {
            Pointer(controller, OverlayPointerKind.Down, mapping.ToScreen(new(120, 100)));
            Pointer(controller, OverlayPointerKind.Move, mapping.ToScreen(new(620, 480)));
            Pointer(controller, OverlayPointerKind.Up, mapping.ToScreen(new(620, 480)));
        }
        else
        {
            PointD[] contour = [new(160, 150), new(470, 120), new(515, 330), new(260, 380)];
            if (shape == CaptureShape.PolygonLasso)
            {
                foreach (var point in contour)
                {
                    Pointer(controller, OverlayPointerKind.Down, point);
                    Pointer(controller, OverlayPointerKind.Up, point);
                }
                controller.HandleCaptureKey(0x0D);
            }
            else
            {
                Pointer(controller, OverlayPointerKind.Down, contour[0]);
                foreach (var point in contour.Skip(1)) Pointer(controller, OverlayPointerKind.Move, point);
                Pointer(controller, OverlayPointerKind.Up, contour[^1]);
            }
        }
        return selection;
    }

    private static void Borders(EditorSession target, EditorController select, CanvasMapping mapping,
        CaptureSelection selection, RectD expectedBody, Action<string, bool> check)
    {
        const double width = 8;
        target.UpdateSelection(image => { image.ImageBorderWidth = width; image.Style.Stroke = 0xFFFF4400; });
        var reference = Bounds(selection.Points.SelectMany(point => Enumerable.Range(0, 4096).Select(index =>
        {
            double angle = index * Math.Tau / 4096;
            return mapping.ScreenToPlane.TransformPoint(point + new PointD(Math.Cos(angle) * width, Math.Sin(angle) * width));
        })));
        check(selection.Shape + " bordered perspective capture selection tightly includes the visible outside stroke",
            Error(PlaneFrame(select, mapping), reference) < .002);
        target.UpdateSelection(image => image.Style.Stroke = 0x00445566);
        check(selection.Shape + " invisible capture border does not enlarge its selection", Error(PlaneFrame(select, mapping), expectedBody) < 1e-6);
        target.UpdateSelection(image => { image.ImageBorderWidth = 0; image.Style.Stroke = 0xFFFF4400; });
        check(selection.Shape + " removing the editable border restores the exact captured body frame", Error(PlaneFrame(select, mapping), expectedBody) < 1e-6);
    }

    private static void Copies(EditorSession target, RectD expected, Action<string, bool> check)
    {
        var image = target.Document.Elements.Single();
        var cloned = image.Clone();
        var loaded = JsonSerializer.Deserialize<SceneDocument>(JsonSerializer.Serialize(target.Document))!;
        SceneValidation.Validate(loaded);
        var clipboard = ObjectClipboardCommand.CreateSnapshot(target);
        check("capture contour survives clone, serialized document and editable object copy",
            cloned.ImageFrame is { Count: >= 4 } && !ReferenceEquals(cloned.ImageFrame, image.ImageFrame) &&
            Error(ElementGeometry.Bounds(cloned), expected) < 1e-6 &&
            Error(ElementGeometry.Bounds(loaded.Elements.Single()), expected) < 1e-6 &&
            Error(ElementGeometry.Bounds(clipboard.Elements.Single()), expected) < 1e-6);
        string directory = Path.Combine(AppContext.BaseDirectory, "perspective-capture-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var project = new ProjectStore();
        string projectPath = Path.Combine(directory, "capture.floatcanvas");
        project.SaveAsync(projectPath, target.Document, target.Assets).GetAwaiter().GetResult();
        var reopened = project.LoadAsync(projectPath).GetAwaiter().GetResult();
        var recovery = new RecoveryStore(Path.Combine(directory, "recovery"));
        recovery.SaveAsync(new WorkspaceState { LiveDocument = target.Document }, target.Assets).GetAwaiter().GetResult();
        var restored = recovery.LoadAsync().GetAwaiter().GetResult();
        check("perspective screenshot frame and source survive project and recovery packages",
            reopened.Document.Elements.Single().ImageFrame?.SequenceEqual(image.ImageFrame ?? []) == true &&
            Error(ElementGeometry.Bounds(reopened.Document.Elements.Single()), expected) < 1e-6 &&
            restored?.State.LiveDocument.Elements.Single().ImageFrame?.SequenceEqual(image.ImageFrame ?? []) == true &&
            Error(ElementGeometry.Bounds(restored.State.LiveDocument.Elements.Single()), expected) < 1e-6 &&
            reopened.Assets.Get(image.AssetId!).Data.Span.SequenceEqual(target.Assets.Get(image.AssetId!).Data.Span));
    }

    private static void Crop(EditorSession target, EditorController select, CanvasMapping mapping, RectD body,
        Action<string, bool> check)
    {
        var original = target.Document.Elements.Single();
        var sourcePixels = target.Assets.Get(original.AssetId!).Data.ToArray();
        var center = mapping.ToScreen(body.Center);
        var screenCrop = new RectD(center.X - 40, center.Y - 30, 80, 60);
        var expected = Bounds(EditorController.Corners(screenCrop).Select(mapping.ScreenToPlane.TransformPoint));
        select.Tool = EditorTool.Crop;
        Pointer(select, OverlayPointerKind.Down, new(screenCrop.Left, screenCrop.Top), OverlayModifiers.Control);
        Pointer(select, OverlayPointerKind.Move, new(screenCrop.Right, screenCrop.Bottom), OverlayModifiers.Control);
        Pointer(select, OverlayPointerKind.Up, new(screenCrop.Right, screenCrop.Bottom), OverlayModifiers.Control);
        select.Tool = EditorTool.Select;
        var cropped = target.Document.Elements.Single();
        check("cropping a perspective screenshot clips its frame to the visible crop without changing source pixels",
            cropped.ImageCrop is not null && Error(PlaneFrame(select, mapping), expected) < 1e-6 &&
            target.Assets.Get(cropped.AssetId!).Data.Span.SequenceEqual(sourcePixels));
        check("cropped screenshot frame is restored by one undo and redo", target.Undo() &&
            Error(PlaneFrame(select, mapping), body) < 1e-6 && target.Redo() && Error(PlaneFrame(select, mapping), expected) < 1e-6);
    }

    private static void LegacyAndBillboard(CaptureRasterResult raster, CanvasMapping mapping, Action<string, bool> check)
    {
        var legacy = new EditorSession(Document());
        var ordinary = SnapshotImageCommand.Insert(legacy, new(raster.PngBytes, raster.Bounds, true), mapping.PlaneToScreen);
        var expected = Bounds(EditorController.Corners(raster.Bounds).Select(mapping.ScreenToPlane.TransformPoint));
        check("ordinary projective image without capture metadata retains its complete texture rectangle",
            ordinary.ImageFrame is null && Error(PlaneFrame(new(legacy), mapping), expected) < 1e-6);
        check("billboard fixture resolves its independent insertion depth", mapping.TryGetEqualDepthLine(new(200, 250), out var depth));
        var billboards = new EditorSession(Document());
        var billboard = SnapshotImageCommand.Insert(billboards, raster, mapping.PlaneToScreen, depth);
        var corners = new EditorController(billboards).SelectionScreenCorners;
        check("billboard screenshots retain their screen-aligned texture frame", billboard.IsBillboard && billboard.ImageFrame is null &&
            corners.Zip(EditorController.Corners(raster.Bounds)).All(pair => pair.First.DistanceTo(pair.Second) < 1e-6));
    }

    private static void Pointer(EditorController controller, OverlayPointerKind kind, PointD screen, OverlayModifiers modifiers = OverlayModifiers.None) =>
        controller.Pointer(new(kind, new(screen.X, screen.Y), OverlayMouseButton.Left, modifiers));

    private static RectD PlaneFrame(EditorController controller, CanvasMapping mapping) =>
        Bounds(controller.SelectionScreenCorners.Select(mapping.ScreenToPlane.TransformPoint));
    private static RectD Bounds(IEnumerable<PointD> points)
    {
        var all = points.ToArray();
        return new(all.Min(point => point.X), all.Min(point => point.Y),
            all.Max(point => point.X) - all.Min(point => point.X), all.Max(point => point.Y) - all.Min(point => point.Y));
    }
    private static double Error(RectD actual, RectD expected) => new[] { Math.Abs(actual.Left - expected.Left),
        Math.Abs(actual.Top - expected.Top), Math.Abs(actual.Right - expected.Right), Math.Abs(actual.Bottom - expected.Bottom) }.Max();

    private static SceneDocument Document()
    {
        var matrix = new Projective2D(1, .18, 25, .04, 1, 18, .0003, .001, 1);
        return new() { Viewport = new(0, 0, 800, 600), Canvas = new() { Projection = new() { Enabled = true,
            Matrix = matrix, CellSize = 100,
            Quad = EditorController.Corners(new(0, 0, 800, 600)).Select(matrix.TransformPoint).ToList() } } };
    }

    private static void ExportPreview(EditorSession target, EditorController select, CaptureShape shape)
    {
        if (Environment.GetEnvironmentVariable("FLOATCANVAS_CAPTURE_BOUNDS_PREVIEW") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        using var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(35, 39, 48));
        using var renderer = new SceneRenderer();
        renderer.Draw(canvas, target.Document, target.Assets, includeBackground: false);
        var points = select.SelectionScreenCorners;
        if (points.Count == 4)
        {
            using var builder = new SKPathBuilder();
            builder.MoveTo((float)points[0].X, (float)points[0].Y);
            foreach (var point in points.Skip(1)) builder.LineTo((float)point.X, (float)point.Y);
            builder.Close();
            using var path = builder.Detach();
            using var paint = new SKPaint { Color = SKColors.Yellow, StrokeWidth = 2, Style = SKPaintStyle.Stroke, IsAntialias = true };
            canvas.DrawPath(path, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(folder, shape + ".png"), encoded.ToArray());
    }
}
