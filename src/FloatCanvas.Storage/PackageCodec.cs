using L = FloatCanvas.Localization.AppText;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using FloatCanvas.Core;

namespace FloatCanvas.Storage;

internal static class PackageCodec
{
    private sealed class Manifest
    {
        public required string Format { get; set; }
        public required int Version { get; set; }
        public required string Payload { get; set; }
        public required List<ManifestAsset> Assets { get; set; }
    }
    private sealed class ManifestAsset
    {
        public required string Id { get; set; }
        public required string MediaType { get; set; }
        public required long Length { get; set; }
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 128, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private const long MaxPackageBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxExpandedBytes = 1024L * 1024 * 1024;
    private const long MaxAssetBytes = 512L * 1024 * 1024;
    public static bool IsAssetId(string? id) => id is { Length: 64 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static AssetResource[] ResolveResources(IEnumerable<string> ids, AssetLibrary assets) => ids.Distinct(StringComparer.Ordinal).Select(id =>
        assets.TryGet(id, out var resource) ? resource : throw new InvalidDataException(L.Format("Assets.MissingResource", id))).ToArray();

    public static async Task SaveAsync<T>(string path, string kind, T value, AssetResource[] resources, CancellationToken ct, byte[]? previewPng = null)
    {
        ct.ThrowIfCancellationRequested(); Validate(value);
        // Never emit a v1 envelope for the richer editable model: published v1 readers
        // ignore fields they do not know and would silently discard groups and styles.
        // Clone instead of upgrading the caller's live document as a side effect of save.
        value = CurrentVersionCopy(value);
        var references = References(value).ToHashSet(StringComparer.Ordinal);
        if (!references.SetEquals(resources.Select(r => r.Id))) throw new InvalidDataException(L.Get("Project.IncompleteAssets"));
        if (resources.Length > 4096 || resources.Any(r => r.Data.Length > MaxAssetBytes) || resources.Sum(r => (long)r.Data.Length) > MaxExpandedBytes) throw new InvalidDataException(L.Get("Project.AssetCapacityExceeded"));
        if (resources.Any(r => string.IsNullOrWhiteSpace(r.MediaType) || r.MediaType.Length > 128)) throw new InvalidDataException(L.Get("Project.InvalidAssetMediaType"));
        string payload = kind == "project" ? "scene.json" : "workspace.json";
        var manifest = new Manifest
        {
            Format = "FloatCanvas." + kind, Version = SceneDocument.CurrentVersion, Payload = payload,
            Assets = resources.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => new ManifestAsset { Id = r.Id, MediaType = r.MediaType, Length = r.Data.Length }).ToList()
        };
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    await WriteJsonAsync(archive, "manifest.json", manifest, ct).ConfigureAwait(false);
                    await WriteJsonAsync(archive, payload, value, ct).ConfigureAwait(false);
                    foreach (var resource in resources)
                    {
                        ct.ThrowIfCancellationRequested();
                        var entry = archive.CreateEntry("assets/" + resource.Id, CompressionLevel.NoCompression);
                        await using var stream = entry.Open();
                        await stream.WriteAsync(resource.Data, ct).ConfigureAwait(false);
                    }
                    if (kind == "recovery" && previewPng is not null)
                    {
                        var entry = archive.CreateEntry(RecoveryPreview.EntryName, CompressionLevel.NoCompression);
                        await using var stream = entry.Open();
                        await stream.WriteAsync(previewPng, ct).ConfigureAwait(false);
                    }
                }
                await output.FlushAsync(ct).ConfigureAwait(false); output.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static async Task<(T Value, AssetLibrary Assets)> LoadAsync<T>(string path, string kind, CancellationToken ct)
    {
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length > MaxPackageBytes) throw new InvalidDataException(L.Get("Project.PackageTooLarge"));
            using var archive = new ZipArchive(file, ZipArchiveMode.Read);
            bool hasPreview = kind == "recovery" && archive.GetEntry(RecoveryPreview.EntryName) is not null;
            if (archive.Entries.Count > (hasPreview ? 4099 : 4098) || archive.Entries.Select(e => e.FullName).Distinct(StringComparer.Ordinal).Count() != archive.Entries.Count) throw new InvalidDataException(L.Get("Project.InvalidArchiveEntries"));
            long expanded = 0;
            foreach (var entry in archive.Entries)
            {
                // The optional preview is never decompressed during recovery and cannot
                // invalidate otherwise complete scene/assets, even if its metadata is bad.
                if (hasPreview && entry.FullName == RecoveryPreview.EntryName) continue;
                if (entry.Length < 0 || entry.Length > MaxAssetBytes || expanded > MaxExpandedBytes - entry.Length) throw new InvalidDataException(L.Get("Project.ExpandedCapacityExceeded"));
                expanded += entry.Length;
            }
            var manifest = await ReadJsonAsync<Manifest>(RequiredEntry(archive, "manifest.json"), 4 * 1024 * 1024, ct).ConfigureAwait(false);
            if (manifest.Format != "FloatCanvas." + kind) throw new InvalidDataException(L.Get("Project.UnexpectedFormat"));
            if (manifest.Version is not (1 or 2 or SceneDocument.CurrentVersion)) throw new InvalidDataException(L.Format("Project.UnsupportedVersion", manifest.Version));
            string payload = kind == "project" ? "scene.json" : "workspace.json";
            if (manifest.Payload != payload || manifest.Assets is null || manifest.Assets.Count > 4096) throw new InvalidDataException(L.Get("Project.InvalidManifest"));
            if (manifest.Assets.Any(a => a is null || !IsAssetId(a.Id) || string.IsNullOrWhiteSpace(a.MediaType) || a.MediaType.Length > 128 || a.Length <= 0 || a.Length > MaxAssetBytes) || manifest.Assets.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() != manifest.Assets.Count)
                throw new InvalidDataException(L.Get("Project.InvalidAssetManifest"));
            var knownEntries = manifest.Assets.Select(a => "assets/" + a.Id).Append("manifest.json").Append(payload).ToHashSet(StringComparer.Ordinal);
            if (hasPreview) knownEntries.Add(RecoveryPreview.EntryName);
            if (!knownEntries.SetEquals(archive.Entries.Select(e => e.FullName))) throw new InvalidDataException(L.Get("Project.UnexpectedArchiveContents"));
            T value = await ReadJsonAsync<T>(RequiredEntry(archive, payload), 128 * 1024 * 1024, ct).ConfigureAwait(false);
            Validate(value);
            RequirePayloadVersion(value, manifest.Version);
            if (!References(value).ToHashSet(StringComparer.Ordinal).SetEquals(manifest.Assets.Select(a => a.Id))) throw new InvalidDataException(L.Get("Project.AssetReferencesMismatch"));
            var assets = new AssetLibrary();
            foreach (var asset in manifest.Assets)
            {
                ct.ThrowIfCancellationRequested();
                var entry = RequiredEntry(archive, "assets/" + asset.Id);
                if (entry.Length != asset.Length) throw new InvalidDataException(L.Get("Project.AssetLengthMismatch"));
                byte[] bytes = await ReadBytesAsync(entry, MaxAssetBytes, ct).ConfigureAwait(false);
                string actualId = assets.Add(bytes, asset.MediaType);
                if (!StringComparer.Ordinal.Equals(actualId, asset.Id)) throw new InvalidDataException(L.Get("Project.AssetHashMismatch"));
            }
            // Legacy scenes gain only the format marker; defaults retain their original
            // arrow, radius, alpha and resource semantics. The deserialized graph is ours.
            UpgradeVersion(value);
            return (value, assets);
        }
        catch (JsonException ex) { throw new InvalidDataException(L.Get("Project.InvalidJson"), ex); }
        catch (OverflowException ex) { throw new InvalidDataException(L.Get("Project.DataOutOfRange"), ex); }
    }
    internal static async Task<byte[]?> LoadRecoveryPreviewAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length > MaxPackageBytes) return null;
            using var archive = new ZipArchive(file, ZipArchiveMode.Read);
            var entry = archive.GetEntry(RecoveryPreview.EntryName);
            if (entry is null || entry.Length > RecoveryPreview.MaximumBytes) return null;
            byte[] bytes = await ReadBytesAsync(entry, RecoveryPreview.MaximumBytes, ct).ConfigureAwait(false);
            if (!RecoveryPreview.IsValid(bytes)) return null;
            // Do not show a plausible thumbnail for a generation whose scene/assets
            // would actually be rejected and replaced by the previous generation.
            await LoadAsync<WorkspaceState>(path, "recovery", ct).ConfigureAwait(false);
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or OverflowException) { return null; }
    }
    private static ZipArchiveEntry RequiredEntry(ZipArchive archive, string name) => archive.GetEntry(name) ?? throw new InvalidDataException(L.Format("Project.MissingEntry", name));
    private static T CurrentVersionCopy<T>(T value)
    {
        T copy = value switch
        {
            SceneDocument document => (T)(object)document.Clone(),
            WorkspaceState workspace => (T)(object)workspace.Clone(),
            _ => throw new InvalidDataException(L.Get("Project.InvalidContentType"))
        };
        UpgradeVersion(copy);
        return copy;
    }
    private static IEnumerable<SceneDocument> Documents<T>(T value) => value switch
    {
        SceneDocument document => [document],
        WorkspaceState workspace => workspace.CaptureDocument is { } capture ? [workspace.LiveDocument, capture] : [workspace.LiveDocument],
        _ => throw new InvalidDataException(L.Get("Project.InvalidContentType"))
    };
    private static void RequirePayloadVersion<T>(T value, int packageVersion)
    {
        foreach (var document in Documents(value))
            if (document.Version != packageVersion) throw new InvalidDataException(L.Format("Scene.UnsupportedVersion", document.Version));
    }
    private static void UpgradeVersion<T>(T value)
    {
        foreach (var document in Documents(value)) document.Version = SceneDocument.CurrentVersion;
    }
    private static void Validate<T>(T value)
    {
        switch (value)
        {
            case SceneDocument document: SceneValidation.Validate(document); break;
            case WorkspaceState workspace: SceneValidation.Validate(workspace); break;
            default: throw new InvalidDataException(L.Get("Project.InvalidContentType"));
        }
    }
    private static IEnumerable<string> References<T>(T value) => value switch
    {
        SceneDocument document => document.ReferencedAssets,
        WorkspaceState workspace => workspace.LiveDocument.ReferencedAssets.Concat(workspace.CaptureDocument?.ReferencedAssets ?? []).Distinct(StringComparer.Ordinal),
        _ => throw new InvalidDataException(L.Get("Project.InvalidContentType"))
    };
    private static async Task WriteJsonAsync<T>(ZipArchive archive, string name, T value, CancellationToken ct)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (bytes.Length > (name == "manifest.json" ? 4 * 1024 * 1024 : 128 * 1024 * 1024)) throw new InvalidDataException(L.Get("Project.JsonTooLarge"));
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        await using var stream = entry.Open();
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }
    private static async Task<T> ReadJsonAsync<T>(ZipArchiveEntry entry, long maximumBytes, CancellationToken ct)
    {
        byte[] bytes = await ReadBytesAsync(entry, maximumBytes, ct).ConfigureAwait(false);
        using var json = JsonDocument.Parse(bytes, new() { MaxDepth = 128 });
        RejectDuplicateProperties(json.RootElement);
        // Scene version and workspace document identity are mandatory, not inferred from C# defaults.
        if (typeof(T) == typeof(SceneDocument)) RequireSceneProperties(json.RootElement);
        if (typeof(T) == typeof(WorkspaceState))
        {
            if (!json.RootElement.TryGetProperty("LiveDocument", out var live) || !json.RootElement.TryGetProperty("CaptureOrigin", out _)) throw new InvalidDataException(L.Get("Recovery.MissingRequiredFields"));
            RequireSceneProperties(live);
            if (json.RootElement.TryGetProperty("CaptureDocument", out var capture) && capture.ValueKind != JsonValueKind.Null) RequireSceneProperties(capture);
        }
        return json.RootElement.Deserialize<T>(JsonOptions) ?? throw new InvalidDataException(L.Get("Project.EmptyContent"));
    }
    private static void RequireSceneProperties(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object || new[] { "Version", "Kind", "Elements", "Viewport", "LayerOpacity", "NextNumber" }.Any(p => !json.TryGetProperty(p, out _))) throw new InvalidDataException(L.Get("Scene.MissingRequiredFields"));
        if (json.GetProperty("Version").TryGetInt32(out int version) && version >= 3)
        {
            RequireProperties(json, "Canvas");
            var canvas = json.GetProperty("Canvas");
            RequireProperties(canvas, "BackgroundColor", "Projection", "Grid");
            var projection = canvas.GetProperty("Projection");
            RequireProperties(projection, "Enabled", "Columns", "Rows", "Origin", "CellSize", "Quad", "Matrix", "AlgorithmVersion");
            RequireMatrixProperties(projection.GetProperty("Matrix"));
            RequireProperties(projection.GetProperty("Origin"), "X", "Y");
            RequireProperties(canvas.GetProperty("Grid"), "Visible", "Color", "Width", "Pattern");
        }
        if (json.GetProperty("Elements").ValueKind == JsonValueKind.Array)
            foreach (var element in json.GetProperty("Elements").EnumerateArray()) RequirePlacementProperties(element);
        if (json.TryGetProperty("Background", out var background) && background.ValueKind != JsonValueKind.Null) RequirePlacementProperties(background);
    }
    private static void RequirePlacementProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("ProjectivePlacement", out var placement) && placement.ValueKind != JsonValueKind.Null)
            RequireMatrixProperties(placement);
    }
    private static void RequireMatrixProperties(JsonElement matrix) => RequireProperties(matrix, "M11", "M12", "M13", "M21", "M22", "M23", "M31", "M32", "M33");
    private static void RequireProperties(JsonElement value, params string[] properties)
    {
        if (value.ValueKind != JsonValueKind.Object || properties.Any(p => !value.TryGetProperty(p, out _)))
            throw new InvalidDataException(L.Get("Scene.MissingRequiredFields"));
    }
    private static void RejectDuplicateProperties(JsonElement json)
    {
        if (json.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in json.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException(L.Get("Project.DuplicateJsonFields"));
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (json.ValueKind == JsonValueKind.Array) foreach (var item in json.EnumerateArray()) RejectDuplicateProperties(item);
    }
    private static async Task<byte[]> ReadBytesAsync(ZipArchiveEntry entry, long maximumBytes, CancellationToken ct)
    {
        if (entry.Length < 0 || entry.Length > maximumBytes || entry.Length > int.MaxValue) throw new InvalidDataException(L.Format("Project.EntryTooLarge", entry.FullName));
        byte[] bytes = new byte[(int)entry.Length];
        await using var stream = entry.Open();
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        byte[] extra = new byte[1];
        if (await stream.ReadAsync(extra, ct).ConfigureAwait(false) != 0) throw new InvalidDataException(L.Get("Project.InvalidEntryLength"));
        return bytes;
    }
}
