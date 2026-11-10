using FloatCanvas.Core;
using System.Text.Json;

internal static class NodeSessionTests
{
    internal static void Run(Action<bool, string> check)
    {
        var failures = new List<string>();
        void Case(string name, Action test)
        {
            try { test(); }
            catch (Exception error)
            {
                string failure = name + ": " + error.GetType().Name + ": " + error.Message;
                failures.Add(failure); Console.WriteLine("NODE_SESSION_FAIL " + failure);
            }
        }
        void Near(PointD actual, PointD expected, string message) => check(actual.DistanceTo(expected) < 1e-6, message);
        static string Json(object value) => JsonSerializer.Serialize(value);

        Case("scope navigation is presentation state", () =>
        {
            var fixture = Nested(); var session = new EditorSession(fixture.Document);
            string before = Json(session.Document); long content = session.ContentVersion, preview = session.PreviewRevision, scene = session.SceneRevision;
            session.EnterScope(fixture.Outer); session.EnterScope(fixture.Inner);
            check(session.EditScopePath.Select(group => group.Id).SequenceEqual([fixture.Outer, fixture.Inner]), "nested edit breadcrumbs follow ownership ancestry");
            session.LeaveScope(); check(session.EditScopeId == fixture.Outer, "leave scope returns exactly one ownership level");
            session.SetEditScope(null);
            check(Json(session.Document) == before && session.ContentVersion == content && session.PreviewRevision == preview && session.SceneRevision == scene &&
                session.UndoCount == 0 && !session.HasPendingChanges, "entering and leaving groups does not dirty content or create undo entries");
            check(ReferenceEquals(session.Selection, session.SelectedNodeIds), "the read-only node selection name aliases the sole authoritative selection");
        });

        Case("world Add and editable import into local scope", () =>
        {
            var fixture = Nested(); var session = new EditorSession(fixture.Document); session.EnterScope(fixture.Inner);
            var world = Box("world draft", 450, 320); var original = world.Clone(); var parent = new SceneGraph(session.Document).NodeWorldPlacement(fixture.Inner);
            session.Add(world);
            var graph = new SceneGraph(session.Document); var owned = graph.FindElement(world.Id)!;
            check(owned.GroupId == fixture.Inner && world.GroupId is null && world.Transform == original.Transform && !ReferenceEquals(owned, world), "Add adopts an independent local child without changing the world draft");
            Near(graph.ToWorld(owned).Transform.TransformPoint(new(7, 11)), world.Transform.TransformPoint(new(7, 11)), "scope Add retains the requested world geometry");
            Near(owned.Transform.TransformPoint(new(7, 11)), parent.Inverse().PlaneTransform.Compose(world.Transform).TransformPoint(new(7, 11)), "scope Add stores inverse-parent local placement");
            var incoming = Nested(); var incomingBefore = Json(incoming.Document); var incomingWorld = SceneEvaluation.Build(incoming.Document).RenderDocument;
            var offset = Affine2D.Translation(70, -20); int history = session.UndoCount;
            session.InsertDocument(incoming.Document, new AssetLibrary(), offset);
            graph = new(session.Document);
            foreach (var source in incomingWorld.Elements)
            {
                var copy = session.SelectedWorldElements().Single(element => element.Name == source.Name);
                if (source.IsBillboard)
                {
                    Near(copy.BillboardAnchor, offset.TransformPoint(source.BillboardAnchor), "imported billboard anchor remains in host world coordinates");
                    check(Math.Abs(copy.BillboardHeight - source.BillboardHeight) < 1e-8, "scope import does not multiply source elevation twice");
                }
                else Near(copy.Transform.TransformPoint(new(3, 5)), offset.Compose(source.Transform).TransformPoint(new(3, 5)), "nested editable import preserves world geometry under the host scope");
            }
            check(session.Selection.All(id => graph.ParentId(id) == fixture.Inner) && session.UndoCount == history + 1 && Json(incoming.Document) == incomingBefore,
                "imported roots become children of the active scope in one transaction without modifying their source");
        });

        Case("scoped leaf duplicate and empty graph copies", () =>
        {
            var fixture = Nested(); var session = new EditorSession(fixture.Document); session.EnterScope(fixture.Inner);
            session.SetSelection([fixture.Plane]); var source = new SceneGraph(session.Document).FindElement(fixture.Plane)!.Clone();
            var original = session.SelectedWorldElements().Single(); int before = session.UndoCount;
            session.DuplicateSelection(17, -9);
            var duplicate = session.SelectedElements().Single(); var duplicateWorld = session.SelectedWorldElements().Single();
            Near(duplicateWorld.Transform.TransformPoint(new(4, 6)), Affine2D.Translation(17, -9).Compose(original.Transform).TransformPoint(new(4, 6)), "duplicating in a transformed scope applies its offset in world coordinates");
            var duplicateGraph = new SceneGraph(session.Document);
            Near(duplicate.Transform.TransformPoint(new(4, 6)), duplicateGraph.ScopeWorldPlacement(duplicate.GroupId).Inverse().PlaneTransform
                .Compose(Affine2D.Translation(17, -9)).Compose(original.Transform).TransformPoint(new(4, 6)), "scope duplicate stores its world offset in the proper local frame");
            check(duplicate.Id != source.Id && duplicate.Width == source.Width && duplicate.Height == source.Height && duplicate.Points.SequenceEqual(source.Points) &&
                duplicateGraph.FindElement(source.Id)!.Transform == source.Transform && session.UndoCount == before + 1,
                "scope duplicate retains local vector dimensions, the original source placement and one undo operation");
            var empty = session.CreatePopulatedGroup("copied group"); var graph = new SceneGraph(session.Document);
            var emptyWorld = graph.NodeWorldPlacement(empty); string emptyBefore = Json(graph.FindGroup(empty)!);
            session.DuplicateSelection(9, 13); graph = new(session.Document);
            var newEmpty = session.Document.Groups.Single(group => group.Name == "copied group" && group.Id != empty);
            Near(graph.NodeWorldPlacement(newEmpty.Id).PlaneTransform.TransformPoint(default), Affine2D.Translation(9, 13).Compose(emptyWorld.PlaneTransform).TransformPoint(default), "populated group duplicate preserves its world origin and explicit offset");
            check(newEmpty.ChildOrder is { Count: 1 } && Json(graph.FindGroup(empty)!) == emptyBefore && session.Selection.Count > 0 && session.SelectedElements().Any(),
                "a populated selected group remains copyable with its artwork or changing the source group");
            var snapshot = session.CreateSelectionSnapshot(); var copied = snapshot.Clone(true);
            var restored = JsonSerializer.Deserialize<SceneDocument>(Json(copied))!; SceneGraph.Validate(restored);
            var target = new EditorSession(); target.RestoreDocument("restore empty graph", restored);
            check(target.Document.Groups.Any(group => group.Name == "copied group" && group.ChildOrder is { Count: 1 }) && target.Undo() && target.Document.Groups.Count == 0 && target.Redo(),
                "editable populated group copy and document restore retain slots through JSON and undo/redo");
        });

        Case("whole group gestures retain source child geometry", () =>
        {
            var fixture = Nested(); var session = new EditorSession(fixture.Document); session.SetSelection([fixture.Outer]);
            string sourceLeaves = Json(session.Document.Elements); var operations = new[]
            {
                (Delta: Affine2D.Translation(31, -17), Elevation: 1d),
                (Delta: Affine2D.Around(new(120, 90), Affine2D.Rotation(37)), Elevation: 1d),
                (Delta: Affine2D.Around(new(120, 90), Affine2D.Scale(1.4, 1.8)), Elevation: 1.8d)
            };
            foreach (var operation in operations)
            {
                var before = SceneEvaluation.Build(session.Document); var original = session.CaptureSelectionTransform(); int history = session.UndoCount;
                session.BeginTransaction("group gesture");
                check(session.TryApplySelectionTransform(original, operation.Delta, operation.Elevation), "complete mixed group accepts a finite layout and elevation transform");
                check(Json(session.Document.Elements) == sourceLeaves && session.HasPendingChanges && session.UndoCount == history, "live parent gesture does not bake even locked children or publish premature history");
                var after = SceneEvaluation.Build(session.Document);
                foreach (var source in before.RenderDocument.Elements)
                {
                    var result = after.FindElement(source.Id)!;
                    if (source.IsBillboard)
                    {
                        Near(result.BillboardAnchor, operation.Delta.TransformPoint(source.BillboardAnchor), "mixed group gesture carries the billboard ground anchor with the group");
                        check(Math.Abs(result.BillboardHeight - source.BillboardHeight * operation.Elevation) < 1e-7, "mixed group scaling changes effective elevation without rewriting the source billboard");
                    }
                    else Near(result.Transform.TransformPoint(new(2, 3)), operation.Delta.Compose(source.Transform).TransformPoint(new(2, 3)), "whole group gesture carries every plane child including its locked child");
                }
                session.Commit(); check(session.UndoCount == history + 1, "one complete group gesture publishes exactly one history entry");
            }
        });

        Case("overall and individual rotation differ by pivot", () =>
        {
            var fixture = TwoGroups(); var overall = new EditorSession(fixture.Document.Clone()); var individual = new EditorSession(fixture.Document.Clone());
            var ids = new[] { fixture.Left, fixture.Right }; overall.SetSelection(ids); individual.SetSelection(ids);
            var original = SceneEvaluation.Build(overall.Document); var common = overall.SelectionBounds()!.Value.Center;
            var delta = Affine2D.Around(common, Affine2D.Rotation(90));
            overall.BeginTransaction("overall rotate"); check(overall.TryApplySelectionTransform(overall.CaptureSelectionTransform(), delta, individually: false), "overall rotation is accepted"); overall.Commit();
            individual.BeginTransaction("individual rotate"); check(individual.TryApplySelectionTransform(individual.CaptureSelectionTransform(), delta, individually: true), "individual rotation is accepted"); individual.Commit();
            foreach (var source in original.RenderDocument.Elements)
            {
                var initialCenter = ElementGeometry.Bounds(source, false).Center;
                var wholeCenter = ElementGeometry.Bounds(SceneEvaluation.Build(overall.Document).FindElement(source.Id)!, false).Center;
                var ownCenter = ElementGeometry.Bounds(SceneEvaluation.Build(individual.Document).FindElement(source.Id)!, false).Center;
                Near(wholeCenter, delta.TransformPoint(initialCenter), "overall rotation moves each group's center around the shared pivot");
                Near(ownCenter, initialCenter, "individual rotation retains each selected group's center");
                check(wholeCenter.DistanceTo(ownCenter) > 20, "the two rotation modes produce observably different layouts");
            }
        });

        Case("gesture snapshots are absolute and cancelable", () =>
        {
            var fixture = Nested(); var session = new EditorSession(fixture.Document); session.SetSelection([fixture.Outer]);
            string before = Json(session.Document); var original = session.CaptureSelectionTransform();
            var old = new SceneGraph(session.Document).FindGroup(fixture.Outer)!.Transform;
            session.BeginTransaction("repeated pointer samples");
            check(session.TryApplySelectionTransform(original, Affine2D.Translation(10, 0)) && session.TryApplySelectionTransform(original, Affine2D.Translation(25, 0)), "multiple pointer samples share their initial transform snapshot");
            check(new SceneGraph(session.Document).FindGroup(fixture.Outer)!.Transform == Affine2D.Translation(25, 0).Compose(old), "later pointer samples replace earlier deltas instead of accumulating them");
            check(session.TryApplySelectionTransform(original, Affine2D.Identity) && Json(session.Document) == before,
                "returning the pointer to its initial position restores the exact snapshot instead of preserving a prior sample");
            session.Cancel(); check(Json(session.Document) == before && session.UndoCount == 0 && !session.HasPendingChanges, "cancel restores the complete original graph and dirty identity");
        });

        Case("nested identity gestures preserve exact source identity", () =>
        {
            foreach (bool group in new[] { true, false })
            {
                var fixture = Nested(); var session = new EditorSession(fixture.Document); session.SetSelection([group ? fixture.Inner : fixture.Plane]);
                string before = Json(session.Document); session.TransformSelection(Affine2D.Identity);
                check(Json(session.Document) == before && session.ContentVersion == 0 && session.UndoCount == 0,
                    "identity transforms inside a nontrivial parent cannot create rounding-only edits or history");
            }
        });

        Case("invalid finite group candidate restores whole last-good state", () =>
        {
            var fixture = TwoGroups(1e308); var session = new EditorSession(fixture.Document); session.SetSelection([fixture.Left, fixture.Right]);
            string before = Json(session.Document); var original = session.CaptureSelectionTransform(); session.BeginTransaction("overflow candidate");
            bool accepted = session.TryApplySelectionTransform(original, Affine2D.Translation(1e308, 0));
            check(!accepted && Json(session.Document) == before && session.UndoCount == 0 && !session.HasPendingChanges, "a later overflowing member cannot leave an earlier member transformed or throw during rollback");
            session.Cancel();
        });

        Case("projection rejection preserves the previous accepted pointer sample", () =>
        {
            var fixture = TwoGroups(60); var document = fixture.Document;
            document.Viewport = new(-250, -500, 500, 900); var initial = CanvasCalibration.CreateInitial(document.Viewport, 4, 5);
            PointD[] quad = [new(-100, -100), new(100, -100), new(200, 200), new(-200, 200)];
            check(CanvasCalibration.TryCalibrate(initial, quad, document.Viewport, out var projection, out _), "finite projection fixture has a visible horizon");
            document.Canvas.Projection = projection;
            var session = new EditorSession(document); session.SetSelection([fixture.Left, fixture.Right]); var original = session.CaptureSelectionTransform();
            session.BeginTransaction("projection candidate");
            check(session.TryApplySelectionTransform(original, Affine2D.Translation(2, 2)), "a small complete candidate is accepted before rejection");
            string accepted = Json(session.Document);
            check(!session.TryApplySelectionTransform(original, Affine2D.Translation(0, -1_000_000_000)) && Json(session.Document) == accepted,
                "a candidate crossing the visible plane restores every selected node to its last accepted sample");
            session.Cancel();
        });

        Case("atomic and legacy ungroup preserve world ink flags and draw order", () =>
        {
            foreach (bool legacy in new[] { false, true })
            {
                var fixture = legacy ? Legacy() : Nested(); var document = fixture.Document; var group = new SceneGraph(document).FindGroup(legacy ? fixture.Outer : fixture.Inner)!;
                group.Visible = false; var session = new EditorSession(document); session.SetSelection([group.Id]);
                var before = SceneEvaluation.Build(document); int history = session.UndoCount;
                session.UngroupSelection(); var after = SceneEvaluation.Build(session.Document);
                check(after.RenderDocument.Elements.Select(element => element.Id).SequenceEqual(before.RenderDocument.Elements.Select(element => element.Id)), "ungroup keeps the exact legacy or atomic leaf draw order");
                foreach (var source in before.RenderDocument.Elements)
                {
                    var result = after.FindElement(source.Id)!;
                    check(result.Visible == source.Visible && result.Locked == source.Locked, "ungroup retains each child's effective visibility and locking");
                    if (source.IsBillboard)
                    {
                        Near(result.BillboardAnchor, source.BillboardAnchor, "ungroup retains billboard world anchor");
                        check(Math.Abs(result.BillboardHeight - source.BillboardHeight) < 1e-8, "ungroup retains billboard elevation");
                    }
                    else Near(result.Transform.TransformPoint(new(4, 7)), source.Transform.TransformPoint(new(4, 7)), "ungroup retains planar world geometry");
                }
                check(session.UndoCount == history + 1 && session.Undo() && SceneEvaluation.Build(session.Document).RenderDocument.Elements.Select(element => element.Id)
                    .SequenceEqual(before.RenderDocument.Elements.Select(element => element.Id)), "ungroup is one reversible ownership operation");
            }
        });

        Case("invalidated edit scope falls back to its surviving ancestor", () =>
        {
            var fixture = Nested(); var session = new EditorSession(fixture.Document); session.EnterScope(fixture.Inner);
            var empty = session.CreatePopulatedGroup("temporary scope"); session.EnterScope(empty); int undo = session.UndoCount;
            check(session.Undo() && session.EditScopeId == fixture.Inner && session.Document.Groups.All(group => group.Id != empty), "undo of the current scope's creation returns to its surviving parent");
            check(session.Redo() && session.EditScopeId == fixture.Inner && session.UndoCount == undo, "redo restores the graph without unexpectedly reentering a former scope");
            session.EnterScope(empty); session.SetSelection([fixture.Inner]); session.DeleteSelection();
            check(session.EditScopeId is null && session.Document.Groups.Count == 0, "deleting the last branch dissolves its empty ancestors and returns to root");
        });

        Case("scope ancestry follows an active group's reparenting", () =>
        {
            var fixture = TwoGroups(); var graph = new SceneGraph(fixture.Document);
            var inner = graph.InsertGroup(new() { Name = "active moved scope", OrderMode = GroupOrderMode.AtomicBlock }, fixture.Left);
            graph.InsertElement(new() { Kind = ElementKind.Rectangle, Width = 8, Height = 8 }, inner.Id);
            var session = new EditorSession(fixture.Document); session.EnterScope(inner.Id); session.MoveNodes([inner.Id], fixture.Right);
            check(session.EditScopePath.Select(group => group.Id).SequenceEqual([fixture.Right, inner.Id]), "active breadcrumbs follow a group's new parent");
            session.SetSelection([inner.Id]); session.DeleteSelection();
            check(session.EditScopeId == fixture.Right, "removing a reparented active scope falls back to its current surviving parent");
        });

        Case("inherited locks protect bulk leaf properties", () =>
        {
            var fixture = Nested(); var graph = new SceneGraph(fixture.Document);
            var editable = graph.InsertElement(Box("editable peer", 0, 0), fixture.Outer, worldCoordinates: false);
            graph.FindGroup(fixture.Inner)!.Locked = true;
            var original = fixture.Document.Elements.Where(element => element.GroupId == fixture.Inner).ToDictionary(element => element.Id, element => element.Style.Fill);
            var session = new EditorSession(fixture.Document); session.SetSelection([fixture.Outer]); session.UpdateSelection(element => element.Style.Fill = 0xFFFF0088);
            check(new SceneGraph(session.Document).FindElement(editable.Id)!.Style.Fill == 0xFFFF0088, "a bulk property edit still reaches unlocked peer leaves");
            check(session.Document.Elements.Where(element => original.ContainsKey(element.Id)).All(element => element.Style.Fill == original[element.Id]),
                "bulk property editing cannot bypass a locked nested group through an unlocked ancestor selection");
        });

        Case("depth-rejected import leaves assets unchanged", () =>
        {
            var target = new SceneDocument(); var graph = new SceneGraph(target); Guid? parent = null;
            for (int depth = 0; depth < SceneLimits.MaximumGroupDepth; depth++)
                parent = graph.InsertGroup(new() { OrderMode = GroupOrderMode.AtomicBlock }, parent).Id;
            graph.InsertElement(Box("depth boundary content", 0, 0), parent, worldCoordinates: false);
            var session = new EditorSession(target); session.EnterScope(parent!.Value);
            var resources = new AssetLibrary(); string asset = resources.Add([1, 2, 3, 4, 5]);
            var incoming = new SceneDocument(); var incomingGraph = new SceneGraph(incoming);
            var group = incomingGraph.InsertGroup(new() { OrderMode = GroupOrderMode.AtomicBlock });
            incomingGraph.InsertElement(new() { Kind = ElementKind.Image, Width = 20, Height = 12, AssetId = asset }, group.Id, worldCoordinates: false);
            string before = Json(session.Document); int undo = session.UndoCount; bool rejected = false;
            try { session.InsertDocument(incoming, resources, Affine2D.Identity); } catch (InvalidDataException) { rejected = true; }
            check(rejected && Json(session.Document) == before && session.UndoCount == undo && !session.IsTransactionActive, "an import beyond the nesting limit restores the complete source graph and history");
            check(session.Assets.Count == 0, "a rejected editable import does not leave newly copied unreferenced resources behind");
        });

        Case("same-position move and layer command keep null legacy representation", () =>
        {
            var a = Box("a", 0, 0); var b = Box("b", 50, 0); var session = new EditorSession(new() { Elements = [a, b] });
            string before = Json(session.Document); session.MoveNodes([b.Id], null); session.MoveNodes([a.Id], null, b.Id);
            check(Json(session.Document) == before && session.Document.RootOrder is null && session.UndoCount == 0 && session.ContentVersion == 0, "dragging to the same position does not materialize or dirty a legacy draw ledger");
            session.SetSelection([b.Id]); session.ReorderSelection(LayerOrder.Front);
            check(Json(session.Document) == before && session.Document.RootOrder is null && session.UndoCount == 0 && session.ContentVersion == 0,
                "sending an already-frontmost node to front is also a representation-preserving no-op");
        });

        Case("cross-parent move and empty source restoration", () =>
        {
            var fixture = TwoGroups(); var session = new EditorSession(fixture.Document);
            var graph = new SceneGraph(session.Document); var leaf = graph.DescendantElementIds(fixture.Left).Single(); var sourceLocal = graph.FindElement(leaf)!.Transform;
            var before = graph.ToWorld(graph.FindElement(leaf)!); session.MoveNodes([leaf], fixture.Right);
            graph = new(session.Document); var moved = graph.FindElement(leaf)!;
            Near(graph.ToWorld(moved).Transform.TransformPoint(new(3, 4)), before.Transform.TransformPoint(new(3, 4)), "cross-parent movement retains world ink under different parent coordinates");
            check(moved.GroupId == fixture.Right && moved.Transform != sourceLocal && graph.FindGroup(fixture.Left) is null, "cross-parent movement updates source local coordinates and dissolves the emptied source group");
            var snapshot = session.CreateCommittedSnapshot(); var restored = JsonSerializer.Deserialize<SceneDocument>(Json(snapshot))!;
            session.RestoreDocument("restore local graph", restored);
            check(session.Document.Groups.All(group => group.Id != fixture.Left) && session.Undo() && session.Undo() &&
                new SceneGraph(session.Document).FindElement(leaf)!.GroupId == fixture.Left, "restoring an unchanged graph keeps no empty group; undo restores its previous parent");
        });

        Case("point selection never retains both an ancestor and its child", () =>
        {
            var fixture = TwoGroups(); var session = new EditorSession(fixture.Document);
            var graph = new SceneGraph(session.Document); var leaf = graph.DescendantElementIds(fixture.Left).Single();
            session.SetSelection([fixture.Left]); session.SelectAt(new(10, 6), additive: true, individual: true);
            check(session.Selection.SetEquals([fixture.Left]), "adding an individual child preserves one canonical ancestor selection");
            session.SetSelection([leaf]); session.SelectAt(new(10, 6), additive: true);
            check(session.Selection.SetEquals([fixture.Left]) && session.UndoCount == 0 && session.ContentVersion == 0,
                "adding the child's owning group replaces the redundant child without content history");
        });

        Case("layer reordering applies independently to every selected paint context", () =>
        {
            var fixture = TwoGroups(); var graph = new SceneGraph(fixture.Document);
            var a = graph.DescendantElementIds(fixture.Left).Single(); var b = graph.DescendantElementIds(fixture.Right).Single();
            var c = graph.InsertElement(Box("left front", 30, 0), fixture.Left, worldCoordinates: false);
            var d = graph.InsertElement(Box("right front", 30, 0), fixture.Right, worldCoordinates: false);
            var session = new EditorSession(fixture.Document); session.SetSelection([a, b]); session.ReorderSelection(LayerOrder.Front);
            graph = new(session.Document);
            check(graph.PaintOrder(fixture.Left).SequenceEqual([c.Id, a]) && graph.PaintOrder(fixture.Right).SequenceEqual([d.Id, b]) && session.UndoCount == 1,
                "one layer command moves the selected member in each parent without merging their contexts");
            session.Undo(); graph = new(session.Document);
            check(graph.PaintOrder(fixture.Left).SequenceEqual([a, c.Id]) && graph.PaintOrder(fixture.Right).SequenceEqual([b, d.Id]),
                "one undo restores both local paint orders");
        });

        if (failures.Count > 0) throw new InvalidOperationException("Node session behavior failures:\n" + string.Join("\n", failures));
    }

