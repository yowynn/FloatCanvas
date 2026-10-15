using FloatCanvas.Core;

internal static class SceneRevisionTests
{
    public static void Run(Action<bool, string> check)
    {
        var session = new EditorSession();
        var element = new CanvasElement { Kind = ElementKind.Rectangle, Width = 20, Height = 30 };
        session.Add(element);
        long revision = session.SceneRevision, transaction = session.TransactionId;
        check(revision > 0 && transaction > 0, "Committed content has a rendering revision and transaction identity");
        session.SelectAll(); session.SelectAt(new(1, 1)); session.SelectRectangle(new(-1, -1, 100, 100)); session.SetSelection([element.Id]);
        check(session.SceneRevision == revision, "Selection operations do not invalidate committed raster content");
        session.BeginTransaction("preview"); session.ApplySelectionTransform(Affine2D.Translation(3, 4)); session.NotifyChanged();
        check(session.SceneRevision == revision && session.TransactionId > transaction, "Preview updates retain committed revision and have a fresh transaction");
        session.Commit();
        check(session.SceneRevision > revision, "Preview commit invalidates cached content");
        revision = session.SceneRevision;
        session.BeginTransaction("cancelled"); session.ApplySelectionTransform(Affine2D.Translation(6, 7)); session.Cancel();
        check(session.SceneRevision > revision, "Cancelling a preview invalidates its cached content");
        revision = session.SceneRevision; session.Undo();
        check(session.SceneRevision > revision, "Undo invalidates raster content");
        revision = session.SceneRevision; session.Redo();
        check(session.SceneRevision > revision, "Redo invalidates raster content");
        revision = session.SceneRevision; session.NotifyChanged();
        check(session.SceneRevision > revision, "Explicit external content notification invalidates raster content");
        revision = session.SceneRevision; session.ReplaceDocument(session.Document.Clone());
        check(session.SceneRevision > revision, "Replacement invalidates raster content even with equivalent document data");
    }
}
