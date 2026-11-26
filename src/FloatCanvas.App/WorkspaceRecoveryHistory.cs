using System.IO;
using System.Text.Json;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed record WorkspaceRecoveryEntry(string DirectoryPath, DateTime SavedAtUtc, bool IsCurrent, string? SourceFile = null);

// Snapshot-based history work is serialized off UI; RecoveryStore retains its atomic package writes.
internal sealed class WorkspaceRecoveryHistory(RecoveryStore store)
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> DirectoryGates =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> InfoLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private string? freshBaseline;
    private string? entryBaseline;
    private long entryEpoch;
    private Task baselineReady = Task.CompletedTask;
    private readonly SemaphoreSlim operations = DirectoryGates.GetOrAdd(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(store.DirectoryPath)), _ => new(1, 1));
    public int MaximumVersions { get; set; } = 60;
    private sealed record HistoryInfo(string Fingerprint, string? SourceFile, bool Eligible = true, long PackageTicks = 0, long PackageLength = 0);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Ticks, long Length, HistoryInfo Info)> legacyInfo = new(StringComparer.OrdinalIgnoreCase);

    public void EnterWorkspace(WorkspaceState workspace)
    {
        Interlocked.Increment(ref entryEpoch);
        entryBaseline = ContentFingerprint(workspace);
        baselineReady = Task.CompletedTask;
    }
    public async Task EnterWorkspaceAsync(WorkspaceState workspace, CancellationToken ct = default)
    {
        long epoch = Interlocked.Increment(ref entryEpoch);
        var ready = Task.Run(() => ContentFingerprint(workspace), ct);
        baselineReady = ready;
        string fingerprint = await ready.ConfigureAwait(false);
        if (epoch == Interlocked.Read(ref entryEpoch)) entryBaseline = fingerprint;
    }
    public Task WaitForBaselineAsync() => baselineReady;
    public bool HasNewVersion(WorkspaceState workspace) => ContentFingerprint(workspace) != entryBaseline && HasValue(workspace);
    public Task<bool> HasNewVersionAsync(WorkspaceState workspace, CancellationToken ct = default)
    {
        return RunAfterBaselineAsync(() =>
        {
            string? baseline = entryBaseline;
            return HasValue(workspace) && ContentFingerprint(workspace) != baseline;
        }, ct);
    }
    private static bool HasValue(WorkspaceState workspace) => HasObjects(workspace);
    private static bool HasObjects(WorkspaceState workspace) => workspace.LiveDocument.Elements.Count > 0 ||
        workspace.LiveDocument.Groups.Count > 0 || workspace.LiveDocument.Instances.Count > 0 || workspace.LiveDocument.Definitions.Count > 0 ||
        workspace.LiveDocument.Background is not null || workspace.CaptureDocument is not null || workspace.PendingPrefabEdit is not null;
    public void WriteCurrentInfo(WorkspaceState workspace, string? sourceFile)
        => CreateCurrentInfoWriter(workspace, sourceFile)();
    // Capture the entry identity on UI; hashing and file IO can then run on a worker
    // without reading mutable recovery policy after a canvas switch.
    public Action CreateCurrentInfoWriter(WorkspaceState workspace, string? sourceFile)
    {
        var path = Path.Combine(store.DirectoryPath, "history-info.json");
        var baseline = entryBaseline;
        return () =>
        {
            string fingerprint = ContentFingerprint(workspace);
            WriteInfo(path, new(fingerprint, sourceFile, fingerprint != baseline && HasValue(workspace)));
        };
    }
    private static void WriteInfo(string path, HistoryInfo info)
    {
        path = Path.GetFullPath(path);
        lock (InfoLocks.GetOrAdd(Path.TrimEndingDirectorySeparator(path), _ => new()))
        {
            var package = new FileInfo(Path.Combine(Path.GetDirectoryName(path)!, "workspace.floatcanvas"));
            if (package.Exists) info = info with { PackageTicks = package.LastWriteTimeUtc.Ticks, PackageLength = package.Length };
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(info));
                File.Move(temporary, path, true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }
    public async Task ArchiveAsync(WorkspaceState workspace, AssetLibrary assets, byte[]? preview,
        string? sourceFile, bool force = false, CancellationToken ct = default, Func<WorkspaceState, AssetLibrary, byte[]?>? createPreview = null)
    {
        await WaitForBaselineAsync().WaitAsync(ct).ConfigureAwait(false);
        var snapshot = workspace.Clone();
        var resources = assets.Resources.ToArray();
        string? baseline = entryBaseline;
        int maximum = MaximumVersions;
        await RunSerializedAsync(() => ArchiveCoreAsync(snapshot, resources, preview, sourceFile, force, baseline, maximum, ct, createPreview), ct)
            .ConfigureAwait(false);
    }

    private async Task RunSerializedAsync(Func<Task> operation, CancellationToken ct)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try { await Task.Run(operation, ct).ConfigureAwait(false); }
        finally { operations.Release(); }
    }
    private async Task<T> RunAfterBaselineAsync<T>(Func<T> operation, CancellationToken ct)
    {
        await WaitForBaselineAsync().WaitAsync(ct).ConfigureAwait(false);
        return await Task.Run(operation, ct).ConfigureAwait(false);
    }

    private async Task ArchiveCoreAsync(WorkspaceState workspace, AssetResource[] resources, byte[]? preview,
        string? sourceFile, bool force, string? baseline, int maximum, CancellationToken ct, Func<WorkspaceState, AssetLibrary, byte[]?>? createPreview)
    {
        ct.ThrowIfCancellationRequested();
        if (maximum <= 0 || !HasValue(workspace)) return;
        string fingerprint = ContentFingerprint(workspace);
        if (!force && fingerprint == baseline) return;
        if (GetEntries(ct).Any(e => !e.IsCurrent && EntryInfo(e.DirectoryPath, ct)?.Fingerprint == fingerprint)) return;
        ct.ThrowIfCancellationRequested();
        var assets = new AssetLibrary();
        foreach (var resource in resources) assets.Add(resource);
        if (createPreview is not null) preview = createPreview(workspace, assets);
        ct.ThrowIfCancellationRequested();
        string parent = Path.Combine(store.DirectoryPath, "history");
        Directory.CreateDirectory(parent);
        string identity = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N");
        string pending = Path.Combine(parent, ".pending-" + identity);
        Directory.CreateDirectory(pending); File.WriteAllText(Path.Combine(pending, ".owner"), "FloatCanvas.History.v1");
        try
        {
            await new RecoveryStore(pending).SaveWithPreviewAsync(workspace, assets, preview, ct).ConfigureAwait(false);
            WriteInfo(Path.Combine(pending, "history-info.json"), new(fingerprint, sourceFile));
            ct.ThrowIfCancellationRequested();
            Directory.Move(pending, Path.Combine(parent, identity));
        }
        finally { PublicationCleanup.RemovePendingDirectory(pending); }
        Trim(maximum, ct);
    }
    public void Trim() => Trim(MaximumVersions);
    public Task TrimAsync(CancellationToken ct = default)
    {
        int maximum = MaximumVersions;
        return RunSerializedAsync(() => { Trim(maximum, ct); return Task.CompletedTask; }, ct);
    }
    private void Trim(int maximum, CancellationToken ct = default)
    {
        string parent = Path.GetFullPath(Path.Combine(store.DirectoryPath, "history"));
        PublicationCleanup.SweepPending(parent);
        foreach (var entry in GetEntries(ct).Where(e => !e.IsCurrent).Skip(Math.Clamp(maximum, 0, 1000)))
        {
            ct.ThrowIfCancellationRequested();
            string target = Path.GetFullPath(entry.DirectoryPath);
            if (!string.Equals(Path.GetDirectoryName(target), parent, StringComparison.OrdinalIgnoreCase) ||
                !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(target), @"^\d{8}-\d{9}-[a-f0-9]{32}$") ||
                (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) continue;
            PublicationCleanup.RemovePendingDirectory(target);
        }
    }
    private HistoryInfo? EntryInfo(string directory, CancellationToken ct = default)
    {
        string path = Path.Combine(directory, "history-info.json");
        var package = new FileInfo(Path.Combine(directory, "workspace.floatcanvas"));
        long ticks = package.Exists ? package.LastWriteTimeUtc.Ticks : 0, length = package.Exists ? package.Length : 0;
        HistoryInfo? metadata = null;
        if (File.Exists(path))
        {
            try
            {
                metadata = JsonSerializer.Deserialize<HistoryInfo>(File.ReadAllText(path));
                if (ticks != 0 && metadata is not null && metadata.Eligible &&
                    metadata.PackageTicks == ticks && metadata.PackageLength == length) return metadata;
            }
            catch (JsonException) { }
        }
        else if (legacyInfo.TryGetValue(directory, out var cached) && ticks == cached.Ticks && length == cached.Length) return cached.Info;
        var generations = new List<(WorkspaceState State, string Fingerprint, bool Eligible, bool FromBackup)>();
        Exception? readFailure = null;
        foreach (bool fromBackup in new[] { false, true })
        {
            try
            {
                var loaded = new RecoveryStore(directory).LoadGenerationAsync(fromBackup, ct).GetAwaiter().GetResult();
                if (loaded is null) continue;
                generations.Add((loaded.State, ContentFingerprint(loaded.State),
                    HasObjects(loaded.State) || JsonSerializer.Serialize(loaded.State.LiveDocument.Canvas) != JsonSerializer.Serialize(new CanvasSettings()),
                    fromBackup));
            }
            catch (Exception error) when (error is InvalidDataException or IOException or JsonException or NotSupportedException or EndOfStreamException)
            {
                readFailure ??= error;
            }
        }
        if (generations.Count == 0)
        {
            if (readFailure is not null)
                throw new InvalidDataException(L.Get("Recovery.BothFilesUnreadable"), readFailure);
            return null;
        }
        if (metadata is { Eligible: false } && generations.FirstOrDefault(generation => !generation.FromBackup) is { State: not null } current &&
            current.Fingerprint == metadata.Fingerprint)
            return metadata;
        var selectedGeneration = generations.FirstOrDefault(generation => generation.Eligible);
        if (selectedGeneration.State is null) selectedGeneration = generations[0];
        string fingerprint = selectedGeneration.Fingerprint;
        bool eligible = generations.Any(generation => generation.Eligible);
        if (File.Exists(path))
        {
            try
            {
                var info = JsonSerializer.Deserialize<HistoryInfo>(File.ReadAllText(path));
                if (info?.Fingerprint == fingerprint)
                    return info with { Eligible = info.Eligible || eligible };
            }
            catch (JsonException) { }
        }
        var result = new HistoryInfo(fingerprint, null, eligible);
        if (!File.Exists(path)) legacyInfo[directory] = (ticks, length, result);
        return result;
    }

    private bool archiveBeforeWrite = true;
    public async Task PrepareClearAsync(CancellationToken ct=default)
    {
        await WaitForBaselineAsync().WaitAsync(ct).ConfigureAwait(false);
        string? baseline = entryBaseline;
        int maximum = MaximumVersions;
        await RunSerializedAsync(() => PrepareClearCoreAsync(baseline, maximum, ct), ct).ConfigureAwait(false);
    }
    private async Task PrepareClearCoreAsync(string? baseline, int maximum, CancellationToken ct)
    {
        if(!archiveBeforeWrite)return;
        // Clearing this recovered canvas must not publish it to history. A fresh
        // launch may still hold a different previous session, which must survive.
        try
        {
            if(EntryInfo(store.DirectoryPath, ct)?.Fingerprint==baseline)
            {archiveBeforeWrite=false;return;}
        }
        catch(Exception ex) when(ex is InvalidDataException or JsonException or NotSupportedException or EndOfStreamException) { }
        await PreserveCurrentCoreAsync(maximum, ct).ConfigureAwait(false);
    }

    public void BeginFreshWorkspace(WorkspaceState baseline) { freshBaseline = ContentFingerprint(baseline); EnterWorkspace(baseline); }
    public void WorkspaceRestored() => freshBaseline = null;
    public void WorkspaceSaved() => freshBaseline = null;
    public bool ShouldWrite(WorkspaceState workspace) => freshBaseline is null || ContentFingerprint(workspace) != freshBaseline;
    public Task<bool> ShouldWriteAsync(WorkspaceState workspace, CancellationToken token = default)
    {
        return RunAfterBaselineAsync(() =>
        {
            var baseline = freshBaseline;
            return baseline is null || ContentFingerprint(workspace) != baseline;
        }, token);
    }

    public async Task PreserveBeforeFirstWriteAsync(CancellationToken ct = default)
    {
        await WaitForBaselineAsync().WaitAsync(ct).ConfigureAwait(false);
        int maximum = MaximumVersions;
        await RunSerializedAsync(() => archiveBeforeWrite ? PreserveCurrentCoreAsync(maximum, ct) : Task.CompletedTask, ct)
            .ConfigureAwait(false);
    }

    public Task PreserveCurrentAsync(CancellationToken ct = default)
    {
        int maximum = MaximumVersions;
        return RunSerializedAsync(() => PreserveCurrentCoreAsync(maximum, ct), ct);
    }
    private async Task PreserveCurrentCoreAsync(int maximum, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sources = new[] { store.CurrentPath, store.BackupPath }.Where(File.Exists).ToArray();
        if (sources.Length == 0)
        {
            archiveBeforeWrite = false;
            return;
        }
        // A disabled history limit must not consume the one-time preservation
        // obligation. If history is later enabled, the launch generation can
        // still be archived before the next recovery rotation overwrites it.
        if (maximum <= 0) return;
        HistoryInfo? info;
        try { info = sources.Length > 0 ? EntryInfo(store.DirectoryPath, ct) : null; }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or NotSupportedException or EndOfStreamException)
        {
            // Keep unreadable packages byte-exact in quarantine before replacing current recovery.
            await store.ArchiveExistingAsync(ct).ConfigureAwait(false);
            archiveBeforeWrite = false;
            return;
        }
        if (info is { Eligible: true } && !GetEntries(ct).Any(e => !e.IsCurrent && EntryInfo(e.DirectoryPath, ct)?.Fingerprint == info.Fingerprint))
        {
            string history = Path.Combine(store.DirectoryPath, "history");
            Directory.CreateDirectory(history);
            string identity = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N");
            string pending = Path.Combine(history, ".pending-" + identity);
            Directory.CreateDirectory(pending); File.WriteAllText(Path.Combine(pending, ".owner"), "FloatCanvas.History.v1");
            try
            {
                foreach (string source in sources)
                {
                    ct.ThrowIfCancellationRequested();
                    string destination = Path.Combine(pending, Path.GetFileName(source));
                    DateTime savedAt = File.GetLastWriteTimeUtc(source);
                    await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
                    await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
                    {
                        await input.CopyToAsync(output, ct).ConfigureAwait(false);
                        await output.FlushAsync(ct).ConfigureAwait(false);
                        output.Flush(true);
                    }
                    File.SetLastWriteTimeUtc(destination, savedAt);
                }
                WriteInfo(Path.Combine(pending, "history-info.json"), info!);
                ct.ThrowIfCancellationRequested();
                // Publish only after both generations are complete. A failed copy cannot authorize a new save.
                Directory.Move(pending, Path.Combine(history, identity));
            }
            finally { PublicationCleanup.RemovePendingDirectory(pending); }
        }
        archiveBeforeWrite = false;
        Trim(maximum, ct);
    }

    public async Task<IReadOnlyList<WorkspaceRecoveryEntry>> GetEntriesAsync(CancellationToken ct = default)
    {
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(() => GetEntries(ct), ct).ConfigureAwait(false); }
        finally { operations.Release(); }
    }

    public IReadOnlyList<WorkspaceRecoveryEntry> GetEntries(CancellationToken ct = default)
    {
        var entries = new List<WorkspaceRecoveryEntry>();
        AddEntry(store.DirectoryPath, true);
        foreach (string category in new[] { "history" })
        {
            string parent = Path.Combine(store.DirectoryPath, category);
            if (Directory.Exists(parent))
                foreach (string directory in Directory.EnumerateDirectories(parent).Where(path => !Path.GetFileName(path).StartsWith('.') && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0))
                    AddEntry(directory, false);
        }
        return entries.OrderBy(entry => entry.IsCurrent).ThenBy(entry => entry.SavedAtUtc)
            .DistinctBy(entry => EntryInfo(entry.DirectoryPath, ct)?.Fingerprint ?? entry.DirectoryPath)
            .OrderByDescending(entry => entry.SavedAtUtc).ToArray();

        void AddEntry(string directory, bool current)
        {
            ct.ThrowIfCancellationRequested();
            var files = new[] { "workspace.floatcanvas", "workspace.previous.floatcanvas" }.Select(name => Path.Combine(directory, name)).Where(File.Exists).ToArray();
            if (files.Length == 0) return;
            try
            {
                var info = EntryInfo(directory, ct);
                if (info is { Eligible: true }) entries.Add(new(directory, files.Max(File.GetLastWriteTimeUtc), current, info.SourceFile));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or NotSupportedException) { }

        }
    }

    public async Task<RecoveredWorkspace?> LoadAsync(WorkspaceRecoveryEntry entry, CancellationToken ct = default)
    {
        var entryStore = new RecoveryStore(entry.DirectoryPath);
        var loaded = await LoadPreferredGenerationAsync(entryStore, ct).ConfigureAwait(false);
        if (loaded is not null) ImageImportBudget.ValidateResources(loaded.Assets.Resources);
        return loaded;
    }

    private static async Task<RecoveredWorkspace?> LoadPreferredGenerationAsync(RecoveryStore entryStore,
        CancellationToken ct)
    {
        RecoveredWorkspace? current = null, backup = null;
        Exception? failure = null;
        try { current = await entryStore.LoadGenerationAsync(fromBackup: false, ct).ConfigureAwait(false); }
        catch (Exception error) when (error is InvalidDataException or IOException or JsonException or NotSupportedException or EndOfStreamException) { failure = error; }
        try { backup = await entryStore.LoadGenerationAsync(fromBackup: true, ct).ConfigureAwait(false); }
        catch (Exception error) when (error is InvalidDataException or IOException or JsonException or NotSupportedException or EndOfStreamException) { failure ??= error; }
        bool Meaningful(RecoveredWorkspace? value) => value is not null &&
            (HasObjects(value.State) || JsonSerializer.Serialize(value.State.LiveDocument.Canvas) != JsonSerializer.Serialize(new CanvasSettings()));
        var loaded = Meaningful(current) ? current : Meaningful(backup) ? backup : current ?? backup;
        if (loaded is null && failure is not null) throw new InvalidDataException(L.Get("Recovery.BothFilesUnreadable"), failure);
        return loaded;
    }

    public async Task<byte[]?> LoadPreviewAsync(WorkspaceRecoveryEntry entry, CancellationToken ct = default)
    {
        var entryStore = new RecoveryStore(entry.DirectoryPath);
        var restored = await LoadPreferredGenerationAsync(entryStore, ct).ConfigureAwait(false);
        if (restored is null) return null;
        var bytes = await entryStore.LoadPreviewAsync(fromBackup: restored.FromBackup, ct).ConfigureAwait(false);
        if (bytes is not null) return bytes;
        // The selected generation may have no embedded preview. Render that same
        // generation on demand; never show a preview from the unselected sibling.
        ImageImportBudget.ValidateResources(restored.Assets.Resources);
        return WorkspacePreview.Render(restored.State.LiveDocument, restored.Assets);
    }

    private static string ContentFingerprint(WorkspaceState workspace)
    {
        var content = WindowSourcePersistence.AuthoredProjection(workspace);
        // Monitor discovery alone must never turn a blank launch into a new recovery generation.
        content.LiveDocument.Viewport = default;
        content.LiveDocument.DisplayLayout = [];
        if (content.LiveDocument.Elements.Count == 0 && content.LiveDocument.Groups.Count == 0 && content.LiveDocument.Instances.Count == 0 &&
            content.LiveDocument.Definitions.Count == 0 && content.LiveDocument.Background is null) content.LiveDocument.ResourceId = Guid.Empty;
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content)));
    }
}
