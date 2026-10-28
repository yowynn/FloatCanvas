using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Threading;

internal static class PresentedPickingTests
{
    internal static void Run(Action<string, bool> check)
    {
        var assets = new AssetLibrary();
        string id = assets.Add(Convert.FromBase64String("R0lGODlhAQABAPIAAAAAAP8AAAD/AAAA////AP///wD///8A/yH/C05FVFNDQVBFMi4wAwEAAAAh+QQJCgAAACwAAAAAAQABAAADAggJACH5BAkKAAAALAAAAAABAAEAAAMCGAkAOw=="), "image/gif");
        var rear = new CanvasElement { Kind = ElementKind.Rectangle, Width = 50, Height = 50, Style = new() { Fill = 0xFF00FF00, StrokeWidth = 0 } };
        var image = new CanvasElement { Kind = ElementKind.Image, AssetId = id, Width = 50, Height = 50 };
        var scene = new SceneDocument { Viewport = new(0, 0, 100, 100), Elements = [rear, image] };
        var session = new EditorSession(scene, assets); var editor = new EditorController(session) { Tool = EditorTool.Select };
        using var worker = new SceneRenderer();
        worker.AnimationTime = TimeSpan.Zero; worker.ExportPng(scene, assets, true);
        using var transparent = worker.CaptureImageSnapshot();
        editor.ApplyPresentedScene(scene.Clone(), transparent, TimeSpan.Zero);
        editor.AnimationTime = TimeSpan.FromMilliseconds(150);
        Click(editor, new(25, 25));
        check("controller picks shown transparent frame while request clock is opaque", session.Selection.SetEquals([rear.Id]));
        session.SetSelection([]);
        worker.AnimationTime = TimeSpan.FromMilliseconds(150); worker.ExportPng(scene, assets, true);
        using var opaque = worker.CaptureImageSnapshot();
        editor.ApplyPresentedScene(scene.Clone(), opaque, TimeSpan.FromMilliseconds(150));
        editor.AnimationTime = TimeSpan.Zero;
        Click(editor, new(25, 25));
        check("controller picks shown opaque frame while request clock is transparent", session.Selection.SetEquals([image.Id]));
        session.SetSelection([]);
        scene.Elements.Remove(image); session.NotifyChanged();
        Click(editor, new(25, 25));
        check("shown snapshot cannot resurrect a removed live element", session.Selection.Count == 0);
        editor.DetachSession();
        PrunedShownAsset(check);
        ShiftTargetSurvivesFrameChange(check);
        BackgroundRectangle(check);
        BackgroundPointer(check);
    }

    private static void PrunedShownAsset(Action<string, bool> check)
    {
        var assets = new AssetLibrary();
        string id = assets.Add(Convert.FromBase64String("R0lGODlhAQABAPIAAAAAAP8AAAD/AAAA////AP///wD///8A/yH/C05FVFNDQVBFMi4wAwEAAAAh+QQJCgAAACwAAAAAAQABAAADAggJACH5BAkKAAAALAAAAAABAAEAAAMCGAkAOw=="), "image/gif");
        var item = new CanvasElement { Kind = ElementKind.Image, AssetId = id, Width = 80, Height = 80 };
        var scene = new SceneDocument { Viewport = new(0, 0, 100, 100), Elements = [item] };
        var session = new EditorSession(scene, assets);
        var editor = new EditorController(session) { Tool = EditorTool.Select, BackgroundPickingEnabled = true };
        using var renderer = new SceneRenderer { CacheByteLimit = 0, AnimationTime = TimeSpan.FromMilliseconds(150) };
        renderer.ExportPng(scene, assets, true); using var evicted = renderer.CaptureImageSnapshot();
        editor.ApplyPresentedScene(scene.Clone(), evicted, TimeSpan.FromMilliseconds(150), assets.Resources.ToArray());
        scene.Elements.Clear(); session.NotifyChanged(); assets.Prune([]);
        int errors = 0; editor.Message += _ => errors++;
        Click(editor, new(40, 40)); Pump(editor.PendingPicking);
        check("shown asset remains queryable after renderer eviction and live resource pruning", errors == 0 && session.Selection.Count == 0 && !session.IsTransactionActive);
        editor.DetachSession();
    }

