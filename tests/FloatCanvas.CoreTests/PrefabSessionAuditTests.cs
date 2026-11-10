using FloatCanvas.Core;
using System.Text.Json;

internal static class PrefabSessionAuditTests
{
    internal static void Run(Action<bool, string> check)
    {
        var results = new List<(bool Passed, string Message)>();
        void Record(bool passed, string message) { results.Add((passed, message)); if (!passed) Console.WriteLine("AUDIT_FAIL " + message); }
        void Case(string name, Action body)
        {
            try { body(); } catch (Exception error) { Record(false, name + " unexpectedly threw " + error); }
        }
        static string Json(object value) => JsonSerializer.Serialize(value);
        static bool Near(Affine2D a, Affine2D b) => a.TransformPoint(default).DistanceTo(b.TransformPoint(default)) < 1e-7 &&
            a.TransformPoint(new(1, 1)).DistanceTo(b.TransformPoint(new(1, 1))) < 1e-7;

        Case("foreign insertion", () =>
        {
            var sourceAssets = new AssetLibrary(); string resource = sourceAssets.Add([31, 41, 59]);
            var definition = new PrefabDefinition { Graph = new() { Elements = [new() { Kind = ElementKind.Image, AssetId = resource, Width = 20, Height = 10 }] } };
            var instance = new PrefabInstance { DefinitionId = definition.Id };
            var source = new SceneDocument { Instances = [instance], RootOrder = [instance.Id], Definitions = [definition] };
            var owner = new EditorSession(); owner.BeginTransaction("foreign input"); owner.Document.NextNumber = 17;
            string before = Json(owner.Document); long transaction = owner.TransactionId;
            bool rejected = false; try { owner.InsertDocument(source, sourceAssets, Affine2D.Identity); } catch (InvalidOperationException) { rejected = true; }
            Record(rejected && owner.IsTransactionActive && owner.TransactionId == transaction && Json(owner.Document) == before && owner.UndoCount == 0,
                "InsertDocument preserves a foreign transaction and document when its own Begin is rejected");
            Record(owner.Assets.Count == 0, "foreign-transaction InsertDocument rejects before publishing imported definition assets");
            owner.Cancel();
        });
        Case("runtime draft", () =>
        {
            var runtime = SceneGraphClone.CloneGraph(SceneEvaluation.Build(new SceneDocument { Elements = [Box("runtime")] }).RenderDocument).Graph;
            var definition = new PrefabDefinition { Graph = runtime }; var owner = new EditorSession(new() { Definitions = [definition] });
            bool rejected = false;
            try { var draft = owner.BeginPrefabEdit(definition.Id); owner.ApplyPrefabEdit(owner.PreparePrefabEdit(draft)); }
            catch (InvalidDataException) { rejected = true; }
            Record(rejected && owner.Document.Definitions[0].Graph.IsRuntimeEvaluated && owner.UndoCount == 0,
                "BeginPrefabEdit rejects runtime definition source instead of laundering it into an ordinary draft graph");
        });
        Case("refresh anchors", () =>
        {
            var definition = new PrefabDefinition { Graph = new() { Elements = [Box("moving source")] } };
            var package = new PrefabPackage { EntryDefinitionId = definition.Id, Definitions = [definition] };
            var metadata = Source(package.ResourceId, new(100, 50), 'A'); var owner = new EditorSession();
            var imported = owner.ImportPrefab(package, new(), new(Affine2D.Translation(200, 300), 1), metadata);
            Guid identity = SceneEvaluation.Build(owner.Document).Leaves.Single().RuntimeElementId; int history = owner.UndoCount;
            var first = metadata.Clone(); first.ImportOrigin = new(120, 70); first.LastAcceptedFileSha256 = new string('B', 64);
            owner.ApplyPrefabEdit(owner.PreparePrefabRefresh(imported.DefinitionId, package, new(), first));
            var firstWorld = SceneEvaluation.Build(owner.Document).Leaves.Single().RenderElement.Transform;
            Record(firstWorld.TransformPoint(default).DistanceTo(new(220, 320)) < 1e-7, "first normalized source refresh moves ink by its absolute-source origin change");
            var second = metadata.Clone(); second.ImportOrigin = new(140, 90); second.LastAcceptedFileSha256 = new string('C', 64);
            owner.ApplyPrefabEdit(owner.PreparePrefabRefresh(imported.DefinitionId, package, new(), second));
            var secondWorld = SceneEvaluation.Build(owner.Document).Leaves.Single();
            Record(secondWorld.RenderElement.Transform.TransformPoint(default).DistanceTo(new(240, 340)) < 1e-7,
                "second normalized source refresh retains the first displacement and reaches the original-anchor-relative position");
            Record(secondWorld.RuntimeElementId == identity && owner.UndoCount == history + 2 && owner.Undo() &&
                Near(SceneEvaluation.Build(owner.Document).Leaves.Single().RenderElement.Transform, firstWorld) && owner.Redo(),
                "consecutive refreshes preserve occurrence identity and undo each accepted snapshot without IO");
            var other = Source(Guid.NewGuid(), new(900, 900), 'D');
            var otherPackage = package.Clone(); otherPackage.ResourceId = other.SourceResourceId;
            owner.ApplyPrefabEdit(owner.PreparePrefabRefresh(imported.DefinitionId, otherPackage, new(), other));
            Record(SceneEvaluation.Build(owner.Document).Leaves.Single().RenderElement.Transform.TransformPoint(default).DistanceTo(new(200, 300)) < 1e-7,
                "explicit replacement source starts a new import anchor rather than inheriting the previous file displacement");
        });
        Case("selection and node scope", () =>
        {
            var leaf = Box("locked source"); leaf.Locked = true;
            var definition = new PrefabDefinition { Graph = new() { Elements = [leaf] } }; var owner = new EditorSession(new() { Definitions = [definition] });
            Guid instance = owner.PlacePrefab(definition.Id, LayerPlacement.Identity); var group = owner.GroupSelection()!.Value;
            Record(owner.SelectionUnitIds(instance).SequenceEqual([group]) && owner.SelectionUnitIds(instance, individual: true).SequenceEqual([instance]),
                "SelectionUnitIds resolves real instances and ignores source-member locks for host selection");
            owner.EnterScope(group); Record(owner.SelectionUnitIds(instance).SequenceEqual([instance]), "an instance resolves to its real source node inside an ordinary group scope");
            owner.SetSelection([instance]); string before = Json(owner.Document); int history = owner.UndoCount; owner.MoveNodes([instance], group);
            Record(before == Json(owner.Document) && owner.UndoCount == history, "moving an instance to its present final slot is a history-free no-op");
            var draft = owner.BeginPrefabEdit(definition.Id); var plan = owner.PreparePrefabEdit(draft);
            owner.LeaveScope(); owner.EnterScope(group); bool stale = false;
            try { owner.ApplyPrefabEdit(plan); } catch (InvalidOperationException) { stale = true; }
            Record(stale, "a prepared plan cannot revive after leaving and reentering the same scope");
        });
        Case("nested merge", () =>
        {
            var child = new PrefabDefinition { Name = "child", Graph = new() { Elements = [Box("child source")] } };
            var a = new PrefabInstance { DefinitionId = child.Id }; var b = new PrefabInstance { DefinitionId = child.Id, Transform = Affine2D.Translation(50, 0) };
            var entry = new PrefabDefinition { Name = "parent", Graph = new() { Instances = [a, b], RootOrder = [a.Id, b.Id] } };
            var dependent = new PrefabDefinition(); var dependentInstance = new PrefabInstance { DefinitionId = entry.Id };
            dependent.Graph.Instances = [dependentInstance]; dependent.Graph.RootOrder = [dependentInstance.Id];
            var unrelated = new PrefabDefinition { Name = "unrelated", Graph = new() { Elements = [Box("unrelated")] } };
            var owner = new EditorSession(new() { Definitions = [child, entry, dependent, unrelated] }); owner.PlacePrefab(entry.Id, LayerPlacement.Identity);
            Guid childDirect = owner.PlacePrefab(child.Id, new(Affine2D.Translation(100, 0), 1));
            string before = Json(owner.Document); int history = owner.UndoCount; var ids = SceneEvaluation.Build(owner.Document).Leaves.Select(leaf => leaf.RuntimeElementId).ToArray();
            var parentDraft = owner.BeginPrefabEdit(entry.Id);
            Record(parentDraft.Session.Document.Definitions.All(definition => definition.Id != entry.Id && definition.Id != dependent.Id) &&
                parentDraft.Session.Document.Definitions.Any(definition => definition.Id == unrelated.Id), "definition draft excludes itself and transitive dependents while retaining available independent resources");
            var childDraft = parentDraft.Session.BeginPrefabEdit(child.Id);
            childDraft.Session.Execute("nested color", document => document.Elements[0].Style.Fill = 0xFFABCDEF);
            parentDraft.Session.ApplyPrefabEdit(parentDraft.Session.PreparePrefabEdit(childDraft));
            Record(Json(owner.Document) == before && owner.UndoCount == history, "publishing a nested draft modifies only its parent draft until the outer context finishes");
            owner.ApplyPrefabEdit(owner.PreparePrefabEdit(parentDraft)); var evaluated = SceneEvaluation.Build(owner.Document);
            Record(evaluated.Leaves.All(leaf => leaf.RenderElement.Style.Fill == 0xFFABCDEF) && evaluated.OccurrencesOfHostNode(childDirect).Count == 1 &&
                ids.SequenceEqual(evaluated.Leaves.Select(leaf => leaf.RuntimeElementId)), "one outer publication merges a child definition once and updates direct and nested shared instances with stable identities");
            Record(owner.UndoCount == history + 1 && owner.Undo() && Json(owner.Document) == before, "nested shared definition edits are one atomic owner undo entry");
            var unchanged = owner.BeginPrefabEdit(entry.Id); before = Json(owner.Document); history = owner.UndoCount;
            owner.ApplyPrefabEdit(owner.PreparePrefabEdit(unchanged));
            Record(before == Json(owner.Document) && owner.UndoCount == history, "an unchanged multi-resource nested draft preserves table order, revisions and history");
        });
        Case("detach appearance", () =>
        {
            var a = Box("A"); a.GroupId = null; a.Locked = true;
            var b = Box("B"); b.Transform = Affine2D.Translation(40, 10); b.Visible = false;
            var c = Box("C"); c.IsBillboard = true; c.BillboardAnchor = new(7, 9); c.BillboardHeight = 12;
            var legacy = new CanvasGroup { Transform = Affine2D.Rotation(27), ElevationScale = 2 }; a.GroupId = c.GroupId = legacy.Id;
            var child = new PrefabDefinition { Graph = new() { Elements = [a, b, c], Groups = [legacy] } };
            var first = new PrefabInstance { DefinitionId = child.Id, Transform = Affine2D.Translation(17, 30), ElevationScale = 3 };
            var second = new PrefabInstance { DefinitionId = child.Id, Transform = Affine2D.Translation(80, -10), ElevationScale = .5, Locked = true };
            var entry = new PrefabDefinition { Graph = new() { Instances = [first, second], RootOrder = [first.Id, second.Id] } };
            var owner = new EditorSession(new() { Definitions = [child, entry] });
            Guid instance = owner.PlacePrefab(entry.Id, new(Affine2D.Translation(200, 100).Compose(Affine2D.Scale(1.5, .75)), 1.5));
            var before = SceneEvaluation.Build(owner.Document).Leaves.Select(leaf => leaf.RenderElement).ToArray(); string definitions = Json(owner.Document.Definitions); int history = owner.UndoCount;
            owner.DetachPrefab(instance); var after = SceneEvaluation.Build(owner.Document).Leaves.Select(leaf => leaf.RenderElement).ToArray();
            Record(owner.Document.Instances.Count == 0 && owner.Document.Groups.Single(group => group.Id == instance).OrderMode == GroupOrderMode.AtomicBlock &&
                after.Length == before.Length && before.Zip(after).All(pair => pair.First.Name == pair.Second.Name && Near(pair.First.Transform, pair.Second.Transform) &&
                    pair.First.BillboardAnchor.DistanceTo(pair.Second.BillboardAnchor) < 1e-7 && Math.Abs(pair.First.BillboardHeight - pair.Second.BillboardHeight) < 1e-7 &&
                    pair.First.Visible == pair.Second.Visible && pair.First.Locked == pair.Second.Locked && Json(pair.First.Style) == Json(pair.Second.Style)),
                "recursive detach preserves legacy draw order, local geometry, mixed billboard elevation and inherited hidden or locked appearance");
            Record(definitions == Json(owner.Document.Definitions) && owner.UndoCount == history + 1 && owner.Undo() && owner.Document.Elements.Count == 0 && owner.Document.Instances.Count == 1,
                "recursive detach leaves definitions unchanged and restores the exact linked representation in one undo step");
            owner.SetNodeLocked(instance, true); string locked = Json(owner.Document); history = owner.UndoCount; bool rejected = false;
            try { owner.DetachPrefab(instance); } catch (InvalidOperationException) { rejected = true; }
            Record(rejected && locked == Json(owner.Document) && history == owner.UndoCount, "locked host instance rejects detach without partial materialization or history");
        });
        Case("capacity and unused assets", () =>
        {
            var library = new AssetLibrary(); string image = library.Add([2, 7, 18]);
            var definitions = new List<PrefabDefinition> { new() { Graph = new() { Elements = [new() { Kind = ElementKind.Image, AssetId = image, Width = 10, Height = 10 }] } } };
            for (int i = 1; i < 64; i++)
            {
                var nested = new PrefabInstance { DefinitionId = definitions[^1].Id };
                definitions.Add(new() { Graph = new() { Instances = [nested], RootOrder = [nested.Id] } });
            }
            var package = new PrefabPackage { EntryDefinitionId = definitions[^1].Id, Definitions = definitions };
            var owner = new EditorSession(); Guid scope = owner.CreatePopulatedGroup(); owner.EnterScope(scope);
            string before = Json(owner.Document); int history = owner.UndoCount; bool rejected = false;
            try { owner.ImportPrefab(package, library, LayerPlacement.Identity); } catch (InvalidDataException) { rejected = true; }
            Record(rejected && Json(owner.Document) == before && owner.UndoCount == history && owner.Assets.Count == 0 && owner.EditScopeId == scope,
                "cumulative depth65 import rejects the complete closure before assets, selection, scope or history publication");
            owner.LeaveScope(); var imported = owner.ImportPrefab(package, library, LayerPlacement.Identity);
            owner.SetSelection([imported.InstanceId]); owner.DeleteSelection(); owner.RemoveUnusedPrefabDefinitions();
            Record(owner.Document.Definitions.Count == 0 && owner.ReferencedAssetsIncludingHistory().Contains(image), "unused definition cleanup keeps encoded bytes held by undo history");
            Record(owner.Undo() && owner.Document.Definitions.Count == 64 && owner.Undo() && owner.Document.Instances.Count == 1,
                "cleanup and deletion undo restore a complete nested closure without reading source assets");
        });
        Case("mutable plan failure", () =>
        {
            var ownerDefinition = new PrefabDefinition { Graph = new() { Elements = [Box("before")] } }; var owner = new EditorSession(new() { Definitions = [ownerDefinition] });
            var metadata = Source(Guid.NewGuid(), default, 'A'); owner.PlacePrefab(ownerDefinition.Id, LayerPlacement.Identity);
            owner.Execute("bind", document => document.Definitions[0].Source = metadata.Clone());
            var assets = new AssetLibrary(); string resource = assets.Add([4, 6, 8]);
            var image = new PrefabDefinition { Graph = new() { Elements = [new() { Kind = ElementKind.Image, AssetId = resource, Width = 10, Height = 10 }] } };
            var package = new PrefabPackage { ResourceId = metadata.SourceResourceId, EntryDefinitionId = image.Id, Definitions = [image] };
            var plan = owner.PreparePrefabRefresh(ownerDefinition.Id, package, assets, metadata);
            owner.BeginTransaction("foreign owner"); owner.Document.NextNumber = 41; string foreign = Json(owner.Document); long transaction = owner.TransactionId;
            bool foreignRejected = false; try { owner.ApplyPrefabEdit(plan); } catch (InvalidOperationException) { foreignRejected = true; }
            Record(foreignRejected && owner.IsTransactionActive && owner.TransactionId == transaction && Json(owner.Document) == foreign && owner.Assets.Count == 0,
                "a refresh plan cannot commit or publish bytes into a later foreign transaction");
            owner.Cancel(); plan = owner.PreparePrefabRefresh(ownerDefinition.Id, package, assets, metadata);
            plan.Candidate.Definitions.Add(new() { Graph = new() { Elements = [new() { Style = null! }] } });
            string before = Json(owner.Document); int history = owner.UndoCount; bool rejected = false;
            try { owner.ApplyPrefabEdit(plan); } catch (Exception) { rejected = true; }
            Record(rejected && Json(owner.Document) == before && history == owner.UndoCount && owner.Assets.Count == 0 && !owner.IsTransactionActive,
                "a malformed caller-mutated plan cannot publish source assets before its complete candidate clone succeeds");
        });
        Case("definition frame history budget", () =>
        {
            var assets = new AssetLibrary(); string image = assets.Add([5, 8, 13]);
            var frame = Enumerable.Range(0, 4096).Select(index => new PointD(.5 + .4 * Math.Cos(index * Math.PI / 2048), .5 + .4 * Math.Sin(index * Math.PI / 2048))).ToList();
            var definition = new PrefabDefinition { Graph = new() { Elements = [new() { Kind = ElementKind.Image, AssetId = image, Width = 20, Height = 20, ImageFrame = frame }] } };
            var owner = new EditorSession(new() { Definitions = [definition] }, assets, historyByteLimit: 16 * 1024);
            owner.PlacePrefab(definition.Id, LayerPlacement.Identity);
            Record(owner.UndoCount == 0 && !owner.CanUndo, "history byte limit counts definition image-frame point lists retained by before and after graph clones");
        });
        foreach (var result in results) check(result.Passed, result.Message);
    }
    private static CanvasElement Box(string name) => new() { Kind = ElementKind.Rectangle, Name = name, Width = 20, Height = 10, Style = new() { Fill = 0xFF225588, StrokeWidth = 0 } };
    private static ExternalPrefabSource Source(Guid resource, PointD origin, char hash) => new()
    {
        SourceResourceId = resource, EntryKind = PrefabSourceKind.ProjectContent, Locator = "C:/audit/source.floatcanvas",
        LastAcceptedFileSha256 = new string(hash, 64), ImportOrigin = origin
    };
}
