using FloatCanvas.Core;

internal static class SceneGraphReadTests
{
    internal static void Run(Action<bool, string> check)
    {
        var group = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] };
        var leaf = new CanvasElement { GroupId = group.Id, Kind = ElementKind.Rectangle, Width = 10, Height = 10 };
        group.ChildOrder.Add(leaf.Id);
        var session = new EditorSession(new() { Groups = [group], Elements = [leaf], RootOrder = [group.Id] });
        var document = session.Document;
        SceneGraph first;
        using (SceneGraph.BeginReadScope())
        {
            first = SceneGraph.Read(document);
            check(ReferenceEquals(first, SceneGraph.Read(document)), "one synchronous read reuses its scene index");
            using (SceneGraph.BeginReadScope())
                check(ReferenceEquals(first, SceneGraph.Read(document)), "nested UI refresh shares the read index");
            check(ReferenceEquals(first, SceneGraph.Read(document)), "nested disposal retains outer read index");
            _ = first.IsLocked(leaf.Id); _ = first.NodeWorldPlacement(leaf.Id);
            session.Execute("Lock parent", scene => scene.Groups[0].Locked = true);
            var after = SceneGraph.Read(session.Document);
            check(!ReferenceEquals(first, after) && after.IsLocked(leaf.Id), "command invalidates cached inherited permissions");
            session.Undo();
            check(!SceneGraph.Read(session.Document).IsLocked(leaf.Id), "undo reads restored document rather than stale index");
            session.Document.Groups[0].Transform = Affine2D.Translation(30, 40); session.NotifyChanged();
            check(SceneGraph.Read(session.Document).NodeWorldPlacement(leaf.Id).PlaneTransform.OffsetX == 30,
                "notified in-place gesture invalidates placement cache");
        }
        check(!ReferenceEquals(first, SceneGraph.Read(document)), "read indexes are released between dispatcher turns");
        var fresh = SceneGraph.Read(document);
        document.Groups[0].Locked = false;
        check(!ReferenceEquals(fresh, SceneGraph.Read(document)) && !SceneGraph.Read(document).IsLocked(leaf.Id),
            "unscoped DTO queries retain their live-read behavior");
        session.SetEditScope(group.Id); session.Document.Groups[0].Locked = true;
        check(!session.CanInsertInEditScope && session.EditScopePath.Count == 1, "scope-only permission lookup reflects direct group changes");
        session.ReplaceDocument(new());
        check(session.EditScopePath.Count == 0 && session.CanInsertInEditScope, "replacement clears scope-only permission state");
        using (SceneGraph.BeginReadScope())
        {
            _ = SceneGraph.Read(session.Document);
            var other = Task.Run(() => SceneGraph.Read(session.Document)).GetAwaiter().GetResult();
            check(!ReferenceEquals(other, SceneGraph.Read(session.Document)), "worker threads do not share UI read caches");
        }
    }
}
