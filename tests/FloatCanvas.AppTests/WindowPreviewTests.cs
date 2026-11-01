using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;

internal static class WindowPreviewTests
{
    internal static void Run(Action<string, bool> check)
    { DirtyAndOwnership(check); PresentedPicking(check); LargeFrozenScene(check); }

    private static void DirtyAndOwnership(Action<string, bool> check)
    {
        var assets = new AssetLibrary(); var image = Image(assets); image.Transform = Affine2D.Translation(100, 80);
        var source = new SceneDocument { Viewport = new(0, 0, 400, 300), Elements = [Box(0xFF00FF00, 10, 10), image, Box(0xFFFFFF00, 350, 250)] };
        source.Canvas.Grid.Visible = false; object epoch = new(); var origin = new PointD(-1920, 40);
        var context = new CanvasPreviewContext(new object(), new object(), origin, false, false, false, WindowEpoch: epoch);
        var freezer = new CanvasPreviewScene(); var frozen = freezer.Capture(source, 1, 1, 0, -1, -1, false);
        using var renderer = new CanvasPreviewRenderer();
        var budget = new WindowFrameBudget(); using var redPixels = Bitmap(SKColors.Red); using var bluePixels = Bitmap(SKColors.Blue);
        using var red = Lease(source, image, redPixels, 1, budget); using var blue = Lease(source, image, bluePixels, 2, budget);
        using var firstSnapshot = Snapshot(red); using var first = renderer.Render(firstSnapshot); using var firstPixels = Pixels(first, origin);
        using var nextSnapshot = Snapshot(blue); using var next = renderer.Render(nextSnapshot); using var nextPixels = Pixels(next, origin);
        check("window preview uses fixed local image geometry independently of the physical monitor origin", firstPixels.GetPixel(150, 130) == SKColors.Red && firstPixels.GetPixel(11, 11) == SKColors.Lime);
        check("new window frame updates the dynamic layer while older raster pixels remain immutable", nextPixels.GetPixel(150, 130) == SKColors.Blue && firstPixels.GetPixel(150, 130) == SKColors.Red);
        check("window sequence changes reuse ordinary prefix and suffix cache tiles", first.Cache!.BuildCount == 2 && next.Cache!.BuildCount == first.Cache.BuildCount && next.Cache.HitCount > first.Cache.HitCount);
        check("window dirty copying stays within the source occurrence rather than the full monitor", next.Pixels.CopiedBytes < 400L * 300 * 4);
        check("window runtime ticks reuse one frozen evaluation and source node clone set", freezer.EvaluationBuildCount == 1 && freezer.ClonedElementCount == 3 && ReferenceEquals(first.PickingDocument, next.PickingDocument));
        firstSnapshot.Dispose();
        check("presented frame owns a separate frame-set lease after its input snapshot is disposed", first.PickingWindowFrames is { } retainedFrames && retainedFrames.TryGet(new(source.ResourceId, new(null, image.Id)), out var kept) && kept.Id.Sequence == 1 && ReferenceEquals(first.WindowOwnerContext, epoch));
        using var submission = first.Retain(); first.Dispose();
        check("a retained native submission owns independent pixels images and window handles", submission.PickingWindowFrames is { } submittedFrames && submittedFrames.TryGet(new(source.ResourceId, new(null, image.Id)), out var submitted) && submitted.Id.Sequence == 1);
        using var foreign = Snapshot(red, new object()); using var rejected = renderer.Render(foreign); using var rejectedPixels = Pixels(rejected, origin);
        check("worker context mismatch prevents a bundle from a different owner epoch being drawn or adopted for picking", rejected.PickingWindowFrames is null && rejectedPixels.GetPixel(150, 130) == SKColors.Blue && rejected.WindowInfo?.PixelBytes == 0);
        check("frame set publication never changes author window settings assets or graph identity", source.Elements.Count == 3 && image.WindowSource is not null && assets.Count == 1 && !source.IsRuntimeEvaluated);

        CanvasPreviewSnapshot Snapshot(WindowFrameLease frame, object? wrongOwner = null) => new()
        {
            Context = context, SceneIdentity = source, Document = frozen, SourceDocument = frozen, FrozenEvaluation = freezer.EvaluationFor(frozen),
            Resources = assets.Resources.ToArray(), Outputs = [new(-1920, 40, 400, 300)], Revision = 1, ContentRevision = 1,
            WindowFrames = WindowFrameSet.Capture(epoch, [frame]), WindowOwnerContext = wrongOwner ?? epoch
        };
    }

