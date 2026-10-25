using FloatCanvas.Core;

internal static class PreviewChangeTests
{
    internal static void Run(Action<bool, string> check)
    {
        var session = Scene();
        session.BeginTransaction("settings notification followed by another mutation");
        session.Document.LayerOpacity = .5;
        session.NotifySettingsChanged();
        long captured = session.PreviewRevision;
        session.Document.Elements[1].Width = 170;
        session.Commit();
        check(session.PreviewElementsRevision > captured,
            "default commit cannot hide a final unnotified element mutation behind an earlier settings notification");
        check(!session.TryGetPreviewChangedElements(captured, out var unknown) && unknown.Count == 0,
            "default commit forces a complete preview refresh even after a known earlier notification");
        KnownNotifications(check);
        SettingsTransactions(check);
        NoOpAndHistory(check);
        BoundedHistory(check);
    }

    private static void KnownNotifications(Action<bool, string> check)
    {
        var session = Scene();
        var a = session.Document.Elements[0]; var b = session.Document.Elements[1];
        long start = session.PreviewRevision;
        session.BeginTransaction("coalesced edits");
        check(session.TransactionPreviewRevision == start, "a transaction exposes its preview base before any intermediate mutation");
        var supplied = new List<Guid> { a.Id };
        a.Width = 110; session.NotifyElementsChanged(supplied);
        supplied.Clear(); supplied.Add(Guid.NewGuid());
        b.Height = 70; session.NotifyElementsChanged([b.Id, b.Id]);
        session.Document.LayerOpacity = .4; session.NotifySettingsChanged();
        check(session.TryGetPreviewChangedElements(start, out var changed) && changed.SetEquals([a.Id, b.Id]),
            "coalesced notifications retain all distinct changed IDs and detach caller-owned collections");
        check(session.TransactionPreviewRevision == start && session.SceneRevision == 0 && session.PreviewRevision == 3,
            "live content updates advance preview versions while preserving committed revision and transaction base");
        var deleted = Guid.NewGuid(); session.NotifyElementsChanged([deleted]);
        check(changed.SetEquals([a.Id, b.Id]) && session.TryGetPreviewChangedElements(start, out var later) && later.SetEquals([a.Id, b.Id, deleted]),
            "returned changed-ID sets stay immutable while later notifications also retain removed IDs");
        long revision = session.PreviewRevision;
        session.SetSelection([a.Id]);
        check(session.PreviewRevision == revision && session.TryGetPreviewChangedElements(revision, out var empty) && empty.Count == 0,
            "selection changes neither advance content versions nor invent changed elements");
        session.NotifyChanged();
        check(!session.TryGetPreviewChangedElements(start, out var unknown) && unknown.Count == 0,
            "an unknown notification invalidates the whole requested range without exposing a partial ID union");
        long rebuilt = session.PreviewRevision;
        session.NotifyElementsChanged([b.Id]);
        check(session.TryGetPreviewChangedElements(rebuilt, out var afterRebuild) && afterRebuild.SetEquals([b.Id]),
            "a full refresh after an unknown notification restores incremental tracking for subsequent changes");
        bool rejected = false; revision = session.PreviewRevision;
        try { session.NotifyElementsChanged(null!); } catch (ArgumentNullException) { rejected = true; }
        check(rejected && revision == session.PreviewRevision, "invalid changed-ID input is rejected before publishing a content notification");
        session.Cancel();
    }

