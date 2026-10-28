using System.IO;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;

internal static class ArchiveWorkerTests
{
    internal static async Task RunAsync(Action<string, bool> check)
    {
        await RestoreFileKindAsync(check);
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-ArchiveWorker-" + Guid.NewGuid().ToString("N"));
        var store = new RecoveryStore(folder);
        var history = new WorkspaceRecoveryHistory(store);
        history.EnterWorkspace(new());
        var workspace = new WorkspaceState();
        workspace.LiveDocument.Elements.Add(new() { Kind = ElementKind.Rectangle, Width = 20, Height = 15 });
        var assets = new AssetLibrary();
        int caller = Environment.CurrentManagedThreadId, previewThread = caller;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        bool released = false;
        Task archive = history.ArchiveAsync(workspace, assets, null, "source.floatcanvas", createPreview: (_, _) =>
        {
            previewThread = Environment.CurrentManagedThreadId;
            entered.Set(); released = release.Wait(TimeSpan.FromSeconds(10));
            return null;
        });
        check("archive returns control while worker preview is blocked", entered.Wait(TimeSpan.FromSeconds(10)) && !archive.IsCompleted && previewThread != caller);
        workspace.LiveDocument.Elements[0].Width = 999;
        history.MaximumVersions = 0; // Captured policy must remain valid after settings/model changes.
        history.EnterWorkspace(workspace);
        release.Set(); await archive.ConfigureAwait(false);
        var entry = (await history.GetEntriesAsync()).Single();
        var readback = await history.LoadAsync(entry);
        check("archive owns a consistent document, source and policy snapshot", released && readback!.State.LiveDocument.Elements[0].Width == 20 && entry.SourceFile == "source.floatcanvas");

        history.MaximumVersions = 60;
        workspace.LiveDocument.Elements[0].Width = 30;
        await Task.WhenAll(history.ArchiveAsync(workspace, assets, null, null), history.ArchiveAsync(workspace, assets, null, null));
        check("concurrent archives serialize duplicate detection and publication", (await history.GetEntriesAsync()).Count == 2);
        workspace.LiveDocument.Elements[0].Width = 40;
        using var cancellation = new CancellationTokenSource();
        bool canceled = false;
        try { await history.ArchiveAsync(workspace, assets, null, null, ct: cancellation.Token, createPreview: (_, _) => { cancellation.Cancel(); return null; }); }
        catch (OperationCanceledException) { canceled = true; }
        check("canceled preview never publishes a partial archive", canceled && (await history.GetEntriesAsync()).Count == 2 &&
            !Directory.EnumerateDirectories(Path.Combine(folder, "history"), ".pending-*").Any());
        bool failed = false;
        try { await history.ArchiveAsync(workspace, assets, null, null, createPreview: (_, _) => throw new IOException("private preview failure")); }
        catch (IOException) { failed = true; }
        await history.ArchiveAsync(workspace, assets, null, null);
        check("a failed archive releases the operation queue for an unchanged retry", failed && (await history.GetEntriesAsync()).Count == 3);
    }

    private static async Task RestoreFileKindAsync(Action<string, bool> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-RestoreKind-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var assets = new AssetLibrary();
        var store = new ProjectStore();
        foreach (var kind in Enum.GetValues<DocumentKind>())
        {
            var session = new EditorSession(new SceneDocument { Kind = kind, Viewport = new(0, 0, 80, 60) }, assets);
            session.Add(new() { Kind = ElementKind.Rectangle, Width = 10, Height = 10 });
            var replacement = session.Document.Clone();
            replacement.Kind = kind == DocumentKind.LiveOverlay ? DocumentKind.FrozenCapture : DocumentKind.LiveOverlay;
            string file = Path.Combine(folder, kind + ".floatcanvas");
            await store.SaveAsync(file, replacement, assets);
            var loaded = await store.LoadAsync(file);
            bool rejected = false;
            int undo = session.UndoCount;
            try { session.RestoreDocument("revert externally replaced file", loaded.Document); }
            catch (ArgumentException) { rejected = true; }
            var recovery = new RecoveryStore(Path.Combine(folder, kind + "-recovery"));
            var workspace = kind == DocumentKind.LiveOverlay ? new WorkspaceState { LiveDocument = session.Document } :
                new WorkspaceState { CaptureDocument = session.Document };
            await recovery.SaveAsync(workspace, assets);
            var readback = await recovery.LoadAsync();
            check("valid cross-kind file rejection preserves immediate recovery save: " + kind, rejected && session.UndoCount == undo &&
                readback is not null && readback.State.LiveDocument.Kind == DocumentKind.LiveOverlay &&
                (kind != DocumentKind.FrozenCapture || readback.State.CaptureDocument?.Kind == DocumentKind.FrozenCapture));
        }
    }
}