    private static void ShiftTargetSurvivesFrameChange(Action<string, bool> check)
    {
        var assets = new AssetLibrary();
        string id = assets.Add(Convert.FromBase64String("R0lGODlhAQABAPIAAAAAAP8AAAD/AAAA////AP///wD///8A/yH/C05FVFNDQVBFMi4wAwEAAAAh+QQJCgAAACwAAAAAAQABAAADAggJACH5BAkKAAAALAAAAAABAAEAAAMCGAkAOw=="), "image/gif");
        var item = new CanvasElement { Kind = ElementKind.Image, AssetId = id, Width = 80, Height = 80 };
        var scene = new SceneDocument { Viewport = new(0, 0, 100, 100), Elements = [item] };
        var session = new EditorSession(scene, assets); session.SetSelection([item.Id]);
        var editor = new EditorController(session) { Tool = EditorTool.Select };
        using var renderer = new SceneRenderer { AnimationTime = TimeSpan.FromMilliseconds(150) };
        renderer.ExportPng(scene, assets, true); using var opaque = renderer.CaptureImageSnapshot();
        editor.ApplyPresentedScene(scene.Clone(), opaque, TimeSpan.FromMilliseconds(150));
        editor.Pointer(new(OverlayPointerKind.Down, new(40, 40), OverlayMouseButton.Left, OverlayModifiers.Shift));
        renderer.AnimationTime = TimeSpan.Zero; renderer.ExportPng(scene, assets, true);
        using var transparent = renderer.CaptureImageSnapshot(); editor.ApplyPresentedScene(scene.Clone(), transparent, TimeSpan.Zero);
        editor.Pointer(new(OverlayPointerKind.Up, new(40, 40), OverlayMouseButton.Left, OverlayModifiers.Shift));
        check("shift deselect retains pressed target when animation turns transparent before release", session.Selection.Count == 0 && !session.IsTransactionActive && session.UndoCount == 0);
        editor.DetachSession();
    }

