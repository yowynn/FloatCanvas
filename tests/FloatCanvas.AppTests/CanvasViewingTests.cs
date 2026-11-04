using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using FloatCanvas.Rendering;
using SkiaSharp;
using System.Text.Json;
using System.Reflection;
using System.Windows;
using System.Diagnostics;

internal static class CanvasViewingTests
{
    internal static void Benchmark(Action<string, bool> check)
    {
        var document = new SceneDocument { Viewport = new(0, 0, 6400, 2160), Elements = Enumerable.Range(0, 5)
            .Select(i => new CanvasElement { Kind = ElementKind.Rectangle, Width = 100, Height = 80,
                Transform = Affine2D.Translation(300 + i * 120, 250), Style = new() { Background = 0xff20cc50, StrokeWidth = 0 } }).ToList() };
        var context = new CanvasPreviewContext(new object(), new object(), default, false, false, false, document, GroupEpoch: new object());
        using var worker = new CanvasPreviewRenderer(); var times = new List<double>(); int accepted = 0;
        for (int i = 0; i < 45; i++)
        {
            var camera = new CanvasViewTransform(1, new(i * 3, i * 2));
            using var snapshot = new CanvasPreviewSnapshot { Context = context with { View = camera }, SceneIdentity = document,
                Document = document, Resources = [], Outputs = [new(0, 0, 3840, 2160), new(3840, 0, 2560, 1440)] };
            var clock = Stopwatch.StartNew(); using var frame = worker.Render(snapshot);
            if (i >= 5) times.Add(clock.Elapsed.TotalMilliseconds);
            if (DesktopSession.CanPresentCanvasFrame(frame, context with { View = camera.Pan(new(3, 2)) })) accepted++;
        }
        times.Sort();
        Console.WriteLine($"NAVIGATION_BENCHMARK outputs=3840x2160+2560x1440 elements=5 samples={times.Count} medianMs={times[times.Count/2]:F2} p95Ms={times[(int)(times.Count*.95)-1]:F2} completedAccepted={accepted}/45");
        check("every completed camera frame remains presentable under continuous newer input", accepted == 45);
    }
    internal static void Run(Action<string, bool> check)
    {
        NavigationFrames(check);
        PerspectiveNavigation(check);
        foreach (var root in new[] { RootCanvasKind.Main, RootCanvasKind.Foreground, RootCanvasKind.Background })
        {
            var element = new CanvasElement { Kind = ElementKind.Rectangle, Width = 80, Height = 40,
                Transform = Affine2D.Translation(500, 260), Style = new() { Foreground = 0, Background = 0xff20cc50, StrokeWidth = 0 } };
            var document = new SceneDocument { Viewport = new(0, 0, 320, 180), Elements = [element] };
            SceneCanvasRoots.Assign(document, element.Id, root);
            var session = new EditorSession(document);
            string before = JsonSerializer.Serialize(document);
            long version = session.ContentVersion;
            var view = new CanvasViewTransform(.5, new(-150, -50));
            using var worker = new CanvasPreviewRenderer();
            using var viewedSnapshot = Snapshot(view);
            using var frame = worker.Render(viewedSnapshot);
            using var surface = SKSurface.Create(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
            frame.Pixels.Draw(surface.Canvas);
            using var image = surface.Snapshot(); using var pixels = SKBitmap.FromImage(image);
            check("view draws content brought in from beyond the old viewport for " + root, pixels.GetPixel(110, 90).Green > 150);
            check("camera does not paint the old clipped frame for " + root, pixels.GetPixel(250, 90).Alpha == 0);
            var editor = new EditorController(session) { Tool = EditorTool.Select };
            var epoch = new object();
            editor.SetCanvasView(CanvasMapping.CreateViewing(document, view, root), default(PointD), epoch,
                displayDocument: document, displayRootCanvas: root, viewing: view);
            session.SetRootCanvas(root);
            editor.ResumePresentedPicking();
            editor.Pointer(new(OverlayPointerKind.Down, new(110, 90), OverlayMouseButton.Left, OverlayModifiers.None));
            editor.Pointer(new(OverlayPointerKind.Up, new(110, 90), OverlayMouseButton.Left, OverlayModifiers.None));
            check("displayed ink can be selected at its viewed position for " + root, session.Selection.Contains(element.Id));
            check("viewing and selecting preserve authored bytes and content history for " + root,
                before == JsonSerializer.Serialize(document) && session.ContentVersion == version && !session.IsTransactionActive);
            using var normalSnapshot = Snapshot(null); using var normal = worker.Render(normalSnapshot);
            surface.Canvas.Clear(SKColors.Transparent); normal.Pixels.Draw(surface.Canvas);
            using var normalImage = surface.Snapshot(); using var normalPixels = SKBitmap.FromImage(normalImage);
            check("leaving the temporary view restores formal presentation for " + root, normalPixels.GetPixel(110, 90).Alpha == 0);

            CanvasPreviewSnapshot Snapshot(CanvasViewTransform? camera) => new()
            {
                Context = new(session, new object(), default, false, false, false, document, View: camera),
                SceneIdentity = document, Document = document, FrozenEvaluation = new(document), Resources = [],
                Outputs = [new(0, 0, 320, 180)], ContentRevision = session.PreviewRevision, GraphChanged = true
            };
        }
        var framed = new SceneDocument { Viewport = new(0, 0, 320, 180), AuthoringReference = new(new(0, 0, 640, 360), new(0, 0, 640, 360)),
            Elements = [new() { Kind = ElementKind.Rectangle, Width = 50, Height = 30, Transform = Affine2D.Translation(500, 260),
                Style = new() { Foreground = 0, Background = 0xff20cc50, StrokeWidth = 0 } }] };
        string original = JsonSerializer.Serialize(framed);
        using (var renderer = new FloatCanvas.Rendering.SceneRenderer())
        using (var export = SKBitmap.Decode(renderer.ExportPng(framed, new(), transparent: true)))
        using (var capture = SKBitmap.Decode(renderer.ExportPng(framed, new(), transparent: true, useAuthoredFrame: false)))
        {
            check("ordinary export retains the saved artboard and draws content outside the smaller runtime viewport", export.Width == 640 && export.Height == 360 && export.GetPixel(520, 270).Green > 150);
            check("explicit viewport capture remains separate from authored-frame export", capture.Width == 320 && capture.Height == 180 && original == JsonSerializer.Serialize(framed));
        }
        DynamicContent(check);
        DesktopNavigation(check);
    }

    private static void NavigationFrames(Action<string, bool> check)
    {
        var document = new SceneDocument { Viewport = new(0, 0, 320, 180) };
        var context = new CanvasPreviewContext(new object(), new object(), default, false, false, false,
            document, GroupEpoch: new object(), View: new CanvasViewTransform(1, new(10, 20)));
        using var worker = new CanvasPreviewRenderer();
        using var snapshot = new CanvasPreviewSnapshot { Context = context, SceneIdentity = document,
            Document = document, Resources = [], Outputs = [new(0, 0, 320, 180)] };
        using var frame = worker.Render(snapshot);
        var next = context with { View = new CanvasViewTransform(1, new(20, 30)) };
        check("continuous navigation presents completed pixels while a newer camera is rendering",
            DesktopSession.CanPresentCanvasFrame(frame, next));
        check("navigation never accepts pixels from a previous group or document",
            !DesktopSession.CanPresentCanvasFrame(frame, next with { GroupEpoch = new object() }) &&
            !DesktopSession.CanPresentCanvasFrame(frame, next with { DocumentEpoch = new object() }));
    }

    private static void PerspectiveNavigation(Action<string, bool> check)
    {
        foreach (double slope in new[] { .001, -.001 })
        {
            var element = new CanvasElement { Kind = ElementKind.Rectangle, Width = 30, Height = 20,
                Transform = Affine2D.Translation(100, 70), Style = new() { Foreground = 0, Background = 0xff20cc50, StrokeWidth = 0 } };
            var document = new SceneDocument { Viewport = new(0, 0, 320, 180), Elements = [element] };
            var projection = CanvasCalibration.CreateInitial(document.Viewport, 8, 6);
            projection.Enabled = true; projection.Matrix = new(1, .2, 0, .1, 1, 0, 0, slope, 1);
            document.Canvas.Projection = projection; document.Canvas.Grid.Visible = false;
            string before = JsonSerializer.Serialize(document);
            var view = new CanvasViewTransform(1.2, new(10, 5)).PanPlane(new(24, -12));
            var point = new PointD(115, 80);
            var expected = view.ToView(projection.Matrix.TransformPoint(point + new PointD(20, -10)));
            var mapping = CanvasMapping.CreateViewing(document, view);
            check("perspective drag follows plane axes for slope " + slope,
                mapping.ToScreen(point).DistanceTo(expected) < 1e-7 && mapping.TryToPlane(expected, out var inverse) && inverse.DistanceTo(point) < 1e-7);
            using var worker = new CanvasPreviewRenderer();
            using var snapshot = new CanvasPreviewSnapshot { Context = new(new object(), new object(), default, false, false, false, View: view),
                SceneIdentity = document, Document = document, Resources = [], Outputs = [new(0, 0, 320, 180)] };
            using var frame = worker.Render(snapshot);
            using var surface = SKSurface.Create(new SKImageInfo(320, 180)); frame.Pixels.Draw(surface.Canvas);
            using var image = surface.Snapshot(); using var pixels = SKBitmap.FromImage(image);
            check("perspective raster matches picking for slope " + slope, pixels.GetPixel((int)expected.X, (int)expected.Y).Green > 150);
            var applied = CanvasViewApplication.Prepare(document, view);
            var baked = CanvasMapping.Create(applied).ElementToScreen(applied.Elements[0]).TransformPoint(new(15, 10));
            check("applying plane navigation retains visible geometry for slope " + slope, baked.DistanceTo(expected) < 1e-6);
            check("plane navigation leaves authored projection and layer data unchanged for slope " + slope, JsonSerializer.Serialize(document) == before);
        }
    }

    private static void DynamicContent(Action<string, bool> check)
    {
        using var raw = SKSurface.Create(new SKImageInfo(400, 300));
        raw.Canvas.Clear(SKColors.Red); using var blue = new SKPaint { Color = SKColors.Blue }; raw.Canvas.DrawRect(200, 0, 200, 300, blue);
        using var capture = raw.Snapshot(); using var backdrop = new DesktopBackdrop([new(new(0, 0, 400, 300), capture.Subset(new(0, 0, 400, 300))!)]);
        using var png = capture.Encode(); var assets = new AssetLibrary(); string asset = assets.Add(png.ToArray());
        var window = new CanvasElement { Kind = ElementKind.Image, AssetId = asset, Width = 100, Height = 100,
            Transform = Affine2D.Translation(50, 80), WindowSource = new() { BindingIntent = Guid.NewGuid() }, Style = new() { StrokeWidth = 0 } };
        var effect = new CanvasElement { Kind = ElementKind.LivePixelate, Width = 50, Height = 50, Transform = Affine2D.Translation(250, 80),
            Effects = [new() { Kind = ImageEffectKind.Pixelate, Amount = 4 }], Style = new() { StrokeWidth = 0 } };
        var document = new SceneDocument { Viewport = new(0, 0, 400, 300), Elements = [window, effect] }; document.Canvas.Grid.Visible = false;
        string before = JsonSerializer.Serialize(document); var view = new CanvasViewTransform(.5, new(20, 30)); object epoch = new();
        using var worker = new CanvasPreviewRenderer(); int sequence = 0;
        foreach (var color in new[] { SKColors.Red, SKColors.Green })
        {
            using var pixels = new SKBitmap(10, 10); pixels.Erase(color);
            using var lease = WindowFrameLease.FromBitmapCopy(new(document.ResourceId, new(null, window.Id)), window.WindowSource.BindingIntent, new(Guid.NewGuid(), 1, ++sequence), pixels);
            using var snapshot = new CanvasPreviewSnapshot
            {
                Context = new(new object(), new object(), default, false, false, false, WindowEpoch: epoch, View: view),
                Document = document, SceneIdentity = document, FrozenEvaluation = new(document), Resources = assets.Resources.ToArray(), Outputs = [new(0, 0, 400, 300)],
                Backdrop = backdrop.Retain(), WindowFrames = WindowFrameSet.Capture(epoch, [lease]), WindowOwnerContext = epoch, ContentRevision = 1
            };
            using var frame = worker.Render(snapshot); using var target = SKSurface.Create(new SKImageInfo(400, 300)); frame.Pixels.Draw(target.Canvas);
            using var image = target.Snapshot(); using var result = SKBitmap.FromImage(image);
            check("viewed window source keeps updating at its viewed location " + sequence, result.GetPixel(60, 80) == color);
            check("viewed live filter retains its authored desktop sample rather than sampling its new view position " + sequence,
                result.GetPixel(155, 80).Blue > 230 && result.GetPixel(155, 80).Red < 20 && result.GetPixel(275, 100).Alpha == 0);
        }
        check("view navigation never rewrites dynamic source descriptors, effect amounts or original asset bytes", before == JsonSerializer.Serialize(document) && assets.Count == 1 && assets.Get(asset).Data.Span.SequenceEqual(png.ToArray()));
        var projected = document.Clone();
        projected.Canvas.Projection = CanvasCalibration.CreateInitial(projected.Viewport, 8, 6);
        projected.Canvas.Projection.Enabled = true; projected.Canvas.Projection.Matrix = Projective2D.Identity;
        using var planeSnapshot = new CanvasPreviewSnapshot
        {
            Context = new(new object(), new object(), default, false, false, false, View: CanvasViewTransform.Identity.PanPlane(new(-100, 0))),
            Document = projected, SceneIdentity = projected, Resources = assets.Resources.ToArray(), Outputs = [new(0, 0, 400, 300)], Backdrop = backdrop.Retain()
        };
        using var planeFrame = worker.Render(planeSnapshot);
        using var planeSurface = SKSurface.Create(new SKImageInfo(400, 300)); planeFrame.Pixels.Draw(planeSurface.Canvas);
        using var planeImage = planeSurface.Snapshot(); using var planePixels = SKBitmap.FromImage(planeImage);
        check("plane navigation preserves authored live-filter sampling while moving its visible mask",
            planePixels.GetPixel(175, 100).Blue > 230 && planePixels.GetPixel(175, 100).Red < 20 && planePixels.GetPixel(275, 100).Alpha == 0);
    }

    private static void DesktopNavigation(Action<string, bool> check)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var monitor = new MonitorInfo("canvas-navigation-hidden", new(0, 0, 640, 360), new(0, 0, 640, 320), 96, 96, true);
        Func<IReadOnlyList<MonitorInfo>> screens = () => [monitor];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), hidden, null,
            [OverlayMonitorScope.PrimaryScreen, screens, false], null)!;
        using var desktop = new DesktopSession(application, host);
        Field<System.Windows.Forms.NotifyIcon>("_tray").Visible = false;
        typeof(OverlayHost).GetProperty("IsVisible")!.SetValue(host, true);
        typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(host, true);
        var editor = Field<EditorController>("_editor");
        editor.BackgroundPickingEnabled = false;
        Invoke("CurrentGroupCanvasView");
        var badge = (PixelBounds)typeof(DesktopSession).GetProperty("CanvasViewingBadgeBounds", hidden)!.GetValue(desktop)!;
        check("the always-visible viewing scale stays above the taskbar inside the working area", badge.Left >= monitor.WorkArea.Left && badge.Bottom <= monitor.WorkArea.Bottom && badge.Right <= monitor.WorkArea.Right);
        var document = editor.Session.Document;
        string before = JsonSerializer.Serialize(document); long revision = editor.Session.ContentVersion;
        editor.Tool = EditorTool.Canvas;
        typeof(DesktopSession).GetField("_canvasNavigationSpaceHeld", hidden)!.SetValue(desktop, true);
        Pointer(OverlayPointerKind.Wheel, OverlayMouseButton.None, 200, 100, 120);
        var camera = Field<CanvasViewTransform>("_canvasView");
        check("Space wheel uses a pointer-centered camera in the production desktop handler", Math.Abs(camera.Zoom - 1.1) < 1e-9 && camera.ToCanvas(new(200, 100)).DistanceTo(new(200, 100)) < 1e-7);
        Pointer(OverlayPointerKind.Down, OverlayMouseButton.Left, 200, 100);
        Pointer(OverlayPointerKind.Move, OverlayMouseButton.Left, 250, 130);
        Pointer(OverlayPointerKind.Up, OverlayMouseButton.Left, 250, 130);
        var panned = Field<CanvasViewTransform>("_canvasView");
        typeof(DesktopSession).GetField("_canvasNavigationSpaceHeld", hidden)!.SetValue(desktop, false);
        check("canvas left drag pans without entering temporary object movement", panned.Offset.DistanceTo(camera.Offset + new PointD(50, 30)) < 1e-7 && !editor.IsTemporaryMove);
        check("production navigation leaves project bytes and undo content unchanged", before == JsonSerializer.Serialize(document) && revision == editor.Session.ContentVersion && !editor.Session.IsTransactionActive);
        using (var snapshot = (CanvasPreviewSnapshot)Invoke("CaptureCanvasPreview")!)
            check("production preview captures the exact current viewing transform", snapshot.Context.View == panned);
        typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(host, false);
        using (var snapshot = (CanvasPreviewSnapshot)Invoke("CaptureCanvasPreview")!)
            check("pass-through presentation omits the editor view", snapshot.Context.View is null);
        typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(host, true);
        Invoke("CurrentGroupCanvasView");
        check("returning to editing restores the same observation", Field<CanvasViewTransform>("_canvasView") == panned);
        Invoke("ExecuteCanvasViewing", CanvasViewingCommand.Reset);
        check("reset view returns to identity without editing content", Field<CanvasViewTransform>("_canvasView") == CanvasViewTransform.Identity && revision == editor.Session.ContentVersion);
        foreach (bool billboard in new[] { false, true })
        foreach (double zoom in new[] { 1d, 4d })
        {
            editor.Tool = EditorTool.Select;
            Invoke("ExecuteCanvasViewing", CanvasViewingCommand.Reset);
            var element = new CanvasElement { Kind = ElementKind.Rectangle, Width = 50, Height = 40,
                Transform = Affine2D.Translation(570, 160), IsBillboard = billboard, BillboardAnchor = new(595, 220), BillboardHeight = 40,
                Style = new() { Foreground = 0, Background = 0xff20cc50, StrokeWidth = 0 } };
            editor.Session.Add(element); Invoke("CurrentGroupCanvasView"); editor.ResumePresentedPicking();
            if (zoom != 1) Invoke("ChangeCanvasViewing", new CanvasViewTransform(zoom, new(-1800, -550)), false);
            editor.ResumePresentedPicking();
            var initialCamera = Field<CanvasViewTransform>("_canvasView");
            var localGrab = new PointD(12, 12);
            var grabbed = editor.CurrentCanvasMapping.ElementToScreen(element).TransformPoint(localGrab);
            string beforeMove = JsonSerializer.Serialize(editor.Session.Document); int history = editor.Session.UndoCount;
            ObjectPointer(OverlayPointerKind.Down, grabbed);
            var edge = new PointD(639, grabbed.Y);
            ObjectPointer(OverlayPointerKind.Move, edge);
            check($"edge pan begins only during a real selected-object move billboard={billboard} zoom={zoom}", editor.IsMovingCanvasSelection && Field<OverlayPointerEvent?>("_canvasEdgePointer") is not null);
            long now = Stopwatch.GetTimestamp();
            Set("_canvasEdgeEntered", now - Stopwatch.Frequency);
            Set("_canvasEdgeLastTick", now - Stopwatch.Frequency / 50);
            Invoke("TickCanvasEdgePan");
            var actual = editor.CurrentCanvasMapping.ElementToScreen(editor.Session.Document.Elements.Single(item => item.Id == element.Id)).TransformPoint(localGrab);
            check($"auto-pan retains the initially grabbed point under the stationary mouse billboard={billboard} zoom={zoom}", actual.DistanceTo(edge) < 1e-6 && Field<CanvasViewTransform>("_canvasView").Offset.X < initialCamera.Offset.X);
            ObjectPointer(OverlayPointerKind.Up, edge);
            check($"release stops auto-pan and commits exactly one object undo billboard={billboard} zoom={zoom}", Field<OverlayPointerEvent?>("_canvasEdgePointer") is null && editor.Session.UndoCount == history + 1 && editor.Session.Undo() && JsonSerializer.Serialize(editor.Session.Document) == beforeMove);
            editor.Session.SetSelection([]);
        }
        var pendingDocument = new SceneDocument { Viewport = new(0, 0, 640, 360) };
        pendingDocument.Canvas.Projection.Enabled = true;
        pendingDocument.Canvas.Projection.Matrix = new(1, 0, 0, 0, -.4, 700, 0, -.002, 1);
        pendingDocument.Canvas.Projection.Quad = [new(0, 0), new(640, 0), new(640, 360), new(0, 360)];
        editor.Session.ReplaceDocument(pendingDocument); Invoke("CurrentGroupCanvasView");
        Invoke("ChangeCanvasViewing", new CanvasViewTransform(2, new(10, 20)), false);
        editor.Tool = EditorTool.Rectangle; editor.BillboardWorkflow = BillboardCreationWorkflow.ScreenFirst; editor.BillboardMode = true;
        editor.Style.Foreground = 0; editor.Style.Background = 0xffff3050; editor.ResumePresentedPicking();
        ObjectPointer(OverlayPointerKind.Down, new(100, 100));
        ObjectPointer(OverlayPointerKind.Move, new(200, 180));
        using (var snapshot = (CanvasPreviewSnapshot)Invoke("CaptureCanvasPreview")!)
        {
            var draft = snapshot.ScreenDraft;
            check("screen-first draft remains private and unresolved in the viewed sky", draft is { ResolvedWorldElement: null } && pendingDocument.Elements.Count == 0);
            check("pending screen draft cancels exactly one camera transform", draft is not null &&
                snapshot.Context.View!.Value.ToView(ElementGeometry.LocalToPlane(draft.ScreenElement).TransformPoint(default)).DistanceTo(new(100, 100)) < 1e-7);
            using var worker = new CanvasPreviewRenderer(); using var frame = worker.Render(snapshot);
            using var surface = SKSurface.Create(new SKImageInfo(640, 360)); frame.Pixels.Draw(surface.Canvas);
            using var image = surface.Snapshot(); using var pixels = SKBitmap.FromImage(image);
            check("pending viewed screen ink is rasterized under the pointer without a second zoom", pixels.GetPixel(150, 140).Alpha > 0 && pixels.GetPixel(350, 250).Alpha == 0);
        }
        editor.Cancel();

        var window = new CanvasElement { Kind = ElementKind.Image, Width = 50, Height = 40, Transform = Affine2D.Translation(900, 100),
            WindowSource = new() { BindingIntent = Guid.NewGuid() } };
        var windowDocument = new SceneDocument { Viewport = new(0, 0, 640, 360), Elements = [window] };
        editor.Session.ReplaceDocument(windowDocument); Invoke("SetGroupCanvasMode", GroupCanvasMode.RootProject); Invoke("ExecuteCanvasViewing", CanvasViewingCommand.Reset);
        Invoke("UpdateWindowSourceRuntime");
        check("offscreen window source initially has no runtime capture demand", Field<WindowRuntimeDemand[]>("_windowDemands").Length == 0);
        var evaluation = Field<EvaluatedScene>("_windowEvaluation"); string unchanged = JsonSerializer.Serialize(windowDocument);
        Invoke("ChangeCanvasViewing", new CanvasViewTransform(1, new(-500, 0)), false); Invoke("UpdateWindowSourceRuntime");
        check("panning an offscreen window source into view updates its real runtime demand", Field<WindowRuntimeDemand[]>("_windowDemands") is [var demand] && demand.BindingIntent == window.WindowSource.BindingIntent);
        check("window demand navigation reuses authored evaluation and leaves source settings unchanged", ReferenceEquals(evaluation, Field<EvaluatedScene>("_windowEvaluation")) && unchanged == JsonSerializer.Serialize(windowDocument));
        typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(host, false); Invoke("UpdateWindowSourceRuntime");
        check("pass-through capture demands ignore the locally remembered editor camera", Field<WindowRuntimeDemand[]>("_windowDemands").Length == 0);
        typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(host, true); Invoke("UpdateWindowSourceRuntime");
        check("returning to editing restores window capture demand for the restored view", Field<WindowRuntimeDemand[]>("_windowDemands").Length == 1);
        var graph = new SceneGraph(windowDocument); var group = graph.InsertGroup(new() { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [], Transform = Affine2D.Translation(2000, 0), PreviewCanvas = new() { Viewport = windowDocument.Viewport } });
        var child = window.Clone(); child.Id = Guid.NewGuid(); child.Transform = Affine2D.Translation(50, 100); child.WindowSource!.BindingIntent = Guid.NewGuid();
        graph.InsertElement(child, group.Id, worldCoordinates: false); editor.Session.NotifyChanged(); editor.Session.SetEditScope(group.Id);
        Invoke("SetGroupCanvasMode", GroupCanvasMode.Temporary); Invoke("CurrentGroupCanvasView"); Invoke("UpdateWindowSourceRuntime");
        check("independent group window demand uses local displayed placement rather than its offscreen host", Field<WindowRuntimeDemand[]>("_windowDemands") is [var localDemand] && localDemand.BindingIntent == child.WindowSource.BindingIntent);
        Invoke("ChangeCanvasViewing", new CanvasViewTransform(1, new(-500, 0)), false); Invoke("UpdateWindowSourceRuntime");
        check("panning an independent window source out of view removes its runtime demand", Field<WindowRuntimeDemand[]>("_windowDemands").Length == 0);

        // Test planning and guides with native observation explicitly suspended.
        Set("_cleanCaptureDepth", 1);
        var keyboard = new CanvasElement { Kind = ElementKind.Presentation, Width = 160, Height = 80,
            Transform = Affine2D.Translation(900, 100), Presentation = new() };
        editor.Session.ReplaceDocument(new() { Viewport = new(0, 0, 640, 360), Elements = [keyboard] });
        Invoke("SetGroupCanvasMode", GroupCanvasMode.RootProject); Invoke("ExecuteCanvasViewing", CanvasViewingCommand.Reset); Invoke("UpdatePresentationRuntime");
        check("offscreen presentation layer is excluded from the runtime viewing plan", Field<IReadOnlyList<ResolvedLeaf>>("_presentationLeaves").Count == 0);
        var keyboardEvaluation = Field<EvaluatedScene>("_presentationEvaluation");
        Invoke("ChangeCanvasViewing", new CanvasViewTransform(1, new(-500, 0)), false); Invoke("UpdatePresentationRuntime");
        check("panning presentation into view updates runtime visibility without rebuilding its authored graph", Field<IReadOnlyList<ResolvedLeaf>>("_presentationLeaves").Count == 1 &&
            ReferenceEquals(keyboardEvaluation, Field<EvaluatedScene>("_presentationEvaluation")));
        using (var guides = SKSurface.Create(new SKImageInfo(640, 360)))
        {
            Invoke("DrawPresentationLayerGuides", guides.Canvas); using var image = guides.Snapshot(); using var pixels = SKBitmap.FromImage(image);
            check("presentation editing guides follow the same post-projection viewing transform", pixels.GetPixel(420, 130).Alpha > 0 && pixels.GetPixel(100, 130).Alpha == 0);
        }
        typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(host, false); Invoke("UpdatePresentationRuntime");
        check("pass-through presentation visibility excludes the remembered editor navigation", Field<IReadOnlyList<ResolvedLeaf>>("_presentationLeaves").Count == 0);

        T Field<T>(string name) => (T)typeof(DesktopSession).GetField(name, hidden)!.GetValue(desktop)!;
        object? Invoke(string name, params object[] arguments) => typeof(DesktopSession).GetMethod(name, hidden)!.Invoke(desktop, arguments);
        void Set(string name, object value) => typeof(DesktopSession).GetField(name, hidden)!.SetValue(desktop, value);
        void Pointer(OverlayPointerKind kind, OverlayMouseButton button, double x, double y, int wheel = 0) =>
            Invoke("HandleCanvasPointer", new OverlayPointerEvent(kind, new(x, y), button, OverlayModifiers.None, wheel));
        void ObjectPointer(OverlayPointerKind kind, PointD point) =>
            Invoke("HandleCanvasPointer", new OverlayPointerEvent(kind, new(point.X, point.Y), OverlayMouseButton.Left, OverlayModifiers.None));
    }
}
