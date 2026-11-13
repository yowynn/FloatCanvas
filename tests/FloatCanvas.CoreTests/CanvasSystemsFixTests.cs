using FloatCanvas.Core;

internal static class CanvasSystemsFixTests
{
    internal static void Run(Action<bool, string> check)
    {
        var source = new CanvasElement { Width = 10, Height = 20, Points = [new(1, 2)], ImageFrame = [new(3, 4)], Effects = [new() { Amount = 2 }] };
        source.Style.AdditionalColors["custom"] = 0xFF112233;
        var copy = source.Clone(); copy.Points[0] = new(8, 9); copy.ImageFrame![0] = new(9, 8); copy.Effects[0].Amount = 7;
        copy.Style.AdditionalColors["custom"] = 0;
        check(copy.Id == source.Id && source.Points[0] == new PointD(1, 2) && source.ImageFrame[0] == new PointD(3, 4) && source.Effects[0].Amount == 2 && source.Style.AdditionalColors["custom"] != 0,
            "optimized snapshot preserves identity and detaches mutable geometry, effects and styles");
        copy = source.Clone(true);
        check(copy.Id != source.Id && copy.Effects[0].Id != source.Effects[0].Id, "new-node cloning still remaps element and effect identity");
        var parent = new CanvasGroup { Name = "Parent", OrderMode = GroupOrderMode.AtomicBlock };
        var left = new CanvasGroup { ParentId = parent.Id, OrderMode = GroupOrderMode.AtomicBlock };
        var right = new CanvasGroup { ParentId = parent.Id, OrderMode = GroupOrderMode.AtomicBlock };
        var a = new CanvasElement { GroupId = left.Id, Width = 20, Height = 30, Transform = Affine2D.Translation(10, 20) };
        var b = new CanvasElement { GroupId = right.Id, Width = 40, Height = 50, Transform = Affine2D.Translation(60, 70) };
        left.ChildOrder = [a.Id]; right.ChildOrder = [b.Id]; parent.ChildOrder = [left.Id, right.Id];
        var session = new EditorSession(new() { Groups = [parent, left, right], Elements = [a, b], RootOrder = [parent.Id] });
        session.SetSelection([a.Id, b.Id]);
        var grouped = session.GroupSelection("Combined")!.Value;
        var graph = new SceneGraph(session.Document);
        check(graph.ParentId(grouped) == parent.Id, "cross-level grouping belongs to the nearest common parent, not the displayed root");
        check(graph.FindGroup(grouped)!.ChildOrder!.SequenceEqual([a.Id, b.Id]), "grouping keeps relative drawing order");
        check(session.UndoCount == 1 && session.Selection.SetEquals([grouped]), "cross-level grouping is one undo and selects its result");
        session.Undo(); graph = new(session.Document);
        check(graph.ParentId(a.Id) == left.Id && graph.ParentId(b.Id) == right.Id && session.Selection.SetEquals([a.Id, b.Id]), "group undo restores ownership and selection");
        var move = session.AnalyzeLayerOperation(LayerOperation.Move);
        check(move.MoveTargetReason(RootCanvasKind.Main, null) is null, "displaying root does not prohibit moving nested selections to root");
        check(move.MoveTargetReason(RootCanvasKind.Main, parent.Id) is null, "common ancestor remains a valid move destination");
        var rootElement = new CanvasElement { Width = 20, Height = 20 };
        var locked = new CanvasElement { Width = 20, Height = 20, Locked = true };
        session = new(new() { Elements = [rootElement, locked] }); session.SetSelection([rootElement.Id, locked.Id]);
        check(session.AnalyzeLayerOperation(LayerOperation.Duplicate).Eligible.Count == 2, "locked source remains eligible for duplication");
        check(session.AnalyzeLayerOperation(LayerOperation.Hide).Eligible.Count == 2, "own lock does not prevent visibility changes");
        session.DeleteSelection();
        check(session.Document.Elements.Count == 1 && session.Selection.SetEquals([locked.Id]), "delete retains skipped locked selection");
        session.Undo();
        var destination = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock };
        var content = new CanvasElement { GroupId = destination.Id, Width = 10, Height = 10 }; destination.ChildOrder = [content.Id];
        session.Document.Groups.Add(destination); session.Document.Elements.Add(content);
        session.Document.RootOrder = [rootElement.Id, locked.Id, destination.Id]; session.NotifyChanged();
        session.MoveNodes([rootElement.Id, locked.Id], destination.Id);
        check(new SceneGraph(session.Document).ParentId(rootElement.Id) == destination.Id &&
            new SceneGraph(session.Document).ParentId(locked.Id) is null && session.Selection.SetEquals([rootElement.Id, locked.Id]),
            "drag and move-out qualification retains skipped locked selection");
        session.Undo();
        check(new SceneGraph(session.Document).ParentId(rootElement.Id) is null && session.Selection.SetEquals([rootElement.Id, locked.Id]),
            "mixed permission move undo restores source selection");
        session = new(new() { Elements = [new() { Width = 20, Height = 20 }, new() { Width = 30, Height = 30 }] });
        var first = session.Document.Elements[0].Id; var second = session.Document.Elements[1].Id;
        session.SetSelection([first]);
        var plan = session.PrepareLayerMove([first], RootCanvasKind.Foreground, null);
        check(SceneCanvasRoots.Resolve(session.Document, first) == RootCanvasKind.Main && session.UndoCount == 0, "preparing a move never changes live content or history");
        session.SetSelection([second]); bool stale = false;
        try { session.CommitLayerMove(plan); } catch (InvalidOperationException) { stale = true; }
        check(stale && session.UndoCount == 0 && SceneCanvasRoots.Resolve(session.Document, first) == RootCanvasKind.Main, "stale move selection rejects without partial changes");
        session.SetSelection([first]); var result = session.CommitLayerMove(session.PrepareLayerMove([first], RootCanvasKind.Foreground, null));
        check(result.Moved.SequenceEqual([first]) && session.Selection.Count == 0 && session.UndoCount == 1, "prepared move commits exactly its result with one history entry");
        session.Undo();
        check(SceneCanvasRoots.Resolve(session.Document, first) == RootCanvasKind.Main && session.Selection.SetEquals([first]), "prepared move undo restores root and selection");
        var container = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock };
        var nested = new CanvasElement { GroupId = container.Id, Width = 20, Height = 20 };
        var stationary = new CanvasElement { Width = 20, Height = 20 }; var already = new CanvasElement { Width = 20, Height = 20 };
        container.ChildOrder = [nested.Id];
        session = new(new() { Elements = [nested, stationary, already], Groups = [container], RootOrder = [container.Id, stationary.Id, already.Id] });
        session.SetSelection([already.Id, nested.Id]);
        result = session.CommitLayerMove(session.PrepareLayerMove(session.Selection, RootCanvasKind.Main, null));
        check(session.Document.RootOrder!.SequenceEqual([stationary.Id, nested.Id, already.Id]) && result.Moved.Count == 2,
            "partially at destination selection forms one ordered block at top");
        check(session.Document.Groups.Count == 0 && session.UndoCount == 1, "move atomically prunes the empty source group");
        int history = session.UndoCount;
        result = session.CommitLayerMove(session.PrepareLayerMove([nested.Id, already.Id], RootCanvasKind.Main, null));
        check(result.Moved.Count == 0 && result.Unchanged.Count == 2 && session.UndoCount == history, "all already at destination is a no-history no-op");
        session.Undo();
        check(session.Document.Groups.Count == 1 && new SceneGraph(session.Document).ParentId(nested.Id) == container.Id && session.Selection.SetEquals([already.Id, nested.Id]),
            "partial destination move undo restores both source ownership and selection");
    }
}
