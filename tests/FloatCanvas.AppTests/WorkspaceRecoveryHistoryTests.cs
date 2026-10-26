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
        assert("background recovery decisions retain the blank baseline", !await history.ShouldWriteAsync(fresh));
        assert("blank startup and exit do not authorize recovery writes", !history.ShouldWrite(fresh));
        fresh.LiveDocument.Viewport = new(-1920, 0, 3840, 1440);
        fresh.LiveDocument.DisplayLayout = [new() { DeviceName = "changed monitor", Bounds = fresh.LiveDocument.Viewport }];
        assert("monitor discovery alone does not replace old recovery", !history.ShouldWrite(fresh));
        assert("untouched recovery packages remain byte-exact", originalCurrent.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(store.CurrentPath).ConfigureAwait(false)) &&
            originalBackup.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(store.BackupPath).ConfigureAwait(false)));
        assert("old workspace is available for manual recovery before any new edits", history.GetEntries().Count == 1 && history.GetEntries()[0].IsCurrent);

        fresh.LiveDocument.Elements.Add(new() { Kind = ElementKind.Rectangle, Width = 100, Height = 80 });
        assert("background recovery decisions recognize committed content", await history.ShouldWriteAsync(fresh));
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

        await ClearPolicyAsync(assert).ConfigureAwait(false);
        await VersionPolicyAsync(assert).ConfigureAwait(false);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        bool rejected = false;
        try { await nextLaunch.PreserveBeforeFirstWriteAsync(cancelled.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { rejected = true; }
        assert("cancelled archive does not authorize replacement or change existing packages", rejected &&
            (await store.LoadAsync().ConfigureAwait(false))!.State.LiveDocument.Elements.Count == 0);
    }
    private static async Task ClearPolicyAsync(Action<string,bool> check)
    {
        foreach(bool recovered in new[]{false,true})
        {
            string directory=Path.Combine(Path.GetTempPath(),"FloatCanvas-ClearPolicy",Guid.NewGuid().ToString("N"));
            var store=new RecoveryStore(directory);var assets=new AssetLibrary();
            var previous=new WorkspaceState();previous.LiveDocument.Elements.Add(new(){Kind=ElementKind.Text,Text="previous canvas"});
            await store.SaveAsync(previous,assets);
            var history=new WorkspaceRecoveryHistory(store);
            if(recovered)history.EnterWorkspace(previous);else history.BeginFreshWorkspace(new());
            await history.PrepareClearAsync();
            await store.SaveAsync(new(),assets);history.WriteCurrentInfo(new(),null);
            var entries=history.GetEntries();
            check(recovered?"clearing a recovered canvas does not push it to history":"clearing a fresh canvas preserves an unrelated previous session",entries.Count==(recovered?0:1));
            if(!recovered)check("the preserved prior session retains its original drawing",(await history.LoadAsync(entries.Single()))!.State.LiveDocument.Elements.Single().Text=="previous canvas");
        }
    }
    private static async Task VersionPolicyAsync(Action<string, bool> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "FloatCanvas-VersionPolicy", Guid.NewGuid().ToString("N"));
        var store = new RecoveryStore(directory); var assets = new AssetLibrary();
        var history = new WorkspaceRecoveryHistory(store);
        var blank = new WorkspaceState();
        history.BeginFreshWorkspace(blank);
        for (int i = 0; i < 10; i++) await history.ArchiveAsync(blank, assets, null, null);
        check("repeated blank new canvases never create history", history.GetEntries().Count == 0);
        blank.LiveDocument.Canvas.BackgroundColor = 0xFF112233;
        check("canvas settings alone do not consume drawing history slots", !history.HasNewVersion(blank));
        await history.ArchiveAsync(blank, assets, null, null);
        history.EnterWorkspace(blank);
        check("inherited settings do not make the next empty canvas a new version", !history.HasNewVersion(blank));
        var entered = blank.Clone();
        entered.LiveDocument.Elements.Add(new() { Kind = ElementKind.Rectangle, Width = 12, Height = 15, Visible = false, Locked = true });
        history.EnterWorkspace(entered);
        await history.ArchiveAsync(entered, assets, null, "example.floatcanvas");
        check("opening an unchanged saved file creates no history", history.GetEntries().Count == 0);
        entered.LiveDocument.Elements[0].Width = 20;
        check("an edited saved file is eligible independently of file saved status", history.HasNewVersion(entered));
        await history.ArchiveAsync(entered, assets, null, "example.floatcanvas");
        var original = history.GetEntries().Single(e => e.SourceFile is not null);
        await store.SaveAsync(entered, assets); history.WriteCurrentInfo(entered, "example.floatcanvas");
        check("current recovery does not duplicate its archived version", history.GetEntries().Count == 1);
        var restored = (await history.LoadAsync(original))!.State;
        history.EnterWorkspace(restored);
        await history.ArchiveAsync(restored, assets, null, null);
        check("restoring without editing preserves history order and timestamps", history.GetEntries().Count == 1 && history.GetEntries().Any(e => e.DirectoryPath == original.DirectoryPath && e.SavedAtUtc == original.SavedAtUtc));
        restored.LiveDocument.Elements[0].Width++;
        await history.ArchiveAsync(restored, assets, null, null);
        var editedRestore = history.GetEntries()[0];
        check("editing a restored canvas adds a new first version without moving its source", editedRestore.DirectoryPath != original.DirectoryPath &&
            (await history.LoadAsync(editedRestore))!.State.LiveDocument.Elements[0].Width == restored.LiveDocument.Elements[0].Width &&
            history.GetEntries().Single(e => e.DirectoryPath == original.DirectoryPath).SavedAtUtc == original.SavedAtUtc);
        restored.LiveDocument.Elements[0].Width--;
        check("undoing to the entry baseline removes new-version eligibility", !history.HasNewVersion(restored));
        await history.ArchiveAsync(restored, assets, null, null, force: true);
        check("archiving the same content twice reuses its prior snapshot", history.GetEntries().Count == 2);
        history.EnterWorkspace(blank);
        for (int i = 0; i < 65; i++)
        {
            var next = new WorkspaceState(); next.LiveDocument.Elements.Add(new() { Kind = ElementKind.Text, Text = "version " + i });
            await history.ArchiveAsync(next, assets, null, null);
        }
        check("default history limit retains exactly sixty archives", history.GetEntries().Count(e => !e.IsCurrent) == 60);
        check("oldest content is evicted when capacity is exceeded", !Directory.Exists(original.DirectoryPath));
        history.MaximumVersions = 2; history.Trim();
        check("reducing the limit immediately trims only oldest archives", history.GetEntries().Count(e => !e.IsCurrent) == 2 && File.Exists(store.CurrentPath));
        check("trimming retains the two newest canvas versions", (await history.LoadAsync(history.GetEntries()[0]))!.State.LiveDocument.Elements.Single().Text == "version 64" &&
            (await history.LoadAsync(history.GetEntries()[1]))!.State.LiveDocument.Elements.Single().Text == "version 63");
        await store.SaveAsync(new WorkspaceState(), assets); history.EnterWorkspace(new()); history.WriteCurrentInfo(new(), null);
        check("empty recovery persists without becoming a visible history entry", (await store.LoadAsync())!.State.LiveDocument.Elements.Count == 0 && history.GetEntries().All(e => !e.IsCurrent));
        check("missing settings default to sixty versions", System.Text.Json.JsonSerializer.Deserialize<UserSettings>("{}")!.HistoryVersionLimit == 60);
        check("zero persisted history limit is retained", System.Text.Json.JsonSerializer.Deserialize<UserSettings>("{\"HistoryVersionLimit\":0}")!.HistoryVersionLimit == 0);
        foreach (int limit in new[] { 0, 1, 60, 1000 })
            check($"valid history limit {limit} is retained", new UserSettings { HistoryVersionLimit = limit }.HistoryVersionLimit == limit);
        foreach (int limit in new[] { -1, int.MinValue })
            check($"invalid history limit {limit} falls back to sixty", new UserSettings { HistoryVersionLimit = limit }.HistoryVersionLimit == 60);
        check("legacy history limits above the new cap normalize to one thousand", System.Text.Json.JsonSerializer.Deserialize<UserSettings>("{\"HistoryVersionLimit\":10000}")!.HistoryVersionLimit == 1000);
        history.MaximumVersions = 0; history.Trim();
        check("zero history limit clears archives and preserves current recovery", history.GetEntries().All(entry => entry.IsCurrent) && File.Exists(store.CurrentPath) && await store.LoadAsync() is not null);
        var noHistory = new WorkspaceState(); noHistory.LiveDocument.Elements.Add(new() { Kind = ElementKind.Text, Text = "disabled history" });
        await history.ArchiveAsync(noHistory, assets, null, null, force: true);
        check("zero history limit creates no new archived version", history.GetEntries().All(entry => entry.IsCurrent));
        string brokenDirectory = Path.Combine(directory, "corrupt-recovery");
        Directory.CreateDirectory(brokenDirectory);
        var brokenStore = new RecoveryStore(brokenDirectory);
        await File.WriteAllBytesAsync(brokenStore.CurrentPath, [1, 2, 3]);
        var brokenHistory = new WorkspaceRecoveryHistory(brokenStore);
        await brokenHistory.PreserveBeforeFirstWriteAsync();
        check("corrupt prior recovery is quarantined byte-exact before a new write", Directory.GetFiles(Path.Combine(brokenDirectory, "quarantine"), "workspace.floatcanvas", SearchOption.AllDirectories).Any(path => File.ReadAllBytes(path).SequenceEqual(new byte[] {1,2,3})));
        var settings = new UserSettings { HistoryVersionLimit = 12 };
        check("history limit survives settings serialization", System.Text.Json.JsonSerializer.Deserialize<UserSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!.HistoryVersionLimit == 12);
    }
}
