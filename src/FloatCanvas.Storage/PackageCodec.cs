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
        public string? CreatedWith { get; set; }
        public string? LastSavedWith { get; set; }
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

    public static async Task<ProjectFileVersion> SaveAsync<T>(string path, string kind, T value, AssetResource[] resources, CancellationToken ct, byte[]? previewPng = null, ProjectFileVersion? expectedVersion = null, string? applicationVersion = null)
    {
        ct.ThrowIfCancellationRequested(); Validate(value);
        string writerVersion = PackageProvenance.ApplicationVersion(applicationVersion);
        // Never emit a v1 envelope for the richer editable model: published v1 readers
        // ignore fields they do not know and would silently discard groups and styles.
        // Clone instead of upgrading the caller's live document as a side effect of save.
        value = CurrentVersionCopy(value);
        var references = References(value).ToHashSet(StringComparer.Ordinal);
        if (!references.SetEquals(resources.Select(r => r.Id))) throw new InvalidDataException(L.Get("Project.IncompleteAssets"));
        if (resources.Length > 4096 || resources.Any(r => r.Data.Length > MaxAssetBytes) || resources.Sum(r => (long)r.Data.Length) > MaxExpandedBytes) throw new InvalidDataException(L.Get("Project.AssetCapacityExceeded"));
        if (resources.Any(r => string.IsNullOrWhiteSpace(r.MediaType) || r.MediaType.Length > 128)) throw new InvalidDataException(L.Get("Project.InvalidAssetMediaType"));
        string payload = PayloadName(kind);
        var manifest = new Manifest
        {
            Format = "FloatCanvas." + kind, Version = SceneDocument.CurrentVersion, Payload = payload,
            CreatedWith = Provenance(value).CreatedWithForSave(writerVersion), LastSavedWith = writerVersion,
            Assets = resources.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => new ManifestAsset { Id = r.Id, MediaType = r.MediaType, Length = r.Data.Length }).ToList()
        };
        PackageProvenance.ValidateVersion(manifest.CreatedWith, allowNull: true);
        byte[] manifestBytes = SerializeJson("manifest.json", manifest);
        byte[] payloadBytes = SerializeJson(payload, value);
        EnsureExpandedCapacity(resources.Sum(r => (long)r.Data.Length), manifestBytes.LongLength, payloadBytes.LongLength);
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                await WriteBytesAsync(archive, "manifest.json", manifestBytes, ct).ConfigureAwait(false);
                await WriteBytesAsync(archive, payload, payloadBytes, ct).ConfigureAwait(false);
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
            output.Position = 0;
            var writtenVersion = new ProjectFileVersion(Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(output, ct).ConfigureAwait(false)));
            await output.DisposeAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (expectedVersion is not null && await ProjectFileVersion.ReadAsync(path, ct).ConfigureAwait(false) != expectedVersion)
                throw new ProjectFileConflictException();
            if (expectedVersion is not null && File.Exists(path))
            {
                // Preserve the replaced generation until its identity is verified. This also
                // retains an external write racing the final comparison instead of losing it.
                string previous = path + "." + Guid.NewGuid().ToString("N") + ".save-backup";
                File.Replace(temporary, path, previous);
                if (await ProjectFileVersion.ReadAsync(previous, ct).ConfigureAwait(false) != expectedVersion)
                    throw new ProjectFileConflictException(previous);
                File.Delete(previous);
            }
            else File.Move(temporary, path, overwrite: expectedVersion is null);
            await using (var committed = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                string committedHash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(committed, ct).ConfigureAwait(false));
                if (committedHash == writtenVersion.Sha256) ValidatedPackageCache.Remember(committed.SafeFileHandle, kind);
            }
            // The origin object is shared with editor/undo snapshots, but is excluded
            // from scene content. Failed writes never establish or alter its identity.
            Provenance(value).RecordSuccessfulSave(writerVersion);
            foreach (var document in Documents(value)) { document.Provenance.RecordSuccessfulSave(writerVersion); document.RecordResourceIdentityPersistence(); }
            if (value is PrefabPackage package) package.RecordResourceIdentityPersistence();
            return writtenVersion;
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
            if (!SceneFormatMigration.Supports(manifest.Version)) throw new InvalidDataException(L.Format("Project.UnsupportedVersion", manifest.Version));
            if (kind == "prefab" && !SceneFormatMigration.SupportsPrefab(manifest.Version)) throw new InvalidDataException(L.Format("Project.UnsupportedVersion", manifest.Version));
            PackageProvenance.ValidateVersion(manifest.CreatedWith, allowNull: true);
            PackageProvenance.ValidateVersion(manifest.LastSavedWith, allowNull: manifest.Version < 9);
            string payload = PayloadName(kind);
            if (manifest.Payload != payload || manifest.Assets is null || manifest.Assets.Count > 4096) throw new InvalidDataException(L.Get("Project.InvalidManifest"));
            if (manifest.Assets.Any(a => a is null || !IsAssetId(a.Id) || string.IsNullOrWhiteSpace(a.MediaType) || a.MediaType.Length > 128 || a.Length <= 0 || a.Length > MaxAssetBytes) || manifest.Assets.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() != manifest.Assets.Count)
                throw new InvalidDataException(L.Get("Project.InvalidAssetManifest"));
            var knownEntries = manifest.Assets.Select(a => "assets/" + a.Id).Append("manifest.json").Append(payload).ToHashSet(StringComparer.Ordinal);
            if (hasPreview) knownEntries.Add(RecoveryPreview.EntryName);
            if (!knownEntries.SetEquals(archive.Entries.Select(e => e.FullName))) throw new InvalidDataException(L.Get("Project.UnexpectedArchiveContents"));
            T value = await ReadJsonAsync<T>(RequiredEntry(archive, payload), JsonCapacity(payload), ct).ConfigureAwait(false);
            Validate(value);
            RequirePayloadVersion(value, manifest.Version);
            if (UpgradeVersion(value)) ValidateCurrent(value);
            var provenance = new DocumentProvenance(manifest.CreatedWith, manifest.LastSavedWith);
            foreach (var document in Documents(value)) { document.Provenance = provenance; if (manifest.Version >= 11) document.RecordResourceIdentityPersistence(); }
            if (value is PrefabPackage prefab) { prefab.Provenance = provenance; prefab.RecordResourceIdentityPersistence(); }
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
            ValidatedPackageCache.Remember(file.SafeFileHandle, kind);
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
            if (!ValidatedPackageCache.Contains(file.SafeFileHandle, "recovery"))
                await LoadAsync<WorkspaceState>(path, "recovery", ct).ConfigureAwait(false);
            if (!ValidatedPackageCache.Contains(file.SafeFileHandle, "recovery") && OperatingSystem.IsWindows()) return null;
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or OverflowException) { return null; }
    }
    internal static bool IsKnownValid(string path, string kind)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return ValidatedPackageCache.Contains(file.SafeFileHandle, kind);
    }
    private static ZipArchiveEntry RequiredEntry(ZipArchive archive, string name) => archive.GetEntry(name) ?? throw new InvalidDataException(L.Format("Project.MissingEntry", name));
    private static T CurrentVersionCopy<T>(T value)
    {
        T copy = value switch
        {
            SceneDocument document => (T)(object)document.Clone(),
            WorkspaceState workspace => (T)(object)workspace.Clone(),
            PrefabPackage package => (T)(object)package.Clone(),
            _ => throw new InvalidDataException(L.Get("Project.InvalidContentType"))
        };
        if (UpgradeVersion(copy)) ValidateCurrent(copy);
        return copy;
    }
    private static IEnumerable<SceneDocument> Documents<T>(T value) => value switch
    {
        SceneDocument document => [document],
        WorkspaceState workspace => new[] { workspace.LiveDocument }.Concat(workspace.CaptureDocument is { } capture ? [capture] : [])
            .Concat(PrefabDraftState.Enumerate(workspace.PendingPrefabEdit).Select(draft => draft.DraftDocument)),
        PrefabPackage => [],
        _ => throw new InvalidDataException(L.Get("Project.InvalidContentType"))
    };
    private static DocumentProvenance Provenance<T>(T value) => value is PrefabPackage package ? package.Provenance : Documents(value).First().Provenance;
    private static string PayloadName(string kind) => kind switch { "project" => "scene.json", "recovery" => "workspace.json", "prefab" => "prefab.json", _ => throw new InvalidDataException(L.Get("Project.InvalidContentType")) };
    private static void RequirePayloadVersion<T>(T value, int packageVersion)
    {
        if (value is PrefabPackage prefab && prefab.Version != packageVersion) throw new InvalidDataException(L.Format("Scene.UnsupportedVersion", prefab.Version));
        foreach (var document in Documents(value))
            if (document.Version != packageVersion) throw new InvalidDataException(L.Format("Scene.UnsupportedVersion", document.Version));
    }
    private static bool UpgradeVersion<T>(T value)
    {
        if (value is PrefabPackage package)
        {
            bool changed = package.Version != SceneDocument.CurrentVersion;
            SceneFormatMigration.Upgrade(package);
            return changed;
        }
        bool upgraded = false;
        foreach (var document in Documents(value))
        {
            upgraded |= document.Version != SceneDocument.CurrentVersion;
            SceneFormatMigration.Upgrade(document);
        }
        return upgraded;
    }
    private static void ValidateCurrent<T>(T value)
    {
        foreach (var document in Documents(value))
            if (document.Version != SceneDocument.CurrentVersion)
                throw new InvalidDataException(L.Format("Scene.UnsupportedVersion", document.Version));
        Validate(value);
    }
    private static void Validate<T>(T value)
    {
        switch (value)
        {
            case SceneDocument document: SceneValidation.Validate(document); break;
            case WorkspaceState workspace: SceneValidation.Validate(workspace); break;
            case PrefabPackage package: SceneValidation.Validate(package); break;
            default: throw new InvalidDataException(L.Get("Project.InvalidContentType"));
        }
    }
    private static IEnumerable<string> References<T>(T value) => value switch
    {
        SceneDocument document => document.ReferencedAssets,
        WorkspaceState workspace => Documents(workspace).SelectMany(document => document.ReferencedAssets).Distinct(StringComparer.Ordinal),
        PrefabPackage package => package.ReferencedAssets,
        _ => throw new InvalidDataException(L.Get("Project.InvalidContentType"))
    };
    internal static void EnsureExpandedCapacity(long resources, long manifest, long payload, long maximum = MaxExpandedBytes)
    {
        if (resources < 0 || manifest < 0 || payload < 0 || resources > maximum || manifest > maximum - resources || payload > maximum - resources - manifest)
            throw new InvalidDataException(L.Get("Project.ExpandedCapacityExceeded"));
    }
    private static long JsonCapacity(string name, long maximumSceneBytes = SceneLimits.MaximumSceneJsonBytes) => name switch
    {
        "manifest.json" => 4L * 1024 * 1024,
        "scene.json" => maximumSceneBytes,
        "prefab.json" => maximumSceneBytes,
        // Each document is independently checked by SceneValidation. The finite envelope
        // contains property names and CaptureOrigin; it must not consume either scene's budget.
        "workspace.json" => Math.Min(MaxAssetBytes, checked((SceneLimits.MaximumGroupDepth + 2L) * maximumSceneBytes + 65536)),
        _ => throw new InvalidDataException(L.Get("Project.InvalidContentType"))
    };
    private static byte[] SerializeJson<T>(string name, T value, long maximumSceneBytes = SceneLimits.MaximumSceneJsonBytes)
    {
        SceneLimits.ValidatePayloadJsonCapacity(value, JsonCapacity(name, maximumSceneBytes));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (bytes.LongLength > JsonCapacity(name, maximumSceneBytes)) throw new InvalidDataException(L.Get("Project.JsonTooLarge"));
        return bytes;
    }
    private static async Task WriteBytesAsync(ZipArchive archive, string name, byte[] bytes, CancellationToken ct)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        await using var stream = entry.Open();
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }
    private static async Task<T> ReadJsonAsync<T>(ZipArchiveEntry entry, long maximumBytes, CancellationToken ct)
    {
        byte[] bytes = await ReadBytesAsync(entry, maximumBytes, ct).ConfigureAwait(false);
        using var json = JsonDocument.Parse(bytes, new() { MaxDepth = 128 });
        RejectDuplicateProperties(json.RootElement);
        if (typeof(T) == typeof(Manifest) && json.RootElement.TryGetProperty("Version", out var packageVersion) && packageVersion.ValueKind == JsonValueKind.Number &&
            packageVersion.TryGetInt32(out int version) && version >= 9)
            RequireProperties(json.RootElement, "CreatedWith", "LastSavedWith");
        // Scene version and workspace document identity are mandatory, not inferred from C# defaults.
        if (typeof(T) == typeof(SceneDocument)) RequireSceneProperties(json.RootElement);
        if (typeof(T) == typeof(PrefabPackage)) RequirePrefabProperties(json.RootElement);
        if (typeof(T) == typeof(WorkspaceState))
        {
            if (!json.RootElement.TryGetProperty("LiveDocument", out var live) || !json.RootElement.TryGetProperty("CaptureOrigin", out _)) throw new InvalidDataException(L.Get("Recovery.MissingRequiredFields"));
            RequireSceneProperties(live);
            if (json.RootElement.TryGetProperty("CaptureDocument", out var capture) && capture.ValueKind != JsonValueKind.Null) RequireSceneProperties(capture);
            if (json.RootElement.TryGetProperty("PendingPrefabEdit", out var pending) && pending.ValueKind != JsonValueKind.Null)
            {
                if (ReadFormatVersion(live) < 11) throw new InvalidDataException("Legacy workspace cannot contain prefab edit state.");
                int depth = 0;
                while (pending.ValueKind != JsonValueKind.Null)
                {
                    if (++depth > SceneLimits.MaximumGroupDepth) throw new InvalidDataException("Nested prefab edit recovery is too deep.");
                    RequireProperties(pending, "OwnerResourceId", "DefinitionId", "ExpectedRevision", "DraftDocument", "ReturnScopeId", "ReturnSelection", "DraftScopeId", "DraftSelection", "IsSourceProject", "SourcePath", "SourceFileSha256");
                    RequireSceneProperties(pending.GetProperty("DraftDocument"));
                    if (pending.TryGetProperty("PreviewOrigin", out var previewOrigin) && previewOrigin.ValueKind != JsonValueKind.Null)
                    {
                        if (ReadFormatVersion(pending.GetProperty("DraftDocument")) < 17) throw new InvalidDataException("Subproject preview origins require scene format 17.");
                        RequireProperties(previewOrigin, "X", "Y");
                    }
                    if (!pending.TryGetProperty("ParentEdit", out pending)) break;
                }
            }
        }
        return json.RootElement.Deserialize<T>(JsonOptions) ?? throw new InvalidDataException(L.Get("Project.EmptyContent"));
    }
    private static void RequireSceneProperties(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object || new[] { "Version", "Kind", "Elements", "Viewport", "LayerOpacity", "NextNumber" }.Any(p => !json.TryGetProperty(p, out _))) throw new InvalidDataException(L.Get("Scene.MissingRequiredFields"));
        int version = ReadFormatVersion(json);
        if (version >= 16)
        {
            RequireProperties(json, "RootCanvasAssignments");
            var assignments = json.GetProperty("RootCanvasAssignments");
            if (assignments.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid root canvas assignments.");
            var rootIds = new HashSet<Guid>();
            foreach (var pair in assignments.EnumerateObject())
                if (rootIds.Count >= SceneLimits.MaximumElements || !Guid.TryParse(pair.Name, out var id) || id == Guid.Empty || !rootIds.Add(id))
                    throw new InvalidDataException("Invalid or repeated root canvas identity.");
        }
        else if (json.TryGetProperty("RootCanvasAssignments", out _)) throw new InvalidDataException("Root canvases require scene format 16.");
        if (version >= 17)
        {
            RequireProperties(json, "RootPreviewCanvases");
            var previews = json.GetProperty("RootPreviewCanvases");
            if (previews.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid root preview canvases.");
            var roots = new HashSet<RootCanvasKind>();
            foreach (var pair in previews.EnumerateObject())
            {
                if (!Enum.TryParse<RootCanvasKind>(pair.Name, out var root) || root is not (RootCanvasKind.Foreground or RootCanvasKind.Background) || !roots.Add(root))
                    throw new InvalidDataException("Invalid or repeated root preview canvas.");
                RequirePreviewProperties(pair.Value, version);
            }
        }
        else if (json.TryGetProperty("RootPreviewCanvases", out _)) throw new InvalidDataException("Subproject canvases require scene format 17.");
        if (version >= 3)
        {
            RequireProperties(json, "Canvas");
            var canvas = json.GetProperty("Canvas");
            RequireCanvasProperties(canvas, version);
        }
        if (json.GetProperty("Elements").ValueKind == JsonValueKind.Array)
            foreach (var element in json.GetProperty("Elements").EnumerateArray()) { RequirePlacementProperties(element); RequireAppearanceProperties(element, version); }
        int graphVersion = version;
        if (graphVersion >= 10)
        {
            RequireProperties(json, "Groups");
            if (json.GetProperty("Groups").ValueKind != JsonValueKind.Array) throw new InvalidDataException(L.Get("Scene.MissingRequiredFields"));
            foreach (var group in json.GetProperty("Groups").EnumerateArray())
            {
                RequireProperties(group, "Id", "ParentId", "Name", "Transform", "ElevationScale", "Visible", "Locked", "OrderMode");
                RequireProperties(group.GetProperty("Transform"), "M11", "M12", "M21", "M22", "OffsetX", "OffsetY");
                RequireOptionalPreview(group, version);
            }
        }
        if (graphVersion >= 11)
        {
            RequireProperties(json, "ResourceId", "Instances", "Definitions");
            RequireGraphProperties(json, version);
            RequireDefinitions(json.GetProperty("Definitions"), version);
        }
        else if (json.TryGetProperty("Instances", out _) || json.TryGetProperty("Definitions", out _) || json.TryGetProperty("ResourceId", out _))
            throw new InvalidDataException("Prefab fields require scene format 11.");
        if (json.TryGetProperty("Background", out var background) && background.ValueKind != JsonValueKind.Null) { RequirePlacementProperties(background); RequireAppearanceProperties(background, version); }
    }
    private static void RequireCanvasProperties(JsonElement canvas, int version)
    {
        RequireProperties(canvas, "BackgroundColor", "Projection", "Grid");
        if (version >= 5) RequireProperties(canvas, "DepthPriorityOcclusion");
        var projection = canvas.GetProperty("Projection");
        RequireProperties(projection, "Enabled", "Columns", "Rows", "Origin", "CellSize", "Quad", "Matrix", "AlgorithmVersion");
        if (version is 6 or 7) RequireProperties(projection, "LockedGridCellSize");
        if (version >= 8)
        {
            RequireProperties(projection, "PinLocation", "PinPlanePoint", "ReferenceBounds");
            if (projection.GetProperty("ReferenceBounds").ValueKind != JsonValueKind.Null)
                RequireProperties(projection.GetProperty("ReferenceBounds"), "X", "Y", "Width", "Height");
            if (projection.GetProperty("PinPlanePoint").ValueKind != JsonValueKind.Null)
                RequireProperties(projection.GetProperty("PinPlanePoint"), "X", "Y");
            RequireProperties(canvas.GetProperty("Grid"), "FlatTranslation");
            RequireProperties(canvas.GetProperty("Grid").GetProperty("FlatTranslation"), "X", "Y");
        }
        RequireMatrixProperties(projection.GetProperty("Matrix"));
        RequireProperties(projection.GetProperty("Origin"), "X", "Y");
        if (projection.TryGetProperty("PlaneBounds", out var planeBounds) && planeBounds.ValueKind != JsonValueKind.Null)
            RequireProperties(planeBounds, "X", "Y", "Width", "Height");
        RequireProperties(canvas.GetProperty("Grid"), "Visible", "Color", "Width", "Pattern");
        if (canvas.GetProperty("Grid").TryGetProperty("PlaneOrigin", out var planeOrigin) && planeOrigin.ValueKind != JsonValueKind.Null)
            RequireProperties(planeOrigin, "X", "Y");
        if (version >= 7)
        {
            var grid = canvas.GetProperty("Grid");
            RequireProperties(grid, "FlatCellSize", "FlatOrigin");
            if (grid.GetProperty("FlatOrigin").ValueKind != JsonValueKind.Null)
                RequireProperties(grid.GetProperty("FlatOrigin"), "X", "Y");
        }
    }
    private static void RequirePrefabProperties(JsonElement json)
    {
        RequireProperties(json, "Version", "ResourceId", "EntryDefinitionId", "Definitions");
        int version = ReadFormatVersion(json);
        if (!SceneFormatMigration.SupportsPrefab(version)) throw new InvalidDataException("Unsupported prefab format.");
        RequireDefinitions(json.GetProperty("Definitions"), version);
    }
    private static void RequireDefinitions(JsonElement definitions, int version)
    {
        if (definitions.ValueKind != JsonValueKind.Array || definitions.GetArrayLength() > SceneLimits.MaximumDefinitions) throw new InvalidDataException("Invalid prefab definitions.");
        foreach (var definition in definitions.EnumerateArray())
        {
            RequireProperties(definition, "Id", "Revision", "Name", "Pivot", "Graph", "Source");
            RequireProperties(definition.GetProperty("Pivot"), "X", "Y");
            RequireOptionalPreview(definition, version);
            RequireGraphProperties(definition.GetProperty("Graph"), version);
            var source = definition.GetProperty("Source");
            if (source.ValueKind == JsonValueKind.Null) continue;
            RequireProperties(source, "Locator", "IsRelative", "SourceResourceId", "EntryKind", "EntryDefinitionId", "LastAcceptedFileSha256", "LastAcceptedContentRevision", "ImportOrigin", "OriginClosure", "ImportedDefinitionIds");
            RequireProperties(source.GetProperty("ImportOrigin"), "X", "Y");
            if (source.GetProperty("OriginClosure").ValueKind != JsonValueKind.Array || source.GetProperty("ImportedDefinitionIds").ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Invalid prefab source metadata.");
            if (source.GetProperty("OriginClosure").GetArrayLength() > SceneLimits.MaximumDefinitions) throw new InvalidDataException("Prefab origin capacity exceeded.");
            var importedIds = new HashSet<Guid>();
            foreach (var pair in source.GetProperty("ImportedDefinitionIds").EnumerateObject())
                if (importedIds.Count >= SceneLimits.MaximumDefinitions || !Guid.TryParse(pair.Name, out var id) || id == Guid.Empty || !importedIds.Add(id))
                    throw new InvalidDataException("Invalid imported prefab identity map.");
            foreach (var origin in source.GetProperty("OriginClosure").EnumerateArray()) RequireProperties(origin, "ResourceId", "EntryKind", "EntryDefinitionId");
        }
    }
    private static void RequireGraphProperties(JsonElement graph, int version)
    {
        RequireProperties(graph, "Elements", "Groups", "Instances");
        if (graph.GetProperty("Elements").ValueKind != JsonValueKind.Array || graph.GetProperty("Groups").ValueKind != JsonValueKind.Array || graph.GetProperty("Instances").ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Invalid prefab graph collections.");
        foreach (var element in graph.GetProperty("Elements").EnumerateArray())
        {
            RequireProperties(element, "Id", "Kind", "GroupId", "Transform", "Points", "Width", "Height", "Style", "Text", "Effects", "Visible", "Locked");
            RequireProperties(element.GetProperty("Transform"), "M11", "M12", "M21", "M22", "OffsetX", "OffsetY");
            RequirePlacementProperties(element);
            RequireAppearanceProperties(element, version);
            if (element.GetProperty("Effects").ValueKind != JsonValueKind.Array) throw new InvalidDataException("Missing source effects.");
            foreach (var effect in element.GetProperty("Effects").EnumerateArray())
            {
                RequireProperties(effect, "Id", "Kind", "Bounds", "Amount", "Color");
                RequireProperties(effect.GetProperty("Bounds"), "X", "Y", "Width", "Height");
            }
        }
        foreach (var group in graph.GetProperty("Groups").EnumerateArray())
        {
            RequireProperties(group, "Id", "ParentId", "Name", "Transform", "ElevationScale", "Visible", "Locked", "OrderMode");
            RequireProperties(group.GetProperty("Transform"), "M11", "M12", "M21", "M22", "OffsetX", "OffsetY");
            RequireOptionalPreview(group, version);
        }
        foreach (var instance in graph.GetProperty("Instances").EnumerateArray())
        {
            RequireProperties(instance, "Id", "ParentId", "DefinitionId", "Name", "Transform", "ElevationScale", "Visible", "Locked");
            RequireProperties(instance.GetProperty("Transform"), "M11", "M12", "M21", "M22", "OffsetX", "OffsetY");
        }
    }
    private static void RequireOptionalPreview(JsonElement owner, int version)
    {
        if (!owner.TryGetProperty("PreviewCanvas", out var preview)) return;
        if (version < 17) throw new InvalidDataException("Subproject canvases require scene format 17.");
        if (preview.ValueKind != JsonValueKind.Null) RequirePreviewProperties(preview, version);
    }
    private static void RequirePreviewProperties(JsonElement preview, int version)
    {
        RequireProperties(preview, "Origin", "Viewport", "Canvas", "LayerOpacity");
        RequireProperties(preview.GetProperty("Origin"), "X", "Y");
        RequireProperties(preview.GetProperty("Viewport"), "X", "Y", "Width", "Height");
        RequireCanvasProperties(preview.GetProperty("Canvas"), version);
        if (preview.TryGetProperty("Background", out var background) && background.ValueKind != JsonValueKind.Null)
        {
            RequireProperties(background, "Id", "Kind", "GroupId", "Transform", "Points", "Width", "Height", "Style", "Text", "Effects", "Visible", "Locked");
            RequireProperties(background.GetProperty("Transform"), "M11", "M12", "M21", "M22", "OffsetX", "OffsetY");
            RequirePlacementProperties(background); RequireAppearanceProperties(background, version);
        }
    }
    private static void RequireAppearanceProperties(JsonElement element, int version)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException(L.Get("Scene.MissingRequiredFields"));
        RequireWindowSourceProperties(element, version);
        RequirePresentationProperties(element, version);
        if (version >= 12)
        {
            RequireProperties(element, "Style", "NumberTextSizeMode");
            var style = element.GetProperty("Style");
            RequireProperties(style, "Foreground", "Background", "AdditionalColors", "Variant");
            if (element.TryGetProperty("NumberUsesForeground", out _) || style.TryGetProperty("Stroke", out _) ||
                style.TryGetProperty("Fill", out _) || style.TryGetProperty("LinkColors", out _) ||
                style.GetProperty("Variant").ValueKind != JsonValueKind.String ||
                style.GetProperty("AdditionalColors").ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Invalid canonical appearance fields.");
        }
        else
        {
            if (element.TryGetProperty("NumberTextSizeMode", out _)) throw new InvalidDataException("Canonical appearance requires scene format 12.");
            if (element.TryGetProperty("NumberUsesForeground", out var sizing) && sizing.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("Invalid legacy number sizing flag.");
            if (!element.TryGetProperty("Style", out var style)) return;
            if (style.ValueKind != JsonValueKind.Object || style.TryGetProperty("Foreground", out _) || style.TryGetProperty("Background", out _) ||
                style.TryGetProperty("AdditionalColors", out _)) throw new InvalidDataException("Canonical appearance requires scene format 12.");
            foreach (string color in new[] { "Stroke", "Fill" })
                if (style.TryGetProperty(color, out var value) && (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt32(out _)))
                    throw new InvalidDataException("Invalid legacy color value.");
            if (style.TryGetProperty("LinkColors", out var linked) && linked.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("Invalid legacy linked-colors flag.");
            if (style.TryGetProperty("Variant", out var variant) && variant.ValueKind != JsonValueKind.Null &&
                (variant.ValueKind != JsonValueKind.String || variant.GetString() is not
                    (ElementAppearance.ShapeIndependent or ElementAppearance.ShapeUniform or ElementAppearance.NumberFilled or ElementAppearance.NumberOutline)))
                throw new InvalidDataException("Unsupported legacy appearance variant.");
        }
    }
    private static void RequireWindowSourceProperties(JsonElement element, int version)
    {
        if (!element.TryGetProperty("WindowSource", out var source)) return;
        // Reject even an explicit null in old schemas instead of silently swallowing a future field.
        if (version < 14) throw new InvalidDataException("Window sources require scene format 14.");
        if (source.ValueKind == JsonValueKind.Null) return;
        RequireProperties(source, "BindingIntent", "TitleHint", "ProcessNameHint", "WindowClassHint", "Fit");
    }
    private static void RequirePresentationProperties(JsonElement element, int version)
    {
        bool presentation = element.TryGetProperty("Kind", out var kind) && kind.ValueKind == JsonValueKind.Number &&
            kind.TryGetInt32(out int kindValue) && kindValue == (int)ElementKind.Presentation;
        bool hasSettings = element.TryGetProperty("Presentation", out var settings);
        if (version < 13)
        {
            if (presentation || hasSettings) throw new InvalidDataException("Presentation content requires scene format 13.");
            return;
        }
        if (!presentation)
        {
            if (hasSettings && settings.ValueKind != JsonValueKind.Null) throw new InvalidDataException("Presentation settings require a presentation element.");
            return;
        }
        RequireProperties(element, "Presentation");
        RequireProperties(settings, "Enabled", "Modules", "KeyDisplay", "RecentChordLimit", "KeyHoldMilliseconds", "KeyPanelAlignment",
            "KeyPanelMaximumWidth", "CursorRadiusPixels", "RippleMilliseconds", "TrailMilliseconds", "TrailWidthPixels");
        if (version >= 17) RequireProperties(settings, "KeyboardScope");
        else if (settings.TryGetProperty("KeyboardScope", out var legacyScope) &&
            (legacyScope.ValueKind != JsonValueKind.Number || !legacyScope.TryGetInt32(out int scope) || scope != 0))
            throw new InvalidDataException("Per-layer keyboard scope requires scene format 17.");
        if (version >= 15) RequireProperties(settings, "DisplayStyle");
        else if (settings.TryGetProperty("DisplayStyle", out var legacyStyle) &&
            (legacyStyle.ValueKind != JsonValueKind.Number || !legacyStyle.TryGetInt32(out int style) || style != 0) ||
            version < 15 && settings.TryGetProperty("KeyboardLayout", out var legacyLayout) && legacyLayout.ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("Keyboard display styles and layouts require scene format 15.");
        if (settings.TryGetProperty("KeyboardLayout", out var layout) && layout.ValueKind != JsonValueKind.Null)
        {
            RequireProperties(layout, "Keys");
            var keys = layout.GetProperty("Keys");
            if (keys.ValueKind != JsonValueKind.Array || keys.GetArrayLength() > PresentationKeyboardLayout.MaximumKeys)
                throw new InvalidDataException("Invalid keyboard layout capacity.");
            foreach (var key in keys.EnumerateArray())
                RequireProperties(key, "VirtualKey", "ScanCode", "IsExtended", "Label", "Enabled", "X", "Y", "Width", "Height");
        }
    }
    private static void RequirePlacementProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("IsBillboard", out var billboard) && billboard.ValueKind == JsonValueKind.True)
        {
            RequireProperties(element, "BillboardAnchor", "BillboardHeight");
            RequireProperties(element.GetProperty("BillboardAnchor"), "X", "Y");
        }
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("ProjectivePlacement", out var placement) && placement.ValueKind != JsonValueKind.Null)
            RequireMatrixProperties(placement);
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("ImageFrame", out var frame) && frame.ValueKind != JsonValueKind.Null)
        {
            if (frame.ValueKind != JsonValueKind.Array) throw new InvalidDataException(L.Get("Scene.MissingRequiredFields"));
            foreach (var point in frame.EnumerateArray()) RequireProperties(point, "X", "Y");
        }
    }
    private static void RequireMatrixProperties(JsonElement matrix) => RequireProperties(matrix, "M11", "M12", "M13", "M21", "M22", "M23", "M31", "M32", "M33");
    private static void RequireProperties(JsonElement value, params string[] properties)
    {
        if (value.ValueKind != JsonValueKind.Object || properties.Any(p => !value.TryGetProperty(p, out _)))
            throw new InvalidDataException(L.Get("Scene.MissingRequiredFields"));
    }
    private static int ReadFormatVersion(JsonElement value)
    {
        if (!value.TryGetProperty("Version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int result))
            throw new InvalidDataException(L.Get("Scene.MissingRequiredFields"));
        return result;
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
