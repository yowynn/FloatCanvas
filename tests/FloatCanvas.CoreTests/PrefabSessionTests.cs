using FloatCanvas.Core;
using System.Text.Json;

internal static class PrefabSessionTests
{
    internal static void Run(Action<bool, string> check)
    {
        static string Json(object value) => JsonSerializer.Serialize(value);
        static CanvasElement Box(string name, double x) => new() { Kind = ElementKind.Rectangle, Name = name, Width = 20, Height = 12,
            Transform = Affine2D.Translation(x, 30), Style = new() { Fill = 0xFF225588, StrokeWidth = 0 } };
        static Dictionary<string, RectD> Bounds(IEnumerable<ResolvedLeaf> leaves) => leaves.ToDictionary(leaf => leaf.RenderElement.Name!, leaf => ElementGeometry.Bounds(leaf.RenderElement));
        static bool SameBounds(IReadOnlyDictionary<string, RectD> first, IReadOnlyDictionary<string, RectD> second) => first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out var other) &&
            pair.Value.Center.DistanceTo(other.Center) < 1e-8 && Math.Abs(pair.Value.Width - other.Width) < 1e-8 && Math.Abs(pair.Value.Height - other.Height) < 1e-8);

        var a = Box("a", 50); var b = Box("b", 120);
        var session = new EditorSession(new() { Elements = [a, b] }); session.SetSelection([a.Id, b.Id]);
        var before = Bounds(SceneEvaluation.Build(session.Document).Leaves); var creation = session.CreatePrefabFromSelection("pair");
        check(session.Document.Elements.Count == 0 && session.Document.Instances.Count == 1 && session.Document.Definitions.Count == 1,
            "creating a shared prefab stores the selected source once and replaces it with one real instance");
        check(session.Selection.SetEquals([creation.InstanceId]) && SameBounds(before, Bounds(SceneEvaluation.Build(session.Document).Leaves)),
            "prefab creation preserves selected ink and selects the real instance node");
        check(session.Undo() && session.Document.Elements.Count == 2 && session.Document.Definitions.Count == 0 && session.Redo(),
            "prefab creation is one reversible owner operation");
        session.DuplicateSelection(200, 0);
        var second = session.Selection.Single(); var graph = new SceneGraph(session.Document);
        check(session.Document.Instances.Count == 2 && session.Document.Definitions.Count == 1 && session.Document.Elements.Count == 0 && graph.FindInstance(second)!.DefinitionId == creation.DefinitionId,
            "duplicating an instance shares its existing owner definition instead of copying source leaves");
        var evaluated = SceneEvaluation.Build(session.Document); var identities = evaluated.Leaves.Select(leaf => leaf.RuntimeElementId).ToHashSet();
        check(identities.Count == 4 && evaluated.Leaves.All(leaf => leaf.RuntimeElementId != leaf.SourceElementId),
            "two instances have four unique runtime occurrences independent of their shared source IDs");
        session.SelectAt(new(60, 36));
        check(session.Selection.SetEquals([creation.InstanceId]), "core point selection resolves a virtual leaf to its real instance");
        session.SelectRectangle(new(245, 25, 105, 25));
        check(session.Selection.SetEquals([second]), "core rectangle selection collects one instance without leaking runtime leaf IDs");
        var draft = session.BeginPrefabEdit(creation.DefinitionId); string originalOwner = Json(session.Document); int history = session.UndoCount;
        check(draft.Session.Document.Definitions.All(definition => definition.Id != creation.DefinitionId), "definition editing cannot place the definition inside itself");
        draft.Session.BeginTransaction("live preview"); draft.Session.Document.Elements[0].Width = 37; draft.Session.NotifyChanged();
        var livePreview = session.CreatePrefabPreview(draft);
        check(SceneEvaluation.Build(livePreview).Leaves.Count(leaf => leaf.RenderElement.Width == 37) == 2 && Json(session.Document) == originalOwner && session.UndoCount == history,
            "a live gesture previews every instance without publishing owner content or history");
        draft.Session.Cancel();
        draft.Session.Execute("paint draft", document => document.Elements.ForEach(element => element.Style.Fill = 0xFF22BB44));
        check(Json(session.Document) == originalOwner && session.UndoCount == history, "editing a detached definition draft does not dirty its owner before publication");
        var plan = session.PreparePrefabEdit(draft);
        check(Json(session.Document) == originalOwner && plan.UnsafeRootIds.Count == 0, "preparing an edit is read-only and validates every occurrence");
        session.ApplyPrefabEdit(plan); evaluated = SceneEvaluation.Build(session.Document);
        check(evaluated.Leaves.All(leaf => leaf.RenderElement.Style.Fill == 0xFF22BB44) && identities.SetEquals(evaluated.Leaves.Select(leaf => leaf.RuntimeElementId)),
            "publishing updates every instance while preserving stable occurrence identities");
        check(session.UndoCount == history + 1 && session.Undo() && Json(session.Document) == originalOwner && session.Redo(),
            "definition publication creates one undo step for all instances");
        bool stale = false; try { session.ApplyPrefabEdit(plan); } catch (InvalidOperationException) { stale = true; }
        check(stale, "an edit plan cannot publish twice or overwrite a later history generation");
        var abandoned = session.BeginPrefabEdit(creation.DefinitionId); originalOwner = Json(session.Document);
        abandoned.Session.Execute("discarded geometry", document => document.Elements[0].Width = 123);
        check(Json(session.Document) == originalOwner, "discarding an uncommitted draft leaves the owner's geometry unchanged");

        var detachedBefore = Bounds(SceneEvaluation.Build(session.Document).OccurrencesOfHostNode(creation.InstanceId));
        string definitionBefore = Json(session.Document.Definitions); session.DetachPrefab(creation.InstanceId);
        graph = new(session.Document);
        check(graph.FindGroup(creation.InstanceId) is not null && graph.FindInstance(creation.InstanceId) is null && session.Document.Instances.Count == 1 &&
            SameBounds(detachedBefore, Bounds(SceneEvaluation.Build(session.Document).OccurrencesOfHostNode(creation.InstanceId))),
            "detaching keeps the root identity and ink while materializing an ordinary editable group");
        check(Json(session.Document.Definitions) == definitionBefore && session.Undo() && session.Document.Elements.Count == 0 && session.Document.Instances.Count == 2,
            "detaching neither modifies the shared definition nor loses its original representation in undo");
        session.SetSelection([second]); var originalSource = Json(session.Document.Definitions); var snapshot = session.CaptureSelectionTransform();
        session.BeginTransaction("instance transform"); check(session.TryApplySelectionTransform(snapshot, Affine2D.Translation(17, -9)), "instance transforms use the normal node transaction"); session.Commit();
        check(Json(session.Document.Definitions) == originalSource, "moving an instance keeps all shared source geometry unchanged");
        check(SameBounds(detachedBefore, Bounds(SceneEvaluation.Build(session.Document).OccurrencesOfHostNode(creation.InstanceId))), "moving one instance does not move another occurrence");

        // Definition IDs live in a table; graph node IDs are deliberately scoped to each definition.
        var childLeaf = Box("child", 0); childLeaf.Locked = true; var child = new PrefabDefinition { Name = "child", Graph = new() { Elements = [childLeaf] } };
        var nested = new PrefabInstance { DefinitionId = child.Id, Transform = Affine2D.Translation(30, 0) };
        var entry = new PrefabDefinition { Name = "nested", Graph = new() { Instances = [nested], RootOrder = [nested.Id] } };
        var package = new PrefabPackage { EntryDefinitionId = entry.Id, Definitions = [child, entry] };
        var target = new EditorSession(); var source = new ExternalPrefabSource { Locator = "C:/prefab-tests/source.floatprefab", SourceResourceId = package.ResourceId,
            EntryKind = PrefabSourceKind.PrefabDefinition, EntryDefinitionId = entry.Id, LastAcceptedFileSha256 = new string('A', 64), LastAcceptedContentRevision = entry.Revision };
        var import = target.ImportPrefab(package, new(), new(Affine2D.Translation(300, 0), 1), source);
        target.SelectAt(new(340, 36));
        check(target.Selection.SetEquals([import.InstanceId]), "an unlocked instance remains selectable when all definition members are locked");
        target.SetNodeLocked(import.InstanceId, true); target.SelectRectangle(new(0, 0, 1000, 1000));
        check(target.Selection.Count == 0 && target.Undo(), "an inherited instance lock suppresses canvas selection and remains undoable");
        var unchanged = Json(target.Document); int unchangedHistory = target.UndoCount;
        var noChange = target.BeginPrefabEdit(import.DefinitionId); target.ApplyPrefabEdit(target.PreparePrefabEdit(noChange));
        check(Json(target.Document) == unchanged && target.UndoCount == unchangedHistory, "finishing an unchanged nested definition does not reorder its table or create history");
        var importedIds = target.Document.Definitions.Single(definition => definition.Id == import.DefinitionId).Source!.ImportedDefinitionIds.ToDictionary(pair => pair.Key, pair => pair.Value);
        var stableOccurrences = SceneEvaluation.Build(target.Document).Leaves.Select(leaf => leaf.RuntimeElementId).ToArray();
        var updated = package.Clone(); updated.Definitions.Single(definition => definition.Id == child.Id).Graph.Elements[0].Width = 45;
        var nextSource = source.Clone(); nextSource.LastAcceptedFileSha256 = new string('B', 64);
        originalOwner = Json(target.Document); var refresh = target.PreparePrefabRefresh(import.DefinitionId, updated, new(), nextSource);
        check(Json(target.Document) == originalOwner, "preparing an external refresh changes neither owner nor its accepted snapshot");
        target.ApplyPrefabEdit(refresh);
        check(stableOccurrences.SequenceEqual(SceneEvaluation.Build(target.Document).Leaves.Select(leaf => leaf.RuntimeElementId)) &&
            importedIds.OrderBy(pair => pair.Key).SequenceEqual(target.Document.Definitions.Single(definition => definition.Id == import.DefinitionId).Source!.ImportedDefinitionIds.OrderBy(pair => pair.Key)),
            "refreshing a nested source preserves both imported definition IDs and runtime occurrence identities");
        check(SceneEvaluation.Build(target.Document).Leaves.Single().RenderElement.Width == 45 && target.Undo() && Json(target.Document) == originalOwner,
            "refresh updates nested content and undo restores the accepted snapshot without reading external files");
        target.DetachPrefab(import.InstanceId);
        check(target.Document.Instances.Count == 0 && target.Document.Groups.Count == 2 && target.Document.Elements.Count == 1,
            "detaching a nested prefab recursively removes every instance while preserving ordinary hierarchy");

        var nestedDraft = session.BeginPrefabEdit(creation.DefinitionId); var nestedHistory = session.UndoCount;
        session.PlacePrefab(creation.DefinitionId, new(Affine2D.Translation(600, 0), 1)); stale = false;
        try { session.PreparePrefabEdit(nestedDraft); } catch (InvalidOperationException) { stale = true; }
        check(stale && session.UndoCount == nestedHistory + 1, "an owner edit invalidates a draft instead of overwriting concurrent content");
        nestedDraft.Session.Document.Elements[0].Width = 111;
        string retainedOwner = Json(session.Document); var retainedDraft = session.ExportPrefabDraft(nestedDraft);
        check(retainedDraft.Definitions.Single(definition => definition.Id == retainedDraft.EntryDefinitionId).Graph.Elements[0].Width == 111 &&
            Json(session.Document) == retainedOwner && retainedDraft.ResourceId != session.Document.ResourceId,
            "a conflicting draft can still be exported as a complete independent editable prefab without publishing its owner");
        var selfSource = source.Clone(); selfSource.SourceResourceId = target.Document.ResourceId; selfSource.EntryKind = PrefabSourceKind.ProjectContent;
        originalOwner = Json(target.Document); bool rejected = false;
        try { target.ImportPrefab(package, new(), LayerPlacement.Identity, selfSource); } catch (InvalidDataException) { rejected = true; }
        check(rejected && Json(target.Document) == originalOwner && target.Assets.Count == 0, "a self-linked project is rejected before owner or asset publication");
        string originalName = target.Document.Definitions.Single(definition => definition.Id == import.DefinitionId).Name;
        target.RenamePrefabDefinition(import.DefinitionId, "  renamed definition  ");
        check(target.Document.Definitions.Single(definition => definition.Id == import.DefinitionId).Name == "renamed definition" && target.Undo() &&
            target.Document.Definitions.Single(definition => definition.Id == import.DefinitionId).Name == originalName,
            "renaming a resource uses an available history label and remains independently undoable");

        var sourceAssets = new AssetLibrary(); string validAsset = sourceAssets.Add([1, 2, 3]);
        var imageA = Box("valid asset", 0); imageA.Kind = ElementKind.Image; imageA.AssetId = validAsset;
        var imageB = Box("missing asset", 50); imageB.Kind = ElementKind.Image; imageB.AssetId = new string('0', 64);
        var imageDefinition = new PrefabDefinition { Graph = new() { Elements = [imageA, imageB] } };
        var imagePackage = new PrefabPackage { EntryDefinitionId = imageDefinition.Id, Definitions = [imageDefinition] };
        var assetTarget = new EditorSession(); rejected = false;
        try { assetTarget.ImportPrefab(imagePackage, sourceAssets, LayerPlacement.Identity); } catch (KeyNotFoundException) { rejected = true; }
        check(rejected && assetTarget.Assets.Count == 0 && assetTarget.Document.Definitions.Count == 0 && assetTarget.Document.Instances.Count == 0 && assetTarget.UndoCount == 0,
            "a missing imported resource rejects the whole prefab before publishing earlier valid bytes");

        foreach (bool scoped in new[] { false, true })
        {
            var sourceLeaf = new CanvasElement { Kind = ElementKind.Rectangle, Name = "nested fixture", Width = 73, Height = 37,
                Transform = Affine2D.Translation(-120, 80).Compose(Affine2D.Rotation(21)), Style = new() { StrokeWidth = 3 } };
            var nestedOwner = new EditorSession(new() { Elements = [sourceLeaf] }); nestedOwner.SetSelection([sourceLeaf.Id]);
            var first = nestedOwner.CreatePrefabFromSelection("inner stamp");
            nestedOwner.TransformSelection(Affine2D.Translation(170, 30).Compose(Affine2D.Rotation(-17)));
            Guid? parentId = null;
            if (scoped)
            {
                var parent = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [],
                    Transform = Affine2D.Translation(90, -40).Compose(Affine2D.Rotation(11)).Compose(Affine2D.Scale(1.3, .8)) };
                nestedOwner.Execute("nested stamp scope", document => { var current = new SceneGraph(document); current.InsertGroup(parent, null); current.ReparentPreservingWorld([first.InstanceId], parent.Id); });
                parentId = parent.Id; nestedOwner.EnterScope(parent.Id);
            }
            nestedOwner.SetSelection([first.InstanceId]);
            string nestedBefore = Json(nestedOwner.Document); int wrappedHistory = nestedOwner.UndoCount;
            var worldBefore = Bounds(SceneEvaluation.Build(nestedOwner.Document).OccurrencesOfHostNode(first.InstanceId));
            var outer = nestedOwner.CreatePrefabFromSelection("outer stamp");
            var outerDefinition = nestedOwner.Document.Definitions.Single(d => d.Id == outer.DefinitionId);
            check(outerDefinition.Graph.Instances is [var inner] && inner.DefinitionId == first.DefinitionId && outerDefinition.Graph.Elements.Count == 0,
                "an existing instance becomes a shared nested stamp without flattening source leaves scoped=" + scoped);
            var worldAfter = Bounds(SceneEvaluation.Build(nestedOwner.Document).OccurrencesOfHostNode(outer.InstanceId));
            check(SameBounds(worldBefore, worldAfter) && new SceneGraph(nestedOwner.Document).FindInstance(outer.InstanceId)!.ParentId == parentId,
                "nested stamp creation preserves complete world ink and the current parent scope scoped=" + scoped);
            check(nestedOwner.UndoCount == wrappedHistory + 1 && nestedOwner.Undo() && Json(nestedOwner.Document) == nestedBefore && nestedOwner.Redo(),
                "wrapping a shared stamp is one exact reversible command scoped=" + scoped);
        }
    }
}
