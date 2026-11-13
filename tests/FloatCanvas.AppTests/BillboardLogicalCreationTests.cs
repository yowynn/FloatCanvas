using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using FloatCanvas.Rendering;
using SkiaSharp;
using System.Text.Json;

internal static class BillboardLogicalCreationTests
{
    internal static EditorSession Scene()
    {
        var scene = new SceneDocument { Viewport = new(0, 0, 800, 700) };
        scene.Canvas.Projection.Enabled = true;
        scene.Canvas.Projection.Matrix = new(1, 0, 0, 0, -.4, 700, 0, -.002, 1);
        scene.Canvas.Projection.Quad = [new(0, 0), new(800, 0), new(800, 700), new(0, 700)];
        scene.Canvas.Projection.PlaneBounds = new(0, 0, 800, 700);
        return new(scene);
    }
    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;
    internal static void Send(EditorController editor, OverlayPointerKind kind, PointD point) =>
        editor.Pointer(new(kind, new(point.X, point.Y), OverlayMouseButton.Left, OverlayModifiers.None));

    internal static void Run(Action<string, bool> check)
    {
        foreach (bool drag in new[] { false, true })
        {
            CanvasElement Create(double y)
            {
                var session = Scene();
                var editor = new EditorController(session) { Tool = EditorTool.Rectangle, BillboardMode = true, DefaultWidth = 160, DefaultHeight = 100 };
                editor.Style.StrokeWidth = 4;
                Send(editor, OverlayPointerKind.Down, new(drag ? 220 : 300, drag ? y - 50 : y));
                Send(editor, OverlayPointerKind.Up, new(drag ? 380 : 300, drag ? y + 50 : y));
                return session.Document.Elements.Single();
            }
            var far = Create(350); var close = Create(600); var map = CanvasMapping.Create(Scene().Document);
            string mode = drag ? "drag" : "click";
            check(mode + " creation does not store depth compensation in its transform", Near(far.Transform.M11, 1) && Near(close.Transform.M11, 1));
            check(mode + " preserves the logical stroke preset", far.Style.StrokeWidth == 4 && close.Style.StrokeWidth == 4);
            if (!drag)
            {
                check("click retains both logical dimensions at every depth", far.Width == 160 && close.Width == 160 && far.Height == 100 && close.Height == 100);
                check("click keeps centers under the pointer", map.ProjectedBounds(far).Center.DistanceTo(new(300, 350)) < 1e-6 && map.ProjectedBounds(close).Center.DistanceTo(new(300, 600)) < 1e-6);
                check("fixed logical preset looks smaller at the far depth", map.ProjectedBounds(far).Width < map.ProjectedBounds(close).Width);
            }
            else check("drag preserves the requested screen outline", Near(map.ProjectedBounds(far).Width, 160) && Near(map.ProjectedBounds(close).Width, 160));
            var moved = far.Clone(); moved.BillboardAnchor = close.BillboardAnchor;
            var farMatrix = map.ElementToScreen(moved); var closeMatrix = map.ElementToScreen(close);
            check(mode + " equal depth restores equal visible strokes", Near(farMatrix.M11 * moved.Style.StrokeWidth, closeMatrix.M11 * close.Style.StrokeWidth));
            if (!drag) check("same preset at equal depth restores equal size", Near(map.ProjectedBounds(moved).Width, map.ProjectedBounds(close).Width));
        }
        ToolFamilies(check);
        TextAndImages(check);
        Capture(check);
        StrokesAndPreservation(check);
    }

