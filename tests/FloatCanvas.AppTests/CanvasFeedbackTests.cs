using System.Reflection;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class CanvasFeedbackTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        var failures = new List<string>();
        void Expect(string name, bool result)
        {
            Console.WriteLine((result ? "PASS " : "FAIL ") + name);
            if (!result) failures.Add(name);
        }
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        MonitorInfo[] monitors = [new("feedback-primary", new(0, 0, 640, 360), new(0, 0, 640, 330), 96, 96, true),
            new("feedback-secondary", new(100, 360, 400, 300), new(100, 360, 400, 270), 120, 120, false)];
        Func<IReadOnlyList<MonitorInfo>> screens = () => monitors;
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Hidden, null,
            [OverlayMonitorScope.AllScreens, screens, false], null)!;
        using var desktop = new DesktopSession(app, host);
        Field<System.Windows.Forms.NotifyIcon>("_tray").Visible = false;
        typeof(OverlayHost).GetProperty("IsVisible")!.SetValue(host, true);
        typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(host, true);
        var editor = Field<EditorController>("_editor");
        editor.BackgroundPickingEnabled = false;
        int uiThread=Environment.CurrentManagedThreadId, commandThread=0;
        Task command = Task.Run(() => (Task)Call("RunCommand",
            new Func<Task>(() => { commandThread=Environment.CurrentManagedThreadId; return Task.CompletedTask; }))!);
        Await(command);
        Expect("background command entry marshals its work back to the production Dispatcher", commandThread==uiThread);
        Call("CurrentGroupCanvasView");
        Expect("multi-screen viewing reference is the primary screen", Field<RectD>("_canvasViewViewport") == new RectD(0, 0, 640, 360));
        var screenBounds = monitors.Select(item => new RectD(item.Bounds.Left, item.Bounds.Top, item.Bounds.Width, item.Bounds.Height)).ToArray();
        Expect("offset monitor layouts use exposed physical edge segments instead of the envelope", CanvasEdgePan.Velocity(new(50, 359), screenBounds).Y > 0 &&
            CanvasEdgePan.Velocity(new(200, 359), screenBounds) == default);
        Expect("an untouched unbound canvas is clean", !(bool)Call("IsDirty", editor.Session)!);
        var element = new CanvasElement { Kind = ElementKind.Rectangle, Width = 60, Height = 40,
            Transform = Affine2D.Translation(20, 30) };
        editor.Session.Add(element);
        Call("CurrentGroupCanvasView");
        var view = new CanvasViewTransform(2, new(27, 35));
        Call("ChangeCanvasViewing", view, false);
        Call("SetGroupCanvasMode", GroupCanvasMode.Temporary);
        Call("CurrentGroupCanvasView");
        Expect("root isolation shares the full-project camera", Field<CanvasViewTransform>("_canvasView") == view);
        editor.Session.Document.Canvas.BackgroundColor = 0xff123456;
        editor.Session.Document.RootPreviewCanvases[RootCanvasKind.Foreground] = new() { Canvas = new() { BackgroundColor = 0xffabcdef } };
        editor.Session.SetRootCanvas(RootCanvasKind.Foreground);
        Call("SetGroupCanvasMode", GroupCanvasMode.RootProject); Call("CurrentGroupCanvasView");
        Call("ChangeCanvasViewing", view, false); Call("SetGroupCanvasMode", GroupCanvasMode.Temporary);
        var isolatedRoot = (GroupCanvasView)Call("CurrentGroupCanvasView")!;
        var rootTarget = (CanvasSettingsTarget)Call("CaptureCanvasSettingsTarget")!;
        Expect("root isolation changes visibility while sharing authored canvas settings and view", !rootTarget.Independent &&
            rootTarget.Settings.BackgroundColor == 0xff123456 && isolatedRoot.Document.Canvas.BackgroundColor == 0xff123456 && Field<CanvasViewTransform>("_canvasView") == view);
        editor.Session.SetRootCanvas(RootCanvasKind.Main);
        Call("SetGroupCanvasMode", GroupCanvasMode.Temporary); Call("CurrentGroupCanvasView");
        var group = new SceneGraph(editor.Session.Document).InsertGroup(new() { Transform = Affine2D.Translation(250, 120),
            OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [], PreviewCanvas = new() { Viewport = new(0, 0, 640, 360) } });
        new SceneGraph(editor.Session.Document).ReparentPreservingWorld([element.Id], group.Id);
        editor.Session.NotifyChanged(); editor.Session.SetEditScope(group.Id);
        Call("CurrentGroupCanvasView");
        Expect("first independent opening preserves authored placement", Field<CanvasViewTransform>("_canvasView") == CanvasViewTransform.Identity);
        using (var editing = (CanvasPreviewSnapshot)Call("CaptureCanvasPreview")!)
            Expect("editing isolates the entered group", editing.Document.Groups.All(item => item.Id != group.Id));
        Call("ChangeCanvasViewing", new CanvasViewTransform(3, new(12, 34)), false);
        typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(host, false);
        using (var presentation = (CanvasPreviewSnapshot)Call("CaptureCanvasPreview")!)
            Expect("pass-through renders the complete root graph", presentation.Document.Groups.Any(item => item.Id == group.Id) && presentation.Context.View is null);
        typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(host, true);
        Call("CurrentGroupCanvasView");
        Expect("reentering editing restores the independent group camera", Field<CanvasViewTransform>("_canvasView") == new CanvasViewTransform(3, new(12, 34)));
        editor.Tool = EditorTool.Canvas;
        typeof(DesktopSession).GetField("_canvasNavigationSpaceHeld", Hidden)!.SetValue(desktop, true);
        string before = JsonSerializer.Serialize(editor.Session.Document);
        var initial = Field<CanvasViewTransform>("_canvasView");
        Pointer(OverlayPointerKind.Down, 150, 120);
        Pointer(OverlayPointerKind.Move, 180, 140);
        Pointer(OverlayPointerKind.Up, 180, 140);
        Expect("Space left drag navigates without editing artwork", Field<CanvasViewTransform>("_canvasView").Offset == initial.Offset + new PointD(30, 20) && before == JsonSerializer.Serialize(editor.Session.Document));
        initial = Field<CanvasViewTransform>("_canvasView");
        Call("HandleCanvasPointer", new OverlayPointerEvent(OverlayPointerKind.Wheel, new(180, 140), OverlayMouseButton.None, OverlayModifiers.Control, 120));
        Expect("Ctrl wheel zooms the view", Field<CanvasViewTransform>("_canvasView").Zoom > initial.Zoom);
        editor.Tool = EditorTool.Rectangle;
        before = JsonSerializer.Serialize(editor.Session.Document);
        initial = Field<CanvasViewTransform>("_canvasView");
        Call("OnCanvasKeyInput", new OverlayKeyEvent(0x20, OverlayModifiers.None, false));
        Expect("canvas key entry rejects an editing fixture without native input ownership", !host.HasEditingInput && !Field<bool>("_canvasNavigationSpaceHeld"));
        // This fixture verifies camera geometry without activating an input surface.
        // Owned key down/up routing is covered by SessionKeyboardRouterTests.
        Set("_canvasNavigationSpaceHeld", true);
        Pointer(OverlayPointerKind.Down, 180, 140); Pointer(OverlayPointerKind.Move, 200, 150); Pointer(OverlayPointerKind.Up, 200, 150);
        Expect("held Space temporarily pans another tool without drawing or changing tools", editor.Tool == EditorTool.Rectangle &&
            Field<CanvasViewTransform>("_canvasView").Offset == initial.Offset + new PointD(20, 10) && before == JsonSerializer.Serialize(editor.Session.Document));
        ((Action<int>)typeof(OverlayHost).GetField("KeyReleased", Hidden)!.GetValue(host)!)(0x20);
        Expect("Space release ends the temporary navigation state", !Field<bool>("_canvasNavigationSpaceHeld"));
        Call("HandleCanvasPointer", new OverlayPointerEvent(OverlayPointerKind.Down, new(200, 150), OverlayMouseButton.Middle, OverlayModifiers.None));
        Expect("middle button starts view navigation without changing objects", !editor.IsTemporaryMove && Field<PointD?>("_canvasViewPanLast") is not null);
        Call("HandleCanvasPointer", new OverlayPointerEvent(OverlayPointerKind.Up, new(200, 150), OverlayMouseButton.Middle, OverlayModifiers.None));
        editor.Tool = EditorTool.Canvas;
        var gridTarget = (CanvasSettingsTarget)Call("CaptureCanvasSettingsTarget")!;
        var gridOrigin = gridTarget.Settings.Grid.FlatOrigin ?? new PointD(320, 180);
        initial = Field<CanvasViewTransform>("_canvasView");
        var authoredTransforms = editor.Session.Document.Elements.Select(item => item.Transform).ToArray();
        string beforeGridClick = JsonSerializer.Serialize(editor.Session.Document); int beforeGridHistory = editor.Session.UndoCount;
        foreach (var kind in new[] { OverlayPointerKind.Down, OverlayPointerKind.Up })
            Call("HandleCanvasPointer", new OverlayPointerEvent(kind, new(200, 150), OverlayMouseButton.Left, OverlayModifiers.Shift));
        Expect("Shift clicking without movement does not dirty the grid or add undo", beforeGridClick == JsonSerializer.Serialize(editor.Session.Document) && editor.Session.UndoCount == beforeGridHistory);
        foreach (var (kind, point) in new[] { (OverlayPointerKind.Down, new OverlayPoint(200, 150)), (OverlayPointerKind.Move, new OverlayPoint(220, 170)), (OverlayPointerKind.Up, new OverlayPoint(220, 170)) })
            Call("HandleCanvasPointer", new OverlayPointerEvent(kind, point, OverlayMouseButton.Left, OverlayModifiers.Shift));
        Expect("Shift left drag moves only the flat grid and leaves camera and layers unchanged", gridTarget.Settings.Grid.FlatOrigin is { } movedOrigin && movedOrigin != gridOrigin &&
            Field<CanvasViewTransform>("_canvasView") == initial && authoredTransforms.SequenceEqual(editor.Session.Document.Elements.Select(item => item.Transform)));
        double cellSize = CanvasGridGeometry.ResolveCellSize(gridTarget.Settings, gridTarget.Viewport);
        Call("HandleCanvasPointer", new OverlayPointerEvent(OverlayPointerKind.Wheel, new(220, 170), OverlayMouseButton.None, OverlayModifiers.Shift, 120));
        Expect("Shift wheel leaves the camera and grid unchanged", CanvasGridGeometry.ResolveCellSize(gridTarget.Settings, gridTarget.Viewport) == cellSize && Field<CanvasViewTransform>("_canvasView") == initial);
        Call("HandleCanvasPointer", new OverlayPointerEvent(OverlayPointerKind.Wheel, new(220, 170), OverlayMouseButton.None, OverlayModifiers.None, -120));
        Expect("bare wheel leaves camera, grid and artwork unchanged", Field<CanvasViewTransform>("_canvasView") == initial &&
            CanvasGridGeometry.ResolveCellSize(gridTarget.Settings, gridTarget.Viewport) == cellSize && authoredTransforms.SequenceEqual(editor.Session.Document.Elements.Select(item => item.Transform)));
        editor.Session.SetEditScope(null); Call("SetGroupCanvasMode", GroupCanvasMode.RootProject);
        var moving = new CanvasElement { Kind = ElementKind.Rectangle, Width = 60, Height = 40,
            Transform = Affine2D.Translation(100, 80), Style = new() { Foreground = 0, Background = 0xff20cc50, StrokeWidth = 0 } };
        editor.Session.ReplaceDocument(new() { Viewport = new(0, 0, 640, 360), Elements = [moving] });
        editor.Tool = EditorTool.Select; Call("CurrentGroupCanvasView");
        using (var snapshot = (CanvasPreviewSnapshot)Call("CaptureCanvasPreview")!)
        using (var worker = new CanvasPreviewRenderer())
        {
            var frame = worker.Render(snapshot);
            var coordinator = typeof(DesktopSession).GetField("_canvasPreview", Hidden)!.GetValue(desktop)!;
            coordinator.GetType().GetProperty("Frame", Hidden)!.SetValue(coordinator, frame);
            host.Render(); editor.ResumePresentedPicking();
            Pointer(OverlayPointerKind.Down, 120, 100);
            Pointer(OverlayPointerKind.Move, 50, 359);
            long now = Stopwatch.GetTimestamp();
            Set("_canvasEdgeEntered", now - Stopwatch.Frequency);
            Set("_canvasEdgeLastTick", now - Stopwatch.Frequency / 50);
            Call("TickCanvasEdgePan");
            host.Render();
            Expect("pending edge-pan raster keeps the live drag instead of rolling it back", editor.IsMovingCanvasSelection && editor.Session.IsTransactionActive &&
                editor.Session.Document.Elements.Single().Transform != Affine2D.Translation(100, 80));
            Pointer(OverlayPointerKind.Up, 50, 359);
        }
        editor.Cancel();
        var a = new CanvasElement { Kind = ElementKind.Rectangle, Width = 40, Height = 30, Transform = Affine2D.Translation(10, 20) };
        var b = a.Clone(); b.Id = Guid.NewGuid(); b.Transform = Affine2D.Translation(70, 60);
        editor.Session.ReplaceDocument(new() { Viewport = new(0, 0, 640, 660), AuthoringReference = new(new(0, 0, 640, 360), new(0, 0, 640, 360)), Elements = [a, b] });
        editor.Session.SetSelection([a.Id, b.Id]);
        var created = editor.Session.GroupSelection()!.Value;
        var hostedBefore = SceneEvaluation.Build(editor.Session.Document).RenderDocument.Elements.Select(item => item.Transform).ToArray();
        editor.Session.SetEditScope(created); Call("SetGroupCanvasMode", GroupCanvasMode.Temporary);
        var groupedView = (GroupCanvasView)Call("CurrentGroupCanvasView")!;
        var ink = SceneEvaluation.Build(groupedView.Document).RenderDocument.Elements;
        var center = ink.Select(item => CanvasMapping.Create(groupedView.Document).ProjectedBounds(item)).Aggregate(RectD.Union).Center;
        Expect("new ordinary group authors content at its own primary-sized canvas center", center.DistanceTo(new(320, 180)) < 1e-7 &&
            hostedBefore.SequenceEqual(SceneEvaluation.Build(editor.Session.Document).RenderDocument.Elements.Select(item => item.Transform)));
        editor.Session.SetEditScope(null); Call("SetGroupCanvasMode", GroupCanvasMode.RootProject);
        var state = Call("FileState", editor.Session)!;
        state.GetType().GetField("SavedContentVersion")!.SetValue(state, editor.Session.ContentVersion);
        Await((Task)Call("ReplaceCanvasAsync")!);
        bool cleanNew = !(bool)Call("IsDirty", editor.Session)!;
        Expect("new canvas after a saved workspace has a clean unbound baseline", cleanNew);
        var confirmation = (Task<bool>)Call("NeedsNewCanvasConfirmationAsync")!; Await(confirmation);
        Expect("untouched new canvas does not arm the toolbar's history confirmation", !confirmation.Result);
        if (cleanNew)
        {
            Await((Task)Call("ReplaceCanvasAsync")!);
            Expect("repeated new on an untouched canvas stays clean", !(bool)Call("IsDirty", editor.Session)!);
        }
        editor.Session.Add(new() { Kind = ElementKind.Rectangle, Width = 20, Height = 20 });
        Expect("actual artwork changes still mark an unbound canvas dirty", (bool)Call("IsDirty", editor.Session)!);
        check("canvas user-feedback regressions: " + string.Join("; ", failures), failures.Count == 0);

        T Field<T>(string name) => (T)typeof(DesktopSession).GetField(name, Hidden)!.GetValue(desktop)!;
        void Set(string name, object value) => typeof(DesktopSession).GetField(name, Hidden)!.SetValue(desktop, value);
        object? Call(string name, params object?[] arguments) => typeof(DesktopSession).GetMethod(name, Hidden)!.Invoke(desktop, arguments);
        void Pointer(OverlayPointerKind kind, double x, double y) => Call("HandleCanvasPointer", new OverlayPointerEvent(kind, new(x, y), OverlayMouseButton.Left, OverlayModifiers.None));
    }
    private static void Await(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(20))
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
            System.Windows.Threading.Dispatcher.PushFrame(frame); Thread.Sleep(1);
        }
        if (!task.IsCompleted) throw new TimeoutException("Canvas feedback task did not complete.");
        task.GetAwaiter().GetResult();
    }
}
