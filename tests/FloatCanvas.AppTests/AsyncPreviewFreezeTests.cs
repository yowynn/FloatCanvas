using System.Diagnostics;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;

internal static class AsyncPreviewFreezeTests
{
    internal static void Run(Action<string, bool> check)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var scene = new SceneDocument
        {
            Elements = Enumerable.Range(0, 100_000).Select(index => new CanvasElement
                { Kind = ElementKind.Rectangle, Width = 10, Height = 10, Transform = Affine2D.Translation(index % 100, index / 100) }).ToList(),
            Background = new() { Kind = ElementKind.Image, Width = 10, Height = 10 },
            Groups = [new() { Name = "old group" }], DisplayLayout = [new() { DeviceName = "old output", DpiX = 96 }]
        };
        var freezer = new CanvasPreviewScene();
        long revision = 0;
        var request = Stopwatch.StartNew();
        var initial = freezer.PrepareAsync(scene, 0, 0, 0, -1, -1, false, null, -1, () => revision == 0).AsTask();
        request.Stop();
        check("an unknown 100k-node freeze returns to the UI before finishing", !initial.IsCompleted && request.ElapsedMilliseconds < 50);
        int input = 0;
        dispatcher.BeginInvoke(DispatcherPriority.Input, () => input++);
        Phase2RenderTests.Until(() => initial.IsCompleted, timeout: 30000);
        check("input dispatch progresses between full-freeze slices", initial.GetAwaiter().GetResult() && input == 1);
        var old = freezer.Capture(scene, 0, 0, 0, -1, -1, false);
        long clones = freezer.ClonedElementCount;
        check("a prepared revision is consumed without a second full clone", freezer.FullBuildCount == 1 &&
            ReferenceEquals(old, freezer.Capture(scene, 0, 0, 0, -1, -1, false)) && freezer.ClonedElementCount == clones);