    private static void BackgroundPointer(Action<string, bool> check)
    {
        var rectangle = new CanvasElement { Kind = ElementKind.Rectangle, Width = 100, Height = 100, Style = new() { Fill = 0xFFFF0000, StrokeWidth = 0 } };
        var session = new EditorSession(new() { Viewport = new(0, 0, 800, 600), Elements = [rectangle] });
        var editor = new EditorController(session) { Tool = EditorTool.Select, BackgroundPickingEnabled = true };
        int before = Environment.CurrentManagedThreadId, callbackThread = -1;
        session.Changed += (_, _) => callbackThread = Environment.CurrentManagedThreadId;
        var timer = Stopwatch.StartNew(); Click(editor, new(50, 50)); timer.Stop();
        check("point selection defers raster work and preserves queued button edges", timer.ElapsedMilliseconds < 1000 && session.Selection.Count == 0);
        Pump(editor.PendingPicking);
        check("queued point down/up selects exact element on UI dispatcher", session.Selection.SetEquals([rectangle.Id]) && callbackThread == before && !session.IsTransactionActive);
        session.SetSelection([]);
        Click(editor, new(50, 50)); editor.Cancel(); Pump(editor.PendingPicking);
        check("cancel before point completion leaves no selection or transaction", session.Selection.Count == 0 && !session.IsTransactionActive);
        var gate = (SemaphoreSlim)typeof(EditorController).GetField("pickingWorker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
        gate.Wait(); Click(editor, new(50, 50)); PumpOne();
        session.BeginTransaction("new external property edit");
        session.Document.Elements[0].Style.Fill = 0xFF00FF00; session.NotifyChanged();
        gate.Release(); Pump(editor.PendingPicking);
        check("stale point preflight preserves a newer external transaction", session.IsTransactionActive && session.Document.Elements[0].Style.Fill == 0xFF00FF00 && session.Selection.Count == 0);
        session.Cancel();
        editor.Tool = EditorTool.Freehand;
        editor.Pointer(new(OverlayPointerKind.Down, new(200, 200), OverlayMouseButton.Left, OverlayModifiers.None));
        check("ordinary unselected pen input begins immediately", session.IsTransactionActive && session.Document.Elements.Count == 2);
        editor.Cancel();
        session.SetSelection([session.Document.Elements[0].Id]);
        gate.Wait();
        editor.Pointer(new(OverlayPointerKind.Down, new(200, 200), OverlayMouseButton.Left, OverlayModifiers.Control)); PumpOne();
        foreach (var point in new[] { new OverlayPoint(210, 200), new(210, 220), new(230, 220) })
            editor.Pointer(new(OverlayPointerKind.Move, point, OverlayMouseButton.Left, OverlayModifiers.Control));
        editor.Pointer(new(OverlayPointerKind.Up, new(230, 240), OverlayMouseButton.Left, OverlayModifiers.Control));
        gate.Release(); Pump(editor.PendingPicking);
        var stroke = session.Document.Elements.Last();
        check("deferred pen start preserves noncollinear queued stroke samples", stroke.Kind == ElementKind.Freehand &&
            stroke.Points.Any(p => p == new PointD(10, 0)) && stroke.Points.Any(p => p == new PointD(10, 20)) && stroke.Points.Any(p => p == new PointD(30, 20)));
        editor.Tool = EditorTool.Eraser;
        Click(editor, new(50, 50)); Pump(editor.PendingPicking);
        // Start an erasing gesture, then deliberately overload only its delayed stream.
        editor.Pointer(new(OverlayPointerKind.Down, new(220, 220), OverlayMouseButton.Left, OverlayModifiers.None)); Pump(editor.PendingPicking);
        gate.Wait(); editor.Pointer(new(OverlayPointerKind.Move, new(221, 221), OverlayMouseButton.Left, OverlayModifiers.None)); PumpOne();
        int messages = 0; editor.Message += _ => messages++;
        for (int i = 0; i < 260; i++) editor.Pointer(new(OverlayPointerKind.Move, new(222 + i, 222), OverlayMouseButton.Left, OverlayModifiers.None));
        var blocked = editor.PendingPicking; gate.Release(); Pump(blocked);
        if (!ReferenceEquals(blocked, editor.PendingPicking)) Pump(editor.PendingPicking);
        check("overloaded pointer stream rolls back its own gesture with visible feedback", !session.IsTransactionActive &&
            session.Document.Elements.Any(e => e.Id == stroke.Id) && messages > 0);
        editor.DetachSession();
    }

    private static void BackgroundRectangle(Action<string, bool> check)
    {
        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(16, 16));
        surface.Canvas.Clear(SkiaSharp.SKColors.Transparent);
        using var image = surface.Snapshot(); using var png = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var assets = new AssetLibrary(); string id = assets.Add(png.ToArray());
        var large = new CanvasElement { Kind = ElementKind.Image, AssetId = id, Width = 30000, Height = 30000 };
        var scene = new SceneDocument { Viewport = new(0, 0, 30000, 30000), Elements = [large] };
        var session = new EditorSession(scene, assets); var editor = new EditorController(session) { Tool = EditorTool.Select, BackgroundPickingEnabled = true };
        var query = typeof(EditorController).GetMethod("SelectScreenRectangle", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var timer = Stopwatch.StartNew(); query.Invoke(editor, [scene.Viewport, false]); timer.Stop();
        check("large transparent marquee starts without scanning its pixels on input thread", timer.ElapsedMilliseconds < 1000 && !editor.PendingPicking.IsCompleted);
        editor.Cancel(); Pump(editor.PendingPicking);
        check("cancelled background marquee cannot publish a partial selection", session.Selection.Count == 0 && editor.PendingPicking.IsCompleted);
        large.Width = large.Height = 700;
        var visible = new CanvasElement { Kind = ElementKind.Rectangle, Width = 20, Height = 20, Transform = Affine2D.Translation(100, 100), Style = new() { Fill = 0xFFFF0000, StrokeWidth = 0 } };
        scene.Elements.Add(visible); session.NotifyChanged();
        query.Invoke(editor, [new RectD(0, 0, 800, 800), false]); Pump(editor.PendingPicking);
        check("background marquee computes complete exact ink result", session.Selection.SetEquals([visible.Id]));
        session.SetSelection([]); large.Width = large.Height = 30000;
        query.Invoke(editor, [scene.Viewport, false]);
        var rectangleQuery = editor.PendingPicking;
        Click(editor, new(110, 110)); Pump(editor.PendingPicking); Pump(rectangleQuery);
        check("new click supersedes pending rectangle without cancelling or overwriting the click", session.Selection.SetEquals([visible.Id]));
        session.SetSelection([]); large.Width = large.Height = 30000;
        query.Invoke(editor, [scene.Viewport, false]); session.NotifyChanged(); editor.Cancel(); Pump(editor.PendingPicking);
        check("obsolete rectangle result is rejected after content revision changes", session.Selection.Count == 0);
        editor.DetachSession();
    }

    private static void Click(EditorController editor, PointD point)
    {
        editor.Pointer(new(OverlayPointerKind.Down, new(point.X, point.Y), OverlayMouseButton.Left, OverlayModifiers.None));
        editor.Pointer(new(OverlayPointerKind.Up, new(point.X, point.Y), OverlayMouseButton.Left, OverlayModifiers.None));
    }
    private static void Pump(Task task)
    {
        var timeout = Stopwatch.StartNew();
        while (!task.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame); Thread.Sleep(1);
        }
        if (!task.IsCompleted) throw new TimeoutException("Background picking did not finish or cancel.");
        task.GetAwaiter().GetResult();
    }
    private static void PumpOne()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
