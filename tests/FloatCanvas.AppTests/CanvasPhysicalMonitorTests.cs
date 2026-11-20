using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;

/// <summary>Real monitor enumeration and native output surfaces on the private test desktop.</summary>
internal static class CanvasPhysicalMonitorTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Hidden, null, [OverlayMonitorScope.AllScreens, false], null)!;
        using var desktop = new DesktopSession(Application.Current, host);
        Field<System.Windows.Forms.NotifyIcon>(desktop, "_tray").Visible = false;
        var editor = Field<EditorController>(desktop, "_editor"); var session = editor.Session;
        var monitors = host.Monitors.ToArray(); var primary = monitors.First(m => m.IsPrimary);
        var extent = monitors.Select(m => new RectD(m.Bounds.Left, m.Bounds.Top, m.Bounds.Width, m.Bounds.Height)).Aggregate(RectD.Union);
        var reference = new RectD(primary.Bounds.Left, primary.Bounds.Top, primary.Bounds.Width, primary.Bounds.Height);
        var view = new CanvasViewTransform(.75, new(90, 70));
        var markers = monitors.Select(m => new PointD(m.Bounds.Left + m.Bounds.Width * .4, m.Bounds.Top + m.Bounds.Height * .4)).ToArray();
        var document = new SceneDocument { Viewport = extent, AuthoringReference = new(reference, reference),
            Elements = markers.Select(point => new CanvasElement { Kind = ElementKind.Rectangle, Width = 80, Height = 60,
                Transform = Affine2D.Translation(view.ToCanvas(point).X, view.ToCanvas(point).Y),
                Style = new() { Fill = 0xFF20CC50, StrokeWidth = 0 } }).ToList() };
        document.Canvas.Grid.Visible = false; session.ReplaceDocument(document);
        Call("SynchronizeLiveDisplayLayout"); host.SetVisible(true);
        typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(host, true);
        try
        {
            Call("CurrentGroupCanvasView"); Call("ChangeCanvasViewing", view, false); Settle();
            string content = JsonSerializer.Serialize(session.Document.Elements), basis = JsonSerializer.Serialize(session.Document.AuthoringReference);
            for (int index = 0; index < monitors.Length; index++)
            {
                var monitor = monitors[index]; var point = markers[index] + new PointD(10, 10);
                var output = ((IEnumerable)typeof(OverlayHost).GetField("_windows", Hidden)!.GetValue(host)!).Cast<object>().ElementAt(index);
                var buffer = output.GetType().GetProperty("Buffer")!.GetValue(output)!;
                var surface = (SKSurface)buffer.GetType().GetProperty("Surface", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(buffer)!;
                using var image = surface.Snapshot(); using var pixels = SKBitmap.FromImage(image);
                using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(Path.Combine(ApplicationPaths.ProfileDirectory, "native-output-" + index + ".png"), encoded.ToArray());
                var ink = pixels.GetPixel((int)(point.X - monitor.Bounds.Left), (int)(point.Y - monitor.Bounds.Top));
                check("viewed artwork reaches the actual native output raster on " + monitor.DeviceName, ink.Green > 150 && ink.Red < 80 && ink.Alpha == 255);
                editor.Tool = EditorTool.Select; editor.ResumePresentedPicking();
                Pointer(OverlayPointerKind.Down, point); Pointer(OverlayPointerKind.Up, point);
                check("actual monitor position selects the source layer through the viewed mapping on " + monitor.DeviceName,
                    session.Selection.Contains(session.Document.Elements[index].Id));
                session.SetSelection([]); Settle();
            }
            check("viewing and picking on mixed-DPI outputs preserve authored geometry and reference", content == JsonSerializer.Serialize(session.Document.Elements) && basis == JsonSerializer.Serialize(session.Document.AuthoringReference));
            var handles = host.Diagnostics.Select(d => d.DisplayHandle).ToArray();
            host.SetMonitorScope(OverlayMonitorScope.PrimaryScreen); Settle();
            check("actual scope contraction recreates outputs without rescaling artwork or authoring basis", host.Monitors.Count == 1 && !handles.SequenceEqual(host.Diagnostics.Select(d => d.DisplayHandle)) &&
                content == JsonSerializer.Serialize(session.Document.Elements) && basis == JsonSerializer.Serialize(session.Document.AuthoringReference));
            host.SetMonitorScope(OverlayMonitorScope.AllScreens); Settle();
            check("actual all-screen restoration retains artwork basis and camera scale", host.Monitors.Count == monitors.Length &&
                content == JsonSerializer.Serialize(session.Document.Elements) && basis == JsonSerializer.Serialize(session.Document.AuthoringReference) &&
                Field<CanvasViewTransform>(desktop, "_canvasView").Zoom == view.Zoom);
            using var renderer = new SceneRenderer(); using var exported = SKBitmap.Decode(renderer.ExportPng(session.Document, session.Assets, transparent: true));
            check("mixed-DPI output and runtime viewport changes do not change formal export dimensions", exported.Width == (int)reference.Width && exported.Height == (int)reference.Height);
            // Scope recreation legitimately drops our simulated editing state:
            // this private fixture never acquires a physical input lease.
            typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(host, true);
            var secondary = monitors.First(m => !m.IsPrimary);
            var quad = new[] { new PointD(secondary.Bounds.Left + 180, secondary.Bounds.Top + 180),
                new(secondary.Bounds.Left + 680, secondary.Bounds.Top + 180), new(secondary.Bounds.Left + 680, secondary.Bounds.Top + 580),
                new(secondary.Bounds.Left + 180, secondary.Bounds.Top + 580) }.Select(view.ToCanvas).ToArray();
            if (!CanvasCalibration.TryCalibrate(CanvasCalibration.CreateInitial(reference), quad, extent, out var projection, out var error)) throw new InvalidOperationException(error);
            projection.Enabled = true; session.ExecuteSettings("physical calibration fixture", scene =>
            { scene.Canvas.Projection = projection; CanvasProjectionGeometry.InitializePlane(scene.Canvas); });
            Call("BeginCanvasCalibrationTool"); Settle();
            string beforeCalibration = JsonSerializer.Serialize(session.Document); int undo = session.UndoCount;
            var corner = view.ToView(quad[0]) + editor.CanvasOrigin; var delta = new PointD(27, 18);
            Pointer(OverlayPointerKind.Down, corner); Pointer(OverlayPointerKind.Move, corner + delta); Pointer(OverlayPointerKind.Up, corner + delta); Settle();
            var calibrated = session.Document.Canvas.Projection;
            Console.WriteLine("PHYSICAL_CALIBRATION " + JsonSerializer.Serialize(new { Expected = view.ToView(quad[0]) + delta, Actual = view.ToView(calibrated.Quad[0]),
                OtherDelta = calibrated.Quad.Skip(1).Zip(quad.Skip(1)).Max(pair => pair.First.DistanceTo(pair.Second)), UndoBefore = undo, UndoAfter = session.UndoCount,
                Camera = Field<CanvasViewTransform>(desktop, "_canvasView"), host.IsEditing, editor.Tool, Origin = editor.CanvasOrigin,
                Requested = Field<bool>(desktop, "_canvasCalibrationRequested"), Active = Field<CanvasCalibrationController>(desktop, "_canvasCalibration").IsActive }));
            check("viewed perspective corner follows physical movement on the 125-percent secondary screen", view.ToView(calibrated.Quad[0]).DistanceTo(view.ToView(quad[0]) + delta) < 1e-5 &&
                calibrated.Quad.Skip(1).SequenceEqual(quad.Skip(1)) && session.UndoCount == undo + 1);
            bool undone = session.Undo(); var afterUndo = JsonSerializer.Serialize(session.Document);
            int firstDifference = Enumerable.Range(0, Math.Min(beforeCalibration.Length, afterUndo.Length)).FirstOrDefault(index => beforeCalibration[index] != afterUndo[index], -1);
            Console.WriteLine("PHYSICAL_CALIBRATION_UNDO " + JsonSerializer.Serialize(new { undone, EqualContent = beforeCalibration == afterUndo,
                Camera = Field<CanvasViewTransform>(desktop, "_canvasView"), firstDifference,
                Before = firstDifference < 0 ? "" : beforeCalibration.Substring(firstDifference, Math.Min(180, beforeCalibration.Length - firstDifference)),
                After = firstDifference < 0 ? "" : afterUndo.Substring(firstDifference, Math.Min(180, afterUndo.Length - firstDifference)) }));
            check("secondary-screen calibration remains one exact authored undo and keeps viewing separate", undone &&
                beforeCalibration == afterUndo && Field<CanvasViewTransform>(desktop, "_canvasView") == view);
            Console.WriteLine("PHYSICAL_CANVAS_VIEWPORT " + JsonSerializer.Serialize(new { session.Document.Viewport, session.Document.AuthoringReference, View = Field<CanvasViewTransform>(desktop, "_canvasView") }));
            Console.WriteLine("PHYSICAL_CANVAS_EVIDENCE " + ApplicationPaths.ProfileDirectory);
        }
        finally { typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(host, false); }

        object? Call(string name, params object?[] args) => typeof(DesktopSession).GetMethod(name, Hidden)!.Invoke(desktop, args);
        void Pointer(OverlayPointerKind kind, PointD point)
        {
            Call("HandleCanvasPointer", new OverlayPointerEvent(kind, new(point.X, point.Y), OverlayMouseButton.Left, OverlayModifiers.None));
            Wait(() => editor.PendingPicking.IsCompleted); editor.PendingPicking.GetAwaiter().GetResult();
        }
        void Settle()
        {
            Call("RequestRender"); var preview = Field<CanvasPreviewPump>(desktop, "_canvasPreview");
            Wait(() => !preview.Busy && !Pending("_renderOperation") && !Pending("_presentOperation") && preview.Frame is { } frame &&
                frame.Context == (CanvasPreviewContext)Call("CurrentPreviewContext")! && Equals(typeof(DesktopSession).GetField("_presentedCanvasContext", Hidden)!.GetValue(desktop), frame.Context));
        }
        bool Pending(string name) => typeof(DesktopSession).GetField(name, Hidden)!.GetValue(desktop) is DispatcherOperation { Status: DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing };
    }
    private static void Wait(Func<bool> ready)
    {
        var timer = Stopwatch.StartNew();
        while (!ready())
        {
            if (timer.ElapsedMilliseconds > 15000) throw new TimeoutException("Physical monitor canvas did not settle.");
            var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame); Thread.Yield();
        }
    }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Hidden)!.GetValue(owner)!;
}
