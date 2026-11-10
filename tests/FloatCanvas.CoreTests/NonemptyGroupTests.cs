using FloatCanvas.Core;
using System.Text.Json;

internal static class NonemptyGroupTests
{
    internal static void Run(Action<bool, string> check)
    {
        var session = new EditorSession();
        check(session.GroupSelection() is null && session.UndoCount == 0, "empty selection cannot create a group or history");
        var leaf = new CanvasElement { Kind = ElementKind.Rectangle, Width = 30, Height = 20 };
        session.Add(leaf); Guid inner = session.GroupSelection("Inner")!.Value;
        Guid outer = session.GroupSelection("Outer")!.Value;
        check(session.Document.Groups.Count == 2 && session.Document.Elements.Count == 1, "one actual layer can form nested groups");
        session.SetEditScope(inner); session.SetSelection([leaf.Id]);
        string before = JsonSerializer.Serialize(session.Document); int history = session.UndoCount;
        session.DeleteSelection();
        check(session.Document.Elements.Count == 0 && session.Document.Groups.Count == 0 && session.EditScopeId is null && session.Selection.Count == 0,
            "deleting the last descendant dissolves the empty chain and normalizes the active scope");
        check(session.UndoCount == history + 1 && session.Undo() && JsonSerializer.Serialize(session.Document) == before,
            "a single undo restores the exact content, groups and paint slots");
        check(session.Redo() && session.Document.Groups.Count == 0, "redo also removes empty ancestors");

        session = new EditorSession(); session.Add(new() { Kind = ElementKind.Rectangle, Width = 10, Height = 10 });
        Guid survivor = session.GroupSelection("Surviving parent")!.Value;
        session.SetEditScope(survivor);
        session.Add(new() { Kind = ElementKind.Rectangle, Width = 20, Height = 20 });
        inner = session.GroupSelection("Last child scope")!.Value; session.SetEditScope(inner);
        session.SetSelection(session.Document.Elements.Where(e => e.GroupId == inner).Select(e => e.Id));
        session.DeleteSelection();
        check(session.EditScopeId == survivor && session.Document.Groups.Count == 1 && session.Document.Elements.Count == 1,
            "removing the last child returns to the nearest populated ancestor without touching its sibling");
        session.SetSelection([survivor]); var canonical = session.CreateCommittedSnapshot();
        session.Execute("temporary empty container", document => new SceneGraph(document).InsertGroup(new() { OrderMode = GroupOrderMode.AtomicBlock }, survivor));
        check(JsonSerializer.Serialize(canonical) == JsonSerializer.Serialize(session.Document), "an empty container cannot survive even a direct committed edit");

        session = new EditorSession(); session.Add(leaf.Clone()); inner = session.GroupSelection("Source")!.Value;
        Guid destination = session.CreatePopulatedGroup("Destination");
        var child = session.Document.Elements.First(e => e.GroupId == inner);
        session.MoveNodes([child.Id], destination);
        check(session.Document.Groups.All(g => g.Id != inner) && new SceneGraph(session.Document).ParentId(child.Id) == destination,
            "moving the last child removes only its empty source group");
        check(session.Undo() && session.Document.Groups.Any(g => g.Id == inner), "move and dissolution share one undo step");

        // A shared instance counts as content, independently of its saved resource.
        session.SetSelection([inner]); var definition = session.CreatePrefabFromSelection("Shared");
        var instance = session.Document.Instances.Single(); session.SetSelection([instance.Id]);
        Guid host = session.GroupSelection("Instance host")!.Value;
        check(session.Document.Groups.Any(g => g.Id == host), "shared instances retain their ordinary host group");
        session.SetSelection([instance.Id]); session.DeleteSelection();
        check(session.Document.Groups.All(g => g.Id != host) && session.Document.Definitions.Any(d => d.Id == definition.DefinitionId),
            "deleting the last instance dissolves its host while retaining the resource");

        var stale = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] };
        var legacy = new SceneDocument { Groups = [stale], RootOrder = [stale.Id] };
        legacy.RootCanvasAssignments[stale.Id] = RootCanvasKind.Foreground;
        var reopened = new EditorSession(legacy);
        check(reopened.Document.Groups.Count == 0 && reopened.Document.RootOrder!.Count == 0 && reopened.Document.RootCanvasAssignments.Count == 0,
            "opening a legacy empty group removes stale paint slots and root assignments");
        SceneGraph.Validate(reopened.Document);

        session = new EditorSession(); session.Add(new() { Kind = ElementKind.Rectangle, Width = 10, Height = 10 });
        inner = session.GroupSelection()!.Value; session.SetNodeLocked(inner, true); session.SetEditScope(inner);
        session.SetSelection(session.Document.Elements.Select(e => e.Id)); history = session.UndoCount;
        try { session.DeleteSelection(); } catch (InvalidOperationException) { }
        check(session.Document.Groups.Any(g => g.Id == inner) && session.UndoCount == history, "locked content cannot be deleted to dissolve its group");
    }
}
