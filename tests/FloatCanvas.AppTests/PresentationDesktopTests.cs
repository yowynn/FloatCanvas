using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

/// <summary>Actual desktop preview/Bake wiring with simulated input and editing metadata on an unshown private desktop.</summary>
internal static class PresentationDesktopTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        var name = new StringBuilder(256);
        if (!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, name, 512, out _) ||
            !name.ToString().StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal))
            throw new InvalidOperationException("Use --isolated-ui-desktop --presentation-desktop-only.");
        if (Application.Current is not null) throw new InvalidOperationException("Run in a fresh child process.");
        string output = Path.Combine(Environment.GetEnvironmentVariable("FLOATCANVAS_PRESENTATION_TEST_OUTPUT") ?? Path.GetTempPath(), "desktop-" + Guid.NewGuid().ToString("N"));
        ApplicationPaths.Configure(["--profile", Path.Combine(output, "profile"), "--primary-screen"]);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var hotkeys = FreeHotkeys();
        new UserSettings { Theme = "dark", Language = "en", Toggle = hotkeys.Item1, Hide = hotkeys.Item2,
            PreferGpuAcceleration = false, ShowFloatingLogo = false }.Save();
        var bounds = new PixelBounds(-22000, -22000, 800, 700);
        Func<IReadOnlyList<MonitorInfo>> monitors = () => [new("presentation-test", bounds, bounds, 96, 96, true)];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Hidden, null, [OverlayMonitorScope.PrimaryScreen, monitors, false], null)!;
        using var desktop = new DesktopSession(app, host);
        Field<System.Windows.Forms.NotifyIcon>(desktop, "_tray").Visible = false;
        Field<Window>(desktop, "_toolbar").Opacity = 0;
        var editor = Field<EditorController>(desktop, "_editor");
        var session = editor.Session;
        session.Document.Viewport = new(0, 0, 800, 700); session.Document.Canvas.Grid.Visible = false;
        var source = new CanvasElement { Kind = ElementKind.Presentation, Width = 800, Height = 700,
            Transform = Affine2D.Translation(bounds.Left, bounds.Top),
            Style = new() { Foreground = 0xFFFFC828, Opacity = .75, Variant = "default" },
            Presentation = new() { Modules = PresentationModules.Keys | PresentationModules.Cursor | PresentationModules.Trail } };
        new SceneGraph(session.Document).InsertElement(source); session.NotifyChanged(); host.SetVisible(true);
        PresentationRuntimeTests.FakeSource? fake = null; long now = 1000;
        using var runtime = new PresentationRuntimeBroker(app.Dispatcher, () => Call("OnPresentationRuntimeChanged"),
            (epoch, _, _, _) => Task.FromResult<IPresentationInputSource>(fake = new(epoch, [])),
            () => TimeSpan.FromMilliseconds(now));
        Set(desktop, "_presentationRuntime", runtime);
        runtime.Toggle(); Call("UpdatePresentationRuntime");
        check("the synthetic source region intersects the actual private desktop document viewport", Field<IReadOnlyList<ResolvedLeaf>>(desktop, "_presentationLeaves").Count == 1 &&
            PresentationGeometry.TryScreenRegion(source, CanvasMapping.Create(session.Document), session.Document.Viewport, out _));
        check("private-desktop production demand gating does not begin native observation", !runtime.Observing);
        string untouched = JsonSerializer.Serialize(session.Document); int initialUndo = session.UndoCount;
        using var renderer = new CanvasPreviewRenderer();

        Prepare();
        check("the simulated source provides two bounded physical-screen trail samples", runtime.Observing && runtime.Frame.Trail.Count == 2);
        using var live = Capture();
        check("actual desktop snapshot freezes the current immutable synthetic presentation frame", ReferenceEquals(live.PresentationFrame, runtime.Frame) && live.Context.PresentationEpoch is not null);
        using var pixels = renderer.Render(live);
        check("worker output records the exact presentation input frame represented by its pixels", ReferenceEquals(pixels.PresentationFrameSnapshot, live.PresentationFrame) && !pixels.PresentationBudgetExceeded);
        Call("RecordPresentedPresentation", pixels, live.Context);
        check("playing and capturing presentation frames do not change authored document or history", JsonSerializer.Serialize(session.Document) == untouched && session.UndoCount == initialUndo);
        Call("PausePresentationRuntime", true);
        check("pausing before editing closes the synthetic input source synchronously", !runtime.Observing && fake!.StopCount > 0);
        check("paused Bake requires actual editing mode metadata", !CanBake(source.Id));
        SetEditing(true);
        check("paused actually displayed trail becomes available to the selected source", CanBake(source.Id));
        using var paused = Capture();
        check("paused preview drops transient pixels and rejects a late live frame", paused.PresentationFrame is null && paused.Context.PresentationEpoch is null && !DesktopSession.CanPresentCanvasFrame(pixels, paused.Context));
        int beforeBake = session.UndoCount;
        Call("BakePresentationTrail", source.Id);
        check("Bake inserts one normal Freehand batch in one history transaction", session.UndoCount == beforeBake + 1 && session.Document.Elements.Any(element => element.Kind == ElementKind.Freehand) && !session.IsTransactionActive);
        var stroke = session.Document.Elements.Single(element => element.Kind == ElementKind.Freehand);
        check("Bake subtracts the physical desktop origin once and preserves source opacity", stroke.Points.Count >= 2 &&
            ElementGeometry.LocalToPlane(stroke).TransformPoint(stroke.Points[0]).DistanceTo(new(bounds.Left + 100, bounds.Top + 100)) < .001 && stroke.Style.Opacity == source.Style.Opacity);
        check("successful Bake consumes the paused snapshot", !CanBake(source.Id));
        session.Undo();
        check("one Undo removes the whole retained trail without removing the presentation source", session.Document.Elements.Count == 1 && session.Document.Elements[0].Kind == ElementKind.Presentation);
        session.Redo();
        check("Redo restores ordinary editable strokes", session.Document.Elements.Any(element => element.Kind == ElementKind.Freehand));
        session.Undo();

        ShowAndPause();
        session.BeginTransaction("foreign");
        check("a foreign transaction prevents Bake without taking ownership", !CanBake(source.Id) && session.IsTransactionActive);
        Call("BakePresentationTrail", source.Id);
        check("rejected Bake leaves the foreign transaction and source unchanged", session.IsTransactionActive && session.Document.Elements.Count == 1);
        session.Cancel();
        check("document restoration invalidates the old paused snapshot", !CanBake(source.Id));

        ShowAndPause();
        session.Execute("unrelated authored change", document => document.LayerOpacity = .8);
        check("any authored revision invalidates the paused trajectory", !CanBake(source.Id));

        var group = new CanvasGroup { Name = "nested", OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] };
        session.Execute("add group", document => new SceneGraph(document).InsertGroup(group));
        ShowAndPause();
        session.EnterScope(group.Id); session.LeaveScope();
        check("leaving and returning to the same scope cannot reuse an earlier paused trail", !CanBake(source.Id));

        session.Execute("lock source", document => document.Elements.Single(element => element.Id == source.Id).Locked = true);
        ShowAndPause();
        check("a locked presentation still renders but cannot be baked", !CanBake(source.Id));
        session.Execute("unlock source", document => document.Elements.Single(element => element.Id == source.Id).Locked = false);
        ShowAndPause();
        runtime.ClearKeyboardForFocusChange();
        check("runtime lifetime changes invalidate any paused Bake snapshot", !CanBake(source.Id));

        ShowAndPause();
        SetEditing(false); runtime.Configure(Demand, true, new(), "Presentation.Status.PausedEditing");
        check("resuming observation cannot reuse the earlier paused trail", !CanBake(source.Id));
        runtime.Disarm(); SetEditing(false);
        session.SetSelection([source.Id]);
        var prefab = session.CreatePrefabFromSelection("presentation source");
        string definitionBefore = JsonSerializer.Serialize(session.Document.Definitions);
        ShowAndPause();
        check("a selected prefab with one presentation occurrence can retain its host trail", CanBake(prefab.InstanceId));
        beforeBake = session.UndoCount;
        Call("BakePresentationTrail", prefab.InstanceId);
        check("instance Bake writes one ordinary host stroke without changing its shared definition", session.UndoCount == beforeBake + 1 &&
            session.Document.Elements.Single().Kind == ElementKind.Freehand && JsonSerializer.Serialize(session.Document.Definitions) == definitionBefore);
        session.Undo();
        session.Execute("add another presentation to definition", document =>
        {
            var definition = document.Definitions.Single(item => item.Id == prefab.DefinitionId);
            new SceneGraph(definition.Graph).InsertElement(definition.Graph.Elements.Single().Clone(true));
        });
        ShowAndPause();
        check("a prefab with several presentation occurrences cannot guess which trail region to bake", !CanBake(prefab.InstanceId));
        runtime.Disarm(); SetEditing(false);
        check("the fixture never acquires native editing input or a keyboard isolation lease", !host.IsKeyboardIsolationActive && !host.HasEditingInput);
        Console.WriteLine("PRESENTATION_DESKTOP " + output);

        void Prepare()
        {
            SetEditing(false);
            if (!runtime.Armed) runtime.Toggle();
            Call("UpdatePresentationRuntime");
            runtime.Configure(Demand, true, new(), "Presentation.Status.PausedEditing");
            now += 100;
            fake!.Queue(cursor: new((int)editor.ScreenOrigin.X + bounds.Left + 100, (int)editor.ScreenOrigin.Y + bounds.Top + 100, now)); Drain();
            now += 33;
            fake.Queue(cursor: new((int)editor.ScreenOrigin.X + bounds.Left + 200, (int)editor.ScreenOrigin.Y + bounds.Top + 180, now)); Drain();
        }
        void ShowAndPause()
        {
            Prepare(); using var capture = Capture(); using var frame = renderer.Render(capture);
            Call("RecordPresentedPresentation", frame, capture.Context); Call("PausePresentationRuntime", true); SetEditing(true);
        }
        CanvasPreviewSnapshot Capture() => (CanvasPreviewSnapshot)Call("CaptureCanvasPreview")!;
        bool CanBake(Guid id) { object?[] arguments = [id, null, null]; return (bool)typeof(DesktopSession).GetMethod("TryGetPresentationBakeSource", Hidden)!.Invoke(desktop, arguments)!; }
        object? Call(string method, params object?[] arguments) => typeof(DesktopSession).GetMethod(method, Hidden)!.Invoke(desktop, arguments);
        void SetEditing(bool value) => typeof(OverlayHost).GetProperty(nameof(OverlayHost.IsEditing))!.SetValue(host, value);
        void Drain() => typeof(PresentationRuntimeBroker).GetMethod("Drain", Hidden)!.Invoke(runtime, null);
    }
    private static readonly PresentationRuntimeDemand Demand = new(false, true, false, true, false, 900, 450, 800);
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Hidden)!.GetValue(instance)!;
    private static void Set(object instance, string name, object value) => instance.GetType().GetField(name, Hidden)!.SetValue(instance, value);
    private static (HotkeyBinding, HotkeyBinding) FreeHotkeys()
    {
        for (uint key = 0x7c; key < 0x87; key += 2)
        {
            if (!RegisterHotKey(0, 0x5d41, 7, key)) continue;
            try { if (!RegisterHotKey(0, 0x5d42, 7, key + 1)) continue; UnregisterHotKey(0, 0x5d42); return (new(7, key), new(7, key + 1)); }
            finally { UnregisterHotKey(0, 0x5d41); }
        }
        throw new InvalidOperationException("No test hotkeys available.");
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder information, int length, out int needed);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
}
