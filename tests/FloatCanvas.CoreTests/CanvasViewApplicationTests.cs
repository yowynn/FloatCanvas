using FloatCanvas.Core;
using System.Text.Json;

internal static class CanvasViewApplicationTests
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (bool perspective in new[] { false, true })
        {
            var document = Document(perspective);
            var source = JsonSerializer.Serialize(document);
            var camera = new CanvasViewTransform(.7, new(80, 40));
            var applied = CanvasViewApplication.Prepare(document, camera);
            check(JsonSerializer.Serialize(document) == source && applied.ResourceId == document.ResourceId, "applying prepares an owned candidate without changing source or resource identity");
            VerifyGeometry(document, applied, camera, check, composite: true);
            check(applied.Viewport == document.Viewport && applied.DisplayLayout.Count == document.DisplayLayout.Count &&
                JsonSerializer.Serialize(applied.Definitions) == JsonSerializer.Serialize(document.Definitions), "composite apply preserves authored extent and dependency resources");
            foreach (var root in Enum.GetValues<RootCanvasKind>())
            {
                var single = CanvasViewApplication.Prepare(document, camera, root);
                var before = SceneCanvasRoots.ExtractRootContent(document, root);
                var after = SceneCanvasRoots.ExtractRootContent(single, root);
                VerifyGeometry(before, after, camera, check, composite: false);
                foreach (var element in document.Elements.Where(element => SceneCanvasRoots.Resolve(document, element.Id) != root))
                    check(JsonSerializer.Serialize(element) == JsonSerializer.Serialize(single.Elements.Single(item => item.Id == element.Id)), "independent root apply leaves every other root's authored data intact");
            }
        }
        GroupAndHistory(check);
        Shared(check);
    }

    private static SceneDocument Document(bool perspective)
    {
        var document = new SceneDocument { Viewport = new(0, 0, 800, 600) };
        document.Canvas.Projection = CanvasCalibration.CreateInitial(document.Viewport);
        document.Canvas.Projection.Enabled = perspective;
        if (perspective)
        {
            document.Canvas.Projection.Matrix = new(1, .08, 40, .05, .9, 30, .0001, .0003, 1);
            var b = CanvasProjectionGeometry.ResolvePlaneBounds(document.Canvas.Projection);
            document.Canvas.Projection.Quad = new[] { new PointD(b.Left, b.Top), new PointD(b.Right, b.Top), new PointD(b.Right, b.Bottom), new PointD(b.Left, b.Bottom) }
                .Select(document.Canvas.Projection.Matrix.TransformPoint).ToList();
        }
        foreach (var root in Enum.GetValues<RootCanvasKind>())
        {
            foreach (bool billboard in new[] { false, true })
            {
                var element = new CanvasElement { Kind = ElementKind.Rectangle, Width = 30, Height = 50,
                    Transform = Affine2D.Translation(50 + (int)root * 100, 80), IsBillboard = billboard,
                    BillboardAnchor = new(80 + (int)root * 100, 180), BillboardHeight = 20, Locked = billboard, Visible = !billboard };
                document.Elements.Add(element); SceneCanvasRoots.Assign(document, element.Id, root);
            }
        }
        return document;
    }

    private static void VerifyGeometry(SceneDocument before, SceneDocument after, CanvasViewTransform camera, Action<bool, string> check, bool composite)
    {
        var oldScene = SceneEvaluation.Build(before).RenderDocument; var nextScene = SceneEvaluation.Build(after).RenderDocument;
        foreach (var oldElement in oldScene.Elements)
        {
            var next = nextScene.Elements.Single(item => item.Id == oldElement.Id);
            var root = composite ? SceneCanvasRoots.Resolve(oldScene, oldElement.Id) : RootCanvasKind.Main;
            var oldMapping = CanvasMapping.Create(before, root); var newMapping = CanvasMapping.Create(after, root);
            foreach (var point in new[] { default(PointD), new PointD(12, 17), new PointD(oldElement.Width, oldElement.Height) })
                check(newMapping.ElementToScreen(next).TransformPoint(point).DistanceTo(camera.ToView(oldMapping.ElementToScreen(oldElement).TransformPoint(point))) < 1e-7,
                    "real applied planar and billboard geometry matches the complete camera for its own projection");
            check(next.Visible == oldElement.Visible && next.Locked == oldElement.Locked, "apply retains hidden and locked flags while including their geometry");
        }
    }

    private static void GroupAndHistory(Action<bool, string> check)
    {
        var document = Document(false); var graph = new SceneGraph(document);
        var outer = graph.InsertGroup(new() { Transform = Affine2D.Translation(50, 40), OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] });
        var group = graph.InsertGroup(new() { Transform = Affine2D.Rotation(20), OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [],
            PreviewCanvas = new() { Origin = new(30, -20) } }, outer.Id);
        var leaf = graph.InsertElement(new() { Kind = ElementKind.Rectangle, Width = 80, Height = 40, Transform = Affine2D.Translation(50, 60), Visible = false, Locked = true }, group.Id, worldCoordinates: false);
        var nested = graph.InsertGroup(new() { Transform = Affine2D.Translation(120, 80), OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] }, group.Id);
        var child = graph.InsertElement(new() { Kind = ElementKind.Text, Width = 100, Height = 30, Text = "vector", Style = new() { FontSize = 20 } }, nested.Id, worldCoordinates: false);
        string source = JsonSerializer.Serialize(document); var originalChild = JsonSerializer.Serialize(child);
        var view = new CanvasViewTransform(1.4, new(200, 100));
        var result = CanvasViewApplication.Prepare(document, view, RootCanvasKind.Main, group.Id);
        check(result.Groups.Single(item => item.Id == group.Id).Transform == group.Transform && result.Groups.Single(item => item.Id == outer.Id).Transform == outer.Transform,
            "independent group apply leaves its own placement and every ancestor unchanged");
        check(JsonSerializer.Serialize(result.Elements.Single(item => item.Id == child.Id)) == originalChild,
            "nested text is transformed only through its direct root, with no duplicate font or length scaling");
        var oldHeader = group.PreviewCanvas!; var newHeader = result.Groups.Single(item => item.Id == group.Id).PreviewCanvas!;
        var oldDocument = new SceneDocument(); oldHeader.ApplyTo(oldDocument); var newDocument = new SceneDocument(); newHeader.ApplyTo(newDocument);
        var oldMapping = CanvasMapping.CreateView(oldDocument, new(Affine2D.Translation(-oldHeader.Origin.X, -oldHeader.Origin.Y), 1));
        var expected = view.ToView(oldMapping.ElementToScreen(leaf).TransformPoint(new(12, 15)));
        var actual = CanvasMapping.Create(newDocument).ElementToScreen(result.Elements.Single(item => item.Id == leaf.Id)).TransformPoint(new(12, 15));
        check(expected.DistanceTo(actual) < 1e-7 && newHeader.Origin == default, "applying folds a nonzero independent origin into real calibration without moving the outer placement");
        var session = new EditorSession(document); session.SetEditScope(group.Id);
        var state = new CanvasViewHistoryChange(new("owned/group", new(20, 40), view.Zoom), new("owned/group", new(400, 300), 1));
        var receipts = new List<CanvasViewHistoryState>(); session.CanvasViewRestored += receipts.Add;
        session.ApplyCanvasView("apply", result, state);
        check(session.UndoCount == 1 && session.ContentVersion > 0 && receipts.SequenceEqual([state.After]), "apply commits content and identity-view receipt in one authored history entry");
        check(session.Undo() && JsonSerializer.Serialize(session.Document) == source && receipts.Last() == state.Before && session.EditScopeId == group.Id,
            "undo restores the complete original graph, origin, calibration, scope and previous view receipt");
        check(session.Redo() && JsonSerializer.Serialize(session.Document) == JsonSerializer.Serialize(result) && receipts.Last() == state.After,
            "redo restores authored applied data and resets the camera again");
        var readOnly = new EditorSession(document.Clone(), readOnly: true);
        bool rejected = false;
        try { readOnly.ApplyCanvasView("apply", result, state); } catch (InvalidOperationException) { rejected = true; }
        check(rejected && readOnly.UndoCount == 0 && JsonSerializer.Serialize(readOnly.Document) == source,
            "read-only snapshots cannot bypass the authored application command");
    }

    private static void Shared(Action<bool, string> check)
    {
        var dependency = new PrefabDefinition { Graph = new() { Elements = [new() { Kind = ElementKind.Rectangle, Width = 30, Height = 40 }] } };
        var nested = new PrefabInstance { DefinitionId = dependency.Id, Transform = Affine2D.Translation(20, 30) };
        var definition = new PrefabDefinition { Pivot = new(19, 23), Graph = new() { Instances = [nested], RootOrder = [nested.Id] } };
        var a = new PrefabInstance { DefinitionId = definition.Id, Transform = Affine2D.Translation(100, 60) };
        var b = new PrefabInstance { DefinitionId = definition.Id, Transform = Affine2D.Translation(400, 250).Compose(Affine2D.Rotation(30)) };
        var owner = new EditorSession(new() { Definitions = [definition, dependency], Instances = [a, b], RootOrder = [a.Id, b.Id] });
        var draft = owner.BeginPrefabEdit(definition.Id);
        string dependencyBefore = JsonSerializer.Serialize(dependency);
        var view = new CanvasViewTransform(.8, new(60, 50));
        var candidate = CanvasViewApplication.Prepare(draft.Session.Document, view, RootCanvasKind.Main);
        draft.Session.ApplyCanvasView("apply", candidate, new(new("shared", new(400, 300), .8), new("shared", new(400, 300), 1)));
        owner.ApplyPrefabEdit(owner.PreparePrefabEdit(draft));
        var published = owner.Document.Definitions.Single(item => item.Id == definition.Id);
        check(published.Pivot == definition.Pivot && JsonSerializer.Serialize(owner.Document.Definitions.Single(item => item.Id == dependency.Id)) == dependencyBefore,
            "shared apply publishes the owned graph without changing pivot or dependency definitions");
        var placed = published.Graph.Instances.Single();
        check(placed.DefinitionId == dependency.Id && placed.Transform != nested.Transform && owner.Document.Instances.Count == 2,
            "shared apply keeps terminal references and all owner instances instead of expanding resources");
        var scene = SceneEvaluation.Build(owner.Document);
        foreach (var host in new[] { a, b })
        {
            var leaf = scene.OccurrencesOfHostNode(host.Id).Single().RenderElement;
            var expected = host.Transform.TransformPoint(view.Matrix.TransformPoint(nested.Transform.TransformPoint(default)));
            check(ElementGeometry.LocalToPlane(leaf).TransformPoint(default).DistanceTo(expected) < 1e-7,
                "each host uses its own existing placement over the real changed shared geometry");
        }
    }
}
