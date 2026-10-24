using L = FloatCanvas.Localization.AppText;
using FloatCanvas.Core;

namespace FloatCanvas.Storage;

public sealed class WorkspaceState
{
    public SceneDocument LiveDocument { get; set; } = new();
    public SceneDocument? CaptureDocument { get; set; }
    public PointD CaptureOrigin { get; set; }
    public WorkspaceState Clone() => new() { LiveDocument = LiveDocument.Clone(), CaptureDocument = CaptureDocument?.Clone(), CaptureOrigin = CaptureOrigin };
}

public sealed record RecoveredWorkspace(WorkspaceState State, AssetLibrary Assets, bool FromBackup);

public sealed class RecoveryStore
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public RecoveryStore(string? directory = null)
    {
        DirectoryPath = Path.GetFullPath(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FloatCanvas", "recovery"));
    }
    public string DirectoryPath { get; }
    public string CurrentPath => Path.Combine(DirectoryPath, "workspace.floatcanvas");
    public string BackupPath => Path.Combine(DirectoryPath, "workspace.previous.floatcanvas");
    /// <summary>Preserves byte-exact generations before the application starts a new recovery history after a failed load.</summary>
    public Task<IReadOnlyList<string>> ArchiveExistingAsync(CancellationToken ct = default) => InOrderAsync<IReadOnlyList<string>>(async () =>
    {
        var files = new[] { CurrentPath, BackupPath }.Where(File.Exists).ToArray();
        if (files.Length == 0) return Array.Empty<string>();
        string archiveDirectory = Path.Combine(DirectoryPath, "quarantine", DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(archiveDirectory);
        var archived = new List<string>();
        foreach (string source in files)
        {
            string destination = Path.Combine(archiveDirectory, Path.GetFileName(source));
            await CopyAtomicallyAsync(source, destination, ct).ConfigureAwait(false);
            archived.Add(destination);
        }
        return archived;
    }, ct);
    public Task SaveAsync(WorkspaceState state, AssetLibrary assets, CancellationToken ct = default)
        => SaveWithPreviewAsync(state, assets, null, ct);

    /// <summary>Saves a workspace and its optional transparent overlay thumbnail in one atomic package. Invalid previews are omitted.</summary>
    public Task SaveWithPreviewAsync(WorkspaceState state, AssetLibrary assets, byte[]? previewPng, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state); ArgumentNullException.ThrowIfNull(assets);
        ct.ThrowIfCancellationRequested();
        var references = state.LiveDocument.ReferencedAssets.Concat(state.CaptureDocument?.ReferencedAssets ?? []);
        var resources = PackageCodec.ResolveResources(references, assets);
        byte[]? preview = RecoveryPreview.Snapshot(previewPng);
        return InOrderAsync(async () =>
        {
            Directory.CreateDirectory(DirectoryPath);
            // Never promote a corrupt current generation over the last valid backup.
            if (File.Exists(CurrentPath))
            {
                try
                {
                    if (!PackageCodec.IsKnownValid(CurrentPath, "recovery"))
                        await PackageCodec.LoadAsync<WorkspaceState>(CurrentPath, "recovery", ct).ConfigureAwait(false);
                    await CopyAtomicallyAsync(CurrentPath, BackupPath, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or System.Text.Json.JsonException or NotSupportedException) { }
            }
            await PackageCodec.SaveAsync(CurrentPath, "recovery", state, resources, ct, preview).ConfigureAwait(false);
            return true;
        }, ct);
    }
    /// <summary>Reads a bounded thumbnail only when the requested generation is recoverable; it never substitutes a different generation's preview.</summary>
    public Task<byte[]?> LoadPreviewAsync(bool fromBackup = false, CancellationToken ct = default) =>
        InOrderAsync(() => PackageCodec.LoadRecoveryPreviewAsync(fromBackup ? BackupPath : CurrentPath, ct), ct);
    public Task<RecoveredWorkspace?> LoadAsync(CancellationToken ct = default) => InOrderAsync<RecoveredWorkspace?>(async () =>
    {
            if (!File.Exists(CurrentPath) && !File.Exists(BackupPath)) return null;
            Exception? currentFailure = null;
            if (File.Exists(CurrentPath))
            {
                try
                {
                    var result = await PackageCodec.LoadAsync<WorkspaceState>(CurrentPath, "recovery", ct).ConfigureAwait(false);
                    return new RecoveredWorkspace(result.Value, result.Assets, false);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or NotSupportedException) { currentFailure = ex; }
            }
            if (File.Exists(BackupPath))
            {
                try
                {
                    var result = await PackageCodec.LoadAsync<WorkspaceState>(BackupPath, "recovery", ct).ConfigureAwait(false);
                    return new RecoveredWorkspace(result.Value, result.Assets, true);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or NotSupportedException)
                {
                    throw new InvalidDataException(L.Get("Recovery.BothFilesUnreadable"), new AggregateException(new[] { currentFailure, ex }.OfType<Exception>()));
                }
            }
            throw new InvalidDataException(L.Get("Recovery.NoValidPreviousFile"), currentFailure);
    }, ct);
    private async Task<T> InOrderAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        // Acquire before dispatching CPU work so a late thread-pool start cannot overwrite a newer snapshot.
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(operation, ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    private static async Task CopyAtomicallyAsync(string source, string destination, CancellationToken ct)
    {
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            { await input.CopyToAsync(output, ct).ConfigureAwait(false); await output.FlushAsync(ct).ConfigureAwait(false); output.Flush(true); }
            ct.ThrowIfCancellationRequested(); File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
