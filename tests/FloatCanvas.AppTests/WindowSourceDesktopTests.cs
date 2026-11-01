using System.Diagnostics.CodeAnalysis;
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
using SkiaSharp;

/// <summary>Real DesktopSession wiring on a never-shown private desktop. Every video source is synthetic.</summary>
internal static class WindowSourceDesktopTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        RequirePrivateDesktop();
        if (Application.Current is not null) throw new InvalidOperationException("Run this fixture in a fresh private-desktop child process.");
        string output = Path.Combine(Environment.GetEnvironmentVariable("FLOATCANVAS_WINDOW_DESKTOP_OUTPUT") ?? AppContext.BaseDirectory,
            "window-desktop-" + Guid.NewGuid().ToString("N"));
        ApplicationPaths.Configure(["--profile", Path.Combine(output, "profile"), "--primary-screen"]);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var hotkeys = FreeHotkeys();
        new UserSettings { Theme = "dark", Language = "en", Toggle = hotkeys.Item1, Hide = hotkeys.Item2,
            PreferGpuAcceleration = false, ShowFloatingLogo = false }.Save();
        var bounds = new PixelBounds(-22000, -21000, 800, 700);
        IReadOnlyList<MonitorInfo> monitors = [new("window-source-test", bounds, bounds, 96, 96, true)];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Hidden, null,
            [OverlayMonitorScope.PrimaryScreen, (Func<IReadOnlyList<MonitorInfo>>)(() => monitors), false], null)!;
        using var desktop = new DesktopSession(app, host);
        Field<System.Windows.Forms.NotifyIcon>(desktop, "_tray").Visible = false;
        Field<Window>(desktop, "_toolbar").Opacity = 0;
        var editor = Field<EditorController>(desktop, "_editor"); editor.BackgroundPickingEnabled = false;
        var session = editor.Session; var runtime = Field<WindowSourceRuntime>(desktop, "_windowRuntime");
        var viewport = new RectD(bounds.Left - editor.ScreenOrigin.X, bounds.Top - editor.ScreenOrigin.Y, bounds.Width, bounds.Height);
        session.Document.Viewport = viewport; session.Document.Canvas.Grid.Visible = false; session.Document.Canvas.BackgroundColor = 0;
        session.Document.Canvas.Projection.Enabled = false;
        var image = Image(40, 40, 160, 120);
        session.Execute("test window image", document => new SceneGraph(document).InsertElement(image));
        host.SetVisible(true); Call("UpdateWindowSourceRuntime");
        object owner = Call("WindowOwnerToken", session)!;
        var source = new FakeSource(); runtime.Bind(owner, session.Document.ResourceId, new(null, image.Id), image.WindowSource!.BindingIntent, source);
        check("real DesktopSession discovers its source in the negative physical monitor viewport", Demands().Length == 1 && Demands()[0].Source == new SourceNodeAddress(null, image.Id));
        check("production private-desktop gate leaves the fake capture inactive without starting native capture", !source.Active && !PresentationDesktopAvailability.IsCurrentInputDesktop());

        string untouched = JsonSerializer.Serialize(session.Document); int assetsBefore = session.Assets.Count, undoBefore = session.UndoCount;
        AllowSynthetic(); var native = source.Publish(SKColors.Red, 1); Drain(source);
        check("actual manager consumes and disposes the native-like owned bitmap", native.Bitmap.Handle == 0);
        using var worker = new CanvasPreviewRenderer();
        using var snapshot = Capture(); using var frame = worker.Render(snapshot); using var pixels = FramePixels(frame);
        check("actual Desktop snapshot carries owned window frames and the exact render epoch", snapshot.WindowFrames is { Count: 1 } ownedFrames &&
            ReferenceEquals(snapshot.Context.WindowEpoch, runtime.RenderEpoch) && ReferenceEquals(snapshot.WindowOwnerContext, runtime.RenderEpoch) &&
            ReferenceEquals(ownedFrames.OwnerContext, snapshot.WindowOwnerContext));
        check("real worker draws the captured source at its negative-origin desktop position", bounds.Left < 0 && pixels.GetPixel(80, 80) == SKColors.Red && pixels.GetPixel(220, 80).Alpha == 0);
        check("window capture snapshot and draw do not modify source history or inject runtime assets", JsonSerializer.Serialize(session.Document) == untouched && session.Assets.Count == assetsBefore && session.UndoCount == undoBefore);
        snapshot.Dispose();
        using (var retained = frame.Retain())
        {
            check("native submission independently retains the snapshot's window frames", retained.PickingWindowFrames is { } submissionFrames && submissionFrames.TryGet(new(session.Document.ResourceId, new(null, image.Id)), out var submitted) && submitted.Id.Sequence == 1);
        }
        Complete(frame, snapshot.Context, [new("window-source-test", bounds, 0, 0, 0, 0)]);
        check("successful actual completion records the owned shown frame collection", Field<WindowFrameSet?>(desktop, "_presentedWindowFrames") is { } shown && shown.TryGet(new(session.Document.ResourceId, new(null, image.Id)), out var shownFrame) && shownFrame.Id.Sequence == 1);
        editor.Tool = EditorTool.Select; session.SetSelection([]); Click(viewport.Left + 80, viewport.Top + 80);
        check("actual Desktop completion hands its rendered window frame to controller picking", session.Selection.SetEquals([image.Id]));

        var beforeOrigin = editor.ScreenOrigin;
        editor.ScreenOrigin += new PointD(19, 7); Call("UpdateWindowSourceRuntime");
        var nextContext = (CanvasPreviewContext)Call("CurrentPreviewContext")!;
        check("changing render plan origin advances its window epoch without changing binding owner", !ReferenceEquals(snapshot.Context.WindowEpoch, nextContext.WindowEpoch) && ReferenceEquals(owner, Call("WindowOwnerToken", session)));
        check("a completed window frame from an older plan cannot be submitted again", !DesktopSession.CanPresentCanvasFrame(frame, nextContext));
        session.SetSelection([]); editor.Tool = EditorTool.Rectangle;
        Send(OverlayPointerKind.Down, viewport.Left + 550, viewport.Top + 400);
        Send(OverlayPointerKind.Move, viewport.Left + 650, viewport.Top + 490);
        long transaction = session.TransactionId;
        check("the controlled newer rectangle gesture owns an active transaction before old completion", session.IsTransactionActive);
        AllowSynthetic(); var lateNative = source.Publish(SKColors.Blue, 2); Drain(source);
        check("late native-like delivery updates pixels without taking ownership of the newer gesture", lateNative.Bitmap.Handle == 0 && session.IsTransactionActive && session.TransactionId == transaction);
        Complete(frame, snapshot.Context, []);
        check("late success from an old window plan cannot cancel the current gesture", session.IsTransactionActive && session.TransactionId == transaction && !Field<bool>(desktop, "_presentationUncertain"));
        editor.Cancel(); editor.ScreenOrigin = beforeOrigin; Call("UpdateWindowSourceRuntime");
        check("disposing retained stale submissions did not destroy the original frame lease", frame.PickingWindowFrames is { } originalFrames && originalFrames.TryGet(new(session.Document.ResourceId, new(null, image.Id)), out var original) && original.Id.Sequence == 1);

        object ownerBeforeUndo = Call("WindowOwnerToken", session)!;
        session.Execute("test temporary opacity", document => document.Elements.Single(element => element.Id == image.Id).Style.Opacity = .8);
        session.Undo(); Call("UpdateWindowSourceRuntime");
        using (var remembered = Retain(session, new(null, image.Id), image.WindowSource.BindingIntent))
            check("ordinary Undo restoration preserves same-owner intent lookup despite a new document object", remembered is not null && remembered.Id.Sequence == 2 && ReferenceEquals(ownerBeforeUndo, Call("WindowOwnerToken", session)));

        var hidden = Image(50, 250, 120, 90); var zero = Image(200, 250, 120, 90); zero.Style.Opacity = 0;
        var definitionImage = Image(0, 0, 100, 80); var definition = new PrefabDefinition { Graph = new() { Elements = [definitionImage] } };
        var unusedImage = Image(0, 0, 100, 80); var unused = new PrefabDefinition { Graph = new() { Elements = [unusedImage] } };
        var extra1 = Image(50, 400, 100, 80); var extra2 = Image(170, 400, 100, 80); var excess = Image(290, 400, 100, 80);
        // Definition geometry is local, while direct authored images use this document's local desktop coordinates.
        definitionImage.Transform = Affine2D.Identity; unusedImage.Transform = Affine2D.Identity;
        var instance1 = new PrefabInstance { DefinitionId = definition.Id, Transform = Affine2D.Translation(viewport.Left + 260, viewport.Top + 120) };
        var instance2 = new PrefabInstance { DefinitionId = definition.Id, Transform = Affine2D.Translation(viewport.Left + 420, viewport.Top + 120) };
        session.Execute("test window demand membership", document =>
        {
            var graph = new SceneGraph(document); var group = graph.InsertGroup(new CanvasGroup { Visible = false, OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] });
            graph.InsertElement(hidden, group.Id); graph.InsertElement(zero);
            document.Definitions.Add(definition); document.Definitions.Add(unused);
            graph.InsertInstance(instance1); graph.InsertInstance(instance2);
            graph.InsertElement(extra1); graph.InsertElement(extra2); graph.InsertElement(excess);
        });
        Call("UpdateWindowSourceRuntime");
        var demand = Demands();
        check("real source plan excludes hidden ancestors zero-opacity images and unused definitions", demand.All(item => item.Source.NodeId != hidden.Id && item.Source.NodeId != zero.Id && item.Source.NodeId != unusedImage.Id));
        check("real source plan retains all five visible authored candidates and deduplicates dependent instances", demand.Length == 5 && demand.Count(item => item.Source == new SourceNodeAddress(definition.Id, definitionImage.Id)) == 1 && demand.Any(item => item.Source.NodeId == excess.Id));
        check("runtime admits four source slots separately from the visible candidate list", DemandedSlots() == WindowFrameBudget.MaximumSources);

        // The earlier first image was bound for picking/lifetime checks. Start a
        // fresh owner lifetime so the fifth candidate is now the only bound source.
        Call("ForgetWindowSourceBindings", session); Call("UpdateWindowSourceRuntime");
        owner = Call("WindowOwnerToken", session)!;
        var onlyFifthSource = Bind(new(null, excess.Id), excess); AllowSynthetic();
        check("four earlier disconnected candidates cannot starve the only bound fifth source in the real Desktop plan", Demands()[4].Source.NodeId == excess.Id && runtime.IsSourceDemanded(new(null, excess.Id), excess.WindowSource!.BindingIntent) && onlyFifthSource.Active && runtime.BindingCount == 1 && DemandedSlots() == WindowFrameBudget.MaximumSources);
        onlyFifthSource.Publish(SKColors.Fuchsia, 1); Drain(onlyFifthSource);
        using (var fifthSnapshot = Capture())
        using (var fifthFrame = worker.Render(fifthSnapshot))
        using (var fifthPixels = FramePixels(fifthFrame))
            check("the only bound fifth candidate reaches actual Desktop snapshot and worker pixels", fifthSnapshot.WindowFrames?.Count == 1 && fifthPixels.GetPixel(330, 440) == SKColors.Fuchsia);
        Call("ForgetWindowSourceBindings", session); Call("UpdateWindowSourceRuntime");
        owner = Call("WindowOwnerToken", session)!;
        source = Bind(new(null, image.Id), image);
        var definitionSource = Bind(new(definition.Id, definitionImage.Id), definitionImage);
        var hiddenSource = Bind(new(null, hidden.Id), hidden); var zeroSource = Bind(new(null, zero.Id), zero);
        var unusedSource = Bind(new(unused.Id, unusedImage.Id), unusedImage);
        var extraSource1 = Bind(new(null, extra1.Id), extra1); var extraSource2 = Bind(new(null, extra2.Id), extra2); var excessSource = Bind(new(null, excess.Id), excess);
        AllowSynthetic();
        check("only four demanded services become active under controlled fake-source allowance", new[] { source, definitionSource, hiddenSource, zeroSource, unusedSource, extraSource1, extraSource2, excessSource }.Count(item => item.Active) == 4 && DemandedSlots() == WindowFrameBudget.MaximumSources && definitionSource.Active && !hiddenSource.Active && !zeroSource.Active && !unusedSource.Active && !excessSource.Active);
        var boundEntries = (System.Collections.IDictionary)typeof(WindowSourceRuntime).GetField("bindings", Hidden)!.GetValue(runtime)!;
        check("every actual runtime binding in the fixture uses the injected fake capture implementation", boundEntries.Count == 8 && boundEntries.Values.Cast<object>().All(entry => entry.GetType().GetField("Capture", Hidden)!.GetValue(entry) is FakeSource));
        source.Publish(SKColors.Red, 3); Drain(source);
        definitionSource.Publish(SKColors.Lime, 1); Drain(definitionSource);
        extraSource1.Publish(SKColors.Blue, 1); Drain(extraSource1); extraSource2.Publish(SKColors.Yellow, 1); Drain(extraSource2);
        using var instancesSnapshot = Capture(); using var instancesFrame = worker.Render(instancesSnapshot); using var instancesPixels = FramePixels(instancesFrame);
        check("two dependent instances draw one definition binding and one retained source frame", runtime.BindingCount == 8 && instancesSnapshot.WindowFrames?.Count == 4 && instancesPixels.GetPixel(300, 160) == SKColors.Lime && instancesPixels.GetPixel(460, 160) == SKColors.Lime);
        session.Execute("test hide all window output", document => document.LayerOpacity = 0); Call("UpdateWindowSourceRuntime");
        check("zero canvas opacity removes every capture demand and deactivates services", Demands().Length == 0 && !definitionSource.Active && !source.Active);
        session.Undo(); Call("UpdateWindowSourceRuntime");
        check("restoring canvas opacity restores five visible candidates and four runtime slots without inventing another binding", Demands().Length == 5 && DemandedSlots() == WindowFrameBudget.MaximumSources && runtime.BindingCount == 8);

        Guid resource = session.Document.ResourceId; var reopen = session.Document.Clone(); object oldOwner = Call("WindowOwnerToken", session)!;
        Call("ForgetWindowSourceBindings", session);
        session.RestoreDocument("test actual reload boundary", reopen); Call("UpdateWindowSourceRuntime");
        using (var forgotten = Retain(session, new(null, image.Id), image.WindowSource.BindingIntent))
            check("explicit reload forget prevents same-resource descriptor intents from reviving old native bindings", forgotten is null && session.Document.ResourceId == resource && !ReferenceEquals(oldOwner, Call("WindowOwnerToken", session)) && runtime.BindingCount == 0 && source.DisposeCount > 0);
        check("retained old frame cannot present into the reopened owner epoch", !DesktopSession.CanPresentCanvasFrame(instancesFrame, (CanvasPreviewContext)Call("CurrentPreviewContext")!));
        check("the private-desktop fixture has no editing input or keyboard isolation lease", !host.HasEditingInput && !host.IsKeyboardIsolationActive);
        Console.WriteLine("WINDOW_SOURCE_DESKTOP " + output);

        CanvasElement Image(double x, double y, double width, double height)
        {
            using var bitmap = new SKBitmap(4, 4); bitmap.Erase(SKColors.Blue);
            using var immutable = SKImage.FromBitmap(bitmap); using var png = immutable.Encode(SKEncodedImageFormat.Png, 100);
            return new() { Kind = ElementKind.Image, Width = width, Height = height, Transform = Affine2D.Translation(viewport.Left + x, viewport.Top + y),
                AssetId = session.Assets.Add(png.ToArray()), WindowSource = new() { BindingIntent = Guid.NewGuid(), Fit = WindowSourceFit.Stretch }, Style = new() { StrokeWidth = 0 } };
        }
        FakeSource Bind(SourceNodeAddress address, CanvasElement element)
        { var capture = new FakeSource(); runtime.Bind(owner, session.Document.ResourceId, address, element.WindowSource!.BindingIntent, capture); return capture; }
        WindowRuntimeDemand[] Demands() => Field<WindowRuntimeDemand[]>(desktop, "_windowDemands");
        int DemandedSlots() => Demands().Count(item => runtime.IsSourceDemanded(item.Source, item.BindingIntent));
        void AllowSynthetic() => Synthetic(() => runtime.Configure(Call("WindowOwnerToken", session)!, session.Document.ResourceId, Demands(), true, Field<object>(desktop, "_windowPlanIdentity")));
        void Drain(FakeSource capture)
        {
            var entries = (System.Collections.IDictionary)typeof(WindowSourceRuntime).GetField("bindings", Hidden)!.GetValue(runtime)!;
            var entry = entries.Values.Cast<object>().Single(item => ReferenceEquals(item.GetType().GetField("Capture", Hidden)!.GetValue(item), capture));
            Synthetic(() => typeof(WindowSourceRuntime).GetMethod("Drain", Hidden)!.Invoke(runtime, [entry]));
        }
        void Synthetic(Action action)
        {
            // Status UI normally refreshes the production private-desktop gate. Guard
            // only these synchronous synthetic deliveries, after asserting that the
            // genuine gate rejected capture; no native backend can be started here.
            bool previous = Field<bool>(desktop, "_updatingWindowSources"); Set(desktop, "_updatingWindowSources", true);
            try { action(); } finally { Set(desktop, "_updatingWindowSources", previous); }
        }
        CanvasPreviewSnapshot Capture() => (CanvasPreviewSnapshot)Call("CaptureCanvasPreview")!;
        WindowFrameLease? Retain(EditorSession target, SourceNodeAddress address, Guid intent)
        { object?[] arguments = [target, address, intent, null]; return (bool)typeof(DesktopSession).GetMethod("TryRetainWindowSourceFrame", Hidden)!.Invoke(desktop, arguments)! ? (WindowFrameLease?)arguments[3] : null; }
        void Complete(CanvasPreviewFrame rendered, CanvasPreviewContext context, OverlayOutputTiming[] outputs)
        {
            Set(desktop, "_submittingCanvasContext", context); Set(desktop, "_submittingCanvasFrame", rendered.Retain());
            Call("CompleteCanvasSubmission", new OverlayFrameTiming(0, 0, true, outputs));
        }
        void Send(OverlayPointerKind kind, double x, double y)
        { var point = new PointD(x, y) + editor.ScreenOrigin; editor.Pointer(new(kind, new(point.X, point.Y), OverlayMouseButton.Left, OverlayModifiers.None)); }
        void Click(double x, double y) { Send(OverlayPointerKind.Down, x, y); Send(OverlayPointerKind.Up, x, y); }
        SKBitmap FramePixels(CanvasPreviewFrame rendered)
        { using var target = SKSurface.Create(new SKImageInfo(800, 700)); target.Canvas.Clear(SKColors.Transparent); target.Canvas.Translate(-bounds.Left, -bounds.Top); rendered.Pixels.Draw(target.Canvas); using var image = target.Snapshot(); return SKBitmap.FromImage(image); }
        object? Call(string method, params object?[] arguments) => typeof(DesktopSession).GetMethod(method, Hidden)!.Invoke(desktop, arguments);
    }

    private sealed class FakeSource : IWindowCaptureSource
    {
        private static long streams;
        private LiveWindowFrame? latest;
        internal bool Active; internal int DisposeCount;
        public WindowCaptureTarget Target { get; } = new(1, 1, 1, 1, "Synthetic only", "test", "Synthetic");
        public long StreamId { get; } = Interlocked.Increment(ref streams);
        public long ConnectionVersion => 1;
        public WindowCaptureStatus Status { get; private set; } = WindowCaptureStatus.Starting;
        public Exception? LastError => null;
        public Task Completion => Task.CompletedTask;
        public Task FirstResult => Task.CompletedTask;
        public event Action? FrameReady { add { } remove { } }
        public event Action? StateChanged { add { } remove { } }
        public void SetActive(bool enabled) => Active = enabled;
        public void Resume() => Active = true;
        public bool TryGetLatest([NotNullWhen(true)] out LiveWindowFrame? frame) { frame = Interlocked.Exchange(ref latest, null); return frame is not null; }
        internal LiveWindowFrame Publish(SKColor color, long sequence)
        {
            var pixels = new SKBitmap(4, 4, SKColorType.Bgra8888, SKAlphaType.Premul); pixels.Erase(color); pixels.SetImmutable();
            var frame = new LiveWindowFrame(StreamId, ConnectionVersion, sequence, new(4, 4), pixels, 1);
            Interlocked.Exchange(ref latest, frame)?.Dispose(); Status = WindowCaptureStatus.Live; return frame;
        }
        public void Dispose() { DisposeCount++; Active = false; Interlocked.Exchange(ref latest, null)?.Dispose(); }
    }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Hidden)!.GetValue(instance)!;
    private static void Set(object instance, string name, object? value) => instance.GetType().GetField(name, Hidden)!.SetValue(instance, value);
    private static void RequirePrivateDesktop()
    {
        var name = new StringBuilder(256);
        if (!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, name, 512, out _) || !name.ToString().StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal))
            throw new InvalidOperationException("Use --isolated-ui-desktop --window-source-desktop-only; this fixture never switches the visible desktop.");
    }
    private static (HotkeyBinding, HotkeyBinding) FreeHotkeys()
    {
        for (uint key = 0x7c; key < 0x87; key += 2)
        {
            if (!RegisterHotKey(0, 0x5e61, 7, key)) continue;
            try { if (!RegisterHotKey(0, 0x5e62, 7, key + 1)) continue; UnregisterHotKey(0, 0x5e62); return (new(7, key), new(7, key + 1)); }
            finally { UnregisterHotKey(0, 0x5e61); }
        }
        throw new InvalidOperationException("No private-desktop fixture hotkeys available.");
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder information, int length, out int needed);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
}
