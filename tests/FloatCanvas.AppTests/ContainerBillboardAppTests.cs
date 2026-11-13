using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;

internal static class ContainerBillboardAppTests
{
    internal static void Run(Action<string, bool> check)
    {
        var session = new EditorSession(new SceneDocument { Viewport = new(0, 0, 800, 700) });
        session.Document.Canvas.Projection = new() { Enabled = true, Matrix = new(1, 0, 0, 0, -.4, 700, 0, -.002, 1),
            Quad = [new(0, 0), new(800, 0), new(800, 700), new(0, 700)] };
        session.Document.Canvas.Grid.Visible = false;
        var a = new CanvasElement { Kind = ElementKind.Rectangle, Width = 160, Height = 100, Style = new() { Fill = 0xFFFF0000, StrokeWidth = 0 } };
        var b = new CanvasElement { Kind = ElementKind.Ellipse, Width = 30, Height = 30, Transform = Affine2D.Translation(50, 20), Style = new() { Fill = 0xFF00FF00, StrokeWidth = 0 } };
        var source = new SceneDocument { Elements = [a, b] };
        var stamp = new LoadedStamp(source, new AssetLibrary());
        var map = CanvasMapping.Create(session.Document);
        StampLibrary.Place(stamp, session, default, billboard: new(map, new(300, 400)));
        var group = session.Document.Groups.Single();
        check("editable stamp inserts one whole card in one undo step", group.Billboard is not null && session.UndoCount == 1);
        var evaluation = SceneEvaluation.Build(session.Document);
        var first = evaluation.Leaves.First().RenderElement;
        check("stamp logical box is centered on the click", map.ProjectedBounds(first).Center.DistanceTo(new(300, 400)) < .01 && first.Width == 160);
        using var renderer = new SceneRenderer();
        using (var image = SKBitmap.Decode(renderer.ExportPng(session.Document, session.Assets, true)))
            check("whole card renders actual vector pixels", image.GetPixel(300, 400).Alpha > 0);
        var editor = new EditorController(session) { Tool = EditorTool.Select, BackgroundPickingEnabled = false };
        session.SetSelection([group.Id]);
        var before = Center(editor.SelectionScreenCorners);
        var grab = before + new PointD(-20, 0);
        var grabbedLocal = map.ElementToScreen(first).Inverse().TransformPoint(grab);
        Send(OverlayPointerKind.Down, grab); Send(OverlayPointerKind.Move, grab + new PointD(40, 60)); Send(OverlayPointerKind.Up, grab + new PointD(40, 60));
        var after = Center(editor.SelectionScreenCorners);
        check("dragging a whole card keeps the grabbed point under the pointer", map.ElementToScreen(SceneEvaluation.Build(session.Document).Leaves.First().RenderElement)
            .TransformPoint(grabbedLocal).DistanceTo(grab + new PointD(40, 60)) < .1);
        check("moving the whole card leaves source members planar", session.Document.Elements.All(element => !element.IsBillboard && element.BillboardSurface is null));
        var heightGuide = editor.BillboardSelectionGuide!.Value; double priorHeight = group.Billboard!.Height;
        Send(OverlayPointerKind.Down, heightGuide.Center); Send(OverlayPointerKind.Move, heightGuide.Center - new PointD(0, 15)); Send(OverlayPointerKind.Up, heightGuide.Center - new PointD(0, 15));
        check("whole card height handle raises content while retaining ground", group.Billboard!.Height > priorHeight && editor.BillboardSelectionGuide!.Value.Anchor.DistanceTo(heightGuide.Anchor) < .01);
        session.EnterScope(group.Id);
        var graph = new SceneGraph(session.Document);
        editor.SetCanvasView(CanvasMapping.CreateSurfaceView(session.Document, graph.ScopeContainerPlacement(group.Id), graph.ScopeWorldPlacement(group.Id), CanvasViewTransform.Identity), default(PointD), new());
        editor.ResumePresentedPicking();
        var member = session.Document.Elements.First(); session.SetSelection([member.Id]);
        before = Center(editor.SelectionScreenCorners);
        Send(OverlayPointerKind.Down, before); Send(OverlayPointerKind.Move, before + new PointD(15, 12)); Send(OverlayPointerKind.Up, before + new PointD(15, 12));
        after = Center(editor.SelectionScreenCorners);
        check("direct member dragging uses the card's displayed plane", after.DistanceTo(before + new PointD(15, 12)) < .1);
        check("editing a planar member never bakes its runtime billboard flag", !session.Document.Elements.First().IsBillboard);
        editor.Tool = EditorTool.Rectangle; editor.DefaultWidth = 20; editor.DefaultHeight = 15; editor.BillboardMode = true;
        var click = after + new PointD(80, -20);
        Send(OverlayPointerKind.Down, click); Send(OverlayPointerKind.Up, click);
        var created = session.Document.Elements.Last();
        var view = SceneEvaluation.Build(session.Document).FindElement(created.Id)!;
        check("new member billboard preserves logical dimensions", created.IsBillboard && created.Width == 20 && created.Height == 15);
        check("new member candidate and committed card location agree", CanvasMapping.Create(session.Document).ProjectedBounds(view, false).Center.DistanceTo(click) < .1);
        editor.Cancel();

        var definition = new PrefabDefinition { Graph = new() { Elements = [a.Clone(), b.Clone()] } };
        var package = new PrefabPackage { EntryDefinitionId = definition.Id, Definitions = [definition] };
        var sharedStamp = new LoadedStamp(source, new AssetLibrary(), package);
        var shared = new EditorSession(session.Document.Clone()); shared.SetEditScope(null);
        StampLibrary.Place(sharedStamp, shared, default, billboard: new(CanvasMapping.Create(shared.Document), new(100, 400)));
        StampLibrary.Place(sharedStamp, shared, new(200, -100));
        var instances = shared.Document.Instances;
        check("shared card and free stamp retain one definition", instances.Count == 2 && instances[0].DefinitionId == instances[1].DefinitionId);
        check("placing a shared card does not change the other instance mode", instances[0].Billboard is not null && instances[1].Billboard is null);
        SharedEditing(shared, instances[0], check);
        Cache(shared, check);
        StampCandidate(stamp, session.Document, check);
        NestedEditing(session.Document.Canvas, check);

        void Send(OverlayPointerKind kind, PointD point) => editor.Pointer(new(kind, new(point.X, point.Y), OverlayMouseButton.Left, OverlayModifiers.None));
    }
    private static void NestedEditing(CanvasSettings canvas, Action<string, bool> check)
    {
        var leaf = new CanvasElement { Kind = ElementKind.Rectangle, Width = 160, Height = 100, Style = new() { Fill = 0xFFFF0000, StrokeWidth = 0 } };
        var inner = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [leaf.Id], Billboard = new(new(80, 50), 50), Transform = Affine2D.Translation(80, 100) };
        var outer = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [inner.Id], Billboard = new(new(80, 50), 50), Transform = Affine2D.Translation(300, -150) };
        leaf.GroupId = inner.Id; inner.ParentId = outer.Id;
        var session = new EditorSession(new SceneDocument { Canvas = canvas.Clone(), Viewport = new(0, 0, 800, 700), Elements = [leaf], Groups = [outer, inner], RootOrder = [outer.Id] });
        session.EnterScope(outer.Id); session.SetSelection([inner.Id]);
        var editor = new EditorController(session) { Tool = EditorTool.Select, BackgroundPickingEnabled = false };
        var graph = SceneGraph.Read(session.Document);
        editor.SetCanvasView(CanvasMapping.CreateSurfaceView(session.Document, graph.ScopeContainerPlacement(outer.Id), graph.ScopeWorldPlacement(outer.Id), CanvasViewTransform.Identity), default(PointD), new());
        editor.ResumePresentedPicking();
        var before = Center(editor.SelectionScreenCorners); var grab = before - new PointD(25, 0);
        var pose = outer.Transform;
        foreach (var (kind, point) in new[] { (OverlayPointerKind.Down, grab), (OverlayPointerKind.Move, grab + new PointD(30, 20)), (OverlayPointerKind.Up, grab + new PointD(30, 20)) })
            editor.Pointer(new(kind, new(point.X, point.Y), OverlayMouseButton.Left, OverlayModifiers.None));
        check("nested card motion stays on its outer card and follows the pointer", Center(editor.SelectionScreenCorners).DistanceTo(before + new PointD(30, 20)) < .01 && outer.Transform == pose);
        var stamp = new LoadedStamp(new SceneDocument { Elements = [new() { Kind = ElementKind.Rectangle, Width = 25, Height = 18, Style = new() { StrokeWidth = 0 } }] }, new());
        var click = before + new PointD(70, -20);
        StampLibrary.Place(stamp, session, default, billboard: new(editor.CurrentCanvasMapping, click));
        var inserted = session.Document.Groups.Last();
        var rendered = SceneEvaluation.Build(session.Document).OccurrencesOfHostNode(inserted.Id).Single().RenderElement;
        check("single-layer stamp inside a card creates a wrapper at the requested screen center", inserted.Billboard is not null && inserted.ParentId == outer.Id &&
            CanvasMapping.Create(session.Document).ProjectedBounds(rendered, false).Center.DistanceTo(click) < .01);
    }
    private static void StampCandidate(LoadedStamp stamp, SceneDocument basis, Action<string, bool> check)
    {
        var session = new EditorSession(basis.Clone()); var editor = new EditorController(session) { Tool = EditorTool.Stamp, BillboardMode = true, BackgroundPickingEnabled = false };
        var bounds = SceneEvaluation.Build(stamp.Document).Leaves.Select(leaf => ElementGeometry.Bounds(leaf.RenderElement)).Aggregate(RectD.Union);
        int before = session.Document.Elements.Count;
        editor.BeginBillboardPlacement((depth, point) => StampLibrary.Place(stamp, session, default, billboard: new(editor.CurrentCanvasMapping, point, depth)),
            (depth, point) => new ContainerBillboardInsertion(editor.CurrentCanvasMapping, point, depth).ResolveCandidate(bounds));
        editor.Pointer(new(OverlayPointerKind.Down, new(400, 350), OverlayMouseButton.Left, OverlayModifiers.None));
        var corners = editor.PendingBillboardOutline!;
        check("stamp press shows its solved perspective outline without adding layers", corners.Count == 4 && session.Document.Elements.Count == before && session.UndoCount == 0);
        editor.Pointer(new(OverlayPointerKind.Up, new(400, 350), OverlayMouseButton.Left, OverlayModifiers.None));
        var group = session.Document.Groups.Last();
        var projected = SceneEvaluation.Build(session.Document).OccurrencesOfHostNode(group.Id).Select(leaf => editor.CurrentCanvasMapping.ProjectedBounds(leaf.RenderElement)).Aggregate(RectD.Union);
        check("stamp release commits the same candidate center and size", projected.Center.DistanceTo(Center(corners)) < .01 && Math.Abs(projected.Width - (corners[1].X - corners[0].X)) < .01 && session.UndoCount == 1);
        editor.BeginBillboardPlacement((depth, point) => throw new InvalidOperationException("Cancelled stamp must not insert"), (_, _) => null);
        editor.Pointer(new(OverlayPointerKind.Down, new(400, 350), OverlayMouseButton.Left, OverlayModifiers.None));
        editor.Pointer(new(OverlayPointerKind.Up, new(400, 350), OverlayMouseButton.Left, OverlayModifiers.None));
        check("invalid stamp candidate clears its outline and cannot commit", editor.PendingBillboardOutline is null && session.UndoCount == 1);
        session.NotifyChanged();
        editor.Pointer(new(OverlayPointerKind.Up, new(400, 350), OverlayMouseButton.Left, OverlayModifiers.None));
        check("external revision cancels pending stamp without stale insertion", !editor.HasPendingBillboardInsertion && session.UndoCount == 1);
    }
    private static void Cache(EditorSession session, Action<string, bool> check)
    {
        var freezer = new CanvasPreviewScene();
        SceneDocument Capture() => freezer.Capture(session.Document, session.PreviewRevision, session.PreviewElementsRevision,
            session.TransactionId, -1, -1, true, definitionsRevision: session.PreviewDefinitionsRevision);
        var initial = Capture(); freezer.EvaluateFrozen(initial);
        long builds = freezer.DefinitionBuildCount;
        var instance = session.Document.Instances.First();
        session.SetSelection([instance.Id]); var transform = session.CaptureSelectionTransform();
        session.BeginTransaction("move card");
        check("cached shared card transform is accepted", session.TryApplySelectionTransform(transform, Affine2D.Translation(10, 0)));
        var moved = Capture(); freezer.EvaluateFrozen(moved);
        check("moving one instance reuses frozen shared content and local evaluation", ReferenceEquals(initial.Definitions, moved.Definitions) && freezer.DefinitionBuildCount == builds);
        session.Commit(); freezer.EvaluateFrozen(Capture());
        check("committing instance motion keeps local definition cache", freezer.DefinitionBuildCount == builds);
        session.Document.Definitions[0].Graph.Elements[0].Width += 1; session.NotifyChanged();
        freezer.EvaluateFrozen(Capture());
        check("unknown content changes invalidate definition geometry", freezer.DefinitionBuildCount > builds);
    }
    private static void SharedEditing(EditorSession owner, PrefabInstance instance, Action<string, bool> check)
    {
        var draft = owner.BeginPrefabEdit(instance.DefinitionId);
        var context = new PrefabEditContext(owner, draft, default, EditorTool.Select) { EnteredInstanceId = instance.Id };
        var view = new GroupCanvasScene().Capture(draft.Session, context, GroupCanvasMode.RootProject, default, owner.Assets);
        check("shared card view exposes its full container placement", view.EditingContainer is { Surface: not null });
        var editing = SceneEvaluation.Build(draft.Session.Document);
        var picking = CanvasPreviewRenderer.EditingDocument(editing, view.Document, view.EditingPlacement!.Value,
            view.EditingRootCanvas, RootCanvasKind.Main, container: view.EditingContainer);
        var displayed = SceneEvaluation.Build(view.Document).OccurrencesOfHostNode(instance.Id);
        var mapping = CanvasMapping.Create(view.Document);
        var localMap = CanvasMapping.CreateSurfaceView(view.Document, view.EditingContainer!.Value, LayerPlacement.Identity,
            CanvasViewTransform.Identity, host: view.EditingContainer);
        check("shared source selection and picking match displayed instance", editing.Leaves.Select((leaf, i) =>
            localMap.ProjectedBounds(leaf.RenderElement).Center.DistanceTo(mapping.ProjectedBounds(displayed[i].RenderElement).Center) < .01 &&
            mapping.ProjectedBounds(picking.Elements[i]).Center.DistanceTo(mapping.ProjectedBounds(displayed[i].RenderElement).Center) < .01).All(value => value));
        var child = draft.Session.Document.Elements[0];
        child.IsBillboard = true; child.BillboardAnchor = new(80, 100); child.BillboardHeight = 50;
        draft.Session.NotifyChanged(); draft.Session.SetSelection([child.Id]);
        var editor = new EditorController(draft.Session) { Tool = EditorTool.Select, BackgroundPickingEnabled = false };
        editor.SetCanvasView(localMap, default(PointD), new(), view.EditingPlacement, view.Document, view.EditingRootCanvas, container: view.EditingContainer);
        editor.ResumePresentedPicking();
        var guide = editor.BillboardSelectionGuide!.Value;
        var expectedGround = view.EditingContainer.Value.PlaneToTarget(mapping.PlaneToScreen).TransformPoint(child.BillboardAnchor);
        check("shared child guide uses the hosting card's complete mapping", guide.Anchor.DistanceTo(expectedGround) < .01);
        foreach (var (kind, point) in new[] { (OverlayPointerKind.Down, guide.Center), (OverlayPointerKind.Move, guide.Center - new PointD(0, 12)), (OverlayPointerKind.Up, guide.Center - new PointD(0, 12)) })
            editor.Pointer(new(kind, new(point.X, point.Y), OverlayMouseButton.Left, OverlayModifiers.None));
        var moved = editor.BillboardSelectionGuide!.Value;
        check("shared child height tracks screen movement without moving its ground", moved.Center.DistanceTo(guide.Center - new PointD(0, 12)) < .01 && moved.Anchor.DistanceTo(guide.Anchor) < .01 && child.BillboardHeight > 50);
        check("shared editing context is available to property controls", editor.HasSharedCardContext);
        editor.ReplaceSession(new EditorSession(), default);
        check("session replacement releases the old shared card context", !editor.HasSharedCardContext);
    }
    private static PointD Center(IReadOnlyList<PointD> corners) => corners.Count == 4 ? (corners[0] + corners[2]) * .5 : throw new InvalidOperationException("Missing card selection frame.");
}