    private static void PresentedPicking(Action<string, bool> check)
    {
        foreach (bool background in new[] { false, true })
        {
            var assets = new AssetLibrary(); var image = Image(assets); image.WindowSource!.Fit = WindowSourceFit.Stretch;
            var rear = new CanvasElement { Kind = ElementKind.Rectangle, Width = 100, Height = 100, Style = new() { Fill = 0xFF00FF00, StrokeWidth = 0 } };
            var source = new SceneDocument { Viewport = new(0, 0, 120, 120), Elements = [rear, image] }; source.Canvas.Grid.Visible = false;
            var session = new EditorSession(source, assets); var editor = new EditorController(session) { Tool = EditorTool.Select, BackgroundPickingEnabled = background };
            object epoch = new(); var key = new WindowSourceKey(source.ResourceId, new(null, image.Id));
            using var clearPixels = new SKBitmap(100, 100); clearPixels.Erase(SKColors.Transparent);
            using var opaquePixels = Bitmap(SKColors.Red);
            using var clear = WindowFrameLease.FromBitmapCopy(key, image.WindowSource.BindingIntent, new(Guid.NewGuid(), 1, 1), clearPixels);
            using var opaque = WindowFrameLease.FromBitmapCopy(key, image.WindowSource.BindingIntent, new(Guid.NewGuid(), 1, 2), opaquePixels);
            using var shown = WindowFrameSet.Capture(epoch, [clear]); using var unshown = WindowFrameSet.Capture(epoch, [opaque]);
            var evaluated = SceneEvaluation.Build(source);
            editor.ApplyPresentedScene(evaluated.RenderDocument, null, TimeSpan.Zero, assets.Resources.ToArray(), session.PreviewRevision, shown, epoch);
            Click(editor, new(50, 50)); Pump(editor.PendingPicking);
            check($"actual controller uses displayed transparent window frame while a newer opaque frame waits background={background}", session.Selection.SetEquals([rear.Id]));
            session.SetSelection([]);
            editor.ApplyPresentedScene(evaluated.RenderDocument, null, TimeSpan.Zero, assets.Resources.ToArray(), session.PreviewRevision, unshown, epoch);
            Click(editor, new(50, 50)); Pump(editor.PendingPicking);
            check($"controller adopts newly displayed opaque window content background={background}", session.Selection.SetEquals([image.Id]));
            source.Elements.Remove(image); session.NotifyChanged(); session.SetSelection([]);
            Click(editor, new(50, 50)); Pump(editor.PendingPicking);
            check($"retained displayed frames cannot resurrect a removed source occurrence background={background}", session.Selection.Count == 0);
            editor.DetachSession();
        }
    }

