using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class ScreenDraftPreviewTests
{
    internal static void Run(Action<string, bool> check)
    {
        Slots(check); Frames(check); SharedBase(check);
    }

    private static void Slots(Action<string, bool> check)
    {
        var source = new SceneDocument(); var graph = new SceneGraph(source);
        var back = graph.InsertElement(Box(0xFF0000FF));
        var outer = graph.InsertGroup(new() { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] });
        var child = graph.InsertElement(Box(0xFF00FF00), outer.Id);
        var empty = graph.InsertGroup(new() { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] }, outer.Id);
        var inner = graph.InsertGroup(new() { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] }, empty.Id);
        var front = graph.InsertElement(Box(0xFFFF0000));
        Verify(source, null, "root"); Verify(source, outer.Id, "nonempty atomic scope");
        Verify(source, empty.Id, "empty atomic scope after a visible child"); Verify(source, inner.Id, "nested empty atomic scope");
        outer.Visible = false; Verify(source, inner.Id, "inherited hidden scope");

        var legacy = new CanvasGroup { Name = "interleaved" };
        var a = Box(0xFF0000FF); a.GroupId = legacy.Id;
        var b = Box(0xFF00FF00); var c = Box(0xFFFF0000); c.GroupId = legacy.Id;
        var old = new SceneDocument { Groups = [legacy], Elements = [a, b, c] };
        Verify(old, legacy.Id, "legacy scattered root scope appends without gathering");
        var block = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [a.Id, b.Id] };
        legacy.ParentId = block.Id; b.GroupId = block.Id; c.GroupId = null;
        old.Groups.Add(block); old.Elements = [a, b, c]; old.RootOrder = [block.Id, c.Id];
        Verify(old, legacy.Id, "legacy scope appends inside its nearest atomic paint context");

        var definition = new PrefabDefinition { Graph = new() { Elements = [Box(0xFF0000FF), Box(0xFF00FF00)] } };
        var instance = new PrefabInstance { DefinitionId = definition.Id };
        var blank = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] };
        var withInstance = new SceneDocument { Groups = [blank], Instances = [instance], Definitions = [definition], RootOrder = [blank.Id, instance.Id] };
        Verify(withInstance, blank.Id, "empty atomic scope precedes the complete following instance occurrence range");
        var frozen = new FrozenSceneEvaluation(withInstance.Clone());
        check("missing screen draft scope is rejected rather than appended to another owner", !frozen.TryScreenDraftSlot(Guid.NewGuid(), out _, out _));

        void Verify(SceneDocument document, Guid? scope, string name)
        {
            string before = JsonSerializer.Serialize(document);
            var frozenDocument = document.Clone(); var handle = new FrozenSceneEvaluation(frozenDocument);
            check(name + " resolves an insertion slot", handle.TryScreenDraftSlot(scope, out int index, out bool visible));
            var marker = Box(0xFFFFFF00); var reference = document.Clone(); new SceneGraph(reference).InsertElement(marker, scope);
            var expected = SceneEvaluation.Build(reference).RenderDocument.Elements.FindIndex(element => element.Id == marker.Id);
            check(name + " matches the real SceneGraph.InsertElement draw ledger", index == expected);
            check(name + " visibility follows the source scope", visible == (scope is null || new SceneGraph(document).IsVisible(scope.Value)));
            check(name + " slot query never inserts a source node", JsonSerializer.Serialize(document) == before &&
                handle.Scene.FindElement(marker.Id) is null && handle.Scene.RenderDocument.Elements.Count == expectedCount());
            int expectedCount() => SceneEvaluation.Build(document).Leaves.Count;
        }
    }

    private static void Frames(Action<string, bool> check)
    {
        var source = new SceneDocument { Viewport = new(0, 0, 160, 120), LayerOpacity = .6 };
        var graph = new SceneGraph(source);
        var group = graph.InsertGroup(new() { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [], Transform = Affine2D.Translation(20, 10) });
        var lower = graph.InsertElement(Box(0xFF0000FF), group.Id);
        var front = Box(0xFF00FF00); front.Transform = Affine2D.Translation(60, 25); graph.InsertElement(front);
        var raw = Box(0xFFFF0000); raw.Transform = Affine2D.Translation(30, 35);
        var mapping = CanvasMapping.Create(source);
        if (!BillboardGeometry.TryResolveScreenBottom(raw, mapping, ElementGeometry.LocalToPlane(raw), out var pose, out _)) throw new InvalidOperationException("screen draft fixture");
        var candidate = raw.Clone(); candidate.IsBillboard = true; candidate.Transform = pose.Linear;
        candidate.BillboardAnchor = pose.Anchor; candidate.BillboardHeight = pose.Height;
        string original = JsonSerializer.Serialize(source);
        var context = new CanvasPreviewContext(new object(), new object(), default, false, false, false, ScreenDraftEpoch: new object());
        var freezer = new CanvasPreviewScene(); var frozen = freezer.Capture(source, 1, 1, 1, -1, -1, false);
        using var renderer = new CanvasPreviewRenderer();
        using var snapshot = Snapshot(raw, candidate, context); using var frame = renderer.Render(snapshot);
        var expected = source.Clone(); new SceneGraph(expected).InsertElement(candidate, group.Id);
        using var referenceRenderer = new SceneRenderer();
        using var reference = SKBitmap.Decode(referenceRenderer.ExportPng(expected, new()));
        using var actual = Pixels(frame);
        check("worker resolves nested scope slot and renders the same pixels as a committed authored element", actual.Pixels.SequenceEqual(reference.Pixels));
        check("draft frame picks only the unchanged real base scene", frame.PickingDocument is { Elements.Count: 2 } &&
            frame.PickingDocument.Elements.All(element => element.Id != raw.Id) && frame.PickingEvaluation?.FindElement(raw.Id) is null &&
            frame.PickingEvaluation?.OccurrencesOfHostNode(group.Id).Count == 1);
        check("preview world candidate and raw content never enter source or its save data", JsonSerializer.Serialize(source) == original &&
            !source.IsRuntimeEvaluated && source.Elements.All(element => element.Id != raw.Id));
        using var pendingSnapshot = Snapshot(raw, null, context); using var pending = renderer.Render(pendingSnapshot);
        var pendingReference = source.Clone(); var half = raw.Clone(); half.Style.Opacity *= .5;
        new SceneGraph(pendingReference).InsertElement(half, group.Id);
        using var pendingExpected = SKBitmap.Decode(referenceRenderer.ExportPng(pendingReference, new()));
        using var pendingActual = Pixels(pending);
        check("pending screen frame follows scope order and single global opacity without a source candidate", pendingActual.Pixels.SequenceEqual(pendingExpected.Pixels));
        using var endedSnapshot = Snapshot(null, null, context with { ScreenDraftEpoch = new object() }); using var ended = renderer.Render(endedSnapshot);
        using var cleanExpected = SKBitmap.Decode(referenceRenderer.ExportPng(source, new())); using var cleanActual = Pixels(ended);
        check("ending a screen draft restores base pixels and the same immutable base picking view", cleanActual.Pixels.SequenceEqual(cleanExpected.Pixels) &&
            ReferenceEquals(frame.PickingDocument, ended.PickingDocument) && freezer.EvaluationBuildCount == 1 && freezer.ClonedElementCount == 2);
        var endingContext = context with { ScreenDraftEpoch = new object() };
        check("draft lifecycle changes request identity while preserving base-scene compatibility", context != endingContext && context.HasSameContent(endingContext));
        bool rejected = false;
        try { new CanvasPreviewScene().Capture(SceneEvaluation.WithTransientElement(frame.PickingDocument!, candidate, 1), 1, 1, 0, -1, -1, false); }
        catch (InvalidOperationException) { rejected = true; }
        check("render-only transient documents cannot return through the source freezer", rejected);

        using var color = SKSurface.Create(new SKImageInfo(12, 12)); color.Canvas.Clear(SKColors.Orange);
        using var colorImage = color.Snapshot(); using var encoded = colorImage.Encode(SKEncodedImageFormat.Png, 100);
        var privateAssets = new AssetLibrary(); string assetId = privateAssets.Add(encoded.ToArray());
        var imageDraft = raw.Clone(); imageDraft.Kind = ElementKind.Image; imageDraft.AssetId = assetId;
        using var imageSnapshot = new CanvasPreviewSnapshot
        {
            Context = context, SceneIdentity = source, Document = frozen, SourceDocument = frozen, FrozenEvaluation = freezer.EvaluationFor(frozen),
            Resources = privateAssets.Resources.ToArray(), Outputs = [new(0, 0, 160, 120)], Revision = 1, Transaction = 1, ContentRevision = 1,
            ScreenDraft = new(imageDraft, 0), ScreenDraftScopeId = group.Id
        };
        using var imageFrame = renderer.Render(imageSnapshot); using var imagePixels = Pixels(imageFrame);
        check("private image resources survive renderer pruning without becoming authored assets", imagePixels.GetPixel(40, 45).Red > 0 &&
            source.ReferencedAssets.All(id => id != assetId) && JsonSerializer.Serialize(source) == original);

        CanvasPreviewSnapshot Snapshot(CanvasElement? body, CanvasElement? world, CanvasPreviewContext captureContext) => new()
        {
            Context = captureContext, SceneIdentity = source, Document = frozen, SourceDocument = frozen, FrozenEvaluation = freezer.EvaluationFor(frozen),
            Resources = [], Outputs = [new(0, 0, 160, 120)], Revision = 1, Transaction = 1, ContentRevision = 1,
            ScreenDraft = body is null ? null : new(body.Clone(), 0, world?.Clone()), ScreenDraftScopeId = group.Id
        };
    }

    private static void SharedBase(Action<string, bool> check)
    {
        var source = new SceneDocument { Viewport = new(0, 0, 160, 120) };
        var group = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] };
        source.Groups.Add(group); source.RootOrder = [group.Id];
        source.Elements = Enumerable.Range(0, 50_000).Select(index => new CanvasElement { Kind = ElementKind.Rectangle,
            Width = 2, Height = 2, GroupId = group.Id, Transform = Affine2D.Translation(index % 500, index / 500) }).ToList();
        group.ChildOrder.AddRange(source.Elements.Select(element => element.Id));
        var freezer = new CanvasPreviewScene(); var frozen = freezer.Capture(source, 1, 1, 1, -1, -1, false);
        var handle = freezer.EvaluationFor(frozen); var world = handle.Scene.RenderDocument;
        long clones = freezer.ClonedElementCount;
        var raw = Box(0xFFFF0000); bool shared = true;
        for (int tick = 0; tick < 100; tick++)
        {
            raw.Transform = Affine2D.Translation(tick, tick);
            if (!handle.TryScreenDraftSlot(group.Id, out int index, out _)) throw new InvalidOperationException("large scope slot");
            var transient = SceneEvaluation.WithTransientElement(world, raw, index);
            shared &= transient.Elements.Count == 50_001 && ReferenceEquals(transient.Elements[0], world.Elements[0]) &&
                ReferenceEquals(transient.Elements[49_999], world.Elements[49_999]) && !ReferenceEquals(transient.Elements[^1], raw);
        }
        check("one hundred 50k-leaf screen draft views borrow base world leaves without cloning or rebuilding definitions", shared &&
            freezer.EvaluationBuildCount == 1 && freezer.ClonedElementCount == clones && handle.Scene.Leaves.Count == 50_000);
        check("transient view creation leaves frozen and authored source leaf tables unchanged", source.Elements.Count == 50_000 && world.Elements.Count == 50_000 &&
            world.Elements.All(element => element.Id != raw.Id) && source.Elements.All(element => element.Id != raw.Id));
    }

    private static CanvasElement Box(uint color) => new() { Kind = ElementKind.Rectangle, Width = 80, Height = 65, Style = new() { Fill = color, StrokeWidth = 0 } };
    private static SKBitmap Pixels(CanvasPreviewFrame frame)
    {
        using var surface = SKSurface.Create(new SKImageInfo(160, 120)); surface.Canvas.Clear(SKColors.Transparent); frame.Pixels.Draw(surface.Canvas);
        using var image = surface.Snapshot(); return SKBitmap.FromImage(image);
    }
}
