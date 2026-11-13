using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;

/// <summary>Actual DesktopSession snapshot wiring on a never displayed private desktop; no input lease or physical input.</summary>
internal static class ScreenBillboardDesktopTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, bool checkEscape = true, bool handoffOnly = false)
    {
        var name = new StringBuilder(256);
        if (!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, name, 512, out _) ||
            !name.ToString().StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal))
            throw new InvalidOperationException("Use --isolated-ui-desktop --screen-billboard-desktop-only.");
        if (Application.Current is not null) throw new InvalidOperationException("Run in a fresh child process.");
        string output = Path.Combine(Environment.GetEnvironmentVariable("FLOATCANVAS_SCREEN_TEST_OUTPUT") ?? Path.GetTempPath(), "desktop-" + Guid.NewGuid().ToString("N"));
        ApplicationPaths.Configure(["--profile", Path.Combine(output, "profile"), "--primary-screen"]);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var hotkeys = FreeHotkeys();
        new UserSettings { Theme = "dark", Language = "en", Toggle = hotkeys.Item1, Hide = hotkeys.Item2,
            PreferGpuAcceleration = false, ShowFloatingLogo = false }.Save();
        var bounds = handoffOnly ? new PixelBounds(0, 0, 800, 700) : new PixelBounds(-22000, -22000, 800, 700);
        Func<IReadOnlyList<MonitorInfo>> monitors = () => [new("screen-billboard", bounds, bounds, 96, 96, true)];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Hidden, null, [OverlayMonitorScope.PrimaryScreen, monitors, false], null)!;
        using var desktop = new DesktopSession(app, host);
        if (checkEscape) DesktopSyntheticKeyboard.Allow(desktop, host);
        Field<System.Windows.Forms.NotifyIcon>(desktop, "_tray").Visible = false;
        Field<Window>(desktop, "_toolbar").Opacity = 0;
        var editor = Field<EditorController>(desktop, "_editor"); editor.BackgroundPickingEnabled = false;
        var session = editor.Session; var document = session.Document;
        document.Viewport = new(0, 0, 800, 700); document.Canvas.Grid.Visible = false;
        document.Canvas.Projection.Enabled = true;
        document.Canvas.Projection.Matrix = new(1, 0, 0, 0, -.4, 700, 0, -.002, 1);
        document.Canvas.Projection.Quad = [new(0, 0), new(800, 0), new(800, 700), new(0, 700)];
        session.NotifyChanged(); host.SetVisible(true);
        editor.Tool = EditorTool.Rectangle; editor.BillboardMode = true;
        using var renderer = new CanvasPreviewRenderer();
        if (handoffOnly)
        {
            session.Add(new CanvasElement { Kind = ElementKind.Rectangle, Width = 40, Height = 30,
                Transform = Affine2D.Translation(50, -100) });
            Acknowledge();
            int cancellationCount = 0; editor.Cancelling += () => cancellationCount++;
            Send(OverlayPointerKind.Down, 100, 100); Send(OverlayPointerKind.Move, 300, 400);
            using var live = Capture(); using var liveFrame = renderer.Render(live);
            Complete(liveFrame, live.Context, [new("screen-billboard", bounds, 0, 0, 0, 0)]);
            typeof(DesktopSession).GetField("_presentedCanvasContext", Hidden)!.SetValue(desktop, live.Context);
            cancellationCount = 0;
            Send(OverlayPointerKind.Up, 300, 400);
            var request = (OverlayFrameRequest)Activator.CreateInstance(typeof(OverlayFrameRequest), Hidden, null, [null], null)!;
            typeof(DesktopSession).GetMethod("BeginCanvasSubmission", Hidden)!.Invoke(desktop, [request]);
            check("billboard commit cannot submit an empty scene while its replacement frame is delayed", request.Deferred);
            check("own billboard commit is not reported as cancellation", cancellationCount == 0);
            check("billboard handoff commits exactly one new object", session.Document.Elements.Count == 2 && !session.IsTransactionActive);
            using var committed = Capture(); using var committedFrame = renderer.Render(committed);
            Complete(committedFrame, committed.Context, [new("screen-billboard", bounds, 0, 0, 0, 0)]);
            Send(OverlayPointerKind.Down, 500, 100); Send(OverlayPointerKind.Move, 650, 400);
            using var cancelling = Capture(); using var cancellingFrame = renderer.Render(cancelling);
            Complete(cancellingFrame, cancelling.Context, [new("screen-billboard", bounds, 0, 0, 0, 0)]);
            typeof(DesktopSession).GetField("_presentedCanvasContext", Hidden)!.SetValue(desktop, cancelling.Context);
            editor.Cancel();
            Complete(cancellingFrame, cancelling.Context, []);
            var cancelRequest = (OverlayFrameRequest)Activator.CreateInstance(typeof(OverlayFrameRequest), Hidden, null, [null], null)!;
            typeof(DesktopSession).GetMethod("BeginCanvasSubmission", Hidden)!.Invoke(desktop, [cancelRequest]);
            var restored = Field<CanvasPreviewFrame?>(desktop, "_submittingCanvasFrame");
            check("cancel submits the retained base rather than a blank or stale draft", !cancelRequest.Deferred && restored is { IsTransitionFallback: true });
            using var expectedSurface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(800, 700));
            using var actualSurface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(800, 700));
            expectedSurface.Canvas.Translate(-bounds.Left, -bounds.Top); actualSurface.Canvas.Translate(-bounds.Left, -bounds.Top);
            committedFrame.Pixels.Draw(expectedSurface.Canvas); restored!.Pixels.Draw(actualSurface.Canvas);
            using var expectedImage = expectedSurface.Snapshot(); using var actualImage = actualSurface.Snapshot();
            using var expectedPixels = SkiaSharp.SKBitmap.FromImage(expectedImage); using var actualPixels = SkiaSharp.SKBitmap.FromImage(actualImage);
            check("cancel keeps every base pixel and removes the private draft", expectedPixels.Pixels.Any(pixel => pixel.Alpha > 0) && expectedPixels.Pixels.SequenceEqual(actualPixels.Pixels));
            using var afterCancel = Capture(); using var afterCancelFrame = renderer.Render(afterCancel);
            Complete(afterCancelFrame, afterCancel.Context, [new("screen-billboard", bounds, 0, 0, 0, 0)]);
            check("replacement frame installs picking for the restored document", ReferenceEquals(Field<SceneDocument?>(editor, "presentedOwner"), session.Document));
            var definition = new PrefabDefinition { Graph = new() { Elements = [new() { Width = 20, Height = 20 }] } };
            session.Document.Definitions.Add(definition); session.NotifyChanged(); Acknowledge();
            session.PlacePrefab(definition.Id, LayerPlacement.Identity);
            var stampRequest = (OverlayFrameRequest)Activator.CreateInstance(typeof(OverlayFrameRequest), Hidden, null, [null], null)!;
            typeof(DesktopSession).GetMethod("BeginCanvasSubmission", Hidden)!.Invoke(desktop, [stampRequest]);
            check("shared insertion hands off a replacement document without blank pixels", stampRequest.Deferred);
            typeof(OverlayHost).GetProperty("IsVisible")!.SetValue(host, false);
            var hiddenRequest = (OverlayFrameRequest)Activator.CreateInstance(typeof(OverlayFrameRequest), Hidden, null, [null], null)!;
            typeof(DesktopSession).GetMethod("BeginCanvasSubmission", Hidden)!.Invoke(desktop, [hiddenRequest]);
            check("hiding the canvas never retains the old displayed scene", !hiddenRequest.Deferred);
            return;
        }
        if (checkEscape) EscapePriority(desktop, host, editor, check, Acknowledge);
        document = session.Document;
        using var initial = Capture(); using var baseFrame = renderer.Render(initial);
        Complete(baseFrame, initial.Context, [new("screen-billboard", bounds, 0, 0, 0, 0)]);
        string untouched = JsonSerializer.Serialize(document); int history = session.UndoCount;
        Send(OverlayPointerKind.Down, 100, 100);
        using var pending = Capture();
        Console.WriteLine("SCREEN_DESKTOP_PENDING " + JsonSerializer.Serialize(new
        {
            editor.Tool, editor.BillboardMode, editor.BillboardWorkflow, editor.HasScreenBillboardDraft,
            Suspended = Field<bool>(editor, "presentedPickingSuspended"), session.IsTransactionActive,
            Matrix = CanvasMapping.Create(session.Document).PlaneToScreen, editor.ScreenOrigin,
            Raw = pending.ScreenDraft?.ScreenElement.Transform, Resolved = pending.ScreenDraft?.ResolvedWorldElement?.BillboardAnchor,
            Epoch = pending.Context.ScreenDraftEpoch is not null, ContextEpoch = pending.Context.Epoch,
            Count = session.Document.Elements.Count
        }));
        check("actual DesktopSession capture includes private sky ink and pending placement", pending.ScreenDraft is { ResolvedWorldElement: null } && pending.Context.ScreenDraftEpoch is not null &&
            ElementGeometry.LocalToPlane(pending.ScreenDraft.ScreenElement).TransformPoint(ElementGeometry.LocalBounds(pending.ScreenDraft.ScreenElement).Center) == new PointD(100, 100));
        check("new draft can keep presenting the real base while its first pixels compute", DesktopSession.CanPresentCanvasFrame(baseFrame, pending.Context));
        Send(OverlayPointerKind.Move, 300, 400);
        using var valid = Capture(); using var draftFrame = renderer.Render(valid);
        check("actual snapshot carries resolved placement without publishing source", valid.ScreenDraft is { ResolvedWorldElement.IsBillboard: true } &&
            JsonSerializer.Serialize(document) == untouched && session.UndoCount == history && !draftFrame.PickingDocument!.Elements.Any(element => element.Id == valid.ScreenDraft.ScreenElement.Id));
        check("moving a draft changes request revision while retaining its lifetime identity", ReferenceEquals(pending.Context.ScreenDraftEpoch, valid.Context.ScreenDraftEpoch) && Field<long>(desktop, "_canvasDraftRevision") >= 2);
        editor.Cancel(); using var cancelled = Capture();
        check("cancelled actual snapshot drops draft and clears its lifetime", cancelled.ScreenDraft is null && cancelled.Context.ScreenDraftEpoch is null && !session.IsTransactionActive);
        check("late draft frame cannot become visible after cancellation", !DesktopSession.CanPresentCanvasFrame(draftFrame, cancelled.Context));
        using var cancelledFrame = renderer.Render(cancelled);
        Complete(cancelledFrame, cancelled.Context, [new("screen-billboard", bounds, 0, 0, 0, 0)]);
        Send(OverlayPointerKind.Down, 100, 100); using var restarted = Capture();
        check("a new draft cannot reuse pixels belonging to the cancelled lifetime", restarted.Context.ScreenDraftEpoch is not null &&
            !ReferenceEquals(restarted.Context.ScreenDraftEpoch, valid.Context.ScreenDraftEpoch) && !DesktopSession.CanPresentCanvasFrame(draftFrame, restarted.Context));
        Complete(draftFrame, valid.Context, []);
        check("successful completion of an older draft submission cannot cancel the new gesture", session.IsTransactionActive && editor.HasScreenBillboardDraft &&
            !Field<bool>(desktop, "_presentationUncertain"));
        editor.Cancel();

        document = session.Document;
        var graph = new SceneGraph(document);
        var group = graph.InsertGroup(new CanvasGroup { Name = "scope", OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] });
        var retained = graph.InsertElement(new CanvasElement { Kind = ElementKind.Rectangle, Width = 10, Height = 10, Visible = false }, group.Id);
        session.EnterScope(group.Id);
        Acknowledge();
        Send(OverlayPointerKind.Down, 100, 100); Send(OverlayPointerKind.Move, 300, 400);
        using var scoped = Capture();
        check("actual capture freezes the editing scope with the private screen draft", scoped.ScreenDraftScopeId == group.Id && scoped.ScreenDraft is not null);
        session.LeaveScope(); session.EnterScope(group.Id); using var stale = Capture();
        check("scope out and back clears an actual desktop draft without publishing it", stale.ScreenDraft is null &&
            session.Document.Elements.Count == 1 && session.Document.Elements[0].Id == retained.Id && !session.IsTransactionActive);
        check("snapshot wiring fixture never acquires an editing or keyboard isolation lease", !host.IsEditing && !host.IsKeyboardIsolationActive);
        Console.WriteLine("SCREEN_BILLBOARD_DESKTOP " + output);

        CanvasPreviewSnapshot Capture() => (CanvasPreviewSnapshot)typeof(DesktopSession).GetMethod("CaptureCanvasPreview", Hidden)!.Invoke(desktop, null)!;
        void Acknowledge()
        {
            using var snapshot = Capture(); using var frame = renderer.Render(snapshot);
            Complete(frame, snapshot.Context, [new("screen-billboard", bounds, 0, 0, 0, 0)]);
        }
        void Complete(CanvasPreviewFrame frame, CanvasPreviewContext context, OverlayOutputTiming[] outputs)
        {
            typeof(DesktopSession).GetField("_submittingCanvasContext", Hidden)!.SetValue(desktop, context);
            typeof(DesktopSession).GetField("_submittingCanvasFrame", Hidden)!.SetValue(desktop, frame with
            { Pixels = frame.Pixels.Retain(), PickingImages = frame.PickingImages?.Retain() });
            typeof(DesktopSession).GetMethod("CompleteCanvasSubmission", Hidden)!.Invoke(desktop, [new OverlayFrameTiming(0, 0, true, outputs)]);
        }
        void Send(OverlayPointerKind kind, double x, double y)
        {
            var p = new PointD(x, y) + editor.ScreenOrigin;
            editor.Pointer(new(kind, new(p.X, p.Y), OverlayMouseButton.Left, OverlayModifiers.None));
        }
    }
    private static void EscapePriority(DesktopSession desktop, OverlayHost host, EditorController editor, Action<string, bool> check, Action acknowledge)
    {
        var session = editor.Session; session.LeaveScope();
        var document = session.Document;
        var originalProjection = document.Canvas.Projection.Matrix;
        var originalTool = editor.Tool; var originalWorkflow = editor.BillboardWorkflow; bool originalMode = editor.BillboardMode;
        document.Canvas.Projection.Matrix = Projective2D.Identity;
        session.NotifyChanged(); editor.Tool = EditorTool.Rectangle;
        editor.BillboardWorkflow = BillboardCreationWorkflow.FixedDepthLine; editor.BillboardMode = true;
        string authored = JsonSerializer.Serialize(document); int history = session.UndoCount, assets = session.Assets.Count;
        var results = new List<(string Name, bool Passed)>();
        var depthPoint = document.Viewport.Center;
        SetEditing(); acknowledge(); Send(OverlayPointerKind.Down); Send(OverlayPointerKind.Up);
        Console.WriteLine("BILLBOARD_ESC_SETUP " + JsonSerializer.Serialize(new { editor.Tool, editor.BillboardMode, editor.BillboardWorkflow,
            Depth = editor.ActiveBillboardDepthLine is not null, Preview = editor.VisibleBillboardDepthLine is not null, session.IsTransactionActive,
            AuthorEqual = JsonSerializer.Serialize(document) == authored, UndoEqual = session.UndoCount == history, editor.ScreenOrigin, document.Viewport }));
        check("fixed depth setup creates no author edit or transaction", editor.ActiveBillboardDepthLine is not null && !session.IsTransactionActive && Unchanged());
        Escape();
        Console.WriteLine("BILLBOARD_ESC_FIXED " + JsonSerializer.Serialize(new { host.IsEditing, Depth = editor.ActiveBillboardDepthLine is not null, Unchanged = Unchanged() }));
        results.Add(("Esc cancels fixed billboard depth before leaving editing", host.IsEditing && editor.ActiveBillboardDepthLine is null && Unchanged()));
        acknowledge(); Send(OverlayPointerKind.Move);
        results.Add(("an idle hover depth preview is not a fixed placement operation", editor.ActiveBillboardDepthLine is null && editor.VisibleBillboardDepthLine is not null));
        bool editingBeforeIdle = host.IsEditing; Escape();
        results.Add(("a second idle Esc after fixed depth cancellation returns to pass-through", editingBeforeIdle && !host.IsEditing && Unchanged()));

        using var bitmap = new SkiaSharp.SKBitmap(4, 3); bitmap.Erase(SkiaSharp.SKColors.CornflowerBlue);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap); using var png = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var pixels = AssetImport.Normalize(png.ToArray());
        foreach (var workflow in new[] { BillboardCreationWorkflow.ScreenFirst, BillboardCreationWorkflow.FixedDepthLine })
        {
            editor.Tool = EditorTool.Image; editor.BillboardWorkflow = workflow; editor.BillboardMode = true; SetEditing();
            int inserted = 0;
            bool began = editor.BeginBillboardPlacement((depth, center) =>
            { inserted++; ImageImportCommand.InsertBillboard(session, [pixels], document.Viewport, depth, center); });
            acknowledge();
            if (workflow == BillboardCreationWorkflow.FixedDepthLine)
            { Send(OverlayPointerKind.Down); Send(OverlayPointerKind.Up); }
            check(workflow + " pending image setup adds no pixels, asset or Undo", began && editor.HasPendingBillboardInsertion && session.Assets.Count == assets && Unchanged());
            Escape();
            Console.WriteLine("BILLBOARD_ESC_IMAGE " + JsonSerializer.Serialize(new { Workflow = workflow.ToString(), host.IsEditing,
                Pending = editor.HasPendingBillboardInsertion, Depth = editor.ActiveBillboardDepthLine is not null, inserted, Assets = session.Assets.Count, Unchanged = Unchanged() }));
            results.Add((workflow + " Esc cancels image placement while preserving editing and author state", host.IsEditing && !editor.HasPendingBillboardInsertion &&
                editor.ActiveBillboardDepthLine is null && inserted == 0 && session.Assets.Count == assets && Unchanged()));
            editingBeforeIdle = host.IsEditing; Escape();
            results.Add((workflow + " a second idle Esc still returns to pass-through", editingBeforeIdle && !host.IsEditing && Unchanged()));
        }
        results.Add(("Esc routing fixture never acquires keyboard isolation or displays the toolbar", !host.IsKeyboardIsolationActive && !Field<Window>(desktop, "_toolbar").IsVisible));
        editor.Cancel(); session.Document.Canvas.Projection.Matrix = originalProjection;
        // The key tests deliberately leave editing; its last isolated-view mapping
        // is not refreshed in pass-through. Start the capture-only phase unbound.
        editor.SetCanvasView(null, null, null);
        editor.Tool = originalTool; editor.BillboardWorkflow = originalWorkflow; editor.BillboardMode = originalMode; session.NotifyChanged();
        foreach (var result in results) check(result.Name, result.Passed);

        bool Unchanged() => JsonSerializer.Serialize(session.Document) == authored && session.UndoCount == history && !session.IsTransactionActive;
        void SetEditing() => typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(host, true);
        void Escape() => typeof(DesktopSession).GetMethod("OnCanvasKeyInput", Hidden)!.Invoke(desktop, [new OverlayKeyEvent(0x1B, OverlayModifiers.None, false)]);
        void Send(OverlayPointerKind kind)
        { var point = depthPoint + editor.ScreenOrigin; editor.Pointer(new(kind, new(point.X, point.Y), OverlayMouseButton.Left, OverlayModifiers.None)); }
    }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Hidden)!.GetValue(instance)!;
    private static (HotkeyBinding, HotkeyBinding) FreeHotkeys()
    {
        for (uint key = 0x7c; key < 0x87; key += 2)
        {
            if (!RegisterHotKey(0, 0x5d21, 7, key)) continue;
            try { if (!RegisterHotKey(0, 0x5d22, 7, key + 1)) continue; UnregisterHotKey(0, 0x5d22); return (new(7, key), new(7, key + 1)); }
            finally { UnregisterHotKey(0, 0x5d21); }
        }
        throw new InvalidOperationException("No test hotkeys available.");
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder information, int length, out int needed);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
}