    private static void SettingsTransactions(Action<bool, string> check)
    {
        var session = Scene();
        long start = session.PreviewRevision, elements = session.PreviewElementsRevision;
        session.BeginTransaction("opacity");
        session.Document.LayerOpacity = .5; session.NotifySettingsChanged();
        session.CommitSettings();
        check(session.TryGetPreviewChangedElements(start, out var changed) && changed.Count == 0 && session.PreviewElementsRevision == elements,
            "explicit settings notification and commit retain unchanged immutable element nodes");
        check(session.SceneRevision == 1 && session.ContentVersion == 1 && session.UndoCount == 1 && !session.IsTransactionActive,
            "settings-only commits preserve ordinary content versions and one atomic history item");
        long committed = session.PreviewRevision;
        check(session.Undo() && session.Document.LayerOpacity == 1 && !session.TryGetPreviewChangedElements(committed, out _),
            "undo of a settings transaction restores content and marks document replacement as unknown");
        long undone = session.PreviewRevision;
        check(session.Redo() && session.Document.LayerOpacity == .5 && !session.TryGetPreviewChangedElements(undone, out _),
            "redo is a conservative preview rebuild while retaining settings history semantics");
        start = session.PreviewRevision; elements = session.PreviewElementsRevision;
        session.ExecuteSettings("canvas background", document => document.Canvas.BackgroundColor = 0xFF123456);
        check(session.TryGetPreviewChangedElements(start, out changed) && changed.Count == 0 && session.PreviewElementsRevision == elements,
            "atomic settings execution records an empty changed-ID set without a separate live notification");
        start = session.PreviewRevision;
        session.ExecuteSettings("misdeclared element edit", document => document.Elements[0].Width = 180);
        check(!session.TryGetPreviewChangedElements(start, out _) && session.PreviewElementsRevision == session.PreviewRevision && session.CanUndo,
            "an element mutation inside settings execution downgrades to unknown without corrupting history");
        start = session.PreviewRevision;
        session.ExecuteSettings("misdeclared background edit", document => document.Background = new() { Kind = ElementKind.Rectangle, Width = 20, Height = 10 });
        check(!session.TryGetPreviewChangedElements(start, out _), "settings execution also guards background element changes");
        session = Scene();
        session.BeginTransaction("element preview followed by settings declaration");
        session.Document.Elements[0].Width = 200; session.NotifyElementsChanged([session.Document.Elements[0].Id]);
        long intermediate = session.PreviewRevision;
        session.Document.Elements[0].Width = 100; session.Document.LayerOpacity = .5;
        session.CommitSettings();
        check(!session.TryGetPreviewChangedElements(intermediate, out _),
            "settings commit cannot retain a previously published intermediate element when final geometry returns to its original value");
    }

    private static void NoOpAndHistory(Action<bool, string> check)
    {
        var session = Scene();
        session.BeginTransaction("return to original without final notify");
        session.Document.Elements[0].Width = 200;
        session.NotifyElementsChanged([session.Document.Elements[0].Id]);
        long intermediate = session.PreviewRevision;
        session.Document.Elements[0].Width = 100;
        session.Commit();
        check(session.PreviewRevision > intermediate && !session.TryGetPreviewChangedElements(intermediate, out _),
            "net-zero default commit invalidates an intermediate frame even when the last restoration was not notified");
        check(session.SceneRevision == 0 && session.ContentVersion == 0 && session.UndoCount == 0,
            "net-zero preview invalidation preserves the existing no-op history and committed revision semantics");
        session.BeginTransaction("opacity returns to original");
        session.Document.LayerOpacity = .3; session.NotifySettingsChanged();
        intermediate = session.PreviewRevision;
        session.Document.LayerOpacity = 1; session.CommitSettings();
        check(session.PreviewRevision > intermediate && session.TryGetPreviewChangedElements(intermediate, out var settings) && settings.Count == 0 && session.UndoCount == 0,
            "net-zero settings commit refreshes header state without discarding unchanged nodes or adding history");
        session.BeginTransaction("cancelled known edit");
        session.Document.Elements[0].Height = 90; session.NotifyElementsChanged([session.Document.Elements[0].Id]);
        intermediate = session.PreviewRevision; session.Cancel();
        check(!session.TryGetPreviewChangedElements(intermediate, out _) && session.Document.Elements[0].Height == 60,
            "cancellation records document restoration as unknown even after known element notifications");
        long previous = session.PreviewRevision;
        session.ReplaceDocument(new());
        check(!session.TryGetPreviewChangedElements(previous, out _) && session.Document.Elements.Count == 0,
            "explicit document replacement cannot reuse IDs from a prior preview document");
    }

    private static void BoundedHistory(Action<bool, string> check)
    {
        var session = Scene();
        for (int i = 0; i < 128; i++) session.NotifySettingsChanged();
        check(session.TryGetPreviewChangedElements(0, out var changed) && changed.Count == 0,
            "the complete 128-entry known history remains available to a delayed preview capture");
        var id = session.Document.Elements[0].Id;
        session.NotifyElementsChanged([id]);
        check(!session.TryGetPreviewChangedElements(0, out _), "a preview older than the bounded journal must fall back to a full refresh");
        check(session.TryGetPreviewChangedElements(1, out changed) && changed.SetEquals([id]),
            "the oldest still-contiguous journal range includes its final changed element");
        check(!session.TryGetPreviewChangedElements(-1, out _) && !session.TryGetPreviewChangedElements(session.PreviewRevision + 1, out _),
            "negative and future capture versions cannot claim a complete change history");
    }

    private static EditorSession Scene() => new(new SceneDocument { Elements =
    [
        new() { Kind = ElementKind.Rectangle, Width = 100, Height = 60 },
        new() { Kind = ElementKind.Rectangle, Width = 100, Height = 60 }
    ] });
}