    private static void ToolFamilies(Action<string, bool> check)
    {
        foreach (var tool in new[] { EditorTool.Rectangle, EditorTool.RoundedRectangle, EditorTool.Ellipse, EditorTool.Symbol, EditorTool.Number, EditorTool.Emoji, EditorTool.Presentation })
        {
            var session = Scene();
            var editor = new EditorController(session) { Tool = tool, BillboardMode = true, CornerRadius = 9 };
            editor.Style.StrokeWidth = 4; editor.Style.FontSize = 36;
            Send(editor, OverlayPointerKind.Down, new(300, 500));
            var candidate = editor.ScreenBillboardCandidate!;
            check(tool + " down previews solved logical size and ground", candidate is not null && candidate.Transform == Affine2D.Identity &&
                editor.CurrentCanvasMapping.ProjectedBounds(candidate, false).Center.DistanceTo(new(300, 500)) < .01);
            Send(editor, OverlayPointerKind.Up, new(300, 500));
            var committed = session.Document.Elements.Single();
            check(tool + " preview and commit use the same placement", JsonSerializer.Serialize(candidate) == JsonSerializer.Serialize(committed));
            check(tool + " leaves logical styles and radius untouched", committed.Style.StrokeWidth == 4 && committed.Style.FontSize == 36 &&
                (tool is not EditorTool.RoundedRectangle || committed.CornerRadius == 9));
            session.Undo(); check(tool + " can undo and redo once", session.Document.Elements.Count == 0 && session.Redo() && session.Document.Elements.Count == 1);
        }
        var changed = Scene(); var controller = new EditorController(changed) { Tool = EditorTool.Rectangle, BillboardMode = true };
        Send(controller, OverlayPointerKind.Down, new(300, 500));
        var clickWidth = controller.ScreenBillboardCandidate!.Width;
        Send(controller, OverlayPointerKind.Move, new(470, 580));
        var draft = controller.ScreenBillboardCandidate!;
        check("crossing the click threshold switches from preset size to drawn outline", draft.Width != clickWidth &&
            controller.CurrentCanvasMapping.ProjectedBounds(draft, false).Width == 170);
        controller.Cancel(); check("cancel leaves no object or history", changed.Document.Elements.Count == 0 && changed.UndoCount == 0);
        Send(controller, OverlayPointerKind.Down, new(300, 500));
        changed.Document.Canvas.Projection.Matrix = Projective2D.Identity; changed.NotifyChanged();
        Send(controller, OverlayPointerKind.Up, new(300, 500));
        check("changed projection cannot commit an old candidate", changed.Document.Elements.Count == 0 && changed.UndoCount == 0);
        var cameraSession = Scene(); var cameraEditor = new EditorController(cameraSession) { Tool = EditorTool.Rectangle, BillboardMode = true };
        var camera = new CanvasViewTransform(1.5, new(20, -100));
        var mapping = CanvasMapping.CreateViewing(cameraSession.Document, camera);
        cameraEditor.SetCanvasView(mapping, default, new object(), viewing: camera);
        cameraEditor.ResumePresentedPicking();
        var pointer = camera.ToView(new(300, 500));
        Send(cameraEditor, OverlayPointerKind.Down, pointer); Send(cameraEditor, OverlayPointerKind.Up, pointer);
        var item = cameraSession.Document.Elements.Single();
        check("controller camera placement keeps logical preset and pointer center", item.Width == 160 && item.Height == 100 && item.Transform == Affine2D.Identity &&
            mapping.ProjectedBounds(item, false).Center.DistanceTo(pointer) < .01);
    }

