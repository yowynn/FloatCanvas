using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using SkiaSharp;

/// <summary>Opt-in production Draw/HandleCanvasPointer evidence, using an isolated bounded HWND host and no physical input.</summary>
internal static class BillboardCanvasRuntimeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        if (Application.Current is not null) throw new InvalidOperationException("Run --billboard-canvas-runtime-only in a fresh process.");
        string output = Environment.GetEnvironmentVariable("FLOATCANVAS_BILLBOARD_CANVAS_OUTPUT") ??
            Path.Combine(Path.GetTempPath(), "FloatCanvas-billboard-canvas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        ApplicationPaths.Configure(["--profile", Path.Combine(output, "profile"), "--primary-screen"]);
        new UserSettings { Language = "zh-CN", Theme = "light" }.Save();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        DesktopSession? desktop = null; OverlayHost? host = null;
        nint foreground = GetForegroundWindow(); nint[] handles = [];
        var frames = new List<object>();
        try
        {
            var primary = ((IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, null)!).First(m => m.IsPrimary);
            var tile = new PixelBounds(primary.WorkArea.Left + 24, primary.WorkArea.Top + 24, 800, 520);
            Func<IReadOnlyList<MonitorInfo>> provider = () => [primary with { Bounds = tile, WorkArea = tile }];
            host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null,
                [OverlayMonitorScope.PrimaryScreen, provider, false], null)!;
            handles = host.Diagnostics.SelectMany(pair => new[] { pair.InputHandle, pair.DisplayHandle }).Append(host.ControlHandle).ToArray();
            desktop = new DesktopSession(app, host);
            Field<System.Windows.Forms.NotifyIcon>(desktop, "_tray").Visible = false;
            // Controlled production callbacks require an editing host, but this fixture
            // does not acquire a global keyboard hook or send native pointer/key events.
            var isolation = typeof(OverlayHost).GetField("_keyboardIsolation", Private)!;
            (isolation.GetValue(host) as IDisposable)?.Dispose(); isolation.SetValue(host, null);
            var toolbar = Field<Window>(desktop, "_toolbar");
            toolbar.ShowActivated = false; toolbar.Show(); Pump();
            handles = handles.Append(new WindowInteropHelper(toolbar).Handle).ToArray();
            host.SetVisible(true);
            bool entered = false;
            Activate(new WindowInteropHelper(toolbar).Handle, () => entered = (bool)Invoke("SetEditing", true, false)!);
            Pump();
            check("billboard drawn-frame fixture enters only its own bounded editor", entered && host.IsEditing && host.IsVisible &&
                host.Diagnostics.All(pair => pair.Bounds.Width == 800 && pair.Bounds.Height == 520));

            var editor = Field<EditorController>(desktop, "_editor");
            var document = editor.Session.Document;
            document.Viewport = new(0, 0, 800, 520);
            document.Canvas.BackgroundColor = 0xFFF7F8FA; document.Canvas.Grid.Visible = false;
            var matrix = new Projective2D(1, .18, 0, .08, 1, 0, .0002, .0007, 1);
            var projection = CanvasCalibration.CreateInitial(document.Viewport);
            projection.Enabled = true; projection.Matrix = matrix;
            projection.Quad = projection.Quad.Select(matrix.TransformPoint).ToList();
            document.Canvas.Projection = projection;
            editor.Session.NotifyChanged();
            editor.Tool = EditorTool.Rectangle;
            editor.Style.Stroke = 0xFFB93349; editor.Style.StrokeWidth = 5; editor.Style.Fill = 0x20404040;
            editor.BillboardMode = true;
            var mapping = CanvasMapping.Create(document);

            PointD depthPoint = new(240, 390);
            Pointer(OverlayPointerKind.Move, depthPoint);
            var previewDepth = editor.VisibleBillboardDepthLine!.Value;
            using (var frame = Render("01-preview-depth"))
            {
                check("production hover previews a tilted equal-depth line without starting a gesture", editor.ActiveBillboardDepthLine is null &&
                    document.Elements.Count == 0 && editor.BillboardDrawingGuide is null && Math.Abs(previewDepth.ScreenDirection.Y) > .02);
                check("preview depth frame contains only a thin amber line with no point or blue center", Count(frame, Amber) > 300 &&
                    Count(frame, Blue) == 0 && OutsideLine(frame, previewDepth.ScreenPoint, previewDepth.ScreenDirection, 2.1) == 0);
            }
            Pointer(OverlayPointerKind.Down, depthPoint); Pointer(OverlayPointerKind.Up, depthPoint);
            var lockedDepth = editor.ActiveBillboardDepthLine!.Value;
            using (var frame = Render("02-fixed-depth"))
            {
                check("first production click locks the same depth and creates no object or undo entry", lockedDepth == previewDepth &&
                    document.Elements.Count == 0 && editor.Session.UndoCount == 0 && editor.BillboardDrawingGuide is null);
                check("fixed depth frame adds no click-point marker", Count(frame, Blue) == 0 &&
                    OutsideLine(frame, lockedDepth.ScreenPoint, lockedDepth.ScreenDirection, 2.1) == 0);
            }

            PointD first = new(260, 130), last = new(420, 270);
            Pointer(OverlayPointerKind.Down, first); Pointer(OverlayPointerKind.Move, last);
            var draft = editor.Session.Document.Elements.Single();
            var drawingGuide = editor.BillboardDrawingGuide!.Value;
            using (var frame = Render("03-drawing"))
            {
                check("production rectangle drag keeps depth and a vertical center-to-anchor connection", draft.IsBillboard &&
                    editor.ActiveBillboardDepthLine == lockedDepth && editor.Session.IsTransactionActive &&
                    drawingGuide.Center.DistanceTo(new(340, 200)) < 1e-7 && Math.Abs(drawingGuide.Center.X - drawingGuide.Anchor.X) < 1e-7 &&
                    lockedDepth.TryVerticalIntersection(drawingGuide.Center.X, out var intersection) && intersection.DistanceTo(drawingGuide.Anchor) < 1e-7);
                check("drawing frame renders the center ring, anchor dot and vertical guide", Near(frame, drawingGuide.Center, 8, Blue) > 20 &&
                    Near(frame, drawingGuide.Anchor, 4, Amber) > 15 &&
                    Near(frame, drawingGuide.Center + (drawingGuide.Anchor - drawingGuide.Center) * .5, 2, Amber) > 2);
                check("drawing frame retains the equal-depth guide beyond the rectangle", OutsideLine(frame, drawingGuide.Center, new(0,1), 6) > 250);
                check("drawing preview projects logical stroke 5 before mouse release", draft.Style.StrokeWidth == 5 &&
                    Math.Abs(draft.Width * lockedDepth.Scale - 160) < 1e-7 && Math.Abs(draft.Height * lockedDepth.Scale - 140) < 1e-7);
            }

            Pointer(OverlayPointerKind.Up, last);
            var guide = editor.BillboardSelectionGuide!.Value;
            using (var frame = Render("04-completed-selected"))
            {
                check("completed creation removes depth while retaining the selected anchor guide", editor.VisibleBillboardDepthLine is null &&
                    !editor.Session.IsTransactionActive && editor.Session.UndoCount == 1 && editor.Session.Selection.SetEquals([draft.Id]) &&
                    guide.Center.DistanceTo(drawingGuide.Center) < 1e-7 && guide.Anchor.DistanceTo(drawingGuide.Anchor) < 1e-7);
                check("completed frame has only the vertical amber guide, without the old equal-depth line", Count(frame, Amber) > 100 &&
                    OutsideLine(frame, guide.Center, new(0,1), 4.5) == 0 && Near(frame, guide.Center, 8, Blue) > 20);
                check("completed frame includes the real production selection outline", editor.SelectionScreenCorners.Count == 4 &&
                    editor.SelectionScreenCorners.All(point => Near(frame, point, 7, Blue) > 8));
            }

            editor.Session.SetSelection([]);
            using (var frame = Render("05-deselected"))
            {
                check("deselection hides every guide with the selection outline", editor.BillboardSelectionGuide is null &&
                    editor.VisibleBillboardDepthLine is null && Count(frame, Amber) == 0 && Count(frame, Blue) == 0);
                check("deselection retains the unchanged billboard drawing", editor.Session.Document.Elements.Count == 1 &&
                    draft.IsBillboard && Count(frame, color => color.Red > 130 && color.Red > color.Green * 1.4 && color.Blue < 150) > 500);
            }
            File.WriteAllText(Path.Combine(output, "frames.json"), JsonSerializer.Serialize(new
            {
                Kind = "Production DesktopSession.Draw SKSurface output; controlled production pointer callbacks; no physical input",
                Viewport = document.Viewport, Projection = matrix, Depth = lockedDepth,
                DrawingGuide = new { drawingGuide.Center, drawingGuide.Anchor },
                LogicalWidth = draft.Width, LogicalHeight = draft.Height, LogicalStroke = draft.Style.StrokeWidth, Frames = frames
            }, new JsonSerializerOptions { WriteIndented = true }));

            object? Invoke(string name, params object[] arguments) => typeof(DesktopSession).GetMethod(name, Private)!.Invoke(desktop, arguments);
            void Pointer(OverlayPointerKind kind, PointD point)
            {
                var physical = point + editor.ScreenOrigin;
                Invoke("HandleCanvasPointer", new OverlayPointerEvent(kind, new(physical.X, physical.Y), OverlayMouseButton.Left, OverlayModifiers.None, 0));
            }
            SKBitmap Render(string name)
            {
                using var surface = SKSurface.Create(new SKImageInfo(800, 520)); surface.Canvas.Clear(SKColors.Transparent);
                Invoke("Draw", surface);
                using var image = surface.Snapshot(); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
                var bytes = png.ToArray(); File.WriteAllBytes(Path.Combine(output, name + ".png"), bytes);
                var bitmap = SKBitmap.Decode(bytes);
                frames.Add(new { Name = name, AmberPixels = Count(bitmap, Amber), BluePixels = Count(bitmap, Blue),
                    Selected = editor.Session.Selection.Count, DepthVisible = editor.VisibleBillboardDepthLine is not null,
                    DrawingGuideVisible = editor.BillboardDrawingGuide is not null, SelectedGuideVisible = editor.BillboardSelectionGuide is not null });
                return bitmap;
            }
        }
        finally
        {
            host?.ExitEditing(); desktop?.Dispose(); if (desktop is null) host?.Dispose();
            foreach (var window in app.Windows.Cast<Window>().ToArray()) window.Close(); Pump();
            SynchronizationContext.SetSynchronizationContext(context); app.Shutdown();
            if (foreground != 0) SetForegroundWindow(foreground);
        }
        check("billboard drawn-frame fixture destroys all of its native windows", handles.All(handle => !IsWindow(handle)));
        Console.WriteLine("BILLBOARD_CANVAS_NATIVE_RENDER " + output);
    }

    // Include antialias coverage: a 1.5px vertical line centered on an integer X
    // need not contain any fully covered pixel. Neutral fill and red artwork are excluded.
    private static bool Amber(SKColor color) => color.Red is >= 205 and <= 245 && color.Green is > 110 and < 239 &&
        color.Red - color.Green > 10 && color.Green - color.Blue > 12;
    private static bool Blue(SKColor color) => color.Blue - color.Red > 65 && color.Green - color.Red > 30 && color.Blue > 170;
    private static int Count(SKBitmap bitmap, Func<SKColor,bool> predicate)
    { int count=0; for(int y=0;y<bitmap.Height;y++) for(int x=0;x<bitmap.Width;x++) if(predicate(bitmap.GetPixel(x,y))) count++; return count; }
    private static int Near(SKBitmap bitmap, PointD point, int radius, Func<SKColor,bool> predicate)
    {
        int count=0;
        for(int y=Math.Max(0,(int)point.Y-radius);y<=Math.Min(bitmap.Height-1,(int)point.Y+radius);y++)
            for(int x=Math.Max(0,(int)point.X-radius);x<=Math.Min(bitmap.Width-1,(int)point.X+radius);x++)
                if(predicate(bitmap.GetPixel(x,y))) count++;
        return count;
    }
    private static int OutsideLine(SKBitmap bitmap, PointD origin, PointD direction, double tolerance)
    {
        int count=0; double length=Math.Sqrt(direction.X*direction.X+direction.Y*direction.Y);
        for(int y=0;y<bitmap.Height;y++) for(int x=0;x<bitmap.Width;x++)
            if(Amber(bitmap.GetPixel(x,y)) && Math.Abs((x+.5-origin.X)*direction.Y-(y+.5-origin.Y)*direction.X)/length>tolerance) count++;
        return count;
    }
    private static T Field<T>(object target,string name)=>(T)target.GetType().GetField(name,Private)!.GetValue(target)!;
    private static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,()=>frame.Continue=false);Dispatcher.PushFrame(frame);}
    private static void Activate(nint window,Action action)
    {
        uint from=GetCurrentThreadId(),to=GetWindowThreadProcessId(GetForegroundWindow(),out _);
        bool attached=from!=to&&AttachThreadInput(from,to,true);
        try{SetForegroundWindow(window);SetFocus(window);action();}
        finally{if(attached)AttachThreadInput(from,to,false);}
    }
    [DllImport("user32.dll")]private static extern bool AttachThreadInput(uint from,uint to,bool attach);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint handle,out uint process);
    [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]private static extern nint SetFocus(nint handle);
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")]private static extern bool IsWindow(nint handle);
}
