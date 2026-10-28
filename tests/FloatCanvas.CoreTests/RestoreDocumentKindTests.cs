using FloatCanvas.Core;

internal static class RestoreDocumentKindTests
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (var kind in Enum.GetValues<DocumentKind>())
        {
            var session = new EditorSession(new SceneDocument { Kind = kind });
            session.Add(new() { Kind = ElementKind.Rectangle, Width = 20, Height = 10 });
            session.SelectAll();
            var before = session.Document;
            int undo = session.UndoCount;
            long version = session.ContentVersion;
            bool rejected = false;
            try { session.RestoreDocument("external type replacement", new() { Kind = kind == DocumentKind.LiveOverlay ? DocumentKind.FrozenCapture : DocumentKind.LiveOverlay }); }
            catch (ArgumentException) { rejected = true; }
            check(rejected && ReferenceEquals(before, session.Document) && session.UndoCount == undo &&
                session.ContentVersion == version && session.Selection.Count == 1 && !session.IsTransactionActive,
                "cross-kind restore preserves document, selection, history and version: " + kind);
            var sameKind = session.Document.Clone(); sameKind.Elements[0].Width = 40;
            session.RestoreDocument("same-kind restore", sameKind);
            check(session.Document.Kind == kind && session.UndoCount == undo + 1 && session.Document.Elements[0].Width == 40,
                "same-kind restore remains one undoable edit: " + kind);
            check(session.Undo() && session.Document.Kind == kind && session.Document.Elements[0].Width == 20 &&
                session.Redo() && session.Document.Kind == kind && session.Document.Elements[0].Width == 40,
                "restore undo and redo keep the owning document slot kind: " + kind);
        }
    }
}
