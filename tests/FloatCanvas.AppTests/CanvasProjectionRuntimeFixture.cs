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
        if(Environment.GetEnvironmentVariable("FLOATCANVAS_CANVAS_TEST_LANGUAGE") is { } language)new UserSettings {Language=language}.Save();
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
            check("first Canvas activation selects the tool without opening its settings", editor.Tool == EditorTool.Canvas &&
                Get<CanvasSettingsWindow?>(session, "_canvasSettingsWindow")?.IsVisible != true);
            Invoke("ToggleCanvasSettings"); Pump();
            var panel = Get<Window>(session, "_canvasSettingsWindow");
            check("second Canvas activation opens settings through the production tool command", panel.IsVisible && panel.Owner == toolbar && editor.Tool == EditorTool.Canvas);
            ((Action<uint>)panel.GetType().GetField("BackgroundChanged", Private)!.GetValue(panel)!)(0x80123456);
            check("panel background event retains alpha in the active document", editor.Session.Document.Canvas.BackgroundColor == 0x80123456);
            SelectPerspectiveCanvas();
            var calibration = Get<CanvasCalibrationController>(session, "_canvasCalibration");
            Pump();
            if(Environment.GetEnvironmentVariable("FLOATCANVAS_ROUND2_PREVIEW_DIR") is { } panelOutput)
            {
                Directory.CreateDirectory(panelOutput);var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth),(int)Math.Ceiling(panel.ActualHeight),96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(panel);
                var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var file=File.Create(Path.Combine(panelOutput,"canvas-settings-"+FloatCanvas.Localization.AppText.CurrentLanguage+".png"));encoder.Save(file);
            }
            var original = editor.Session.Document.Canvas.Projection.Quad.ToArray();
            int undo = editor.Session.UndoCount;
            Pointer(OverlayPointerKind.Down, original[0]); Pointer(OverlayPointerKind.Move, original[0] + new PointD(12, 12)); Pointer(OverlayPointerKind.Up, original[0] + new PointD(12, 12));
            check("production pointer route commits one calibration drag", calibration.IsActive && !calibration.IsDragging && editor.Session.UndoCount == undo + 1 && editor.Session.Document.Canvas.Projection.Quad[0].DistanceTo(original[0] + new PointD(12, 12)) < .001);
            var moved = editor.Session.Document.Canvas.Projection.Quad.ToArray();
            var snapCorner = moved[1];
            Pointer(OverlayPointerKind.Down, snapCorner);
            Pointer(OverlayPointerKind.Move, snapCorner + new PointD(5,25));
            check("production pointer route snaps a corner to its neighbor axis", Math.Abs(editor.Session.Document.Canvas.Projection.Quad[1].X-snapCorner.X) < .001);
            host.TryFocusEditingCanvas();
            var keyState=new byte[256];keyState[0x11]=keyState[0xA2]=0x80;
            var controlPacket=(KeyboardInputPacket)Activator.CreateInstance(typeof(KeyboardInputPacket),Private,null,
                [1L,1L,KeyboardInputPacketKind.Key,0xA2u,0x1Du,0u,(uint)Environment.TickCount,false,false,GetForegroundWindow(),keyState,false,null,0x100u],null)!;
            Get<Action<KeyboardInputPacket,Action>>(host,"_dispatchPrivateKey")(controlPacket,()=>{});Pump();
            check("real private Ctrl key preserves the calibration transaction",calibration.IsDragging&&editor.Session.IsTransactionActive);
            Pointer(OverlayPointerKind.Move,snapCorner + new PointD(5,25),OverlayModifiers.Control);
            check("production Ctrl pointer route bypasses snapping", editor.Session.Document.Canvas.Projection.Quad[1].DistanceTo(snapCorner+new PointD(5,25)) < .001);
            Pointer(OverlayPointerKind.Move,snapCorner + new PointD(15,50),OverlayModifiers.Shift|OverlayModifiers.Control);
            Pointer(OverlayPointerKind.Up,snapCorner + new PointD(15,50),OverlayModifiers.Shift|OverlayModifiers.Control);
            check("production Shift pointer route constrains from the drag origin", editor.Session.Document.Canvas.Projection.Quad[1].DistanceTo(snapCorner+new PointD(0,50)) < .001);
            editor.Session.Undo();
            var nativeCorner=editor.Session.Document.Canvas.Projection.Quad[1];
            nint input=host.Diagnostics.Single().InputHandle;
            NativePointer(0x201,nativeCorner,1);NativePointer(0x200,nativeCorner+new PointD(5,25),1);
            check("native corner press owns mouse capture",GetCapture()==input&&calibration.IsDragging);
            HookControl(false);NativePointer(0x200,nativeCorner+new PointD(6,26),9);
            check("hook Ctrl during a native corner drag still follows the unsnapped pointer",calibration.IsDragging&&editor.Session.Document.Canvas.Projection.Quad[1].DistanceTo(nativeCorner+new PointD(6,26))<.001);
            HookControl(true);NativePointer(0x202,nativeCorner+new PointD(6,26),0);editor.Session.Undo();
            nativeCorner=editor.Session.Document.Canvas.Projection.Quad[1];
            HookControl(false);NativePointer(0x203,nativeCorner,9);
            check("Ctrl double-click on a corner starts a new captured calibration drag",calibration.IsDragging&&editor.Session.IsTransactionActive);
            NativePointer(0x200,nativeCorner+new PointD(7,27),9);
            check("Ctrl corner double-click drag follows its pointer",editor.Session.Document.Canvas.Projection.Quad[1].DistanceTo(nativeCorner+new PointD(7,27))<.001);
            NativePointer(0x202,nativeCorner+new PointD(7,27),8);HookControl(true);editor.Session.Undo();
            Invoke("RefreshToolbarState");
            string Glyph() => ((System.Windows.Shapes.Path)((System.Windows.Controls.Viewbox)Get<Dictionary<EditorTool,System.Windows.Controls.ContentPresenter>>(
                session,"_toolbarGlyphHosts")[EditorTool.Canvas].Content).Child).Data.ToString();
            string perspectiveGlyph = Glyph();
            Invoke("SetCanvasProjection",false); Invoke("RefreshToolbarState");
            check("toolbar switches to rectangular canvas glyph when perspective is disabled", Glyph() != perspectiveGlyph);
            Invoke("SetCanvasProjection",true); Invoke("RefreshToolbarState");
            check("toolbar returns to perspective glyph without changing its command", Glyph() == perspectiveGlyph);
            var reset = Get<System.Windows.Controls.Button>(panel,"reset");
            var resetReference = new RectD(bounds.Left - editor.ScreenOrigin.X, bounds.Top - editor.ScreenOrigin.Y, bounds.Width, bounds.Height);
            var beforeReset = editor.Session.Document.Canvas.Clone();
            int resetUndo = editor.Session.UndoCount;
            var expectedReset = CanvasCalibration.CreateInitial(resetReference, beforeReset.Projection.Columns, beforeReset.Projection.Rows);
            reset.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            check("actual primary reset fits the sole active screen while retaining its count pair and center",
                editor.Session.Document.Canvas.Projection.Quad.SequenceEqual(expectedReset.Quad) &&
                editor.Session.Document.Canvas.Projection.CellSize == expectedReset.CellSize &&
                editor.Session.Document.Canvas.Grid.FlatCellSize == expectedReset.CellSize &&
                editor.Session.Document.Canvas.Projection.Columns == beforeReset.Projection.Columns &&
                editor.Session.Document.Canvas.Projection.Rows == beforeReset.Projection.Rows &&
                editor.Session.Document.Canvas.Projection.Matrix == Projective2D.Identity && editor.Session.UndoCount == resetUndo + 1 &&
                CanvasGridGeometry.ReferenceCenter(editor.Session.Document.Canvas.Projection).DistanceTo(resetReference.Center) < 1e-10);
            editor.Session.Undo();
            check("actual primary reset restores the prior projection and spacing with one undo",
                editor.Session.Document.Canvas.Projection.Quad.SequenceEqual(beforeReset.Projection.Quad) &&
                editor.Session.Document.Canvas.Projection.CellSize == beforeReset.Projection.CellSize &&
                editor.Session.Document.Canvas.Grid.FlatCellSize == beforeReset.Grid.FlatCellSize && editor.Session.UndoCount == resetUndo);
            moved = editor.Session.Document.Canvas.Projection.Quad.ToArray();
            SelectPerspectiveCanvas();
            Invoke("SetCanvasGridCounts", 12, 7);
            check("grid count changes preserve all calibrated screen corners", editor.Session.Document.Canvas.Projection.Columns == 12 && editor.Session.Document.Canvas.Projection.Quad.SequenceEqual(moved));
            undo = editor.Session.UndoCount;
            Pointer(OverlayPointerKind.Down, moved[0]); Pointer(OverlayPointerKind.Move, moved[0] + new PointD(9, 4)); Invoke("OnKey", 0x1B);
            check("production Escape rolls back the whole calibration gesture", calibration.IsActive && !calibration.IsDragging && editor.Session.UndoCount == undo && editor.Session.Document.Canvas.Projection.Quad.SequenceEqual(moved));
            Invoke("ActivateTool", EditorTool.Rectangle);
            check("selecting a drawing tool ends the Canvas reference interaction while preserving perspective",
                editor.Tool == EditorTool.Rectangle && !calibration.IsActive && editor.Session.Document.Canvas.Projection.Enabled);
            var h = editor.Session.Document.Canvas.Projection.Matrix;
            PointD first = new(260, 170), last = new(300, 200);
            Pointer(OverlayPointerKind.Down, first); Pointer(OverlayPointerKind.Move, last); Pointer(OverlayPointerKind.Up, last);
            var element = editor.Session.Document.Elements.Single();
            var actual = h.TransformPoint(ElementGeometry.LocalToPlane(element).TransformPoint(new PointD(0, 0)));
            check("production drawing maps the screen press through inverse projection", actual.DistanceTo(first) < .01);
            Invoke("BeginCanvasFourPoints");
            check("four-point placement selects Canvas and owns its reference interaction", editor.Tool == EditorTool.Canvas && calibration.IsPlacing);
            var placement=CanvasCalibration.CreateInitial(editor.Session.Document.Viewport).Quad;
            var beforePlacement=editor.Session.Document.Canvas.Projection.Clone();undo=editor.Session.UndoCount;
            int[] clickOrder=[2,0,3,1];
            for(int stage=0;stage<3;stage++)
            {
                Pointer(OverlayPointerKind.Down,placement[clickOrder[stage]]);Pointer(OverlayPointerKind.Up,placement[clickOrder[stage]]);
                Pointer(OverlayPointerKind.Move,placement[clickOrder[stage+1]]);
                check("production four-point stage "+stage+" previews without committing",calibration.IsPlacing&&editor.Session.UndoCount==undo);
                if(Environment.GetEnvironmentVariable("FLOATCANVAS_ROUND2_PREVIEW_DIR") is { } output)
                {
                    Directory.CreateDirectory(output);using var surface=SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(640,360));
                    Invoke("Draw",surface);using var image=surface.Snapshot();using var data=image.Encode();
                    using var file=File.Create(Path.Combine(output,"four-points-stage-"+stage+".png"));data.SaveTo(file);
                }
            }
            Pointer(OverlayPointerKind.Down,placement[clickOrder[3]]);Pointer(OverlayPointerKind.Up,placement[clickOrder[3]]);
            check("production fourth click commits the ordered perspective in one undo",!calibration.IsPlacing&&editor.Session.UndoCount==undo+1&&editor.Session.Document.Canvas.Projection.Quad.SequenceEqual(placement));
            editor.Session.Undo();check("production four-point undo restores previous corners",editor.Session.Document.Canvas.Projection.Quad.SequenceEqual(beforePlacement.Quad));
            SelectPerspectiveCanvas(); moved = editor.Session.Document.Canvas.Projection.Quad.ToArray();
            Pointer(OverlayPointerKind.Down, moved[0]); Pointer(OverlayPointerKind.Move, moved[0] + new PointD(7, 3));
            if (preview)
            {
                Invoke("OnKey", 0x1B);
                editor.Session.Document.Canvas.Grid.Visible = true;
                SelectPerspectiveCanvas(); Invoke("OpenCanvasSettings"); Pump();
                Console.WriteLine("PREVIEW_READY: isolated production canvas settings and grid, 120 seconds"); Console.Out.Flush();
                var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(120) };
                timer.Tick += (_, _) => frame.Continue = false;
                try { timer.Start(); Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
            }
            Invoke("ToggleCanvasVisibility"); Pump();
            check("hide cancels calibration and hides its real settings window", !calibration.IsActive && !calibration.IsDragging && !panel.IsVisible && !host.IsVisible && editor.Session.Document.Canvas.Projection.Quad.SequenceEqual(moved));
            Console.WriteLine($"PROFILE {profile}; bounded native HWND host; production pointer event route; no physical input injection or multimonitor claim.");

            void SelectPerspectiveCanvas()
            {
                if (editor.Tool != EditorTool.Canvas) Invoke("ActivateTool", EditorTool.Canvas);
                Invoke("SetCanvasProjection", true);
            }
            object? Invoke(string name, params object[] args) => typeof(DesktopSession).GetMethod(name, Private)!.Invoke(session, args);
            void Pointer(OverlayPointerKind kind, PointD p, OverlayModifiers modifiers = OverlayModifiers.None)
            {
                var callback = (Action<OverlayPointerEvent>)typeof(OverlayHost).GetField("Pointer", Private)!.GetValue(host)!;
                callback(new(kind, new(p.X + editor.ScreenOrigin.X, p.Y + editor.ScreenOrigin.Y), OverlayMouseButton.Left, modifiers));
            }
            void NativePointer(int message,PointD point,int buttons)
            {
                int x=(int)(point.X+editor.ScreenOrigin.X-bounds.Left),y=(int)(point.Y+editor.ScreenOrigin.Y-bounds.Top);
                SendMessage(input,message,(nint)buttons,(nint)((y<<16)|(x&65535)));Pump();
            }
            void HookControl(bool up)
            {
                var isolation=Get<object>(host,"_keyboardIsolation");
                var key=new HookKey {VirtualKey=0xA2,ScanCode=0x1D,Flags=up?0x80u:0,Time=unchecked((uint)Environment.TickCount)};
                nint memory=Marshal.AllocHGlobal(Marshal.SizeOf<HookKey>());
                try {Marshal.StructureToPtr(key,memory,false);isolation.GetType().GetMethod("OnKeyboard",Private)!.Invoke(isolation,[0,(nuint)(up?0x101:0x100),memory]);Pump();}
                finally {Marshal.FreeHGlobal(memory);}
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
    [StructLayout(LayoutKind.Sequential)]private struct HookKey {public uint VirtualKey,ScanCode,Flags,Time;public nuint ExtraInfo;}
    [DllImport("user32.dll")]private static extern nint SendMessage(nint handle,int message,nint wParam,nint lParam);
    [DllImport("user32.dll")]private static extern nint GetCapture();
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
