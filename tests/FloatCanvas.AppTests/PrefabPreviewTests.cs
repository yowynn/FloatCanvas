using System.Text.Json;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class PrefabPreviewTests
{
    internal static void Run(Action<string, bool> check)
    {
        SharedFreeze(check); Frames(check); AsyncDefinitions(check);
    }
    private static void SharedFreeze(Action<string, bool> check)
    {
        var fixture = PrefabInstanceBoundaryTests.Fixture(); var source = fixture.Session.Document;
        for (int i = 1; i < 20_000; i++)
        {
            var instance = fixture.Instance.Clone(); instance.Id = Guid.NewGuid(); instance.Transform = Affine2D.Translation(i % 100 * 10, i / 100 * 10);
            source.Instances.Add(instance); source.RootOrder!.Add(instance.Id);
        }
        var freezer = new CanvasPreviewScene(); string before = JsonSerializer.Serialize(source);
        var frozen = freezer.Capture(source, 1, 1, 1, -1, -1, false); var handle = freezer.EvaluationFor(frozen);
        check("twenty thousand instances freeze one copy of their definition's authored leaves", freezer.ClonedElementCount == 2 && freezer.EvaluationBuildCount == 0 &&
            frozen.Elements.Count == 0 && frozen.Definitions.Single().Graph.Elements.Count == 2 && frozen.Instances.Count == 20_000);
        var evaluated = Task.Run(() => handle.Scene).GetAwaiter().GetResult();
        check("prefab occurrences materialize once on a worker with unique render keys", evaluated.Leaves.Count == 40_000 && freezer.EvaluationBuildCount == 1 &&
            freezer.EvaluationBuildThreadId != Environment.CurrentManagedThreadId && evaluated.Leaves.Select(leaf => leaf.RuntimeElementId).Distinct().Count() == 40_000);
        for (int tick = 0; tick < 100; tick++)
            if (!ReferenceEquals(freezer.EvaluateFrozen(freezer.Capture(source, 1, 1, 1, -1, -1, false)), evaluated)) throw new InvalidOperationException("An unchanged prefab tick rebuilt its graph.");
        check("one hundred prefab ticks do not clone definitions per instance or rebuild world leaves", freezer.ClonedElementCount == 2 && freezer.EvaluationBuildCount == 1 && JsonSerializer.Serialize(source) == before);
        fixture.Definition.Graph.Elements[0].Style.Fill = 0xFF11AA44; fixture.Instance.Name = "later source edit";
        check("published definition graphs and instance metadata never alias mutable source DTOs", frozen.Definitions[0].Graph.Elements[0].Style.Fill != 0xFF11AA44 &&
            frozen.Instances[0].Name != fixture.Instance.Name && evaluated.Leaves.First().RenderElement.Style.Fill != 0xFF11AA44);
    }
    private static void Frames(Action<string, bool> check)
    {
        var fixture = PrefabInstanceBoundaryTests.Fixture(); var source = fixture.Session.Document; source.Viewport = new(0, 0, 400, 250);
        var image = fixture.Definition.Graph.Elements[0]; image.Kind = ElementKind.Image; image.AssetId = fixture.Session.Assets.Add(Gif(), "image/gif");
        var second = fixture.Instance.Clone(); second.Id = Guid.NewGuid(); second.Transform = Affine2D.Translation(230, 100);
        source.Instances.Add(second); source.RootOrder!.Add(second.Id);
        var background = new CanvasElement { Kind = ElementKind.Rectangle, Width = 30, Height = 20, Transform = Affine2D.Translation(20, 20), Style = new() { Fill = 0xFF44AA22, StrokeWidth = 0 } };
        source.Elements.Add(background); source.RootOrder.Insert(0, background.Id);
        var context = new CanvasPreviewContext(new object(), new object(), default, false, false, false);
        var freezer = new CanvasPreviewScene(); using var renderer = new CanvasPreviewRenderer();
        using var initial = Snapshot(1, 30); using var first = renderer.Render(initial);
        check("prefab preview publishes occurrence picking and both animated dirty rectangles", first.PickingEvaluation is { } evaluation &&
            first.Animation is { HasAnimations: true } animation && animation.DirtyBounds.Count == 2 &&
            evaluation.OccurrencesOfHostNode(fixture.Instance.Id).Count == 2 && evaluation.OccurrencesOfHostNode(second.Id).Count == 2);
        image.Style.Opacity = .55; // Deliberately retain Revision: mutable DTOs still require a safe freeze.
        using var edited = Snapshot(2, 150); using var next = renderer.Render(edited);
        check("a definition-only change invalidates every occurrence even with unchanged leaf table and revision token", freezer.GraphChanged &&
            next.PickingDocument!.Elements.Where(e => e.Kind == ElementKind.Image).All(e => e.Style.Opacity == .55) &&
            first.PickingDocument!.Elements.Where(e => e.Kind == ElementKind.Image).All(e => e.Style.Opacity == 1) && PixelsMatch(next));
        second.Transform = Affine2D.Translation(245, 130);
        using var moved = Snapshot(3, 150); using var positioned = renderer.Render(moved);
        check("instance-only placement updates preview bounds without writing definition geometry", positioned.PickingEvaluation!.OccurrencesOfHostNode(second.Id).First().ParentWorld.PlaneTransform.OffsetX == 245 &&
            fixture.Definition.Graph.Elements[0].Transform == Affine2D.Identity && PixelsMatch(positioned));
        long clones = freezer.ClonedElementCount, builds = freezer.EvaluationBuildCount; long hits = positioned.Cache!.HitCount;
        bool stable = true;
        for (int tick = 0; tick < 8; tick++)
        {
            using var snapshot = Snapshot(3, 150 + tick * 80); using var frame = renderer.Render(snapshot);
            stable &= ReferenceEquals(frame.PickingDocument, positioned.PickingDocument);
            hits = frame.Cache!.HitCount;
        }
        check("animated instance frames reuse one world view and static raster tiles after metadata changes", stable && freezer.ClonedElementCount == clones &&
            freezer.EvaluationBuildCount == builds && hits > 0);
        fixture.Definition.Graph.Elements.Add(new() { Kind = ElementKind.LiveBlur, Width = 30, Height = 30, Transform = Affine2D.Translation(20, 20),
            Effects = [new() { Kind = ImageEffectKind.Blur, Bounds = new(0, 0, 1, 1), Amount = 4 }], Style = new() { StrokeWidth = 0 } });
        var live = SceneEvaluation.Build(source);
        var regions = live.Leaves.Where(leaf => ElementGeometry.IsLiveEffect(leaf.RenderElement.Kind))
            .Select(leaf => SceneRenderer.LiveEffectSamplingBounds(live.RenderDocument, leaf.RenderElement)).ToArray();
        check("desktop capture discovery finds filters stored only in shared definitions", DesktopSession.MayContainLiveEffects(source) &&
            source.Elements.All(element => !ElementGeometry.IsLiveEffect(element.Kind)) && SceneRenderer.HasLiveEffects(source));
        check("each shared live filter has its own world sampling region", regions.Length == 2 && regions[0] != regions[1] && regions.All(region => !region.IsEmpty));

        CanvasPreviewSnapshot Snapshot(long revision, int milliseconds)
        {
            var frozen = freezer.Capture(source, revision, 1, revision, -1, -1, false, new HashSet<Guid>(), revision - 1);
            return new() { Context = context, SceneIdentity = source, Document = frozen, SourceDocument = frozen, FrozenEvaluation = freezer.EvaluationFor(frozen),
                Resources = fixture.Session.Assets.Resources.ToArray(), Outputs = [new(0, 0, 400, 250)], Revision = revision, Transaction = revision, ContentRevision = revision,
                GraphChanged = freezer.GraphChanged, AnimationTime = TimeSpan.FromMilliseconds(milliseconds) };
        }
        bool PixelsMatch(CanvasPreviewFrame frame)
        {
            using var surface = SKSurface.Create(new SKImageInfo(400, 250)); surface.Canvas.Clear(SKColors.Transparent); frame.Pixels.Draw(surface.Canvas);
            using var rendered = surface.Snapshot(); using var actual = SKBitmap.FromImage(rendered);
            using var direct = new SceneRenderer { AnimationTime = frame.AnimationTime }; using var expected = SKBitmap.Decode(direct.ExportPng(source, fixture.Session.Assets, true));
            return actual.Pixels.SequenceEqual(expected.Pixels);
        }
    }
    private static void AsyncDefinitions(Action<string, bool> check)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var fixture = PrefabInstanceBoundaryTests.Fixture(); var source = fixture.Session.Document; var freezer = new CanvasPreviewScene();
        var old = freezer.Capture(source, 1, 1, 1, -1, -1, false);
        fixture.Definition.Graph.Elements = Enumerable.Range(0, 50_000).Select(index => new CanvasElement { Kind = ElementKind.Rectangle, Width = 5, Height = 5 }).ToList();
        long revision = 2;
        var partial = freezer.PrepareAsync(source, 2, 1, 2, -1, -1, false, new HashSet<Guid>(), 1, () => revision == 2).AsTask();
        Console.WriteLine($"PREFAB_ASYNC_FREEZE nodes={source.Definitions[0].Graph.Elements.Count} completed={partial.IsCompleted} status={partial.Status} error={partial.Exception}");
        check("a large settings-only definition freeze yields before finishing instead of blocking input", !partial.IsCompleted);
        dispatcher.BeginInvoke(DispatcherPriority.Input, () => { revision = 3; fixture.Definition.Graph.Elements[0].Style.Fill = 0xFF993366; });
        Phase2RenderTests.Until(() => partial.IsCompleted, timeout: 30000);
        check("changing a definition during async freeze cannot publish a partial revision", !partial.GetAwaiter().GetResult() && freezer.CapturedRevision == 1 && old.Definitions[0].Graph.Elements.Count == 2);
        var latest = freezer.PrepareAsync(source, 3, 1, 3, -1, -1, false, new HashSet<Guid>(), 1, () => revision == 3).AsTask();
        Phase2RenderTests.Until(() => latest.IsCompleted, timeout: 30000);
        var captured = freezer.Capture(source, 3, 1, 3, -1, -1, false);
        check("the next bounded freeze publishes a coherent detached definition graph", latest.GetAwaiter().GetResult() && captured.Definitions[0].Graph.Elements.Count == 50_000 &&
            captured.Definitions[0].Graph.Elements[0].Style.Fill == 0xFF993366 && old.Definitions[0].Graph.Elements.Count == 2);
    }
    private static byte[] Gif() => [.. "GIF89a"u8.ToArray(), 1,0,1,0,0x80,0,0,255,0,0,0,0,255,
        0x21,0xFF,11,.. "NETSCAPE2.0"u8.ToArray(),3,1,0,0,0,
        0x21,0xF9,4,0,10,0,0,0,0x2C,0,0,0,0,1,0,1,0,0,2,2,0x44,1,0,
        0x21,0xF9,4,0,10,0,0,0,0x2C,0,0,0,0,1,0,1,0,0,2,2,0x4C,1,0,0x3B];
}
