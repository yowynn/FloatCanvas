using FloatCanvas.Core;
using System.Text.Json;

internal static class RootCanvasTests
{
    internal static void Run(Action<bool, string> check)
    {
        static CanvasElement Box(string name, double x = 20) => new() { Kind = ElementKind.Rectangle, Name = name, Width = 40, Height = 25,
            Transform = Affine2D.Translation(x, 30), Style = new() { Fill = 0xFF225588 } };
        static string Json(object value) => JsonSerializer.Serialize(value);
        var owner = new EditorSession(); var main = Box("main"); owner.Add(main);
        int history = owner.UndoCount; long content = owner.ContentVersion, scopeVersion = owner.EditScopeVersion;
        string original = Json(owner.Document);
        owner.SetRootCanvas(RootCanvasKind.Background);
        check(owner.ActiveRootCanvas == RootCanvasKind.Background && owner.EditScopeId is null && owner.Selection.Count == 0 &&
            owner.UndoCount == history && owner.ContentVersion == content && Json(owner.Document) == original && owner.EditScopeVersion > scopeVersion,
            "changing a root is navigation only and invalidates pending scope work");
        var sky = Box("sky"); owner.Add(sky);
        owner.SetRootCanvas(RootCanvasKind.Foreground); var front = Box("front"); owner.Add(front);
        var front2 = Box("front2", 80); owner.Add(front2);
        owner.SelectAll();
        check(owner.Selection.SetEquals([front.Id, front2.Id]), "SelectAll is limited to the active root");
        owner.SelectAt(new(30, 40));
        check(owner.Selection.SetEquals([front.Id]), "overlapping other roots never capture active-root hit testing");
        owner.SelectRectangle(new(0, 0, 200, 100));
        check(owner.Selection.SetEquals([front.Id, front2.Id]), "marquee selects only the active root");
        var evaluated = SceneEvaluation.Build(owner.Document);
        check(evaluated.Leaves.Select(leaf => leaf.RenderElement.Name).SequenceEqual(["sky", "main", "front", "front2"]),
            "runtime roots compose background/main/foreground with stable ordering inside each root");
        check(evaluated.Leaves.All(leaf => evaluated.RootCanvasOf(leaf.RuntimeElementId) == leaf.RootCanvas &&
            SceneCanvasRoots.Resolve(evaluated.RenderDocument, leaf.RuntimeElementId) == leaf.RootCanvas), "runtime leaf identities retain root metadata");
        check(owner.Document.Elements.Select(e => e.Name).SequenceEqual(["main", "sky", "front", "front2"]), "evaluation never reorders the authored entity table");

        owner.SetSelection([front.Id, front2.Id]); string beforeGroup = Json(owner.Document);
        Guid group = owner.GroupSelection("front group")!.Value;
        check(owner.Document.RootCanvasAssignments.ContainsKey(group) && !owner.Document.RootCanvasAssignments.ContainsKey(front.Id) &&
            SceneCanvasRoots.Resolve(owner.Document, front.Id) == RootCanvasKind.Foreground, "grouping transfers root ownership to one top-level group");
        check(owner.Undo() && Json(owner.Document) == beforeGroup && owner.Redo(), "group root assignments undo and redo with exact ownership");
        owner.SetEditScope(group); Guid empty = owner.CreateEmptyGroup("empty");
        check(SceneCanvasRoots.Resolve(owner.Document, empty) == RootCanvasKind.Foreground && !owner.Document.RootCanvasAssignments.ContainsKey(empty),
            "new nested empty groups inherit the top-level root without redundant assignments");
        owner.SetEditScope(null); owner.SetSelection([group]);
        var snapshot = owner.CreateSelectionSnapshot(normalizeOrigin: false);
        check(SceneCanvasRoots.Resolve(snapshot, group) == RootCanvasKind.Foreground, "selection snapshots retain their source root for raster consumers");
        var clone = snapshot.Clone(newIds: true);
        check(clone.RootCanvasAssignments.Count == 1 && !clone.RootCanvasAssignments.ContainsKey(group) &&
            clone.RootCanvasAssignments.Keys.All(id => new SceneGraph(clone).ParentId(id) is null), "new-ID cloning remaps root keys without losing empty groups");
        owner.DuplicateSelection(); Guid duplicate = owner.Selection.Single();
        check(SceneCanvasRoots.Resolve(owner.Document, duplicate) == RootCanvasKind.Foreground && owner.Document.Groups.Any(g => g.Name == "empty" && g.ParentId == duplicate),
            "duplicate preserves the destination root and the nested empty group");
        owner.SetSelection([group]); owner.UngroupSelection();
        check(!owner.Document.RootCanvasAssignments.ContainsKey(group) && new[] { front.Id, front2.Id, empty }.All(id =>
            owner.Document.RootCanvasAssignments.GetValueOrDefault(id) == RootCanvasKind.Foreground), "ungrouping propagates the root to each new top-level child");
        check(owner.Undo() && SceneCanvasRoots.Resolve(owner.Document, front.Id) == RootCanvasKind.Foreground, "ungroup Undo restores one inherited root");
        owner.SetSelection([duplicate]); owner.DeleteSelection();
        check(!owner.Document.RootCanvasAssignments.ContainsKey(duplicate) && owner.Undo() && owner.Document.RootCanvasAssignments.ContainsKey(duplicate),
            "delete cleans root keys and Undo restores them");

        owner.SetSelection([group]); var shared = owner.CreatePrefabFromSelection("shared front");
        check(SceneCanvasRoots.Resolve(owner.Document, shared.InstanceId) == RootCanvasKind.Foreground, "a new shared instance keeps the selected content's root");
        owner.SetRootCanvas(RootCanvasKind.Background); Guid backInstance = owner.PlacePrefab(shared.DefinitionId, LayerPlacement.Identity);
        var sharedView = SceneEvaluation.Build(owner.Document);
        check(sharedView.OccurrencesOfHostNode(backInstance).All(leaf => leaf.RootCanvas == RootCanvasKind.Background) &&
            sharedView.OccurrencesOfHostNode(shared.InstanceId).All(leaf => leaf.RootCanvas == RootCanvasKind.Foreground),
            "one shared definition can occur in different roots without sharing a host mapping");
        owner.DetachPrefab(backInstance);
        check(SceneCanvasRoots.Resolve(owner.Document, backInstance) == RootCanvasKind.Background && new SceneGraph(owner.Document).FindGroup(backInstance) is not null,
            "detaching a shared instance retains the top-level root");
        owner.InsertDocument(snapshot, owner.Assets, Affine2D.Translation(10, 15));
        check(owner.Selection.All(id => SceneCanvasRoots.Resolve(owner.Document, id) == RootCanvasKind.Background), "imported local content inherits the target root");
        var package = owner.ExportPrefab(shared.DefinitionId); var foreign = new EditorSession(); foreign.SetRootCanvas(RootCanvasKind.Foreground);
        var imported = foreign.ImportPrefab(package, owner.Assets, LayerPlacement.Identity);
        check(SceneCanvasRoots.Resolve(foreign.Document, imported.InstanceId) == RootCanvasKind.Foreground, "prefab package import inherits the active root");

        var local = new EditorSession(); var one = Box("one"); local.Add(one); local.SetRootCanvas(RootCanvasKind.Foreground);
        var separator = Box("separator"); local.Add(separator); local.SetRootCanvas(RootCanvasKind.Main); var two = Box("two"); local.Add(two);
        local.SetSelection([one.Id]); local.ReorderSelection(LayerOrder.Forward);
        check(SceneEvaluation.Build(local.Document).Leaves.Where(leaf => leaf.RootCanvas == RootCanvasKind.Main).Select(leaf => leaf.RenderElement.Id).SequenceEqual([two.Id, one.Id]),
            "one reorder step skips other roots instead of consuming an invisible move");
        local.MoveNodesToRoot([one.Id], RootCanvasKind.Foreground);
        check(SceneCanvasRoots.Resolve(local.Document, one.Id) == RootCanvasKind.Foreground && local.Undo() &&
            SceneCanvasRoots.Resolve(local.Document, one.Id) == RootCanvasKind.Main, "explicit cross-root logical transfer is atomic and undoable");
        string beforeInvalid = Json(local.Document); bool rejected = false;
        try { local.MoveNodesToRoot([one.Id], (RootCanvasKind)999); } catch (ArgumentOutOfRangeException) { rejected = true; }
        check(rejected && Json(local.Document) == beforeInvalid, "invalid destination roots do not mutate the source");

        owner.Document.Canvas.Grid.FlatTranslation = new(77, -33);
        check(CanvasMapping.Create(owner.Document).ToScreen(new(10, 20)) == new PointD(87, -13) &&
            CanvasMapping.Create(owner.Document, RootCanvasKind.Foreground).ToScreen(new(10, 20)) == new PointD(10, 20) &&
            CanvasMapping.Create(owner.Document, RootCanvasKind.Background).ToScreen(new(10, 20)) == new PointD(10, 20),
            "auxiliary roots ignore the main canvas's flat translation");
        owner.Document.Canvas.Projection = CanvasCalibration.CreateInitial(owner.Document.Viewport);
        owner.Document.Canvas.Projection.Enabled = true;
        check(CanvasMapping.Create(owner.Document, RootCanvasKind.Foreground).PlaneToScreen == Projective2D.Identity,
            "auxiliary roots remain identity when the main perspective is enabled");
        var placed = CanvasMapping.CreateView(owner.Document, new(Affine2D.Translation(5, 9), 1), RootCanvasKind.Background);
        check(placed.ToScreen(new(10, 20)) == new PointD(15, 29), "root engineering views apply only their instance placement for auxiliary roots");
        foreach (var invalid in new[] { new KeyValuePair<Guid, RootCanvasKind>(Guid.NewGuid(), RootCanvasKind.Foreground),
            new KeyValuePair<Guid, RootCanvasKind>(main.Id, RootCanvasKind.Main), new KeyValuePair<Guid, RootCanvasKind>(main.Id, (RootCanvasKind)99) })
        {
            var bad = owner.Document.Clone(); bad.RootCanvasAssignments[invalid.Key] = invalid.Value; rejected = false;
            try { SceneCanvasRoots.Validate(bad); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "root validation rejects missing, redundant and unknown assignments");
        }
        var badChild = owner.Document.Clone(); var child = badChild.Elements.First(e => e.GroupId is not null);
        badChild.RootCanvasAssignments[child.Id] = RootCanvasKind.Foreground; rejected = false;
        try { SceneCanvasRoots.Validate(badChild); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "a descendant cannot acquire a conflicting root assignment");
        var runtime = SceneEvaluation.Build(local.Document).RenderDocument; var draft = Box("draft");
        var transient = SceneEvaluation.WithTransientElement(runtime, draft, runtime.Elements.Count, RootCanvasKind.Background);
        check(transient.Elements[0].Id == draft.Id && SceneEvaluation.RuntimeView(transient)!.RootCanvasOf(draft.Id) == RootCanvasKind.Background,
            "transient draft insertion keeps its root and fixed composite order");
        local.SetRootCanvas(RootCanvasKind.Foreground); string beforeClear = Json(local.Document); int undoClear = local.UndoCount;
        check(local.ClearRootCanvas("clear foreground") && local.Document.Elements.All(element => element.Id != separator.Id) &&
            local.Document.Elements.Any(element => element.Id == one.Id) && local.Document.Elements.Any(element => element.Id == two.Id) && local.UndoCount == undoClear + 1,
            "clearing the active root preserves the other roots and creates one undo entry");
        check(local.Undo() && Json(local.Document) == beforeClear, "one Undo restores cleared content and its root assignment exactly");
        var historyOwner = new EditorSession(); historyOwner.SetRootCanvas(RootCanvasKind.Foreground);
        historyOwner.Add(Box("old front")); historyOwner.Add(Box("new front")); historyOwner.SetRootCanvas(RootCanvasKind.Main);
        check(historyOwner.Undo() && historyOwner.Selection.Count == 0 && historyOwner.ActiveRootCanvas == RootCanvasKind.Main &&
            historyOwner.Redo() && historyOwner.Selection.Count == 0, "undo from another active root restores content without selecting off-root history nodes");
    }
}
