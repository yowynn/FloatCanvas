using System.Security.Cryptography;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Storage;

/// <summary>Content identity, insensitive to a sync client merely changing timestamps.</summary>
public sealed record ProjectFileVersion(string? Sha256)
{
    public static async Task<ProjectFileVersion> ReadAsync(string path, CancellationToken ct = default)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return new(Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)));
        }
        catch (FileNotFoundException) { return new((string?)null); }
        catch (DirectoryNotFoundException) { return new((string?)null); }
    }
}

public sealed class ProjectFileConflictException(string? preservedPath = null) : IOException(
    L.Get("Project.ExternalChange") + (preservedPath is null ? "" : "\n" + L.Format("Project.ExternalCopyPreserved", preservedPath)))
{
    public string? PreservedPath { get; } = preservedPath;
}