    private static void LargeFrozenScene(Action<string, bool> check)
    {
        const int count = 40_000; var assets = new AssetLibrary(); var image = Image(assets); image.Transform = Affine2D.Translation(100, 80);
        var source = new SceneDocument { Viewport = new(0, 0, 400, 300), Elements = Enumerable.Range(0, count).Select(index => Box(0xFF334455, index % 400, index / 400)).ToList() };
        source.Elements.Insert(count / 2, image); source.Canvas.Grid.Visible = false;
        var session = new EditorSession(source, assets); string before = JsonSerializer.Serialize(source);
        var freezer = new CanvasPreviewScene(); var frozen = freezer.Capture(source, 1, 1, 0, -1, -1, false);
        object epoch = new(); var context = new CanvasPreviewContext(new object(), new object(), default, false, false, false, WindowEpoch: epoch);
        using var renderer = new CanvasPreviewRenderer(); using var pixels = Bitmap(SKColors.Red); var connection = Guid.NewGuid();
        bool stable = true; long builds = -1, hits = -1; SceneDocument? world = null;
        for (int tick = 0; tick < 100; tick++)
        {
            using var lease = WindowFrameLease.FromBitmapCopy(new(source.ResourceId, new(null, image.Id)), image.WindowSource!.BindingIntent, new(connection, 1, tick + 1), pixels);
            using var snapshot = new CanvasPreviewSnapshot
            {
                Context = context, SceneIdentity = source, Document = freezer.Capture(source, 1, 1, 0, -1, -1, false), SourceDocument = frozen,
                FrozenEvaluation = freezer.EvaluationFor(frozen), Resources = assets.Resources.ToArray(), Outputs = [new(0, 0, 400, 300)], Revision = 1, ContentRevision = 1,
                WindowFrames = WindowFrameSet.Capture(epoch, [lease]), WindowOwnerContext = epoch
            };
            using var frame = renderer.Render(snapshot); world ??= frame.PickingDocument; if (builds < 0) builds = frame.Cache!.BuildCount;
            stable &= ReferenceEquals(world, frame.PickingDocument) && frame.Cache!.BuildCount == builds && frame.Cache.WindowPlanBuildCount == 1;
            hits = frame.Cache!.HitCount;
        }
        check("40k static leaves and 100 video frames retain one evaluation and static cached spans", stable && freezer.EvaluationBuildCount == 1 && freezer.ClonedElementCount == count + 1 && hits >= 99);
        check("video publication leaves author content history assets and serialized settings unchanged", session.ContentVersion == 0 && session.UndoCount == 0 && assets.Count == 1 && JsonSerializer.Serialize(source) == before);
    }

    private static CanvasElement Image(AssetLibrary assets)
    {
        using var bitmap = Bitmap(SKColors.Blue); using var image = SKImage.FromBitmap(bitmap); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return new() { Kind = ElementKind.Image, Width = 100, Height = 100, AssetId = assets.Add(png.ToArray()), WindowSource = new() { BindingIntent = Guid.NewGuid() }, Style = new() { StrokeWidth = 0 } };
    }
    private static SKBitmap Bitmap(SKColor color) { var bitmap = new SKBitmap(100, 100); bitmap.Erase(color); return bitmap; }
    private static WindowFrameLease Lease(SceneDocument source, CanvasElement image, SKBitmap bitmap, long sequence, WindowFrameBudget budget) =>
        WindowFrameLease.FromBitmapCopy(new(source.ResourceId, new(null, image.Id)), image.WindowSource!.BindingIntent, new(Guid.NewGuid(), 1, sequence), bitmap, budget: budget);
    private static CanvasElement Box(uint color, double x, double y) => new() { Kind = ElementKind.Rectangle, Width = 2, Height = 2, Transform = Affine2D.Translation(x, y), Style = new() { Fill = color, StrokeWidth = 0 } };
    private static SKBitmap Pixels(CanvasPreviewFrame frame, PointD origin)
    { using var surface = SKSurface.Create(new SKImageInfo(400, 300)); surface.Canvas.Clear(SKColors.Transparent); surface.Canvas.Translate(-(float)origin.X, -(float)origin.Y); frame.Pixels.Draw(surface.Canvas); using var image = surface.Snapshot(); return SKBitmap.FromImage(image); }
    private static void Click(EditorController editor, PointD point)
    { editor.Pointer(new(OverlayPointerKind.Down, new(point.X, point.Y), OverlayMouseButton.Left, OverlayModifiers.None)); editor.Pointer(new(OverlayPointerKind.Up, new(point.X, point.Y), OverlayMouseButton.Left, OverlayModifiers.None)); }
    private static void Pump(Task task)
    {
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(10))
        { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); Thread.Sleep(1); }
        if (!task.IsCompleted) throw new TimeoutException("Window picking did not finish."); task.GetAwaiter().GetResult();
    }
}
