using System.IO;
using System.Text.Json;
using FloatCanvas.Core;
using FloatCanvas.Storage;

namespace FloatCanvas.App;

internal sealed record WorkspaceRecoveryEntry(string DirectoryPath, DateTime SavedAtUtc, bool IsCurrent);

// DesktopSession serializes recovery operations; RecoveryStore retains its atomic package writes.
internal sealed class WorkspaceRecoveryHistory(RecoveryStore store)
{
    private string? freshBaseline;
    private bool archiveBeforeWrite = true;

    public void BeginFreshWorkspace(WorkspaceState baseline) => freshBaseline = ContentFingerprint(baseline);
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
        if (sources.Length > 0)
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
            ct.ThrowIfCancellationRequested();
            // Publish only after both generations are complete. A failed copy cannot authorize a new save.
            Directory.Move(pending, Path.Combine(history, identity));
        }
        archiveBeforeWrite = false;
    }

    public IReadOnlyList<WorkspaceRecoveryEntry> GetEntries()
    {
        var entries = new List<WorkspaceRecoveryEntry>();
        AddEntry(store.DirectoryPath, true);
        foreach (string category in new[] { "history", "quarantine" })
        {
            string parent = Path.Combine(store.DirectoryPath, category);
            if (Directory.Exists(parent))
                foreach (string directory in Directory.EnumerateDirectories(parent).Where(path => !Path.GetFileName(path).StartsWith('.')))
                    AddEntry(directory, false);
        }
        return entries.OrderByDescending(entry => entry.IsCurrent).ThenByDescending(entry => entry.SavedAtUtc).ToArray();

        void AddEntry(string directory, bool current)
        {
            var files = new[] { "workspace.floatcanvas", "workspace.previous.floatcanvas" }.Select(name => Path.Combine(directory, name)).Where(File.Exists).ToArray();
            if (files.Length > 0) entries.Add(new(directory, files.Max(File.GetLastWriteTimeUtc), current));
        }
    }

    public Task<RecoveredWorkspace?> LoadAsync(WorkspaceRecoveryEntry entry, CancellationToken ct = default) => new RecoveryStore(entry.DirectoryPath).LoadAsync(ct);

    private static string ContentFingerprint(WorkspaceState workspace)
    {
        var content = workspace.Clone();
        // Monitor discovery alone must never turn a blank launch into a new recovery generation.
        content.LiveDocument.Viewport = default;
        content.LiveDocument.DisplayLayout = [];
        return JsonSerializer.Serialize(content);
    }
}
