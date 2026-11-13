using System.Text.Json;
using FloatCanvas.Core;

internal static class ContainerBillboardTests
{
    internal static void Run(Action<bool, string> check)
    {
        var first = new CanvasElement { Kind = ElementKind.Rectangle, Width = 100, Height = 60,
            Transform = Affine2D.Translation(20, 30) };
        var second = new CanvasElement { Kind = ElementKind.Rectangle, Width = 30, Height = 20,
            IsBillboard = true, BillboardAnchor = new(180, 90), BillboardHeight = 25 };
        var group = new CanvasGroup { Transform = Affine2D.Translation(100, 200), OrderMode = GroupOrderMode.AtomicBlock,
            ChildOrder = [first.Id, second.Id] };
        first.GroupId = second.GroupId = group.Id;
        var document = new SceneDocument { Elements = [first, second], Groups = [group], RootOrder = [group.Id] };
        var session = new EditorSession(document);
        string members = JsonSerializer.Serialize(document.Elements);
        var original = group.Transform;
        var originalFlat = SceneEvaluation.Build(document).Leaves.Select(leaf => ElementGeometry.LocalToPlane(leaf.RenderElement)).ToArray();
        session.SetContainerBillboard(group.Id, true);
        check(group.Billboard is not null && group.Billboard.IsValid, "mixed group supports a single card");
        check(JsonSerializer.Serialize(document.Elements) == members, "container mode does not rewrite child modes or geometry");
        var evaluated = SceneEvaluation.Build(document);
        check(evaluated.Leaves.All(leaf => leaf.RenderElement.BillboardSurface is not null), "all mixed members resolve through the card");
        check(evaluated.Leaves.Select(leaf => leaf.RenderElement.BillboardAnchor).Distinct().Count() == 1, "card members share a depth owner");
        for (int i = 0; i < originalFlat.Length; i++)
            check(Near(originalFlat[i], ElementGeometry.LocalToPlane(evaluated.Leaves[i].RenderElement)), "unit plane preserves mixed content layout " + i);
        var mapping = new Projective2D(1, 0, 0, 0, 1, 0, 0, .001, 1);
        var rendered = evaluated.Leaves[1].RenderElement;
        check(BillboardGeometry.ElementToTarget(rendered, mapping).IsFinite, "card projection is finite");
        double beforeY = ElementGeometry.LocalToPlane(rendered).TransformPoint(default).Y;
        second.BillboardHeight += 10; session.NotifyChanged();
        var raised = SceneEvaluation.Build(document).Leaves[1].RenderElement;
        check(Math.Abs(ElementGeometry.LocalToPlane(raised).TransformPoint(default).Y - beforeY + 10) < 1e-8, "inner billboard height remains effective on flat card");
        var stable = group.Billboard;
        first.Width += 200; first.Visible = false; session.NotifyChanged();
        check(group.Billboard == stable, "content and visibility changes do not recenter the card");
        session.SetSelection([group.Id]); bool rejected = false;
        try { session.UngroupSelection(); } catch (InvalidOperationException) { rejected = true; }
        check(rejected && document.Groups.Count == 1, "whole card cannot be ungrouped directly");
        session.SetContainerBillboard(group.Id, false);
        check(group.Billboard is null && group.Transform == original && second.IsBillboard, "flat mode restores parent placement and child modes");
        session.Undo(); check(session.Document.Groups[0].Billboard == stable, "undo restores whole card placement");
        session.Redo(); check(session.Document.Groups[0].Billboard is null, "redo restores free mode");

        var definition = new PrefabDefinition { Graph = new() { Elements = [new() { Width = 50, Height = 30 }] } };
        var a = new PrefabInstance { DefinitionId = definition.Id }; var b = new PrefabInstance { DefinitionId = definition.Id, Transform = Affine2D.Translation(300, 0) };
        var shared = new EditorSession(new SceneDocument { Definitions = [definition], Instances = [a, b], RootOrder = [a.Id, b.Id] });
        string source = JsonSerializer.Serialize(definition);
        shared.SetContainerBillboard(a.Id, true);
        check(a.Billboard is not null && b.Billboard is null && JsonSerializer.Serialize(definition) == source, "shared outer mode changes only its instance");
        var sharedView = SceneEvaluation.Build(shared.Document);
        check(sharedView.OccurrencesOfHostNode(a.Id)[0].RenderElement.BillboardSurface is not null &&
            sharedView.OccurrencesOfHostNode(b.Id)[0].RenderElement.BillboardSurface is null, "shared instances resolve independent placement");
        var copied = shared.Document.Clone(true);
        check(copied.Instances.Count(instance => instance.Billboard is not null) == 1, "new-ID clone retains container placement");
        string json = JsonSerializer.Serialize(shared.Document);
        check(!json.Contains("BillboardSurface") && !json.Contains("IsValid"), "runtime geometry is not persisted");
        var loaded = JsonSerializer.Deserialize<SceneDocument>(json)!;
        check(loaded.Version == 19 && loaded.Instances[0].Billboard == a.Billboard, "container placement roundtrips in format 19");
        var selectionCopy = shared.Document.CloneElements([a.Id], newIds: true);
        check(selectionCopy.Instances.Single().Billboard == a.Billboard, "selection copy retains shared card placement");
        var detached = new EditorSession(shared.Document.Clone());
        detached.DetachPrefab(a.Id);
        check(detached.Document.Groups.Single(group => group.Id == a.Id).Billboard == a.Billboard &&
            detached.Document.Instances.Single().Billboard is null, "shared-to-ordinary conversion preserves only the selected instance's outer card");
        string beforeLocked = JsonSerializer.Serialize(shared.Document); a.Locked = true;
        bool lockedRejected = false; try { shared.SetContainerBillboard(a.Id, false); } catch (InvalidOperationException) { lockedRejected = true; }
        a.Locked = false;
        check(lockedRejected && JsonSerializer.Serialize(shared.Document) == beforeLocked, "locked container mode conversion cannot partially change members");
        var readOnly = new EditorSession(shared.Document.Clone(), readOnly: true); bool readOnlyRejected = false;
        try { readOnly.SetContainerBillboard(a.Id, false); } catch (InvalidOperationException) { readOnlyRejected = true; }
        check(readOnlyRejected && readOnly.UndoCount == 0, "read-only shared browsing cannot change container placement");
        Reparent(check);
        Nested(check);
        NestedMemberMove(check);
    }

