using FloatCanvas.Core;

namespace FloatCanvas.Storage;

public sealed record LoadedProject(SceneDocument Document, AssetLibrary Assets);

public sealed class ProjectStore
{
    public Task SaveAsync(string path, SceneDocument document, AssetLibrary assets, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assets);
        ct.ThrowIfCancellationRequested();
        // Capture immutable resource references before the caller can prune the shared library.
        var resources = PackageCodec.ResolveResources(document.ReferencedAssets, assets);
        return Task.Run(() => PackageCodec.SaveAsync(path, "project", document, resources, ct), ct);
    }
    public Task<LoadedProject> LoadAsync(string path, CancellationToken ct = default) => Task.Run(async () =>
    {
        var result = await PackageCodec.LoadAsync<SceneDocument>(path, "project", ct).ConfigureAwait(false);
        return new LoadedProject(result.Value, result.Assets);
    }, ct);
}
