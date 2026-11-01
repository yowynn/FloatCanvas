using System.Text.Json;
using System.IO;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using SkiaSharp;

internal static class GroupedPreviewTests
{
    internal static void Run(Action<string, bool> check)
    {
        FrozenEvaluation(check);
        PreviewWorldView(check);
    }
    private static void FrozenEvaluation(Action<string, bool> check)
    {
        var group = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [], Transform = Affine2D.Translation(30, 50) };
        var leaves = Enumerable.Range(0, 20_000).Select(index => new CanvasElement { Kind = ElementKind.Ellipse, GroupId = group.Id, Width = 12, Height = 9,
            Transform = Affine2D.Translation(index % 100 * 4, index / 100 * 3), Style = new() { Fill = 0xFF00AA55, StrokeWidth = 0 } }).ToList();
        group.ChildOrder = leaves.Select(item => item.Id).Reverse().ToList();
        var source = new SceneDocument { Groups = [group], Elements = leaves, RootOrder = [group.Id], Viewport = new(0, 0, 500, 400) };
        string before = JsonSerializer.Serialize(source);
        var freezer = new CanvasPreviewScene(); var snapshot = freezer.Capture(source, 1, 1, 1, 0, 0, false);
        var handle = freezer.EvaluationFor(snapshot);
        check("freezing a large group does not evaluate all world leaves on the UI thread", freezer.EvaluationBuildCount == 0);
        var evaluated = Task.Run(() => handle.Scene).GetAwaiter().GetResult();
        check("the render worker can materialize the shared evaluation once", freezer.EvaluationBuildCount == 1 && freezer.EvaluationBuildThreadId != Environment.CurrentManagedThreadId && evaluated.RenderDocument.IsRuntimeEvaluated);
        long clones = freezer.ClonedElementCount;
        for (int frame = 0; frame < 100; frame++)
        {
            var same = freezer.Capture(source, 1, 1, 1, 0, 0, false);
            if (!ReferenceEquals(freezer.EvaluateFrozen(same), evaluated)) throw new InvalidOperationException("Repeated animation ticks rebuilt the evaluated graph.");
        }
        check("one hundred unchanged preview ticks reuse one world graph without cloning any source leaves", freezer.EvaluationBuildCount == 1 && freezer.ClonedElementCount == clones);
        check("capturing and evaluating a graph never changes its serialized source", JsonSerializer.Serialize(source) == before && !source.IsRuntimeEvaluated);
        var range = handle.RenderRange(0, 0);
        check("source-table preview indices map into the reversed nested draw order", range == (19_999, 19_999));
        var oldWorld = evaluated.RenderDocument.Elements[^1].Transform;
        group.Transform = Affine2D.Translation(70, 80); group.Name = "changed"; group.Locked = true;
        var changed = freezer.Capture(source, 2, 1, 2, -1, -1, false, new HashSet<Guid> { group.Id }, 1);
        var latest = freezer.EvaluateFrozen(changed);
        check("a group-only known-ID change refreshes parent metadata while retaining source leaf snapshots", freezer.GraphChanged &&
            freezer.ClonedElementCount == clones && ReferenceEquals(snapshot.Elements, changed.Elements) && latest.RenderDocument.Elements[^1].Transform.OffsetX == 70 && latest.RenderDocument.Elements[^1].Locked);
        check("new group placement never mutates an older evaluated view", evaluated.RenderDocument.Elements[^1].Transform == oldWorld && snapshot.Groups[0].Name == "" && !evaluated.RenderDocument.Elements[^1].Locked);
        var staleOrder = changed.Groups[0].ChildOrder!.ToArray(); group.ChildOrder!.Reverse();
        var reordered = freezer.Capture(source, 3, 1, 3, -1, -1, false, new HashSet<Guid> { group.Id }, 2);
        var reorderedWorld = freezer.EvaluateFrozen(reordered);
        check("group child slots are detached even when the editor reports settings-only changes", changed.Groups[0].ChildOrder!.SequenceEqual(staleOrder) &&
            reorderedWorld.RenderDocument.Elements[0].Id == source.Elements[0].Id && freezer.EvaluationBuildCount == 3);
        bool guarded = false; try { SceneValidation.Validate(evaluated.RenderDocument); } catch (InvalidDataException) { guarded = true; }
        check("published world views cannot be persisted as editable source documents", guarded);
        guarded = false;
        try { freezer.Capture(evaluated.RenderDocument, 9, 9, 9, -1, -1, false); } catch (InvalidOperationException) { guarded = true; }
        check("the source freezer cannot launder a runtime view into an editable-looking snapshot", guarded);
    }

    private static void PreviewWorldView(Action<string, bool> check)
    {
        var group = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [], Transform = Affine2D.Translation(90, 60) };
        var image = new CanvasElement { Kind = ElementKind.Ellipse, GroupId = group.Id, Width = 60, Height = 40, Style = new() { Fill = 0xFF3366FF, StrokeWidth = 0 } };
        var other = new CanvasElement { Kind = ElementKind.Rectangle, Width = 30, Height = 30, Transform = Affine2D.Translation(300, 100), Style = new() { Fill = 0xFFFF0000, StrokeWidth = 0 } };
        group.ChildOrder = [image.Id];
        var source = new SceneDocument { Viewport = new(0, 0, 400, 250), Elements = [other, image], Groups = [group], RootOrder = [group.Id, other.Id] };
        var context = new CanvasPreviewContext(new object(), new object(), default, false, false, false);
        var freezer = new CanvasPreviewScene(); using var renderer = new CanvasPreviewRenderer(); var assets = new AssetLibrary();
        using var initial = Snapshot(1, 1, null);
        using var first = renderer.Render(initial);
        check("preview pixels and picking share the same evaluated occurrence and source mapping", first.PickingDocument is { IsRuntimeEvaluated: true } &&
            first.PickingSourceDocument is { IsRuntimeEvaluated: false } && first.PickingEvaluation?.ResolveSelectableNode(image.Id) == group.Id &&
            first.PickingDocument.Elements[0].Transform == Affine2D.Translation(90, 60));
        group.Transform = Affine2D.Translation(160, 80);
        using var moved = Snapshot(2, 1, new HashSet<Guid> { group.Id });
        using var next = renderer.Render(moved);
        using var composed = SKSurface.Create(new SKImageInfo(400, 250)); composed.Canvas.Clear(SKColors.Transparent); next.Pixels.Draw(composed.Canvas);
        using var pixels = composed.Snapshot(); using var actual = SKBitmap.FromImage(pixels);
        using var reference = new SceneRenderer(); using var expected = SKBitmap.Decode(reference.ExportPng(source, assets, true));
        check("group-only changes invalidate preview pixels without changing a child source entity", actual.Pixels.SequenceEqual(expected.Pixels) && image.Transform == Affine2D.Identity && source.Groups.Count == 1);
        check("world-frame publication preserves earlier occurrence geometry", first.PickingDocument!.Elements[0].Transform == Affine2D.Translation(90, 60) && next.PickingDocument!.Elements[0].Transform == Affine2D.Translation(160, 80));
        long evaluations = freezer.EvaluationBuildCount;
        using var animationTick = Snapshot(2, 1, null); using var same = renderer.Render(animationTick);
        check("worker frames reuse the same evaluated document until the source revision changes", ReferenceEquals(next.PickingDocument, same.PickingDocument) && freezer.EvaluationBuildCount == evaluations);

        CanvasPreviewSnapshot Snapshot(long revision, long elementRevision, IReadOnlySet<Guid>? changed)
        {
            var frozen = freezer.Capture(source, revision, elementRevision, revision, -1, -1, false, changed, revision - 1);
            return new() { Context = context, SceneIdentity = source, Document = frozen, SourceDocument = frozen, FrozenEvaluation = freezer.EvaluationFor(frozen),
                Resources = [], Outputs = [new(0, 0, 400, 250)], Revision = revision, Transaction = revision, ContentRevision = revision,
                BypassCache = freezer.GraphChanged };
        }
    }
}
