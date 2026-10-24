using FloatCanvas.Core;

namespace FloatCanvas.Storage;

public sealed record LoadedProject(SceneDocument Document, AssetLibrary Assets)
{
    public ProjectFileVersion? FileVersion { get; init; }
}

public sealed class ProjectStore
{
    public Task SaveAsync(string path, SceneDocument document, AssetLibrary assets, CancellationToken ct = default)
        => SaveVersionedAsync(path, document, assets, ct);

    public Task<ProjectFileVersion> SaveVersionedAsync(string path, SceneDocument document, AssetLibrary assets, CancellationToken ct = default, ProjectFileVersion? expectedVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assets);
        ct.ThrowIfCancellationRequested();
        // Capture immutable resource references before the caller can prune the shared library.
        var resources = PackageCodec.ResolveResources(document.ReferencedAssets, assets);
        return Task.Run(() => PackageCodec.SaveAsync(path, "project", document, resources, ct, expectedVersion: expectedVersion), ct);
    }
    public Task<LoadedProject> LoadAsync(string path, CancellationToken ct = default) => Task.Run(async () =>
    {
        var version = await ProjectFileVersion.ReadAsync(path, ct).ConfigureAwait(false);
        var result = await PackageCodec.LoadAsync<SceneDocument>(path, "project", ct).ConfigureAwait(false);
        if (version != await ProjectFileVersion.ReadAsync(path, ct).ConfigureAwait(false)) throw new ProjectFileConflictException();
        return new LoadedProject(result.Value, result.Assets) { FileVersion = version };
    }, ct);
}