        revision = 1;
        scene.Elements[0].Transform = Affine2D.Translation(30, 40);
        var invalidated = freezer.PrepareAsync(scene, 1, 1, 1, -1, -1, true, null, -1, () => revision == 1).AsTask();
        dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            revision = 2; scene.Elements[0].Transform = Affine2D.Translation(50, 60);
            scene.Background!.Transform = Affine2D.Translation(7, 8);
            scene.Groups[0].Name = "new group"; scene.DisplayLayout[0].DeviceName = "new output";
        });
        Phase2RenderTests.Until(() => invalidated.IsCompleted, timeout: 30000);
        check("a changed revision discards a partial build without publishing mixed nodes", !invalidated.GetAwaiter().GetResult() &&
            freezer.CapturedRevision == 0 && old.Elements[0].Transform.OffsetX == 0 && old.Groups[0].Name == "old group");
        var latest = freezer.PrepareAsync(scene, 2, 2, 2, -1, -1, true, null, -1, () => revision == 2).AsTask();
        Phase2RenderTests.Until(() => latest.IsCompleted, timeout: 30000);
        var frozen = freezer.Capture(scene, 2, 2, 2, -1, -1, true);
        check("the retried unknown build detaches elements, background, groups and layout coherently", latest.GetAwaiter().GetResult() &&
            frozen.Elements[0].Transform.OffsetX == 50 && frozen.Background!.Transform.OffsetX == 7 &&
            frozen.Groups[0].Name == "new group" && frozen.DisplayLayout[0].DeviceName == "new output" &&
            old.Background!.Transform.OffsetX == 0 && old.DisplayLayout[0].DeviceName == "old output");

        revision = 3; scene.Elements[0].Transform = Affine2D.Translation(70, 80);
        clones = freezer.ClonedElementCount;
        var known = freezer.PrepareAsync(scene, 3, 3, 3, -1, -1, false,
            new HashSet<Guid> { scene.Elements[0].Id }, 2, () => revision == 3).AsTask();
        Phase2RenderTests.Until(() => known.IsCompleted, timeout: 30000);
        check("a declared element delta clones only its changed node", known.GetAwaiter().GetResult() &&
            freezer.ClonedElementCount == clones + 1);
        var beforeMany = freezer.Capture(scene, 3, 3, 3, -1, -1, false);
        revision = 4;
        foreach (var element in scene.Elements) element.Style.Fill = 0xFF00AA55;
        var allIds = scene.Elements.Select(element => element.Id).ToHashSet();
        clones = freezer.ClonedElementCount;
        long fullBuilds = freezer.FullBuildCount;
        request.Restart();
        var many = freezer.PrepareAsync(scene, 4, 4, 4, -1, -1, false, allIds, 3, () => revision == 4).AsTask();
        request.Stop();
        check("a known 100k-element change also returns before copying all changed nodes", !many.IsCompleted && request.ElapsedMilliseconds < 50);
        input = 0;
        dispatcher.BeginInvoke(DispatcherPriority.Input, () => input++);
        Phase2RenderTests.Until(() => many.IsCompleted, timeout: 30000);
        var afterMany = freezer.Capture(scene, 4, 4, 4, -1, -1, false);
        check("input progresses during a large known-element copy and old nodes remain immutable", many.GetAwaiter().GetResult() && input == 1 &&
            freezer.ClonedElementCount == clones + scene.Elements.Count && freezer.FullBuildCount == fullBuilds &&
            afterMany.Elements[0].Style.Fill == 0xFF00AA55 && afterMany.Elements[^1].Style.Fill == 0xFF00AA55 &&
            beforeMany.Elements[0].Style.Fill != 0xFF00AA55 && ReferenceEquals(beforeMany.Background, afterMany.Background));
        AsyncPump(check, dispatcher);
    }

    private static void AsyncPump(Action<string, bool> check, Dispatcher dispatcher)
    {
        var context = new CanvasPreviewContext(new object(), new object(), new(0, 0), false, false, false);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int captures = 0, current = 1, presented = 0;
        async ValueTask<CanvasPreviewSnapshot> Capture()
        {
            captures++; await release.Task;
            return Snapshot(current);
        }
        CanvasPreviewSnapshot Snapshot(int value, DesktopBackdrop? backdrop = null) => new()
        {
            Context = context, SceneIdentity = context.Session,
            Document = new() { LayerOpacity = value / 100d }, Resources = [], Outputs = [], Backdrop = backdrop
        };
        using (var pump = new CanvasPreviewPump(dispatcher, () => throw new InvalidOperationException("Synchronous capture was used."),
            snapshot => new(snapshot.Context, new([]), OverlayRenderBackend.Cpu, false, null, null, snapshot.Document.LayerOpacity), Capture))
        {
            pump.FrameReady += () => presented++;
            pump.Request(); current = 100;
            for (int index = 0; index < 40; index++) pump.Request();
            check("async freezing is busy and coalesces requests without starting parallel captures", pump.Busy && captures == 1);
            release.SetResult();
            Phase2RenderTests.Until(() => !pump.Busy && pump.CompletedFrames == 2);
            check("async freezing preserves the final pending input", captures == 2 && presented == 2 && pump.Frame!.RenderMilliseconds == 1);
        }

        var late = new TaskCompletionSource<CanvasPreviewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        int lateFrames = 0;
        var closing = new CanvasPreviewPump(dispatcher, () => Snapshot(1),
            snapshot => new(snapshot.Context, new([]), OverlayRenderBackend.Cpu, false, null, null, 0),
            () => new(late.Task));
        closing.FrameReady += () => lateFrames++;
        closing.Request(); var timer = Stopwatch.StartNew(); closing.Dispose(); timer.Stop();
        using var surface = SKSurface.Create(new SKImageInfo(2, 2));
        using var lateImage = surface.Snapshot();
        late.SetResult(Snapshot(100, new DesktopBackdrop([new(new(0, 0, 2, 2), lateImage)])));
        Phase2RenderTests.Until(() => lateImage.Handle == 0);
        check("shutdown never waits for a pending freeze or publishes its late result", timer.ElapsedMilliseconds < 50 && !closing.Busy && lateFrames == 0);

        var failure = new TaskCompletionSource<CanvasPreviewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0, errors = 0;
        using var recovering = new CanvasPreviewPump(dispatcher, () => Snapshot(100),
            snapshot => new(snapshot.Context, new([]), OverlayRenderBackend.Cpu, false, null, null, snapshot.Document.LayerOpacity),
            () => ++attempts == 1 ? new(failure.Task) : new(Snapshot(100)));
        recovering.Failed += _ => errors++;
        recovering.Request(); recovering.Request();
        failure.SetException(new InvalidOperationException("Own async freeze failure."));
        Phase2RenderTests.Until(() => !recovering.Busy && recovering.CompletedFrames == 1);
        check("an async capture failure does not lose a newer pending final value", errors == 1 && attempts == 2 &&
            recovering.Frame!.RenderMilliseconds == 1);
        Console.WriteLine("ASYNC_PREVIEW_FREEZE fullNodes=100000 inputSlices=true canceledPartial=true finalValue=true");
    }
}
