using System.IO;
using System.Text.Json;
using FloatCanvas.Core;
using FloatCanvas.Storage;

namespace FloatCanvas.App;

internal sealed record WorkspaceRecoveryEntry(string DirectoryPath, DateTime SavedAtUtc, bool IsCurrent, string? SourceFile = null);

// DesktopSession serializes recovery operations; RecoveryStore retains its atomic package writes.
internal sealed class WorkspaceRecoveryHistory(RecoveryStore store)
{
    private string? freshBaseline;
    private string? entryBaseline;
    public int MaximumVersions { get; set; } = 60;
    private sealed record HistoryInfo(string Fingerprint, string? SourceFile, bool Eligible = true, long PackageTicks = 0, long PackageLength = 0);
    private readonly Dictionary<string, (long Ticks, long Length, HistoryInfo Info)> legacyInfo = new(StringComparer.OrdinalIgnoreCase);

    public void EnterWorkspace(WorkspaceState workspace)
    {
        entryBaseline = ContentFingerprint(workspace);
    }
    public bool HasNewVersion(WorkspaceState workspace) => ContentFingerprint(workspace) != entryBaseline && HasValue(workspace);
    private static bool HasValue(WorkspaceState workspace) => HasObjects(workspace);
    private static bool HasObjects(WorkspaceState workspace) => workspace.LiveDocument.Elements.Count > 0 ||
        workspace.LiveDocument.Background is not null || workspace.CaptureDocument is not null;
    public void WriteCurrentInfo(WorkspaceState workspace, string? sourceFile)
    {
        var path = Path.Combine(store.DirectoryPath, "history-info.json");
        WriteInfo(path, new(ContentFingerprint(workspace), sourceFile, HasNewVersion(workspace)));
    }
    private static void WriteInfo(string path, HistoryInfo info)
    {
        var package = new FileInfo(Path.Combine(Path.GetDirectoryName(path)!, "workspace.floatcanvas"));
        if (package.Exists) info = info with { PackageTicks = package.LastWriteTimeUtc.Ticks, PackageLength = package.Length };
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(info));
        File.Move(path + ".tmp", path, true);
    }
    public async Task ArchiveAsync(WorkspaceState workspace, AssetLibrary assets, byte[]? preview,
        string? sourceFile, bool force = false, CancellationToken ct = default)
    {
        if (!(force ? HasValue(workspace) : HasNewVersion(workspace))) return;
        string fingerprint = ContentFingerprint(workspace);
        if (GetEntries().Any(e => !e.IsCurrent && EntryInfo(e.DirectoryPath)?.Fingerprint == fingerprint)) return;
        string parent = Path.Combine(store.DirectoryPath, "history");
        Directory.CreateDirectory(parent);
        string identity = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N");
        string pending = Path.Combine(parent, ".pending-" + identity);
        await new RecoveryStore(pending).SaveWithPreviewAsync(workspace, assets, preview, ct).ConfigureAwait(false);
        WriteInfo(Path.Combine(pending, "history-info.json"), new(fingerprint, sourceFile));
        ct.ThrowIfCancellationRequested();
        Directory.Move(pending, Path.Combine(parent, identity));
        Trim();
    }
    public void Trim()
    {
        string parent = Path.GetFullPath(Path.Combine(store.DirectoryPath, "history"));
        foreach (var entry in GetEntries().Where(e => !e.IsCurrent).Skip(Math.Clamp(MaximumVersions, 1, 10000)))
        {
            string target = Path.GetFullPath(entry.DirectoryPath);
            if (!string.Equals(Path.GetDirectoryName(target), parent, StringComparison.OrdinalIgnoreCase) ||
                !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(target), @"^\d{8}-\d{9}-[a-f0-9]{32}$") ||
                (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) continue;
            Directory.Delete(target, true);
        }
    }
    private HistoryInfo? EntryInfo(string directory)
    {
        string path = Path.Combine(directory, "history-info.json");
        var package = new FileInfo(Path.Combine(directory, "workspace.floatcanvas"));
        long ticks = package.Exists ? package.LastWriteTimeUtc.Ticks : 0, length = package.Exists ? package.Length : 0;
        if (File.Exists(path))
        {
            try
            {
                var metadata = JsonSerializer.Deserialize<HistoryInfo>(File.ReadAllText(path));
                if (ticks != 0 && metadata is not null && metadata.PackageTicks == ticks && metadata.PackageLength == length) return metadata;
            }
            catch (JsonException) { }
        }
        else if (legacyInfo.TryGetValue(directory, out var cached) && ticks == cached.Ticks && length == cached.Length) return cached.Info;
        var recovered = new RecoveryStore(directory).LoadAsync().GetAwaiter().GetResult();
        if (recovered is null) return null;
        var state = recovered.State;
        string fingerprint = ContentFingerprint(state);
        if (File.Exists(path))
        {
            try
            {
                var info = JsonSerializer.Deserialize<HistoryInfo>(File.ReadAllText(path));
                if (info?.Fingerprint == fingerprint) return info;
            }
            catch (JsonException) { }
        }
        var result = new HistoryInfo(fingerprint, null, HasObjects(state) ||
            JsonSerializer.Serialize(state.LiveDocument.Canvas) != JsonSerializer.Serialize(new CanvasSettings()));
        if (!File.Exists(path)) legacyInfo[directory] = (ticks, length, result);
        return result;
    }

    private bool archiveBeforeWrite = true;
    public async Task PrepareClearAsync(CancellationToken ct=default)
    {
        if(!archiveBeforeWrite)return;
        // Clearing this recovered canvas must not publish it to history. A fresh
        // launch may still hold a different previous session, which must survive.
        try
        {
            if(EntryInfo(store.DirectoryPath)?.Fingerprint==entryBaseline)
            {archiveBeforeWrite=false;return;}
        }
        catch(Exception ex) when(ex is InvalidDataException or JsonException or NotSupportedException or EndOfStreamException) { }
        await PreserveBeforeFirstWriteAsync(ct).ConfigureAwait(false);
    }

    public void BeginFreshWorkspace(WorkspaceState baseline) { freshBaseline = ContentFingerprint(baseline); EnterWorkspace(baseline); }
    public void WorkspaceRestored() => freshBaseline = null;
    public void WorkspaceSaved() => freshBaseline = null;
    public bool ShouldWrite(WorkspaceState workspace) => freshBaseline is null || ContentFingerprint(workspace) != freshBaseline;

    public async Task PreserveBeforeFirstWriteAsync(CancellationToken ct = default)
    {
        if (!archiveBeforeWrite) return;
        await PreserveCurrentAsync(ct).ConfigureAwait(false);
    }

    public async Task PreserveCurrentAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var sources = new[] { store.CurrentPath, store.BackupPath }.Where(File.Exists).ToArray();
        HistoryInfo? info;
        try { info = sources.Length > 0 ? EntryInfo(store.DirectoryPath) : null; }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or NotSupportedException or EndOfStreamException)
        {
            // Keep unreadable packages byte-exact in quarantine before replacing current recovery.
            await store.ArchiveExistingAsync(ct).ConfigureAwait(false);
            archiveBeforeWrite = false;
            return;
        }
        if (sources.Length > 0 && info is { Eligible: true } && !GetEntries().Any(e => !e.IsCurrent && EntryInfo(e.DirectoryPath)?.Fingerprint == info.Fingerprint))
        {
            string history = Path.Combine(store.DirectoryPath, "history");
            Directory.CreateDirectory(history);
            string identity = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N");
            string pending = Path.Combine(history, ".pending-" + identity);
            Directory.CreateDirectory(pending);
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
        archiveBeforeWrite = false;
        Trim();
    }

    public IReadOnlyList<WorkspaceRecoveryEntry> GetEntries()
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
            .DistinctBy(entry => EntryInfo(entry.DirectoryPath)?.Fingerprint ?? entry.DirectoryPath)
            .OrderByDescending(entry => entry.SavedAtUtc).ToArray();

        void AddEntry(string directory, bool current)
        {
            var files = new[] { "workspace.floatcanvas", "workspace.previous.floatcanvas" }.Select(name => Path.Combine(directory, name)).Where(File.Exists).ToArray();
            if (files.Length == 0) return;
            try
            {
                var info = EntryInfo(directory);
                if (info is { Eligible: true }) entries.Add(new(directory, files.Max(File.GetLastWriteTimeUtc), current, info.SourceFile));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or NotSupportedException) { }

        }
    }

    public Task<RecoveredWorkspace?> LoadAsync(WorkspaceRecoveryEntry entry, CancellationToken ct = default) => new RecoveryStore(entry.DirectoryPath).LoadAsync(ct);

    public async Task<byte[]?> LoadPreviewAsync(WorkspaceRecoveryEntry entry, CancellationToken ct = default)
    {
        var entryStore = new RecoveryStore(entry.DirectoryPath);
        var bytes = await entryStore.LoadPreviewAsync(ct: ct).ConfigureAwait(false);
        if (bytes is not null) return bytes;
        // Older history has no embedded preview. Render its own saved annotations on demand;
        // do not alter the archive or capture whatever happens to be on the desktop now.
        var restored = await entryStore.LoadAsync(ct).ConfigureAwait(false);
        return restored is null ? null : WorkspacePreview.Render(restored.State.LiveDocument, restored.Assets);
    }

    private static string ContentFingerprint(WorkspaceState workspace)
    {
        var content = workspace.Clone();
        // Monitor discovery alone must never turn a blank launch into a new recovery generation.
        content.LiveDocument.Viewport = default;
        content.LiveDocument.DisplayLayout = [];
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content)));
    }
}
