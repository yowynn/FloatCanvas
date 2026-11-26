using FloatCanvas.Core;

internal static class EditorEventIsolationTests
{
    internal static void Run(Action<bool, string> check)
    {
        var element = new CanvasElement { Kind = ElementKind.Rectangle, Width = 10, Height = 10 };
        var session = new EditorSession(new SceneDocument { Elements = [element] });
        int changed = 0, committed = 0;
        session.Changed += (_, _) => throw new InvalidOperationException("observer failed");
        session.Changed += (_, _) => changed++;
        session.ContentCommitted += (_, _) => throw new InvalidOperationException("commit observer failed");
        session.ContentCommitted += (_, _) => committed++;

        bool completed = true;
        try { session.Execute("isolated observer", document => document.Elements[0].Width = 20); }
        catch { completed = false; }
        check(completed && session.Document.Elements[0].Width == 20 && session.UndoCount == 1 &&
            changed == 1 && committed == 1,
            "subscriber failures do not turn a committed editor transaction into a caller-visible failure");

        bool undone = session.Undo();
        check(undone && session.Document.Elements[0].Width == 10 && changed == 2,
            "subscriber failures do not interrupt undo or block later observers");
    }
}
