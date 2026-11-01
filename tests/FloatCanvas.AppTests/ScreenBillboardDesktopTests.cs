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

/// <summary>Actual DesktopSession snapshot wiring on a never displayed private desktop; no input lease or physical input.</summary>
internal static class ScreenBillboardDesktopTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
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
        var bounds = new PixelBounds(-22000, -22000, 800, 700);
        Func<IReadOnlyList<MonitorInfo>> monitors = () => [new("screen-billboard", bounds, bounds, 96, 96, true)];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Hidden, null, [OverlayMonitorScope.PrimaryScreen, monitors, false], null)!;
        using var desktop = new DesktopSession(app, host);
        Field<System.Windows.Forms.NotifyIcon>(desktop, "_tray").Visible = false;
        Field<Window>(desktop, "_toolbar").Opacity = 0;
        var editor = Field<EditorController>(desktop, "_editor"); editor.BackgroundPickingEnabled = false;
        var session = editor.Session; var document = session.Document;
        document.Viewport = new(0, 0, 800, 700); document.Canvas.Grid.Visible = false;
        document.Canvas.Projection.Enabled = true;
        document.Canvas.Projection.Matrix = new(1, 0, 0, 0, -.4, 700, 0, -.002, 1);
        document.Canvas.Projection.Quad = [new(0, 0), new(800, 0), new(800, 700), new(0, 700)];
        session.NotifyChanged(); host.SetVisible(true); editor.Tool = EditorTool.Rectangle; editor.BillboardMode = true;
        using var renderer = new CanvasPreviewRenderer();
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
        Send(OverlayPointerKind.Down, 100, 100); using var restarted = Capture();
        check("a new draft cannot reuse pixels belonging to the cancelled lifetime", restarted.Context.ScreenDraftEpoch is not null &&
            !ReferenceEquals(restarted.Context.ScreenDraftEpoch, valid.Context.ScreenDraftEpoch) && !DesktopSession.CanPresentCanvasFrame(draftFrame, restarted.Context));
        Complete(draftFrame, valid.Context, []);
        check("successful completion of an older draft submission cannot cancel the new gesture", session.IsTransactionActive && editor.HasScreenBillboardDraft &&
            !Field<bool>(desktop, "_presentationUncertain"));
        editor.Cancel();

        document = session.Document;
        var group = new SceneGraph(document).InsertGroup(new CanvasGroup { Name = "scope", OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] }); session.EnterScope(group.Id);
        Send(OverlayPointerKind.Down, 100, 100); Send(OverlayPointerKind.Move, 300, 400);
        using var scoped = Capture();
        check("actual capture freezes the editing scope with the private screen draft", scoped.ScreenDraftScopeId == group.Id && scoped.ScreenDraft is not null);
        session.LeaveScope(); session.EnterScope(group.Id); using var stale = Capture();
        check("scope out and back clears an actual desktop draft without publishing it", stale.ScreenDraft is null && document.Elements.Count == 0 && !session.IsTransactionActive);
        check("snapshot wiring fixture never acquires an editing or keyboard isolation lease", !host.IsEditing && !host.IsKeyboardIsolationActive);
        Console.WriteLine("SCREEN_BILLBOARD_DESKTOP " + output);

        CanvasPreviewSnapshot Capture() => (CanvasPreviewSnapshot)typeof(DesktopSession).GetMethod("CaptureCanvasPreview", Hidden)!.Invoke(desktop, null)!;
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
