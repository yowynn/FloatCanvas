using FloatCanvas.Core;

namespace FloatCanvas.Storage;

public sealed record LoadedPrefab(PrefabPackage Package, AssetLibrary Assets)
{
    public ProjectFileVersion? FileVersion { get; init; }
}

public sealed class PrefabStore
{
    private readonly string applicationVersion;
    public PrefabStore(string? applicationVersion = null) => this.applicationVersion = PackageProvenance.ApplicationVersion(applicationVersion);
    public Task SaveAsync(string path, PrefabPackage package, AssetLibrary assets, CancellationToken ct = default) => SaveVersionedAsync(path, package, assets, ct);
    public Task<ProjectFileVersion> SaveVersionedAsync(string path, PrefabPackage package, AssetLibrary assets,
        CancellationToken ct = default, ProjectFileVersion? expectedVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); ArgumentNullException.ThrowIfNull(package); ArgumentNullException.ThrowIfNull(assets);
        ct.ThrowIfCancellationRequested(); PrefabClosure.RequireSourceGraphs(package);
        var resources = PackageCodec.ResolveResources(package.ReferencedAssets, assets);
        return Task.Run(() => PackageCodec.SaveAsync(path, "prefab", package, resources, ct, expectedVersion: expectedVersion, applicationVersion: applicationVersion), ct);
    }
    public Task<LoadedPrefab> LoadAsync(string path, CancellationToken ct = default) => Task.Run(async () =>
    {
        var result = await PackageCodec.LoadVersionedAsync<PrefabPackage>(path, "prefab", ct).ConfigureAwait(false);
        return new LoadedPrefab(result.Value, result.Assets) { FileVersion = result.Version };
    }, ct);
}
