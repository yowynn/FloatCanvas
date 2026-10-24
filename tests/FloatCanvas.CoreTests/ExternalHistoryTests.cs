using FloatCanvas.Core;

internal static class ExternalHistoryTests
{
    public static void Run(Action<bool, string> check)
    {
        MixedOrder(check);
        FailureAtomicity(check);
        LimitsAndTransactions(check);
    }

    private static void MixedOrder(Action<bool, string> check)
    {
        var session = new EditorSession();
        int resource = 0, notifications = 0;
        var element = new CanvasElement { Kind = ElementKind.Rectangle, Width = 20, Height = 30 };
        session.Add(element, "draw");
        var document = session.Document;
        var selection = session.Selection.ToArray();
        long version = session.ContentVersion, revision = session.SceneRevision;
        session.Changed += (_, _) => notifications++;
        session.ExecuteExternal("save preset", () => resource = 1, () => resource = 0, () => resource = 1, 50);
        check(resource == 1 && session.UndoCount == 2 && session.UndoLabel == "save preset", "External resource uses the same history queue and label");
        check(ReferenceEquals(document, session.Document) && session.ContentVersion == version && session.SceneRevision == revision && !session.HasPendingChanges && session.Selection.SetEquals(selection), "Saving a resource does not clone or dirty the scene or alter selection");
        check(notifications == 1, "Saving a resource notifies command eligibility");
        session.Execute("change canvas", d => d.LayerOpacity = .5);
        check(session.Undo() && session.Document.LayerOpacity == 1 && resource == 1, "Mixed undo first restores the later canvas operation");
        document = session.Document; version = session.ContentVersion; revision = session.SceneRevision;
        check(session.Undo() && resource == 0 && session.UndoLabel == "draw", "Mixed undo next restores the resource");
        check(ReferenceEquals(document, session.Document) && session.ContentVersion == version && session.SceneRevision == revision && !session.HasPendingChanges && session.Selection.SetEquals(selection), "Resource undo leaves scene identity, versions and selection unchanged");
        check(session.Undo() && session.Document.Elements.Count == 0, "Mixed undo reaches the original drawing");
        check(session.Redo() && session.Document.Elements.Count == 1 && resource == 0, "Mixed redo first restores drawing");
        document = session.Document; version = session.ContentVersion; revision = session.SceneRevision;
        check(session.Redo() && resource == 1 && ReferenceEquals(document, session.Document) && session.ContentVersion == version && session.SceneRevision == revision, "Resource redo preserves scene identity and versions");
        check(session.Redo() && session.Document.LayerOpacity == .5 && resource == 1, "Mixed redo restores canvas operation in chronological order");
        session.Undo();
        session.ExecuteExternal("rename preset", () => resource = 2, () => resource = 1, () => resource = 2);
        check(!session.CanRedo && session.UndoLabel == "rename preset", "New resource edit clears the shared redo branch");
        session.ReplaceDocument(new());
        check(!session.CanUndo && !session.CanRedo, "Replacing a document clears resource entries with scene history");
    }

    private static void FailureAtomicity(Action<bool, string> check)
    {
        var session = new EditorSession();
        int resource = 0;
        bool failUndo = true, failRedo = false;
        session.ExecuteExternal("save", () => resource = 1,
            () => { if (failUndo) throw new IOException("write failed"); resource = 0; },
            () => { if (failRedo) throw new IOException("write failed"); resource = 1; });
        try { session.Undo(); } catch (IOException) { }
        check(resource == 1 && session.UndoCount == 1 && session.RedoCount == 0 && session.UndoLabel == "save", "Failed resource undo retains its entry for retry");
        failUndo = false; session.Undo(); failRedo = true;
        try { session.Redo(); } catch (IOException) { }
        check(resource == 0 && session.UndoCount == 0 && session.RedoCount == 1 && session.RedoLabel == "save", "Failed resource redo retains its entry for retry");
        int notifications = 0; session.Changed += (_, _) => notifications++;
        try { session.ExecuteExternal("failed new save", () => throw new IOException("write failed"), () => resource = 0, () => resource = 2); } catch (IOException) { }
        check(session.UndoCount == 0 && session.RedoCount == 1 && session.RedoLabel == "save" && notifications == 0, "Failed resource apply preserves redo and emits no successful history notification");
        failRedo = false;
        check(session.Redo() && resource == 1 && session.UndoCount == 1, "Resource retry succeeds without losing its history entry");
    }

    private static void LimitsAndTransactions(Action<bool, string> check)
    {
        int resource = 0;
        var limited = new EditorSession(historyLimit: 2, historyByteLimit: 200);
        for (int i = 1; i <= 3; i++)
        {
            int before = i - 1, after = i;
            limited.ExecuteExternal("save " + i, () => resource = after, () => resource = before, () => resource = after, 50);
        }
        check(limited.UndoCount == 2 && limited.HistoryBytes == 100, "Resources share the existing history count and byte budget");
        limited.Undo(); limited.Undo();
        check(resource == 1 && !limited.CanUndo && limited.RedoCount == 2 && limited.HistoryBytes == 100, "Count eviction removes the oldest resource operation only");
        var tiny = new EditorSession(historyByteLimit: 10);
        tiny.ExecuteExternal("large preset", () => resource = 4, () => resource = 1, () => resource = 4, 11);
        check(resource == 4 && !tiny.CanUndo && tiny.HistoryBytes == 0, "Over-budget resource applies but is not retained in history");
        var session = new EditorSession(); session.BeginTransaction("drawing");
        bool applied = false, rejected = false;
        try { session.ExecuteExternal("save", () => applied = true, () => { }, () => { }); } catch (InvalidOperationException) { rejected = true; }
        check(rejected && !applied && session.IsTransactionActive && session.UndoCount == 0, "Resource operation rejects an unfinished gesture before applying");
        session.Cancel();
        rejected = false;
        try { session.ExecuteExternal("negative budget", () => applied = true, () => { }, () => { }, -1); } catch (ArgumentOutOfRangeException) { rejected = true; }
        check(rejected && !applied && !session.CanUndo, "Invalid resource byte estimate is rejected before applying");
        string asset = session.Assets.Add([1, 2, 3]);
        var image = new CanvasElement { Kind = ElementKind.Image, AssetId = asset, Width = 20, Height = 20 };
        session.Add(image); session.DeleteSelection();
        session.ExecuteExternal("save resource", () => resource = 5, () => resource = 4, () => resource = 5, 7);
        check(session.ReferencedAssetsIncludingHistory().Contains(asset), "Resource entries do not discard assets retained by scene history");
    }
}
