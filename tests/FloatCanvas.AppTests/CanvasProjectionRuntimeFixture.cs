using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Interop;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

// Opt-in production session integration. Owns only a bounded host and a fresh temporary profile.
internal static class CanvasProjectionRuntimeFixture
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, bool preview = false)
    {
        if (Application.Current is not null) throw new InvalidOperationException("Run this fixture alone.");
        string profile = Path.Combine(Path.GetTempPath(), "FloatCanvas-ProjectionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile); ApplicationPaths.Configure(["--profile", profile, "--primary-screen"]);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        nint foreground = GetForegroundWindow(); DesktopSession? session = null; OverlayHost? host = null;
        try
        {
            var primary = ((IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!).First(m => m.IsPrimary);
            var bounds = new PixelBounds(primary.WorkArea.Left + 24, primary.WorkArea.Top + 24, 640, 360);
            Func<IReadOnlyList<MonitorInfo>> provider = () => [primary with { Bounds = bounds, WorkArea = bounds }];
            host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null, [OverlayMonitorScope.PrimaryScreen, provider, false], null)!;
            session = new DesktopSession(app, host);
            Get<System.Windows.Forms.NotifyIcon>(session, "_tray").Visible = false;
            var toolbar = Get<Window>(session, "_toolbar"); toolbar.ShowActivated = false; toolbar.Show(); Pump();
            bool entered = false;
            host.SetVisible(true); Activate(new WindowInteropHelper(toolbar).Handle, () => entered = (bool)Invoke("SetEditing", true, false)!); Pump();
            nint currentForeground = GetForegroundWindow();
            GetWindowThreadProcessId(currentForeground, out uint foregroundProcess);
            Console.WriteLine($"ENTRY result={entered} editing={host.IsEditing} visible={host.IsVisible} foreground={currentForeground:X} foregroundPid={foregroundProcess} fixturePid={Environment.ProcessId} toolbar={new WindowInteropHelper(toolbar).Handle:X} tiles={string.Join(";", host.Diagnostics.Select(p => $"{p.InputHandle:X}:{p.Bounds.Left},{p.Bounds.Top},{p.Bounds.Width},{p.Bounds.Height}:visible={p.InputVisible}"))}");
            Console.Out.Flush();
            check("projection runtime enters its own bounded production editor", host.IsEditing && host.IsVisible && host.Diagnostics.Single().Bounds.Width == 640);
            var editor = Get<EditorController>(session, "_editor");
            Invoke("ToggleCanvasSettings"); Pump();
            var panel = Get<Window>(session, "_canvasSettingsWindow");
            check("canvas settings opens through the production command", panel.IsVisible && panel.Owner == toolbar);
            ((Action<uint>)panel.GetType().GetField("BackgroundChanged", Private)!.GetValue(panel)!)(0x80123456);
            check("panel background event retains alpha in the active document", editor.Session.Document.Canvas.BackgroundColor == 0x80123456);
            Invoke("BeginCanvasCalibration");
            var calibration = Get<CanvasCalibrationController>(session, "_canvasCalibration");
            var original = editor.Session.Document.Canvas.Projection.Quad.ToArray();
            int undo = editor.Session.UndoCount;
            Pointer(OverlayPointerKind.Down, original[0]); Pointer(OverlayPointerKind.Move, original[0] + new PointD(12, 8)); Pointer(OverlayPointerKind.Up, original[0] + new PointD(12, 8));
            check("production pointer route commits one calibration drag", calibration.IsActive && !calibration.IsDragging && editor.Session.UndoCount == undo + 1 && editor.Session.Document.Canvas.Projection.Quad[0].DistanceTo(original[0] + new PointD(12, 8)) < .001);
            var moved = editor.Session.Document.Canvas.Projection.Quad.ToArray();
            Invoke("SetCanvasGridCounts", 12, 7);
            check("grid count changes preserve all calibrated screen corners", editor.Session.Document.Canvas.Projection.Columns == 12 && editor.Session.Document.Canvas.Projection.Quad.SequenceEqual(moved));
            undo = editor.Session.UndoCount;
            Pointer(OverlayPointerKind.Down, moved[0]); Pointer(OverlayPointerKind.Move, moved[0] + new PointD(9, 4)); Invoke("OnKey", 0x1B);
            check("production Escape rolls back the whole calibration gesture", calibration.IsActive && !calibration.IsDragging && editor.Session.UndoCount == undo && editor.Session.Document.Canvas.Projection.Quad.SequenceEqual(moved));
            Invoke("EndCanvasCalibration", false);
            editor.Tool = EditorTool.Rectangle;
            var h = editor.Session.Document.Canvas.Projection.Matrix;
            PointD first = new(260, 170), last = new(300, 200);
            Pointer(OverlayPointerKind.Down, first); Pointer(OverlayPointerKind.Move, last); Pointer(OverlayPointerKind.Up, last);
            var element = editor.Session.Document.Elements.Single();
            var actual = h.TransformPoint(ElementGeometry.LocalToPlane(element).TransformPoint(new PointD(0, 0)));
            check("production drawing maps the screen press through inverse projection", actual.DistanceTo(first) < .01);
            Invoke("BeginCanvasCalibration"); moved = editor.Session.Document.Canvas.Projection.Quad.ToArray();
            Pointer(OverlayPointerKind.Down, moved[0]); Pointer(OverlayPointerKind.Move, moved[0] + new PointD(7, 3));
            if (preview)
            {
                Invoke("OnKey", 0x1B);
                editor.Session.Document.Canvas.Grid.Visible = true;
                Invoke("EndCanvasCalibration", false); Invoke("BeginCanvasCalibration"); Pump();
                Console.WriteLine("PREVIEW_READY: isolated production canvas settings and grid, 120 seconds"); Console.Out.Flush();
                var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(120) };
                timer.Tick += (_, _) => frame.Continue = false;
                try { timer.Start(); Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
            }
            Invoke("ToggleCanvasVisibility"); Pump();
            check("hide cancels calibration and hides its real settings window", !calibration.IsActive && !calibration.IsDragging && !panel.IsVisible && !host.IsVisible && editor.Session.Document.Canvas.Projection.Quad.SequenceEqual(moved));
            Console.WriteLine($"PROFILE {profile}; bounded native HWND host; production pointer event route; no physical input injection or multimonitor claim.");

            object? Invoke(string name, params object[] args) => typeof(DesktopSession).GetMethod(name, Private)!.Invoke(session, args);
            void Pointer(OverlayPointerKind kind, PointD p)
            {
                var callback = (Action<OverlayPointerEvent>)typeof(OverlayHost).GetField("Pointer", Private)!.GetValue(host)!;
                callback(new(kind, new(p.X + editor.ScreenOrigin.X, p.Y + editor.ScreenOrigin.Y), OverlayMouseButton.Left, OverlayModifiers.None));
            }
        }
        finally
        {
            host?.ExitEditing(); session?.Dispose();
            foreach (var window in app.Windows.Cast<Window>().ToArray()) window.Close(); Pump();
            if (foreground != 0) SetForegroundWindow(foreground);
        }
    }
    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
    private static void Activate(nint window, Action action)
    {
        uint from = GetCurrentThreadId(), to = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        bool attached = from != to && AttachThreadInput(from, to, true);
        try
        {
            bool accepted = SetForegroundWindow(window); nint previousFocus = SetFocus(window);
            Console.WriteLine($"ACTIVATION attached={attached} accepted={accepted} previousFocus={previousFocus:X} foreground={GetForegroundWindow():X} requested={window:X} foregroundThread={to} fixtureThread={from}");
            action();
        }
        finally { if (attached) AttachThreadInput(from, to, false); }
    }
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint process);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint SetFocus(nint handle);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
}
