using FloatCanvas.Core;

namespace FloatCanvas.Storage;

public sealed record LoadedProject(SceneDocument Document, AssetLibrary Assets)
{
    public ProjectFileVersion? FileVersion { get; init; }
}

public sealed class ProjectStore
{
    private readonly string applicationVersion;
    public ProjectStore(string? applicationVersion = null) => this.applicationVersion = PackageProvenance.ApplicationVersion(applicationVersion);

    public Task SaveAsync(string path, SceneDocument document, AssetLibrary assets, CancellationToken ct = default)
        => SaveVersionedAsync(path, document, assets, ct);

    public Task<ProjectFileVersion> SaveVersionedAsync(string path, SceneDocument document, AssetLibrary assets, CancellationToken ct = default, ProjectFileVersion? expectedVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assets);
        PrefabClosure.RequireSourceGraphs(document);
        ct.ThrowIfCancellationRequested();
        // Capture immutable resource references before the caller can prune the shared library.
        var resources = PackageCodec.ResolveResources(document.ReferencedAssets, assets);
        return Task.Run(() => PackageCodec.SaveAsync(path, "project", document, resources, ct, expectedVersion: expectedVersion, applicationVersion: applicationVersion), ct);
    }
    public Task<LoadedProject> LoadAsync(string path, CancellationToken ct = default) => Task.Run(async () =>
    {
        var result = await PackageCodec.LoadVersionedAsync<SceneDocument>(path, "project", ct).ConfigureAwait(false);
        return new LoadedProject(result.Value, result.Assets) { FileVersion = result.Version };
    }, ct);
}
