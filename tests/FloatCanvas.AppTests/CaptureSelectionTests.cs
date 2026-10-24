using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class CaptureSelectionTests
{
    internal static void Run(Action<string, bool> check)
    {
        DragSelections(check);
        Lassos(check);
        SelectionAndCancellation(check);
        BillboardCaptures(check);
        ParameterCompatibility(check);
    }

    private static void BillboardCaptures(Action<string, bool> check)
    {
        foreach (var shape in Enum.GetValues<CaptureShape>())
        {
            var document = new SceneDocument { Viewport = new(0,0,800,600), Canvas = new()
            { Projection = new() { Enabled = true, Matrix = new(1,.08,0,0,1,0,0,.001,1),
                Quad = [new(0,0),new(800,0),new(600,400),new(0,400)] } } };
            var controller = new EditorController(new(document)) { Tool = EditorTool.CaptureRegion,
                CaptureShape = shape, CaptureBorderWidth = 5, BillboardMode = true };
            CaptureSelection? captured = null; controller.CaptureSelectionCompleted += value => captured = value;
            Click(controller,new(150,250));
            var depth = controller.ActiveBillboardDepthLine;
            check(shape + " billboard capture fixes depth before beginning the original tool", depth is not null &&
                controller.CapturePreview is null && !controller.HasCaptureGesture && document.Elements.Count == 0 && controller.Session.UndoCount == 0);
            Pointer(controller,OverlayPointerKind.Down,new(100,100));
            if (shape is CaptureShape.FreehandLasso or CaptureShape.PolygonLasso)
            {
                if (shape == CaptureShape.PolygonLasso) Pointer(controller,OverlayPointerKind.Up,new(100,100));
                if (shape == CaptureShape.PolygonLasso) Click(controller,new(240,100));
                else Pointer(controller,OverlayPointerKind.Move,new(240,100));
                if (shape == CaptureShape.PolygonLasso) Click(controller,new(240,190));
                else Pointer(controller,OverlayPointerKind.Move,new(240,190));
            }
            else Pointer(controller,OverlayPointerKind.Move,new(240,190));
            check(shape + " billboard capture preview keeps screen geometry and projects preset border", controller.CapturePreview is { PlaneBounds: null } preview &&
                preview.BillboardDepthLine == depth && Math.Abs(preview.BorderWidth - 5 * depth!.Value.Scale) < 1e-7);
            if (shape == CaptureShape.PolygonLasso) controller.HandleCaptureKey(0x0D);
            else Pointer(controller,OverlayPointerKind.Up,shape == CaptureShape.FreehandLasso ? new(100,190) : new(240,190));
            check(shape + " billboard capture completes with frozen insertion depth and clears the transient guide", captured is not null &&
                captured.BillboardDepthLine == depth && controller.ActiveBillboardDepthLine is null && !controller.HasCaptureGesture && controller.CapturePreview is null);
        }
    }

    private static void DragSelections(Action<string, bool> check)
    {
        foreach (var shape in new[] { CaptureShape.Rectangle, CaptureShape.Ellipse })
        foreach (var end in new[] { new PointD(180, 150), new PointD(20, 150), new PointD(180, 10), new PointD(20, 10) })
        {
            var session = new EditorSession();
            var controller = new EditorController(session) { Tool = EditorTool.CaptureRegion, CaptureShape = shape,
                CaptureSource = CaptureSource.CanvasOnly, CaptureBorderWidth = 3, CaptureBorderColor = 0x7F112233, ScreenOrigin = new(-400, 200) };
            CaptureSelection? result = null; int completions = 0;
            controller.CaptureSelectionCompleted += value => { result = value; completions++; };
            Pointer(controller, OverlayPointerKind.Down, new(100, 80));
            Pointer(controller, OverlayPointerKind.Move, end);
            var expected = RectD.FromPoints(new(100, 80), end);
            check(shape + " capture previews normalized geometry in each drag direction " + end,
                controller.HasCaptureGesture && controller.CapturePreview?.Bounds == expected && session.Document.Elements.Count == 0 &&
                !session.IsTransactionActive && session.UndoCount == 0 && controller.RasterPreview == (-1, -1, false));
            Pointer(controller, OverlayPointerKind.Up, end);
            check(shape + " completion carries source and border without editing the document " + end,
                completions == 1 && result is { Source: CaptureSource.CanvasOnly, BorderWidth: 3, BorderColor: 0x7F112233 } && result.Bounds == expected &&
                result.Points.Count == 0 && !controller.HasCaptureGesture && controller.CapturePreview is null && session.UndoCount == 0);
            var translated = result!.Translate(controller.ScreenOrigin);
            check(shape + " selection explicitly translates document pixels to screen pixels " + end,
                translated.Bounds == (expected with { X = expected.X - 400, Y = expected.Y + 200 }) && result.Bounds == expected);
        }
        var square = new EditorController(new()) { Tool = EditorTool.CaptureRegion, CaptureShape = CaptureShape.Ellipse };
        CaptureSelection? circle = null; square.CaptureSelectionCompleted += value => circle = value;
        Pointer(square, OverlayPointerKind.Down, new(50, 50));
        Pointer(square, OverlayPointerKind.Up, new(80, 60), OverlayModifiers.Shift);
        check("Shift constrains ellipse capture to a circle without changing the stored tool", circle?.Bounds == new RectD(50, 50, 30, 30) && !square.ConstrainAspect);
        int small = 0; square.CaptureSelectionCompleted += _ => small++;
        Pointer(square, OverlayPointerKind.Down, new(10, 10)); Pointer(square, OverlayPointerKind.Up, new(11, 11));
        check("tiny click capture creates no snapshot request or transaction", small == 0 && !square.HasCaptureGesture && square.Session.UndoCount == 0);
    }

    private static void Lassos(Action<string, bool> check)
    {
        var controller = new EditorController(new()) { Tool = EditorTool.CaptureRegion, CaptureShape = CaptureShape.FreehandLasso };
        CaptureSelection? result = null; int completions = 0;
        controller.CaptureSelectionCompleted += value => { result = value; completions++; };
        Pointer(controller, OverlayPointerKind.Down, new(10, 10));
        Pointer(controller, OverlayPointerKind.Move, new(100, 10));
        Pointer(controller, OverlayPointerKind.Move, new(100, 100));
        Pointer(controller, OverlayPointerKind.Up, new(10, 100));
        check("freehand lasso closes only on mouse release and retains its contour", completions == 1 && result is { Shape: CaptureShape.FreehandLasso } &&
            result.Points.SequenceEqual(new[] { new PointD(10, 10), new PointD(100, 10), new PointD(100, 100), new PointD(10, 100) }));
        var frozen = result!;
        Pointer(controller, OverlayPointerKind.Down, new(20, 20)); controller.Cancel();
        check("completed lasso geometry cannot be cleared by the next gesture", frozen.Points.Count == 4 && frozen.Bounds == new RectD(10, 10, 90, 90));

        controller.CaptureShape = CaptureShape.PolygonLasso;
        Click(controller, new(10, 10)); Click(controller, new(90, 10));
        Pointer(controller, OverlayPointerKind.Move, new(90, 90));
        check("polygon hover previews its next edge without committing a vertex", controller.CapturePreview?.Points.Count == 3 && controller.HasCaptureGesture && completions == 1);
        Click(controller, new(90, 90));
        check("polygon Backspace removes exactly the most recent committed vertex", controller.HandleCaptureKey(0x08) && controller.CapturePreview?.Points.Count == 2);
        Click(controller, new(90, 80));
        Pointer(controller, OverlayPointerKind.Move, new(1, 200));
        check("polygon Enter completes committed vertices and excludes hover position", controller.HandleCaptureKey(0x0D) && completions == 2 &&
            result!.Points.Count == 3 && result.Bounds == new RectD(10, 10, 80, 70) && !controller.HasCaptureGesture);
        Click(controller, new(10, 10)); Click(controller, new(80, 10)); Click(controller, new(80, 80));
        Pointer(controller, OverlayPointerKind.DoubleClick, new(80, 80)); Pointer(controller, OverlayPointerKind.Up, new(80, 80));
        check("native double click finishes polygon once without a repeated vertex", completions == 3 && result!.Points.Count == 3 && !controller.HasCaptureGesture);
        Click(controller, new(10, 10)); Click(controller, new(90, 10)); Click(controller, new(90, 80)); Click(controller, new(12, 12));
        check("clicking near the polygon's first vertex closes without adding a duplicate point", completions == 4 && result!.Points.Count == 3 && !controller.HasCaptureGesture);
        Click(controller, new(10, 10));
        check("Backspace of the final polygon point cancels its transient gesture", controller.HandleCaptureKey(0x08) && !controller.HasCaptureGesture && controller.CapturePreview is null);
        Click(controller, new(10, 10)); Click(controller, new(20, 20)); Click(controller, new(30, 30)); controller.HandleCaptureKey(0x0D);
        check("collinear polygon emits no empty capture", completions == 4 && !controller.HasCaptureGesture);
        Click(controller, new(10, 10)); Click(controller, new(90, 90)); Click(controller, new(90, 10)); Click(controller, new(10, 90)); controller.HandleCaptureKey(0x0D);
        check("self intersecting even-odd lasso is valid despite cancelling signed areas", completions == 5 && result!.Points.Count == 4);
        check("lasso selection geometry never enters the document or undo stack", controller.Session.Document.Elements.Count == 0 && controller.Session.UndoCount == 0);
    }

    private static void SelectionAndCancellation(Action<string, bool> check)
    {
        var session = new EditorSession();
        var original = new CanvasElement { Kind = ElementKind.Rectangle, Width = 100, Height = 100, Style = new() { Fill = 0xFF445566 } };
        session.Add(original);
        var controller = new EditorController(session) { Tool = EditorTool.CaptureRegion };
        int requests = 0; controller.CaptureSelectionCompleted += _ => requests++;
        Pointer(controller, OverlayPointerKind.Down, new(50, 50)); Pointer(controller, OverlayPointerKind.Move, new(70, 70)); Pointer(controller, OverlayPointerKind.Up, new(70, 70));
        check("capture tool retains ordinary selected-object movement", requests == 0 && session.UndoCount == 2 &&
            session.Document.Elements.Single().Transform == Affine2D.Translation(20, 20));
        Pointer(controller, OverlayPointerKind.Down, new(50, 50), OverlayModifiers.Control);
        Pointer(controller, OverlayPointerKind.Move, new(80, 80), OverlayModifiers.Control);
        check("Ctrl begins a capture inside the selected object without modifying its selection", controller.HasCaptureGesture && session.Selection.SetEquals([original.Id]) && session.UndoCount == 2);
        check("capture Escape is handled before edit-mode exit", controller.HandleCaptureKey(0x1B) && !controller.HasCaptureGesture &&
            requests == 0 && session.UndoCount == 2 && session.Selection.SetEquals([original.Id]));
        Pointer(controller, OverlayPointerKind.Down, new(200, 200)); Pointer(controller, OverlayPointerKind.Move, new(250, 250));
        controller.Tool = EditorTool.Freehand;
        check("changing tools cancels capture geometry without a document mutation", controller.CapturePreview is null && requests == 0 && session.UndoCount == 2 && !session.IsTransactionActive);
    }

    private static void ParameterCompatibility(Action<string, bool> check)
    {
        string directory = Path.Combine(Environment.GetEnvironmentVariable("FLOATCANVAS_SNAPSHOT_TEST_OUTPUT") ?? Path.GetTempPath(), "parameters-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string file = Path.Combine(directory, "tool-state.json");
            var parameters = new ToolParameters { Tool = EditorTool.CaptureRegion, CaptureShape = CaptureShape.PolygonLasso,
                CaptureSource = CaptureSource.BackgroundAndCanvas, CaptureBorderWidth = 4, CaptureBorderColor = 0x7F123456 };
            var controller = new EditorController(new()); controller.ApplyParameters(parameters);
            check("capture options clone and apply independently of mutable source parameters", JsonSerializer.Serialize(controller.Parameters) == JsonSerializer.Serialize(parameters) &&
                JsonSerializer.Serialize(parameters.Clone()) == JsonSerializer.Serialize(parameters));
            var store = new ToolStateStore(file); store.Tools[EditorTool.CaptureRegion] = parameters;
            store.Selections[EditorTool.CaptureRegion] = "official.capture";
            store.OfficialBaselines["official.capture"] = parameters.Clone(); store.PreviewSources[EditorTool.CaptureRegion] = parameters.Clone(); store.Save();
            var restored = new ToolStateStore(file);
            check("all tool-state parameter dictionaries persist capture options", restored.Tools[EditorTool.CaptureRegion].CaptureShape == CaptureShape.PolygonLasso &&
                restored.OfficialBaselines["official.capture"].CaptureSource == CaptureSource.BackgroundAndCanvas && restored.PreviewSources[EditorTool.CaptureRegion].CaptureBorderWidth == 4 &&
                restored.Tools[EditorTool.CaptureRegion].CaptureBorderColor == 0x7F123456);
            var legacy = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            foreach (string dictionary in new[] { "Tools", "OfficialBaselines", "PreviewSources" })
                foreach (var entry in legacy[dictionary]!.AsObject())
                    foreach (string field in new[] { "CaptureShape", "CaptureSource", "CaptureBorderWidth", "CaptureBorderColor" }) entry.Value!.AsObject().Remove(field);
            string oldBytes = legacy.ToJsonString(); File.WriteAllText(file, oldBytes);
            restored = new(file);
            check("pre-capture v1 tool cache retains its original choices and gets explicit new defaults", restored.Tools[EditorTool.CaptureRegion] is
                { CaptureShape: CaptureShape.Rectangle, CaptureSource: CaptureSource.Background, CaptureBorderWidth: 0 } &&
                restored.Selections[EditorTool.CaptureRegion] == "official.capture" && restored.Tools[EditorTool.CaptureRegion].CaptureBorderColor == ColorLibrary.ForegroundRed);
            check("reading a legacy cache does not rewrite or replace the user's file", File.ReadAllText(file) == oldBytes);
            string expectedTools = JsonSerializer.Serialize(restored.Tools), expectedBaselines = JsonSerializer.Serialize(restored.OfficialBaselines), expectedPreviews = JsonSerializer.Serialize(restored.PreviewSources);
            legacy["Tools"]!["CaptureRegion"]!.AsObject().Remove("NumberStep"); string missingStepBytes = legacy.ToJsonString(); File.WriteAllText(file, missingStepBytes);
            restored = new(file);
            check("a missing legacy parameter defaults locally while other capture state and source bytes remain intact", restored.Tools[EditorTool.CaptureRegion].NumberStep == 1 &&
                JsonSerializer.Serialize(restored.Tools) == expectedTools && JsonSerializer.Serialize(restored.OfficialBaselines) == expectedBaselines &&
                JsonSerializer.Serialize(restored.PreviewSources) == expectedPreviews && restored.Selections[EditorTool.CaptureRegion] == "official.capture" && File.ReadAllText(file) == missingStepBytes);
            check("invalid capture enum and border width fail parameter validation", !new ToolParameters { CaptureShape = (CaptureShape)99 }.IsValid() &&
                !new ToolParameters { CaptureSource = (CaptureSource)99 }.IsValid() && !new ToolParameters { CaptureBorderWidth = -1 }.IsValid() &&
                !new ToolParameters { CaptureBorderWidth = double.NaN }.IsValid());
            var library = new ToolPresetLibrary(Path.Combine(directory, "tool-presets.json"));
            var saved = library.Save("capture", parameters);
            check("user capture preset round trip retains independent source shape and border", new ToolPresetLibrary(Path.Combine(directory, "tool-presets.json")).User.Single().Parameters is
                { CaptureShape: CaptureShape.PolygonLasso, CaptureSource: CaptureSource.BackgroundAndCanvas, CaptureBorderWidth: 4, CaptureBorderColor: 0x7F123456 });
            var official = library.Apply("official.capture");
            check("explicit default capture preset resets capture fields and style", official is
                { CaptureShape: CaptureShape.Rectangle, CaptureSource: CaptureSource.Background, CaptureBorderWidth: 0 } && official.Style.Stroke == ColorLibrary.ForegroundRed);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void Click(EditorController controller, PointD point)
    { Pointer(controller, OverlayPointerKind.Down, point); Pointer(controller, OverlayPointerKind.Up, point); }
    private static void Pointer(EditorController controller, OverlayPointerKind kind, PointD point, OverlayModifiers modifiers = OverlayModifiers.None) =>
        controller.Pointer(new(kind, new(point.X + controller.ScreenOrigin.X, point.Y + controller.ScreenOrigin.Y), OverlayMouseButton.Left, modifiers));
}
