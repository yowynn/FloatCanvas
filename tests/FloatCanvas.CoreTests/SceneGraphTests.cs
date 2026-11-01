using FloatCanvas.Core;
using System.Text.Json;

internal static class SceneGraphTests
{
    internal static void Run(Action<bool, string> check)
    {
        void Near(PointD actual, PointD expected, string message) => check(actual.DistanceTo(expected) < 1e-7, message);
        void Reject(Action action, string message)
        {
            bool rejected = false; try { action(); } catch (InvalidDataException) { rejected = true; }
            check(rejected, message);
        }
        var a = Box(10, 20); var b = Box(30, 40); var c = Box(50, 60);
        var legacy = new CanvasGroup { Name = "A/C" }; a.GroupId = c.GroupId = legacy.Id;
        var document = new SceneDocument { Elements = [a, b, c], Groups = [legacy] };
        var graph = new SceneGraph(document);
        var evaluated = SceneEvaluation.Build(document);
        check(evaluated.RenderDocument.Elements.Select(element => element.Id).SequenceEqual([a.Id, b.Id, c.Id]), "legacy interleaved A/B/C retains its exact draw order");
        check(evaluated.Leaves.All(leaf => leaf.RenderElement.Transform == document.Elements.Single(element => element.Id == leaf.SourceElementId).Transform), "identity legacy ownership never rebakes geometry");
        check(document.RootOrder is null && legacy.ChildOrder is null, "query and evaluation do not materialize a legacy ledger");
        check(graph.DescendantElementIds(legacy.Id).SequenceEqual([a.Id, c.Id]) && graph.DescendantElementIds(b.Id).SequenceEqual([b.Id]),
            "subtree and single-leaf expansion follow actual draw ranks without including interleaved outsiders");
        check(evaluated.RenderDocument.IsRuntimeEvaluated && evaluated.RenderDocument.Clone().IsRuntimeEvaluated &&
            !JsonSerializer.Serialize(evaluated.RenderDocument).Contains("IsRuntimeEvaluated"), "runtime marker survives clone but is never a format property");
        check(evaluated.ResolveSelectableNode(a.Id) == legacy.Id && evaluated.ResolveSelectableNode(a.Id, legacy.Id) == a.Id &&
            evaluated.ResolveSelectableNode(b.Id, legacy.Id) is null, "one hit resolves to a node in the current scope and rejects outside content");
        check(evaluated.Leaves.Select(leaf => leaf.OccurrencePath).Distinct().Count() == 3, "ordinary leaves have distinct occurrence identity");

        var draft = a.Clone(); draft.Transform = Affine2D.Translation(111, 222); draft.Width = 77;
        var resolvedDraft = evaluated.ResolveElement(draft);
        check(resolvedDraft.Width == 77 && resolvedDraft.Transform == draft.Transform && a.Width != 77, "draft resolution uses current draft content instead of replacing it by ID");
        a.Style.Fill = 0xFF010203; a.Transform = Affine2D.Translation(333, 444);
        check(evaluated.FindElement(a.Id)!.Style.Fill != a.Style.Fill && evaluated.FindElement(a.Id)!.Transform != a.Transform,
            "published evaluation is independent of later mutable source edits");

        graph = new SceneGraph(document); graph.MaterializeOrder();
        check(document.RootOrder!.SequenceEqual([a.Id, b.Id, c.Id]) && graph.OrderedLeafIds.SequenceEqual(document.RootOrder!), "materializing a ledger changes no old draw slot");
        graph.CollectLegacyGroup(legacy.Id);
        check(legacy.OrderMode == GroupOrderMode.AtomicBlock && document.RootOrder!.SequenceEqual([b.Id, legacy.Id]) &&
            legacy.ChildOrder!.SequenceEqual([a.Id, c.Id]) && graph.OrderedLeafIds.SequenceEqual([b.Id, a.Id, c.Id]), "only explicit legacy collection gathers its members to the highest former slot");

        var empty = new CanvasGroup { Name = "empty", OrderMode = GroupOrderMode.AtomicBlock };
        var ownedEmpty = graph.InsertGroup(empty, legacy.Id);
        check(!ReferenceEquals(empty, ownedEmpty) && ownedEmpty.ChildOrder is { Count: 0 } && legacy.ChildOrder!.Last() == empty.Id,
            "an empty atomic group owns a stable draw slot and independent mutable metadata");
        document.PruneGroups();
        check(document.Groups.Any(group => group.Id == empty.Id), "legacy metadata pruning never deletes a user atomic empty group");
        var emptySnapshot = document.CloneElements([empty.Id], true);
        check(emptySnapshot.Elements.Count == 0 && emptySnapshot.Groups.Count == 2 && emptySnapshot.RootOrder!.Count == 1 &&
            emptySnapshot.Groups.Single(group => group.Name == "empty").ChildOrder is { Count: 0 }, "copying an empty group retains required ancestors and remaps its draw slots");
        SceneGraph.Validate(emptySnapshot);

        ownedEmpty.Transform = Affine2D.Translation(40, -30).Compose(Affine2D.Rotation(25)); ownedEmpty.ElevationScale = 2;
        legacy.Transform = Affine2D.Translation(-100, 70).Compose(Affine2D.Scale(1.5, .7)); legacy.ElevationScale = 3;
        graph = new SceneGraph(document);
        var worldDraft = Box(80, 90); var worldBefore = worldDraft.Clone();
        var inserted = graph.InsertElement(worldDraft, empty.Id);
        Near(graph.ToWorld(inserted).Transform.TransformPoint(new(7, 9)), worldBefore.Transform.TransformPoint(new(7, 9)), "inserting world content into a rotated nested scope preserves its world geometry");
        check(worldDraft.GroupId is null && worldDraft.Transform == worldBefore.Transform && !ReferenceEquals(worldDraft, inserted), "scope insertion never mutates or stores the caller's world draft");
        var billboard = Box(0, 0); billboard.IsBillboard = true; billboard.BillboardAnchor = new(12, 18); billboard.BillboardHeight = 4;
        billboard.Transform = Affine2D.Rotation(10); graph.InsertElement(billboard, empty.Id, worldCoordinates: false);
        var placement = graph.NodeWorldPlacement(empty.Id);
        var worldBillboard = graph.ToWorld(graph.FindElement(billboard.Id)!);
        Near(worldBillboard.BillboardAnchor, placement.PlaneTransform.TransformPoint(billboard.BillboardAnchor), "mixed-group billboard anchor inherits the complete parent placement");
        check(Math.Abs(worldBillboard.BillboardHeight - 24) < 1e-9 && worldBillboard.Transform ==
            new Affine2D(placement.PlaneTransform.M11, placement.PlaneTransform.M12, placement.PlaneTransform.M21, placement.PlaneTransform.M22, 0, 0).Compose(billboard.Transform),
            "nested elevation scales multiply independently of the inherited billboard surface linear transform");
        var localAgain = graph.ToLocal(worldBillboard, empty.Id);
        Near(localAgain.BillboardAnchor, billboard.BillboardAnchor, "billboard world-to-scope conversion restores its local anchor");
        check(Math.Abs(localAgain.BillboardHeight - billboard.BillboardHeight) < 1e-9, "billboard world-to-scope conversion restores height");

        var beforeReparent = graph.ToWorld(inserted);
        var beforeBillboard = graph.ToWorld(graph.FindElement(billboard.Id)!);
        graph.ReparentPreservingWorld([inserted.Id, billboard.Id], null, 0);
        Near(graph.ToWorld(graph.FindElement(inserted.Id)!).Transform.TransformPoint(new(7, 9)), beforeReparent.Transform.TransformPoint(new(7, 9)), "reparenting a planar child preserves world ink without resampling");
        Near(graph.ToWorld(graph.FindElement(billboard.Id)!).BillboardAnchor, beforeBillboard.BillboardAnchor, "reparenting a billboard preserves its world anchor");
        check(graph.FindElement(billboard.Id)!.BillboardHeight == beforeBillboard.BillboardHeight && document.RootOrder!.Take(2).SequenceEqual([inserted.Id, billboard.Id]), "reparenting preserves billboard height and creates the requested destination slots");
        graph.ReparentPreservingWorld([empty.Id], null);
        check(graph.ParentId(empty.Id) is null && graph.EnumerateChildren(null).Contains(empty.Id), "an empty nested group can move to the root without disappearing");
        Reject(() => graph.ReparentPreservingWorld([legacy.Id], legacy.Id), "a group cannot be moved into itself");

        var lockedChild = graph.FindElement(a.Id)!; lockedChild.Locked = true; legacy.Visible = false;
        graph = new SceneGraph(document);
        check(!graph.IsVisible(a.Id) && graph.IsLocked(a.Id) && !graph.ToWorld(lockedChild).Visible,
            "ancestor visibility and leaf locks are inherited in the effective view");
        legacy.Visible = true; legacy.Locked = true; graph = new SceneGraph(document);
        check(graph.IsLocked(c.Id) && !graph.FindElement(c.Id)!.Locked, "parent lock does not overwrite the child's own persisted lock");
        legacy.Locked = false; graph = new SceneGraph(document);
        check(!graph.IsLocked(c.Id) && graph.IsLocked(a.Id), "releasing a parent lock restores each child's original state");

        var fullClone = document.Clone(true);
        SceneGraph.Validate(fullClone);
        check(!fullClone.Elements.Select(element => element.Id).Intersect(document.Elements.Select(element => element.Id)).Any() &&
            !fullClone.Groups.Select(group => group.Id).Intersect(document.Groups.Select(group => group.Id)).Any(), "full new-ID clone remaps both entity tables and every draw reference");
        var partial = document.CloneElements([a.Id]); SceneGraph.Validate(partial);
        check(partial.Elements.Count == 1 && partial.Groups.Count == 1 && partial.Groups[0].Transform == legacy.Transform &&
            partial.Groups[0].ChildOrder!.SequenceEqual([a.Id]), "partial copying preserves only required ancestors and clips atomic child order");
        var beforeDelete = document.Groups.Count; graph.RemoveNodes([a.Id, legacy.Id]);
        check(graph.FindGroup(legacy.Id) is null && graph.FindElement(a.Id) is null && graph.FindGroup(empty.Id) is not null &&
            document.Groups.Count == beforeDelete - 1, "node deletion removes its complete subtree once while preserving other empty groups");

        var captureSource = new SceneDocument(); var captureGraph = new SceneGraph(captureSource);
        var captureGroup = captureGraph.InsertGroup(new() { OrderMode = GroupOrderMode.AtomicBlock, Transform = Affine2D.Translation(200, 100) });
        var captureLeaf = captureGraph.InsertElement(Box(10, 20), captureGroup.Id, worldCoordinates: false);
        var captured = captureSource.CreateCapture(new(190, 90, 100, 100), new string('a', 64));
        var capturedWorld = SceneEvaluation.Build(captured).RenderDocument.Elements.Single();
        Near(capturedWorld.Transform.TransformPoint(new(0, 0)), new(20, 30), "capture selects by world bounds and translates only the copied ownership roots");
        check(captured.Elements.Single().Transform == captureLeaf.Transform && captured.Groups.Single().Transform == Affine2D.Translation(10, 10), "capture retains editable child-local geometry and a new group identity");

        var oldEmpty = new CanvasGroup { Name = "legacy empty" };
        var emptyEditor = new EditorSession(new() { Groups = [oldEmpty] });
        emptyEditor.Execute("rename retained empty", scene => scene.Groups[0].Name = "renamed empty");
        check(emptyEditor.Document.Groups.Single().Id == oldEmpty.Id && emptyEditor.Undo() && emptyEditor.Document.Groups.Single().Name == "legacy empty",
            "an ordinary commit and undo never prune a valid retained legacy empty group");
        var hiddenEditor = new EditorSession(captureSource.Clone());
        hiddenEditor.SetSelection([captureGroup.Id]); hiddenEditor.SetVisible(false); hiddenEditor.UngroupSelection();
        check(hiddenEditor.Document.Groups.Count == 0 && !hiddenEditor.Document.Elements.Single().Visible,
            "ungrouping hidden content preserves its effective visibility as well as its world placement");

        foreach (var corrupt in new Action<SceneDocument>[]
        {
            d => d.RootOrder!.Add(d.RootOrder[0]),
            d => d.RootOrder!.Clear(),
            d => d.Groups[0].ParentId = d.Groups[0].Id,
            d => d.Groups[0].Transform = Affine2D.Scale(0, 1),
            d => d.Groups[0].ElevationScale = double.NaN,
            d => d.Groups[0].ChildOrder = null,
            d => d.RootOrder = null,
            d => d.Elements[0].GroupId = Guid.NewGuid()
        })
        {
            var invalid = captureSource.Clone(); corrupt(invalid);
            Reject(() => SceneGraph.Validate(invalid), "malformed ownership, paint slots or parent placement cannot be evaluated");
        }
    }
    private static CanvasElement Box(double x, double y) => new()
    {
        Kind = ElementKind.Rectangle, Width = 30, Height = 20, Transform = Affine2D.Translation(x, y),
        Style = new() { Fill = 0xFF116688, StrokeWidth = 0 }
    };
}
