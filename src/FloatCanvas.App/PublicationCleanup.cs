using System.IO;
using System.Text.Json;

namespace FloatCanvas.App;

/// <summary>Ownership evidence for incomplete multi-file publications; unknown files are never swept.</summary>
internal static class PublicationCleanup
{
    private sealed record Journal(string Owner, DateTime StartedUtc, string[] Files);
    internal static string Begin(string directory, IEnumerable<string> paths)
    {
        Directory.CreateDirectory(directory);
        string root = Path.GetFullPath(directory);
        var names = paths.Select(path =>
        {
            string full = Path.GetFullPath(path);
            if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Publication output is outside its directory.");
            return Path.GetFileName(full);
        }).ToArray();
        string journal = Path.Combine(root, ".publication-" + Guid.NewGuid().ToString("N") + ".json");
        using var file = new FileStream(journal, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        JsonSerializer.Serialize(file, new Journal("FloatCanvas.Publication.v1", DateTime.UtcNow, names)); file.Flush(true);
        return journal;
    }
    internal static void Finish(string? journal, bool published)
    {
        if (journal is null) return;
        try { if (!published) RemoveOutputs(journal, new HashSet<string>(StringComparer.OrdinalIgnoreCase), null); File.Delete(journal); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }
    internal static void Sweep(string directory, IEnumerable<string> retainedPaths, DateTime? nowUtc = null)
    {
        if (!Directory.Exists(directory)) return;
        var retained = retainedPaths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string journal in Directory.EnumerateFiles(directory, ".publication-*.json"))
            try { if (RemoveOutputs(journal, retained, (nowUtc ?? DateTime.UtcNow).AddDays(-1))) File.Delete(journal); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }
    private static bool RemoveOutputs(string journal, HashSet<string> retained, DateTime? cutoff)
    {
        var file = new FileInfo(journal);
        if (file.Length > 64 * 1024 || (file.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        var value = JsonSerializer.Deserialize<Journal>(File.ReadAllText(journal));
        if (value?.Owner != "FloatCanvas.Publication.v1" || value.Files is null || value.Files.Length > 16 || cutoff is { } time && value.StartedUtc > time) return false;
        string root = Path.GetDirectoryName(Path.GetFullPath(journal))!;
        foreach (string name in value.Files)
        {
            if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) ||
                !System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-f0-9]{32}-[a-f0-9]{32}(\.source)?\.(floatcanvas|png)$")) return false;
            string path = Path.Combine(root, name);
            if (!retained.Contains(path) && File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) File.Delete(path);
        }
        return true;
    }
    internal static void RemovePendingDirectory(string directory)
    {
        if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
        // Only our exact publication outputs, and no recursive delete of unknown files.
        foreach (string name in new[] { "workspace.floatcanvas", "workspace.previous.floatcanvas", "history-info.json", "history-info.json.tmp", ".owner" })
        {
            string path = Path.Combine(directory, name);
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) File.Delete(path);
        }
        if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }
    internal static void SweepPending(string parent)
    {
        if (!Directory.Exists(parent)) return;
        foreach (string pending in Directory.EnumerateDirectories(parent, ".pending-*"))
        {
            string marker = Path.Combine(pending, ".owner");
            if (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(pending), @"^\.pending-\d{8}-\d{9}-[a-f0-9]{32}$") || !File.Exists(marker)) continue;
            try
            {
                if (File.GetLastWriteTimeUtc(marker) < DateTime.UtcNow.AddDays(-1) && new FileInfo(marker).Length < 128 && File.ReadAllText(marker) == "FloatCanvas.History.v1") RemovePendingDirectory(pending);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
