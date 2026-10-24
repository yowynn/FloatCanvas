using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;

/// <summary>Real editing overlay and pointer state, without HWNDs or desktop input.</summary>
internal static class ProjectionUnsafeHoverTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var document = new SceneDocument { Viewport = new(0, 0, 240, 180) };
        document.Canvas.Projection = CanvasCalibration.CreateInitial(document.Viewport);
        document.Canvas.Projection.Enabled = true;
        document.Canvas.Projection.Matrix = new(1, 0, 0, 0, 1, 200, 0, .01, 1);
        var state = new EditorSession(document);
        var editor = new EditorController(state) { ScreenOrigin = new(-500, 100), Tool = EditorTool.Select };
        var host = (OverlayHost)RuntimeHelpers.GetUninitializedObject(typeof(OverlayHost));
        MonitorInfo[] monitors = [
            new("hover-first", new(-500, 100, 240, 180), new(-500, 100, 240, 180), 96, 96, true),
            new("hover-second", new(-260, 100, 240, 180), new(-260, 100, 240, 180), 96, 96, false)
        ];
        Set(host, "_monitors", monitors);
        var focus = typeof(OverlayHost).GetField("_focusTransitions", Private)!;
        focus.SetValue(host, Activator.CreateInstance(focus.FieldType));
        typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(host, true);
        typeof(OverlayHost).GetProperty("IsVisible")!.SetValue(host, true);
        var session = (DesktopSession)RuntimeHelpers.GetUninitializedObject(typeof(DesktopSession));
        using var renderer = new SceneRenderer();
        using var cache = new OverlaySceneCache(renderer);
        var calibration = new CanvasCalibrationController();
        Set(session, "_app", app); Set(session, "_host", host); Set(session, "_editor", editor);
        Set(session, "_assets", state.Assets); Set(session, "_renderer", renderer); Set(session, "_overlayCache", cache);
        Set(session, "_canvasCalibration", calibration);
        // GetUninitializedObject bypasses the production collection initializer.
        var dirtyRegions = new List<PixelBounds>();
        Set(session, "_pixelDirtyRegions", dirtyRegions);
        string before = JsonSerializer.Serialize(document);
        try
        {
            Observe(new(120, 140));
            check("safe hover keeps the production editing overlay transparent", Alpha() == 0 && !Pending());
            Observe(new(120, 50));
            check("unsafe hover requests a pixel refresh and softly highlights the whole forbidden area", Pending() && Alpha(20, 50) is >= 18 and <= 28 && Alpha(120, 140) == 0);
            check("hover without a dirty region invalidates every active output", dirtyRegions.SequenceEqual(monitors.Select(monitor => monitor.Bounds)));
            Abort(); Observe(new(130, 60));
            check("moving inside the same forbidden area does not queue redundant refreshes", !Pending() && Alpha() is >= 18 and <= 28);
            Observe(new(120, 140));
            check("returning to the safe area clears the highlight and schedules its removal", Pending() && Alpha() == 0);
            Abort(); Observe(new(120, 50)); Abort();
            Invoke(session, "ClearProjectionHover");
            check("leaving the canvas clears the hint until another real canvas pointer event", Pending() && Alpha() == 0);
            Abort(); Observe(new(120, 50)); Abort();
            calibration.Begin(state);
            check("perspective adjustment suppresses the ordinary hover tint", Alpha() == 0);
            calibration.End();
            Set(session, "_documentBusyValue", true);
            check("a document operation suppresses the hover tint", Alpha() == 0);
            Set(session, "_documentBusyValue", false); Set(session, "_cleanCaptureDepth", 1);
            check("clean capture suppresses the hover tint", Alpha() == 0);
            Set(session, "_cleanCaptureDepth", 0);
            document.Canvas.Projection.Enabled = false;
            check("disabled perspective has no forbidden-area tint", Alpha() == 0);
            document.Canvas.Projection.Enabled = true;
            typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(host, false);
            check("pass-through mode omits the hover overlay", Alpha() == 0);
            typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(host, true);
            Observe(new(260, 50));
            check("a point beyond the document viewport does not highlight the canvas", Alpha() == 0);
            Observe(new(120, 50)); Abort();
            Invoke(session, "ObserveProjectionHover", new OverlayPointerEvent(OverlayPointerKind.Cancel, new(-380, 150), OverlayMouseButton.None, OverlayModifiers.None));
            check("cancel clears the hint without modifying the document or undo history", Alpha() == 0 && JsonSerializer.Serialize(document) == before && state.UndoCount == 0);
            Abort();
            var local = new PixelBounds(-490, 110, 20, 20);
            Invoke(session, "RequestPixelRender", (object)new[] { local });
            check("a localized pixel refresh retains its requested region", Pending() && dirtyRegions.SequenceEqual([local]));
            Invoke(session, "RequestPixelRender", (object?)null);
            check("an unbounded refresh coalesced after a local refresh still includes every output", monitors.All(monitor => dirtyRegions.Contains(monitor.Bounds)));
            Invoke(session, "RequestRender");
            Invoke(session, "RequestPixelRender", (object)new[] { local });
            check("a full edit promotes the pixel queue and cannot be narrowed by a later local refresh", dirtyRegions.Count == 0 &&
                !(bool)typeof(DesktopSession).GetField("_pixelsOnlyRender", Private)!.GetValue(session)! &&
                ((DispatcherOperation)typeof(DesktopSession).GetField("_renderOperation", Private)!.GetValue(session)!).Priority == DispatcherPriority.Render);
            NativeLeaveOrdering(host, check);
        }
        finally { Abort(); }

        void Observe(PointD point) => Invoke(session, "ObserveProjectionHover", new OverlayPointerEvent(OverlayPointerKind.Move,
            new(point.X + editor.ScreenOrigin.X, point.Y + editor.ScreenOrigin.Y), OverlayMouseButton.None, OverlayModifiers.None));
        bool Pending() => (typeof(DesktopSession).GetField("_renderOperation", Private)!.GetValue(session) as DispatcherOperation)?.Status == DispatcherOperationStatus.Pending;
        void Abort()
        {
            (typeof(DesktopSession).GetField("_renderOperation", Private)!.GetValue(session) as DispatcherOperation)?.Abort();
            Set(session, "_renderOperation", null);
            Set(session, "_pixelsOnlyRender", false);
            dirtyRegions.Clear();
        }
        byte Alpha(int x = 120, int y = 50)
        {
            using var surface = SKSurface.Create(new SKImageInfo(240, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.Translate(500, -100);
            Invoke(session, "Draw", surface);
            using var image = surface.Snapshot(); using var pixels = SKBitmap.FromImage(image);
            return pixels.GetPixel(x, y).Alpha;
        }
    }

    private static void NativeLeaveOrdering(OverlayHost host, Action<string, bool> check)
    {
        var type = typeof(OverlayHost).GetNestedType("WindowState", BindingFlags.NonPublic)!;
        object Window(nint handle)
        {
            var window = RuntimeHelpers.GetUninitializedObject(type);
            type.GetField("Handle", Private)!.SetValue(window, handle);
            type.GetField("TrackingPointerLeave", Private)!.SetValue(window, true);
            return window;
        }
        int left = 0; host.PointerLeft += () => left++;
        Set(host, "_hoverInputWindow", (nint)2);
        Invoke(host, "OnPointerLeft", Window(1));
        check("a delayed leave from the previous monitor does not clear the newer hover", left == 0);
        Invoke(host, "OnPointerLeft", Window(2)); Invoke(host, "OnPointerLeft", Window(2));
        check("leaving the current input window raises one hover-clear event", left == 1);

        var monitor = new MonitorInfo("hover-second", new(240, 0, 240, 180), new(240, 0, 240, 180), 96, 96, false);
        var second = Window(2);
        var pair = typeof(OverlayHost).GetNestedType("MonitorPair", BindingFlags.NonPublic)!;
        var windows = (System.Collections.IList)Activator.CreateInstance(typeof(OverlayHost).GetField("_windows", Private)!.FieldType)!;
        windows.Add(Activator.CreateInstance(pair, [monitor, Window(3), second, null]));
        Set(host, "_windows", windows);
        Set(host, "_pressedButton", OverlayMouseButton.Left);
        Set(host, "_hoverInputWindow", (nint)1);
        Invoke(host, "TrackPointerWindowAt", (nint)2);
        Invoke(host, "OnPointerLeft", Window(1));
        check("a captured move tracks the actual second monitor instead of clearing it for the source monitor's leave", left == 1 &&
            (nint)typeof(OverlayHost).GetField("_hoverInputWindow", Private)!.GetValue(host)! == (nint)2 &&
            (bool)type.GetField("TrackingPointerLeave", Private)!.GetValue(second)! &&
            (OverlayMouseButton)typeof(OverlayHost).GetField("_pressedButton", Private)!.GetValue(host)! == OverlayMouseButton.Left);
        Set(host, "_pressedButton", OverlayMouseButton.None);
        Invoke(host, "OnPointerLeft", second);
        check("leaving the tracked second monitor still clears hover after the captured gesture ends", left == 2);

        Set(host, "_pressedButton", OverlayMouseButton.Left);
        Invoke(host, "TrackPointerWindowAt", (nint)99);
        Invoke(host, "TrackPointerWindowAt", (nint)99);
        check("captured moves over an auxiliary HWND clear each latest hover without arming a canvas leave", left == 4 &&
            (nint)typeof(OverlayHost).GetField("_hoverInputWindow", Private)!.GetValue(host)! == 0 &&
            (OverlayMouseButton)typeof(OverlayHost).GetField("_pressedButton", Private)!.GetValue(host)! == OverlayMouseButton.Left);
        Set(host, "_pressedButton", OverlayMouseButton.None);
    }

    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
}