    private static void TextAndImages(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer();
        foreach (double y in new[] { 350d, 550d })
        {
            var session = Scene(); var map = CanvasMapping.Create(session.Document);
            using var edit = new InlineTextEdit(session, null, new(120, y), new() { FontSize = 24 }, renderer, billboardScreenFirst: true);
            edit.Input.Text = "logical text";
            var one = edit.ResolvedScreenDraft!; edit.Input.Text += "\nsecond line";
            var two = edit.ResolvedScreenDraft!;
            check("text relayout retains logical font and original input reference", two.Style.FontSize == 24 && two.Transform == Affine2D.Identity &&
                two.Height > one.Height && edit.LocalToScreen.TransformPoint(default).DistanceTo(new(120, y)) < .01);
            edit.Finish(true); check("text commit matches reflow preview", JsonSerializer.Serialize(two) == JsonSerializer.Serialize(session.Document.Elements.Single()));
        }
        using var surface = SKSurface.Create(new SKImageInfo(100, 60)); surface.Canvas.Clear(SKColors.CornflowerBlue);
        using var snapshot = surface.Snapshot(); using var bytes = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        var image = new NormalizedImage(bytes.ToArray(), 100, 60);
        foreach (double zoom in new[] { .5, 1d, 2d })
        {
            var session = Scene(); var camera = new CanvasViewTransform(zoom, new(20, 30));
            var map = CanvasMapping.CreateViewing(session.Document, camera);
            ImageImportCommand.InsertBillboard(session, [image], new(0, 0, 20, 20), null, camera.ToView(new(300, 500)), placementMapping: map);
            var item = session.Document.Elements.Single();
            check("bitmap sizing ignores observation zoom and visible screen area", item.Width == 100 && item.Height == 60 && item.Transform == Affine2D.Identity &&
                map.ProjectedBounds(item, false).Center.DistanceTo(camera.ToView(new(300, 500))) < .01 && session.Assets.Get(item.AssetId!).Data.Span.SequenceEqual(image.PngBytes));
        }
        var target = Scene(); target.Document.Canvas.Projection.PlaneBounds = new(0, 0, 200, 100);
        var fitted = BillboardImagePlacement.Prepare(target, CanvasMapping.Create(target.Document), 1000, 500, new(300, 500));
        check("image fit uses eighty percent of logical canvas without upscaling", fitted.Width == 160 && fitted.Height == 80);
        var descriptor = new WindowSourceDescriptor { BindingIntent = Guid.NewGuid(), TitleHint = "synthetic", ProcessNameHint = "test", WindowClassHint = "test" };
        var window = WindowSourceInsertion.Prepare(target, descriptor, 1000, 500, null, target.Document.Viewport, true, screenCenter: new(300, 500));
        check("new window source uses the same logical image fit", window.Width == fitted.Width && window.Height == fitted.Height && window.Transform == Affine2D.Identity);
        var graph = new SceneGraph(target.Document);
        var group = graph.InsertGroup(new() { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] });
        graph.InsertElement(new() { Kind = ElementKind.Rectangle, Width = 10, Height = 10 }, group.Id); target.EnterScope(group.Id);
        var settings = new CanvasSettingsTarget(target, independent: true);
        settings.Change(canvas => { canvas.Projection.Enabled = true; canvas.Projection.Quad = [new(0, 0), new(80, 0), new(80, 50), new(0, 50)];
            canvas.Projection.PlaneBounds = new(0, 0, 80, 50); });
        var independentBounds = BillboardImagePlacement.LogicalBounds(settings.Settings, settings.Viewport);
        var independent = BillboardImagePlacement.Prepare(target, CanvasMapping.Create(target.Document), 1000, 500, new(300, 500), independentBounds);
        check("independent group image fit follows its target canvas instead of the host document", independent.Width == 64 && independent.Height == 32);
    }

    private static void Capture(Action<string, bool> check)
    {
        foreach (double y in new[] { 350d, 550d })
        {
            var session = Scene(); var map = CanvasMapping.Create(session.Document);
            var editor = new EditorController(session) { Tool = EditorTool.CaptureRegion, BillboardMode = true, CaptureBorderWidth = 4 };
            CaptureSelection? selection = null; editor.CaptureSelectionCompleted += value => selection = value;
            Send(editor, OverlayPointerKind.Down, new(200, y)); Send(editor, OverlayPointerKind.Move, new(360, y + 80));
            var preview = editor.CapturePreview!;
            Send(editor, OverlayPointerKind.Up, new(360, y + 80));
            var image = SnapshotImageCommand.PrepareBillboardElement(new([], selection!.Bounds, true) { ImageBorderWidth = selection.BillboardLogicalBorderWidth!.Value }, map, null);
            var bounds = map.ProjectedBounds(image, false); map.TryGetDepthScale(image.BillboardAnchor, out var scale);
            Console.WriteLine("CAPTURE_LOGICAL " + JsonSerializer.Serialize(new { Actual = bounds, Expected = selection.Bounds, image.ImageBorderWidth, image.Transform }));
            check("capture keeps screen frame and logical border separately", bounds.Center.DistanceTo(selection.Bounds.Center) < .01 && Near(bounds.Width, selection.Bounds.Width) &&
                Near(bounds.Height, selection.Bounds.Height) && image.ImageBorderWidth == 4 && Near(image.Transform.M11, 1) && Near(image.Transform.M22, 1));
            check("capture border preview equals committed visible border", Near(preview.BorderWidth, image.ImageBorderWidth * scale));
            var captureScene = new SceneDocument { Viewport = session.Document.Viewport, Elements = [new CanvasElement {
                Kind = ElementKind.Rectangle, Width = 800, Height = 700, Style = new() { Fill = 0xff123456, StrokeWidth = 0 } }] };
            var raster = SnapshotImageCommand.Render(captureScene, [], selection with { Source = CaptureSource.CanvasOnly }, null, [captureScene.Viewport]);
            var stored = SnapshotImageCommand.CreateBillboardElement(session.Assets, raster, map, null);
            using var pixels = SKBitmap.Decode(raster.PngBytes);
            check("real snapshot pipeline preserves captured pixels and logical border", pixels.Width == 160 && pixels.Height == 80 &&
                pixels.GetPixel(80, 40) == new SKColor(0x12, 0x34, 0x56) && stored.ImageBorderWidth == 4 &&
                session.Assets.Get(stored.AssetId!).Data.Span.SequenceEqual(raster.PngBytes));
        }
    }

    private static void StrokesAndPreservation(Action<string, bool> check)
    {
        foreach (var tool in new[] { EditorTool.Line, EditorTool.Arrow, EditorTool.DoubleArrow, EditorTool.Freehand, EditorTool.Highlighter })
        {
            var session = Scene(); var editor = new EditorController(session) { Tool = tool, BillboardMode = true };
            double pen = tool == EditorTool.Highlighter ? 20 : 4;
            editor.Style.StrokeWidth = pen; editor.Style.ArrowHeadSize = 18; editor.Style.StrokePattern = StrokePattern.Dashed;
            Send(editor, OverlayPointerKind.Down, new(200, 350)); Send(editor, OverlayPointerKind.Move, new(260, 400));
            var preview = editor.ScreenBillboardCandidate!;
            Send(editor, OverlayPointerKind.Up, new(260, 400));
            var item = session.Document.Elements.Single();
            check(tool + " drag preserves logical pen dash and arrow preset", item.Style.StrokeWidth == pen && item.Style.ArrowHeadSize == 18 && item.Style.StrokePattern == StrokePattern.Dashed &&
                Near(item.Transform.M11, 1) && Near(item.Transform.M22, 1));
            check(tool + " stroke preview scale agrees with final output", Near(editor.CurrentCanvasMapping.ElementToScreen(preview).M11, editor.CurrentCanvasMapping.ElementToScreen(item).M11));
        }
        var old = Scene(); var map = CanvasMapping.Create(old.Document);
        var legacy = new CanvasElement { Kind = ElementKind.Rectangle, Width = 160, Height = 100, Style = new() { StrokeWidth = 4 } };
        BillboardGeometry.TryResolveScreenBottom(legacy, map, Projective2D.FromAffine(Affine2D.Translation(220, 300)), out var oldPlacement, out _);
        legacy.IsBillboard = true; legacy.BillboardAnchor = oldPlacement.Anchor; legacy.BillboardHeight = oldPlacement.Height; legacy.Transform = oldPlacement.Linear;
        old.Add(legacy); var serialized = JsonSerializer.Serialize(old.Document.Elements[0]);
        var editorNew = new EditorController(old) { Tool = EditorTool.Rectangle, BillboardMode = true };
        old.SetSelection([]); Send(editorNew, OverlayPointerKind.Down, new(600, 600)); Send(editorNew, OverlayPointerKind.Up, new(600, 600));
        check("creating new content leaves even unsaved legacy compensation untouched", old.Document.Elements.Count == 2 && JsonSerializer.Serialize(old.Document.Elements[0]) == serialized);
        old.Undo(); old.Redo(); check("undo and redo do not migrate preexisting billboards", JsonSerializer.Serialize(old.Document.Elements[0]) == serialized);
        var sourceAssets = new AssetLibrary();
        using var bitmap = SKSurface.Create(new SKImageInfo(100, 60)); bitmap.Canvas.Clear(SKColors.Red);
        using var snapshot = bitmap.Snapshot(); using var png = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        string asset = sourceAssets.Add(png.ToArray());
        var stamp = new LoadedStamp(new() { Elements = [new() { Kind = ElementKind.Image, Width = 100, Height = 60, AssetId = asset }] }, sourceAssets);
        var target = Scene(); StampLibrary.PlaceBillboardRaster(stamp, target, null, new(300, 500));
        var placed = target.Document.Elements.Single();
        check("flattened stamp is a logical new image and keeps original pixels", placed.Width == 100 && placed.Height == 60 && placed.Transform == Affine2D.Identity &&
            target.Assets.Get(placed.AssetId!).Data.Span.SequenceEqual(png.ToArray()));
        var stale = Scene(); var staleEditor = new EditorController(stale) { Tool = EditorTool.Rectangle, BillboardMode = true };
        Send(staleEditor, OverlayPointerKind.Down, new(300, 500));
        stale.Document.Canvas.BackgroundColor = 0xff000000; stale.NotifySettingsChanged();
        Send(staleEditor, OverlayPointerKind.Up, new(300, 500));
        check("intermediate content revision invalidates a private candidate", stale.Document.Elements.Count == 0 && !stale.IsTransactionActive && stale.UndoCount == 0);
        var location = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "billboard-old-" + Guid.NewGuid().ToString("N") + ".floatcanvas");
        var store = new FloatCanvas.Storage.ProjectStore();
        // This synthetic horizon fixture needs a fully consistent reference quad for package validation.
        old.Document.Canvas.Projection.PlaneBounds = new(0, -1000, 800, 1000);
        old.Document.Canvas.Projection.Quad = new[] { new PointD(0, -1000), new PointD(800, -1000), new PointD(800, 0), new PointD(0, 0) }
            .Select(old.Document.Canvas.Projection.Matrix.TransformPoint).ToList();
        var beforeSave = JsonSerializer.Serialize(old.Document.Elements);
        System.Threading.Tasks.Task.Run(async () => {
            await store.SaveAsync(location, old.Document, old.Assets);
            var loaded = await store.LoadAsync(location);
            check("project roundtrip leaves legacy and new object transforms styles and geometry intact", JsonSerializer.Serialize(loaded.Document.Elements) == beforeSave);
        }).GetAwaiter().GetResult();
    }
}
