using System.IO;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;

internal static class WorkspaceRecoveryHistoryTests
{
    public static async Task RunAsync(Action<string, bool> assert)
    {
        string directory = Path.Combine(Path.GetTempPath(), "FloatCanvas-RecoveryHistoryTests", Guid.NewGuid().ToString("N"));
        var store = new RecoveryStore(directory);
        var assets = new AssetLibrary();
        string imageId = assets.Add(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jJ1kAAAAASUVORK5CYII="));
        var live = new SceneDocument { Elements = [new() { Kind = ElementKind.Image, AssetId = imageId, Width = 32, Height = 32 }] };
        var old = new WorkspaceState { LiveDocument = live, CaptureDocument = live.CreateCapture(new(-300, 50, 800, 500), imageId), CaptureOrigin = new(-300, 50) };
        await store.SaveAsync(old, assets).ConfigureAwait(false);
        old.LiveDocument.NextNumber = 2;
        await store.SaveAsync(old, assets).ConfigureAwait(false);
        byte[] originalCurrent = await File.ReadAllBytesAsync(store.CurrentPath).ConfigureAwait(false);
        byte[] originalBackup = await File.ReadAllBytesAsync(store.BackupPath).ConfigureAwait(false);

        var history = new WorkspaceRecoveryHistory(store);
        var fresh = new WorkspaceState();
        history.BeginFreshWorkspace(fresh);
        assert("blank startup and exit do not authorize recovery writes", !history.ShouldWrite(fresh));
        fresh.LiveDocument.Viewport = new(-1920, 0, 3840, 1440);
        fresh.LiveDocument.DisplayLayout = [new() { DeviceName = "changed monitor", Bounds = fresh.LiveDocument.Viewport }];
        assert("monitor discovery alone does not replace old recovery", !history.ShouldWrite(fresh));
        assert("untouched recovery packages remain byte-exact", originalCurrent.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(store.CurrentPath).ConfigureAwait(false)) &&
            originalBackup.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(store.BackupPath).ConfigureAwait(false)));
        assert("old workspace is available for manual recovery before any new edits", history.GetEntries().Count == 1 && history.GetEntries()[0].IsCurrent);

        fresh.LiveDocument.Elements.Add(new() { Kind = ElementKind.Rectangle, Width = 100, Height = 80 });
        assert("first new committed content authorizes recovery", history.ShouldWrite(fresh));
        await history.PreserveBeforeFirstWriteAsync().ConfigureAwait(false);
        await store.SaveAsync(fresh, new()).ConfigureAwait(false);
        history.WorkspaceSaved();
        var archived = history.GetEntries().Single(entry => !entry.IsCurrent);
        assert("both prior recovery generations are preserved before replacement", originalCurrent.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(archived.DirectoryPath, "workspace.floatcanvas")).ConfigureAwait(false)) &&
            originalBackup.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(archived.DirectoryPath, "workspace.previous.floatcanvas")).ConfigureAwait(false)));
        var restored = (await history.LoadAsync(archived).ConfigureAwait(false))!;
        assert("history restores the complete live/capture workspace and capture origin", JsonSerializer.Serialize(restored.State) == JsonSerializer.Serialize(old));
        assert("history retains all original image bytes", restored.Assets.Get(imageId).Data.Span.SequenceEqual(assets.Get(imageId).Data.Span));
        var editable = new EditorSession(restored.State.LiveDocument, restored.Assets);
        editable.Execute("move restored image", document => document.Elements[0].Transform = Affine2D.Translation(15, 20));
        assert("restored workspace assets remain editable with undo", editable.CanUndo && editable.Document.Elements[0].Transform.OffsetX == 15 && editable.Undo() && editable.Assets.TryGet(imageId, out _));

        fresh.LiveDocument.Elements.Clear();
        await history.PreserveBeforeFirstWriteAsync().ConfigureAwait(false);
        await store.SaveAsync(fresh, new()).ConfigureAwait(false);
        assert("later autosaves and clearing the canvas do not repeatedly archive", history.ShouldWrite(fresh) && history.GetEntries().Count(entry => !entry.IsCurrent) == 1);
        assert("earlier immutable history survives subsequent recovery rotation", originalCurrent.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(archived.DirectoryPath, "workspace.floatcanvas")).ConfigureAwait(false)));

        var nextLaunch = new WorkspaceRecoveryHistory(store);
        nextLaunch.BeginFreshWorkspace(new WorkspaceState());
        assert("each new process again starts with an unwritten blank baseline", !nextLaunch.ShouldWrite(new WorkspaceState()));
        nextLaunch.WorkspaceRestored();
        assert("an explicit history selection can be saved even when selected workspace is empty", nextLaunch.ShouldWrite(new WorkspaceState()));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        bool rejected = false;
        try { await nextLaunch.PreserveBeforeFirstWriteAsync(cancelled.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { rejected = true; }
        assert("cancelled archive does not authorize replacement or change existing packages", rejected &&
            (await store.LoadAsync().ConfigureAwait(false))!.State.LiveDocument.Elements.Count == 0);
    }
}
