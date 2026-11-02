using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class GroupCanvasSceneTests
{
    internal static void Run(Action<string, bool> check)
    {
        OrdinaryGroups(check); SharedDrafts(check); ExternalDrafts(check); FullCanvasFrames(check); TemporaryRefreshPixels(check);
    }

    private static void OrdinaryGroups(Action<string, bool> check)
    {
        var parent = new CanvasGroup { Transform = Affine2D.Translation(40, 35), ElevationScale = 1.3, OrderMode = GroupOrderMode.AtomicBlock };
        var group = new CanvasGroup { ParentId = parent.Id, Transform = new(1.2, .15, .1, .9, 25, 20), ElevationScale = 1.2, OrderMode = GroupOrderMode.AtomicBlock };
        var plane = Box(0xff33aa55, 10, 15); plane.GroupId = group.Id;
        var billboard = Box(0xff3355cc, 80, 50); billboard.GroupId = group.Id;
        billboard.IsBillboard = true; billboard.BillboardAnchor = new(80, 110); billboard.BillboardHeight = 35;
        var outside = Box(0xffaa3322, 400, 100);
        group.ChildOrder = [billboard.Id, plane.Id]; parent.ChildOrder = [group.Id];
        var document = new SceneDocument { Viewport = new(0, 0, 600, 400), Groups = [parent, group],
            Elements = [outside, plane, billboard], RootOrder = [outside.Id, parent.Id], Background = Box(0xffdddddd, 0, 0),
            Kind = DocumentKind.FrozenCapture };
        document.Canvas.BackgroundColor = 0xffeef0f2;
        document.Canvas.Grid.FlatTranslation = new(3, 4);
        var session = new EditorSession(document); var cache = new GroupCanvasScene(); var origin = new PointD(-500, 40);
        var initial = cache.Capture(session, null, GroupCanvasMode.Temporary, origin, session.Assets);
        check("main canvas without a group uses its owning document directly", ReferenceEquals(initial.Document, document) && !initial.IsProjected && !initial.IsRootPreview && initial.Origin == origin);
        session.SetEditScope(group.Id);
        string before = JsonSerializer.Serialize(document); int undo = session.UndoCount, assets = session.Assets.Count;
        var isolated = cache.Capture(session, null, GroupCanvasMode.Temporary, origin, session.Assets);
        var wholeWorld = SceneEvaluation.Build(document); var isolatedWorld = SceneEvaluation.Build(isolated.Document);
        check("independent group canvas exposes its own local subtree without scope or placement ancestors", isolated.IsProjected && !isolated.IsRootPreview &&
            isolated.Document.Elements.Count == 2 && isolated.Document.Groups.Count == 0 && isolated.Document.Elements.All(element => element.GroupId is null) &&
            isolatedWorld.Leaves.Select(leaf => leaf.Source).SequenceEqual(wholeWorld.OccurrencesOfHostNode(group.Id).Select(leaf => leaf.Source)));
        check("independent group canvas strips host placement while source editing mapping reconstructs local geometry", isolated.EditingPlacement is { } local && isolatedWorld.Leaves.All(leaf =>
        {
            var placed = wholeWorld.FindElement(leaf.RuntimeElementId)!.Clone(); SceneGraph.ApplyPlacement(placed, local);
            return new[] { default(PointD), new PointD(20, 10) }.All(point => placed.Transform.TransformPoint(point).DistanceTo(leaf.RenderElement.Transform.TransformPoint(point)) < 1e-7) &&
                placed.BillboardAnchor.DistanceTo(leaf.RenderElement.BillboardAnchor) < 1e-7 && Math.Abs(placed.BillboardHeight - leaf.RenderElement.BillboardHeight) < 1e-7;
        }) && isolated.Origin == origin);
        check("independent group defaults are neutral and never inherit the host canvas or background", isolated.Document.Background is null && isolated.Document.Kind == document.Kind &&
            isolated.Document.Canvas.Grid.FlatTranslation == default && isolated.Document.Canvas.BackgroundColor == 0 && isolated.Document.Viewport == document.Viewport);
        check("temporary canvas capture leaves authored content assets history and edit scope unchanged", JsonSerializer.Serialize(document) == before && session.UndoCount == undo &&
            session.Assets.Count == assets && session.EditScopeId == group.Id);
        var unchanged = cache.Capture(session, null, GroupCanvasMode.Temporary, origin, session.Assets);
        check("unchanged animation ticks reuse one isolated source and view epoch", ReferenceEquals(isolated, unchanged));
        session.BeginTransaction("group sample"); plane.Width = 75; session.NotifyChanged();
        var sample = cache.Capture(session, null, GroupCanvasMode.Temporary, origin, session.Assets);
        check("active group gestures refresh detached pixels while keeping the same view identity", sample.Document.Elements.Single(element => element.Id == plane.Id).Width == 75 &&
            isolated.Document.Elements.Single(element => element.Id == plane.Id).Width != 75 && ReferenceEquals(sample.Epoch, isolated.Epoch) && session.IsTransactionActive);
        session.Cancel();
        var root = cache.Capture(session, null, GroupCanvasMode.RootProject, origin, session.Assets);
        check("root group preview includes other layers and background without leaving the internal list", root.IsRootPreview && ReferenceEquals(root.Document, session.Document) &&
            root.Document.Elements.Count == 3 && root.Document.Background is not null && session.EditScopeId == group.Id && session.UndoCount == undo);
        var monitors = new object();
        var oldContext = new CanvasPreviewContext(session, monitors, origin, false, false, false, GroupEpoch: isolated.Epoch);
        var rootContext = oldContext with { GroupEpoch = root.Epoch };
        var restored = cache.Capture(session, null, GroupCanvasMode.Temporary, origin, session.Assets);
        check("view mode A to B to A rejects both older main-canvas frame identities", !oldContext.HasSameContent(rootContext) &&
            !oldContext.HasSameContent(oldContext with { GroupEpoch = restored.Epoch }));
        session.SetEditScope(parent.Id); cache.Capture(session, null, GroupCanvasMode.Temporary, origin, session.Assets);
        session.SetEditScope(group.Id); var scopeRestored = cache.Capture(session, null, GroupCanvasMode.Temporary, origin, session.Assets);
        check("scope A to B to A cannot revive an older isolated frame", !ReferenceEquals(restored.Epoch, scopeRestored.Epoch));

        session.Document.Canvas.Projection = CanvasCalibration.CreateInitial(document.Viewport);
        session.Document.Canvas.Projection.Enabled = true; session.NotifyChanged();
        var perspective = cache.Capture(session, null, GroupCanvasMode.Temporary, origin, session.Assets);
        var mapping = CanvasMapping.Create(session.Document); var isolatedMapping = CanvasMapping.Create(perspective.Document);
        check("host perspective changes do not alter an independent group's neutral projection", mapping.IsPerspective && !isolatedMapping.IsPerspective &&
            perspective.Document.Canvas.Grid.FlatTranslation == default);
    }

    private static void SharedDrafts(Action<string, bool> check)
    {
        var owner = new EditorSession(new() { Elements = [Box(0xffcc3333, 30, 40)], Viewport = new(0, 0, 500, 300) });
        owner.SetSelection(owner.Document.Elements.Select(element => element.Id)); var created = owner.CreatePrefabFromSelection("Shared");
        owner.PlacePrefab(created.DefinitionId, new(Affine2D.Translation(220, 80), 1));
        var outside = Box(0xff3399cc, 400, 230); owner.Add(outside);
        var draft = owner.BeginPrefabEdit(created.DefinitionId); var returnOrigin = new PointD(-1920, 70);
        var context = new PrefabEditContext(owner, draft, returnOrigin, EditorTool.Select);
        var cache = new GroupCanvasScene(); string before = JsonSerializer.Serialize(owner.Document); int undo = owner.UndoCount;
        var local = cache.Capture(draft.Session, context, GroupCanvasMode.Temporary, new(200, 10), owner.Assets);
        check("temporary shared canvas edits its real draft document directly", ReferenceEquals(local.Document, draft.Session.Document) && !local.IsRootPreview && local.Origin == new PointD(200, 10));
        draft.Session.BeginTransaction("shared sample"); draft.Session.Document.Elements.Single().Width = 80; draft.Session.NotifyChanged();
        var root = cache.Capture(draft.Session, context, GroupCanvasMode.RootProject, new(200, 10), owner.Assets);
        var leaves = SceneEvaluation.Build(root.Document).Leaves;
        check("root shared preview updates every occurrence and retains unrelated root layers", root.IsRootPreview && root.IsProjected && leaves.Count == 3 &&
            leaves.Count(leaf => leaf.RenderElement.Width == 80) == 2 && leaves.Any(leaf => leaf.HostNodeId == outside.Id));
        check("root shared preview uses the outer owner's physical origin", root.Origin == returnOrigin);
        check("switching view never publishes a draft gesture or changes owner undo", draft.Session.IsTransactionActive && owner.UndoCount == undo && JsonSerializer.Serialize(owner.Document) == before);
        draft.Session.Cancel(); draft.Session.SetSelection(draft.Session.Document.Elements.Select(element => element.Id));
        var nested = draft.Session.CreatePrefabFromSelection("Nested"); var inner = draft.Session.BeginPrefabEdit(nested.DefinitionId);
        var innerContext = new PrefabEditContext(draft.Session, inner, new(80, 90), EditorTool.Select, context);
        inner.Session.BeginTransaction("nested sample"); inner.Session.Document.Elements.Single().Width = 92; inner.Session.NotifyChanged();
        var nestedRoot = cache.Capture(inner.Session, innerContext, GroupCanvasMode.RootProject, new(10, 20), owner.Assets);
        check("nested shared previews compose all staged parents and use the outermost return origin", nestedRoot.Origin == returnOrigin &&
            SceneEvaluation.Build(nestedRoot.Document).Leaves.Count(leaf => leaf.RenderElement.Width == 92) == 2 && JsonSerializer.Serialize(owner.Document) == before);
        inner.Session.Cancel();
    }

    private static void ExternalDrafts(Action<string, bool> check)
    {
        var edited = new SceneDocument { Elements = [Box(0xff4488cc, 30, 40)], Viewport = new(0, 0, 500, 300) };
        var owner = new EditorSession(new() { Viewport = edited.Viewport });
        var (package, origin) = PrefabLinkResolver.ProjectContent(new(edited, owner.Assets), "Linked");
        var source = new ExternalPrefabSource { Locator = "C:/never-read/group-canvas.floatcanvas", SourceResourceId = edited.ResourceId,
            EntryKind = PrefabSourceKind.ProjectContent, LastAcceptedFileSha256 = new string('a', 64), ImportOrigin = origin };
        var imported = owner.ImportPrefab(package, owner.Assets, LayerPlacement.Identity, source);
        var session = new EditorSession(edited, owner.Assets);
        var context = new PrefabEditContext(owner, session, imported.DefinitionId, owner.Document.Definitions.Single().Revision,
            new(-100, 30), EditorTool.Select, sourcePath: source.Locator);
        string before = JsonSerializer.Serialize(owner.Document); int undo = owner.UndoCount;
        session.BeginTransaction("external sample"); edited.Elements.Single().Width = 71; session.NotifyChanged();
        var root = new GroupCanvasScene().Capture(session, context, GroupCanvasMode.RootProject, default, owner.Assets);
        check("external source draft root view composes current contents without reading or saving its file", SceneEvaluation.Build(root.Document).Leaves.Single().RenderElement.Width == 71 &&
            JsonSerializer.Serialize(owner.Document) == before && owner.UndoCount == undo && session.IsTransactionActive);
        check("external link metadata remains distinct from the ordinary embedded definition", root.Document.Definitions.Single().Source?.Locator == source.Locator &&
            owner.Document.Definitions.Single().Source?.IsLinked == true && context.IsSource);
        session.Cancel();
    }

    private static void FullCanvasFrames(Action<string, bool> check)
    {
        var group = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock };
        var inside = Box(0xff33aa55, 10, 15); inside.GroupId = group.Id; group.ChildOrder = [inside.Id];
        var outside = Box(0xffcc3322, 1100, 800);
        var live = new CanvasElement { Kind = ElementKind.LiveBlur, Width = 40, Height = 30, Transform = Affine2D.Translation(1300, 850),
            Effects = [new() { Kind = ImageEffectKind.Blur, Amount = 3 }] };
        var session = new EditorSession(new() { Elements = [inside, outside, live], Groups = [group], RootOrder = [group.Id, outside.Id, live.Id], Viewport = new(0, 0, 1500, 950) });
        session.SetEditScope(group.Id); var scene = new GroupCanvasScene(); var origin = new PointD(-800, 50); var monitors = new object();
        using var renderer = new CanvasPreviewRenderer();
        var root = scene.Capture(session, null, GroupCanvasMode.RootProject, origin, session.Assets);
        using var rootSnapshot = Snapshot(root); using var full = renderer.Render(rootSnapshot); using var fullPixels = Pixels(full, origin);
        check("root preview renders full monitor-size pixels beyond the old thumbnail bounds", fullPixels.Width == 1500 && fullPixels.Height == 950 && fullPixels.GetPixel(1105, 805) == new SKColor(0xffcc3322));
        check("main preview preserves live effects instead of substituting thumbnail placeholders", full.PickingDocument!.Elements.Single(element => element.Id == live.Id).Kind == ElementKind.LiveBlur && full.GpuWorkload);
        var local = scene.Capture(session, null, GroupCanvasMode.Temporary, origin, session.Assets);
        using var localSnapshot = Snapshot(local); using var isolated = renderer.Render(localSnapshot); using var localPixels = Pixels(isolated, origin);
        check("temporary main-canvas pixels show the current subtree and omit outside artwork", localPixels.GetPixel(15, 20) == new SKColor(0xff33aa55) && localPixels.GetPixel(1105, 805).Alpha == 0);
        check("isolated pixel frame retains the owning editable graph as source identity", ReferenceEquals(isolated.PickingSourceDocument, session.Document) && isolated.PickingEvaluation!.ResolveSelectableNode(inside.Id, group.Id) == inside.Id);

        CanvasPreviewSnapshot Snapshot(GroupCanvasView view)
        {
            var freezer = new CanvasPreviewScene(); var frozen = freezer.Capture(view.Document, session.PreviewRevision, session.PreviewElementsRevision, session.TransactionId, -1, -1, false);
            return new() { Context = new(session, monitors, view.Origin, false, false, false, GroupEpoch: view.Epoch), SceneIdentity = view.Document,
                Document = frozen, SourceDocument = view.IsRootPreview ? frozen : session.Document, FrozenEvaluation = freezer.EvaluationFor(frozen),
                EditingEvaluation = view.EditingPlacement is null ? null : new FrozenSceneEvaluation(session.Document),
                EditingPlacement = view.EditingPlacement, EditingRootCanvas = view.EditingRootCanvas, AuthorRootCanvas = session.ActiveRootCanvas, EditingScopeId = session.EditScopeId,
                Resources = [], Outputs = [new(-800, 50, 1500, 950)], ContentRevision = session.PreviewRevision, Revision = session.SceneRevision };
        }
    }

    private static void TemporaryRefreshPixels(Action<string, bool> check)
    {
        // Keep the actual temporary-view caller contract: SourceDocument is the live
        // owning graph for picking, while Document/Evaluation are detached snapshots.
        foreach (bool shared in new[] { true, false })
        {
            string kind = shared ? "shared definition" : "ordinary child";
            var group = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock };
            var child = Box(0xffff2040, 40, 50); child.Width = 60; child.Height = 50;
            var document = new SceneDocument { Viewport = new(0, 0, 320, 240), Groups = [group], RootOrder = [group.Id] };
            PrefabDefinition? definition = null;
            if (shared)
            {
                definition = new() { Name = "temporary shared", Graph = new() { Elements = [child], RootOrder = [child.Id] } };
                var instance = new PrefabInstance { DefinitionId = definition.Id, ParentId = group.Id };
                document.Definitions.Add(definition); document.Instances.Add(instance); group.ChildOrder = [instance.Id];
            }
            else
            {
                child.GroupId = group.Id; document.Elements.Add(child); group.ChildOrder = [child.Id];
            }
            document.Canvas.Grid.Visible = false;
            var session = new EditorSession(document); session.SetEditScope(group.Id);
            var scene = new GroupCanvasScene(); var freezer = new CanvasPreviewScene(); var monitors = new object();
            using var renderer = new CanvasPreviewRenderer();
            using var initial = Snapshot(); using var first = renderer.Render(initial); using var firstPixels = SmallPixels(first);
            var initialLeaf = initial.FrozenEvaluation!.Scene.RenderDocument.Elements.Single();
            var initialDefinitionRevision = definition?.Revision;
            check($"temporary {kind} starts with nonempty colored pixels and live owner picking identity",
                firstPixels.GetPixel(60, 70) == new SKColor(0xffff2040) && firstPixels.GetPixel(180, 70).Alpha == 0 &&
                ReferenceEquals(first.PickingSourceDocument, session.Document));
            session.BeginTransaction("temporary pixel refresh");
            if (shared)
            {
                child.Style.Fill = 0xff22cc77; definition!.Revision = Guid.NewGuid();
            }
            else child.Transform = Affine2D.Translation(160, 50);
            session.NotifyChanged();
            using var changed = Snapshot(); using var next = renderer.Render(changed); using var nextPixels = SmallPixels(next);
            using var direct = DirectPixels(changed.Document);
            check($"temporary {kind} change freezes new content without mutating the published old snapshot",
                initialLeaf.Transform.OffsetX == 40 && initialLeaf.Style.Fill == 0xffff2040 &&
                initial.Document.Definitions.SingleOrDefault()?.Revision == initialDefinitionRevision &&
                changed.FrozenEvaluation!.Scene.RenderDocument.Elements.Single().Style.Fill == child.Style.Fill &&
                ReferenceEquals(initial.Context.GroupEpoch, changed.Context.GroupEpoch) && session.IsTransactionActive &&
                changed.Revision == session.SceneRevision && changed.Transaction == session.TransactionId &&
                changed.ContentRevision == session.PreviewRevision && changed.ContentRevision != initial.ContentRevision);
            bool expectedChanged = shared ? nextPixels.GetPixel(60, 70) == new SKColor(0xff22cc77)
                : nextPixels.GetPixel(60, 70).Alpha == 0 && nextPixels.GetPixel(180, 70) == new SKColor(0xffff2040);
            check($"temporary {kind} edit refreshes the same worker pixels exactly like fresh direct rendering",
                expectedChanged && !nextPixels.Bytes.SequenceEqual(firstPixels.Bytes) && nextPixels.Bytes.SequenceEqual(direct.Bytes));
            if (!shared)
            {
                child.Style.Fill = 0xff22cc77; session.NotifyChanged();
                using var recolored = Snapshot(); using var colorFrame = renderer.Render(recolored); using var colorPixels = SmallPixels(colorFrame);
                using var colorDirect = DirectPixels(recolored.Document);
                check("temporary ordinary child color edit refreshes after a prior geometry edit without replacing the worker",
                    colorPixels.GetPixel(60, 70).Alpha == 0 && colorPixels.GetPixel(180, 70) == new SKColor(0xff22cc77) &&
                    !colorPixels.Bytes.SequenceEqual(nextPixels.Bytes) && colorPixels.Bytes.SequenceEqual(colorDirect.Bytes) &&
                    initialLeaf.Style.Fill == 0xffff2040 && ReferenceEquals(colorFrame.PickingSourceDocument, session.Document));
            }

            CanvasPreviewSnapshot Snapshot()
            {
                var view = scene.Capture(session, null, GroupCanvasMode.Temporary, default, session.Assets);
                var frozen = freezer.Capture(view.Document, session.PreviewRevision, session.PreviewElementsRevision,
                    session.TransactionId, -1, -1, false);
                return new() { Context = new(session, monitors, view.Origin, false, false, false,
                        DocumentEpoch: session.Document, GroupEpoch: view.Epoch),
                    SceneIdentity = view.Document, Document = frozen, SourceDocument = session.Document,
                    FrozenEvaluation = freezer.EvaluationFor(frozen), Resources = [], Outputs = [new(0, 0, 320, 240)],
                    Revision = session.SceneRevision, Transaction = session.TransactionId, ContentRevision = session.PreviewRevision,
                    GraphChanged = freezer.GraphChanged, PreferGpu = false };
            }
        }

        static SKBitmap SmallPixels(CanvasPreviewFrame frame)
        {
            using var surface = SKSurface.Create(new SKImageInfo(320, 240)); surface.Canvas.Clear(SKColors.Transparent);
            frame.Pixels.Draw(surface.Canvas); using var image = surface.Snapshot(); return SKBitmap.FromImage(image);
        }
        static SKBitmap DirectPixels(SceneDocument frozen)
        {
            using var renderer = new SceneRenderer(); using var surface = SKSurface.Create(new SKImageInfo(320, 240));
            surface.Canvas.Clear(SKColors.Transparent); renderer.Draw(surface.Canvas, SceneEvaluation.Build(frozen).RenderDocument, new AssetLibrary());
            using var image = surface.Snapshot(); return SKBitmap.FromImage(image);
        }
    }

    private static SKBitmap Pixels(CanvasPreviewFrame frame, PointD origin)
    {
        using var surface = SKSurface.Create(new SKImageInfo(1500, 950)); surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Translate((float)-origin.X, (float)-origin.Y); frame.Pixels.Draw(surface.Canvas);
        using var image = surface.Snapshot(); return SKBitmap.FromImage(image);
    }

    private static CanvasElement Box(uint color, double x, double y) => new()
    {
        Kind = ElementKind.Rectangle, Width = 40, Height = 30, Transform = Affine2D.Translation(x, y),
        Style = new() { Fill = color, StrokeWidth = 0 }
    };
}