    private static void Reparent(Action<bool, string> check)
    {
        var first = new CanvasElement { Width = 100, Height = 60 };
        var other = new CanvasElement { Width = 40, Height = 20 };
        var group = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [first.Id], Transform = Affine2D.Translation(100, 100) };
        first.GroupId = group.Id;
        var document = new SceneDocument { Elements = [first, other], Groups = [group], RootOrder = [group.Id, other.Id] };
        document.Canvas.Projection.Enabled = true;
        document.Canvas.Projection.Matrix = new(1, 0, 0, 0, 1, 0, 0, .001, 1);
        document.Canvas.Projection.Quad = [new(0, 0), new(800, 0), new(800, 600), new(0, 600)];
        var session = new EditorSession(document); session.SetContainerBillboard(group.Id, true);
        var map = CanvasMapping.Create(document);
        Projective2D Screen(Guid id) => map.ElementToScreen(SceneEvaluation.Build(document).FindElement(id)!);
        var before = Screen(first.Id); new SceneGraph(document).ReparentPreservingWorld([first.Id], null);
        check(Near(before, Screen(first.Id)) && !first.IsBillboard, "moving flat member out of card preserves its screen matrix and mode");
        new SceneGraph(document).ReparentPreservingWorld([first.Id], group.Id);
        check(Near(before, Screen(first.Id)), "moving flat member into card preserves appearance");
        var nested = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [other.Id] };
        document.Groups.Add(nested); other.GroupId = nested.Id; document.RootOrder![1] = nested.Id;
        session.SetContainerBillboard(nested.Id, true);
        before = Screen(other.Id);
        new SceneGraph(document).ReparentPreservingWorld([nested.Id], group.Id);
        check(Near(before, Screen(other.Id)) && nested.Billboard is not null, "whole card moves into another card without rewriting its members");
        new SceneGraph(document).ReparentPreservingWorld([nested.Id], null);
        check(Near(before, Screen(other.Id)) && nested.Billboard is not null, "nested card moves back out with exact screen appearance");

        var flatGroup = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [first.Id] };
        document.Groups.Add(flatGroup); first.GroupId = flatGroup.Id; group.ChildOrder!.Remove(first.Id); document.RootOrder!.Add(flatGroup.Id);
        first.ProjectivePlacement = null;
        string unchanged = JsonSerializer.Serialize(document); bool failed = false;
        try { new SceneGraph(document).ReparentPreservingWorld([flatGroup.Id], group.Id); } catch (InvalidOperationException) { failed = true; }
        check(failed && JsonSerializer.Serialize(document) == unchanged, "non-affine container compensation fails atomically");
    }

    private static void Nested(Action<bool, string> check)
    {
        var leaf = new CanvasElement { Width = 30, Height = 40, IsBillboard = true, BillboardAnchor = new(30, 60), BillboardHeight = 20 };
        var inner = new CanvasGroup { ChildOrder = [leaf.Id], OrderMode = GroupOrderMode.AtomicBlock };
        var outer = new CanvasGroup { ChildOrder = [inner.Id], OrderMode = GroupOrderMode.AtomicBlock, Transform = Affine2D.Translation(70, 80) };
        leaf.GroupId = inner.Id; inner.ParentId = outer.Id;
        var doc = new SceneDocument { Elements = [leaf], Groups = [outer, inner], RootOrder = [outer.Id] };
        var session = new EditorSession(doc); session.SetContainerBillboard(inner.Id, true); session.SetContainerBillboard(outer.Id, true);
        var baseline = SceneEvaluation.Build(doc).FindElement(leaf.Id)!;
        var graph = new SceneGraph(doc);
        var composed = graph.ScopeContainerPlacement(outer.Id).Compose(ContainerPlacementContext.Identity.Enter(inner.Transform, inner.ElevationScale, inner.Billboard)).Resolve(leaf);
        check(Near(ElementGeometry.LocalToPlane(baseline), ElementGeometry.LocalToPlane(composed)), "nested namespace composition equals ordinary hierarchy");
        var draft = leaf.Clone(); draft.Width += 5;
        check(Near(ElementGeometry.LocalToPlane(SceneEvaluation.Build(doc).ResolveElement(draft)), ElementGeometry.LocalToPlane(graph.ResolveElement(draft))), "incremental draft resolution retains complete card mapping");
        outer.PreviewCanvas = new(); outer.PreviewCanvas.Canvas.Projection.Enabled = true;
        check(Near(ElementGeometry.LocalToPlane(baseline), ElementGeometry.LocalToPlane(SceneEvaluation.Build(doc).FindElement(leaf.Id)!)), "independent preview perspective is excluded from card geometry");
    }
    private static void NestedMemberMove(Action<bool, string> check)
    {
        foreach (double shear in new[] { 0.0, .3 })
        foreach (double scale in new[] { 1.0, 2.0 })
        foreach (bool upright in new[] { false, true })
        {
            var leaf = new CanvasElement { Kind = ElementKind.Rectangle, Width = 30, Height = 40, IsBillboard = upright,
                BillboardAnchor = new(30, 60), BillboardHeight = 20 };
            var inner = new CanvasGroup { ChildOrder = [leaf.Id], OrderMode = GroupOrderMode.AtomicBlock,
                Transform = new Affine2D(scale, shear, shear, scale, 100, 80), ElevationScale = 1.7, Billboard = new(new(10, 15), 25) };
            var outer = new CanvasGroup { ChildOrder = [inner.Id], OrderMode = GroupOrderMode.AtomicBlock,
                Transform = Affine2D.Translation(300, 300), Billboard = new(new(50, 50), 40) };
            leaf.GroupId = inner.Id; inner.ParentId = outer.Id;
            var doc = new SceneDocument { Elements = [leaf], Groups = [outer, inner], RootOrder = [outer.Id] };
            doc.Canvas.Projection.Enabled = true;
            doc.Canvas.Projection.Matrix = new(1, 0, 0, 0, 1, 0, 0, .001, 1);
            var map = CanvasMapping.Create(doc);
            Projective2D Screen() => map.ElementToScreen(SceneEvaluation.Build(doc).FindElement(leaf.Id)!);
            var before = Screen();
            var graph = new SceneGraph(doc);
            check(graph.ScopeContainerPlacement(inner.Id).Surface == graph.ScopeContainerPlacement(outer.Id).Surface &&
                graph.ScopeContainerPlacement(inner.Id).FlatPrefix != graph.ScopeContainerPlacement(outer.Id).FlatPrefix,
                "nested member regression shares outer surface but has distinct flat prefix");
            string unchanged = JsonSerializer.Serialize(doc);
            try { graph.ReparentPreservingWorld([leaf.Id], outer.Id); }
            catch (InvalidOperationException)
            {
                check(upright && shear != 0 && JsonSerializer.Serialize(doc) == unchanged,
                    "unrepresentable sheared upright member rejects without any document mutation");
                continue;
            }
            check(Near(before, Screen()), "nested member out preserves screen geometry and stroke mapping: " + upright);
            new SceneGraph(doc).ReparentPreservingWorld([leaf.Id], inner.Id);
            check(Near(before, Screen()), "nested member back preserves screen geometry and stroke mapping: " + upright);
            var session = new EditorSession(doc);
            session.SetSelection([leaf.Id]);
            string snapshot = JsonSerializer.Serialize(session.Document);
            session.MoveLayerNodes([leaf.Id], RootCanvasKind.Main, outer.Id);
            var moved = session.Document;
            check(Near(before, CanvasMapping.Create(moved).ElementToScreen(SceneEvaluation.Build(moved).FindElement(leaf.Id)!)) &&
                new SceneGraph(moved).ParentId(leaf.Id) == outer.Id && session.UndoCount == 1,
                "session nested member move preserves appearance in one transaction");
            session.Undo();
            check(JsonSerializer.Serialize(session.Document) == snapshot && session.Selection.Contains(leaf.Id),
                "undo nested member move restores placement topology and selection");
            session.Redo();
            check(new SceneGraph(session.Document).ParentId(leaf.Id) == outer.Id &&
                Near(before, CanvasMapping.Create(session.Document).ElementToScreen(SceneEvaluation.Build(session.Document).FindElement(leaf.Id)!)),
                "redo nested member move restores compensated appearance");
        }
    }
    private static bool Near(Projective2D a, Projective2D b) => new[] { new PointD(0, 0), new PointD(31, 19), new PointD(200, 100) }
        .All(point => a.TransformPoint(point).DistanceTo(b.TransformPoint(point)) < 1e-8);
}