    internal static void RunScale(Action<bool, string> check)
    {
        const int count = 50_000;
        var document = new SceneDocument { Elements = Enumerable.Range(0, count).Select(index => Box("scale", index % 500 * 24, index / 500 * 16)).ToList() };
        var session = new EditorSession(document); session.SetSelection(document.Elements.Select(element => element.Id));
        // Exercise the public operation that used to resolve every node by scanning
        // all leaves and constructing a separate graph for every bound calculation.
        long allocated = GC.GetAllocatedBytesForCurrentThread(); var timer = System.Diagnostics.Stopwatch.StartNew();
        var snapshot = session.CaptureSelectionTransform(); timer.Stop(); allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Console.WriteLine($"NODE_SESSION_SCALE operation=CaptureSelectionTransform selected={count} elapsedMs={timer.Elapsed.TotalMilliseconds:F3} allocatedBytes={allocated}");
        check(snapshot is not null && session.Selection.Count == count && session.ContentVersion == 0 && session.UndoCount == 0 && document.RootOrder is null,
            "50k selected leaves can capture a real transform snapshot without mutating content, history or the legacy ledger");
        var graph = new SceneGraph(document); int resolved = 0; timer.Restart();
        foreach (var id in session.Selection) resolved += graph.DescendantElementIds(id).Count();
        timer.Stop(); Console.WriteLine($"NODE_SESSION_SCALE operation=ExpandSelectedLeaves selected={count} resolved={resolved} elapsedMs={timer.Elapsed.TotalMilliseconds:F3}");
        check(resolved == count, "50k disjoint leaf selections expand exactly once each");
        SceneLimits.ValidateCapacity(document);

        const int groupCount = 10_000, members = 5;
        var grouped = new SceneDocument { RootOrder = [] };
        for (int index = 0; index < groupCount; index++)
        {
            var group = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [], Transform = Affine2D.Translation(index % 200 * 30, index / 200 * 80) };
            grouped.Groups.Add(group); grouped.RootOrder.Add(group.Id);
            for (int member = 0; member < members; member++)
            {
                var leaf = Box("stamp member", 0, member * 14); leaf.GroupId = group.Id;
                grouped.Elements.Add(leaf); group.ChildOrder.Add(leaf.Id);
            }
        }
        var groupedSession = new EditorSession(grouped); groupedSession.SetSelection(grouped.RootOrder);
        allocated = GC.GetAllocatedBytesForCurrentThread(); timer.Restart();
        var groupedSnapshot = groupedSession.CaptureSelectionTransform(); timer.Stop(); allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Console.WriteLine($"NODE_SESSION_SCALE operation=CaptureGroupedSelection groups={groupCount} leaves={count} elapsedMs={timer.Elapsed.TotalMilliseconds:F3} allocatedBytes={allocated}");
        check(groupedSnapshot is not null && groupedSession.Selection.Count == groupCount && grouped.Elements.Count == count && groupedSession.UndoCount == 0 && groupedSession.ContentVersion == 0,
            "10k disjoint five-member groups capture a real transform snapshot without a full-scene scan for every selected group");
        SceneLimits.ValidateCapacity(grouped);
    }

    private sealed record Fixture(SceneDocument Document, Guid Outer, Guid Inner, Guid Plane, Guid Locked, Guid Billboard);
    private static Fixture Nested()
    {
        var document = new SceneDocument(); var graph = new SceneGraph(document);
        var outer = graph.InsertGroup(new() { Name = "outer", OrderMode = GroupOrderMode.AtomicBlock,
            Transform = Affine2D.Translation(100, 80).Compose(Affine2D.Rotation(20)).Compose(Affine2D.Scale(1.3, .7)), ElevationScale = 2 });
        var inner = graph.InsertGroup(new() { Name = "inner", OrderMode = GroupOrderMode.AtomicBlock,
            Transform = Affine2D.Translation(30, 40).Compose(Affine2D.Rotation(-15)), ElevationScale = 3 }, outer.Id);
        var plane = graph.InsertElement(Box("plane", 10, 20), inner.Id, worldCoordinates: false);
        var locked = Box("locked child", 80, 30); locked.Locked = true; graph.InsertElement(locked, inner.Id, worldCoordinates: false);
        var billboard = Box("billboard", 0, 0); billboard.IsBillboard = true; billboard.BillboardAnchor = new(25, 35); billboard.BillboardHeight = 4;
        billboard.Transform = Affine2D.Rotation(10); graph.InsertElement(billboard, inner.Id, worldCoordinates: false);
        return new(document, outer.Id, inner.Id, plane.Id, locked.Id, billboard.Id);
    }
    private static (SceneDocument Document, Guid Left, Guid Right) TwoGroups(double right = 120)
    {
        var document = new SceneDocument(); var graph = new SceneGraph(document);
        var a = graph.InsertGroup(new() { Name = "left", OrderMode = GroupOrderMode.AtomicBlock });
        var b = graph.InsertGroup(new() { Name = "right", OrderMode = GroupOrderMode.AtomicBlock, Transform = Affine2D.Translation(right, 0) });
        graph.InsertElement(Box("left child", 0, 0), a.Id, worldCoordinates: false); graph.InsertElement(Box("right child", 0, 0), b.Id, worldCoordinates: false);
        return (document, a.Id, b.Id);
    }
    private static Fixture Legacy()
    {
        var outer = new CanvasGroup { Name = "legacy outer", Transform = Affine2D.Translation(20, 30).Compose(Affine2D.Rotation(15)), ElevationScale = 2 };
        var inner = new CanvasGroup { Name = "legacy inner", ParentId = outer.Id, Transform = Affine2D.Rotation(-10), ElevationScale = 3 };
        var a = Box("legacy A", 10, 20); a.GroupId = inner.Id;
        var b = Box("outside B", 50, 60); b.Locked = true;
        var c = Box("legacy C", 80, 90); c.GroupId = outer.Id; c.Locked = true;
        return new(new() { Elements = [a, b, c], Groups = [outer, inner] }, outer.Id, inner.Id, a.Id, c.Id, Guid.Empty);
    }
    private static CanvasElement Box(string name, double x, double y) => new()
    {
        Name = name, Kind = ElementKind.Rectangle, Width = 20, Height = 12,
        Transform = Affine2D.Translation(x, y), Style = new() { Fill = 0xFF116688, StrokeWidth = 0 }
    };
}
