using L = FloatCanvas.Localization.AppText;
using System.IO.Compression;
using System.Collections.Concurrent;
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
    private sealed class SaveIntent
    {
        public required string Schema { get; set; }
        public required string Kind { get; set; }
        public required string TargetPath { get; set; }
        public required string ExpectedSha256 { get; set; }
        public required string WrittenSha256 { get; set; }
        public string? SidecarPath { get; set; }
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 128, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathGates =
        new(StringComparer.OrdinalIgnoreCase);
    private const long MaxPackageBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxExpandedBytes = 1024L * 1024 * 1024;
    private const long MaxAssetBytes = 512L * 1024 * 1024;
    internal static Action<string>? BeforeVersionedReplaceForTesting { get; set; }
    internal static Action<string>? AfterVersionedReplaceForTesting { get; set; }
    internal static Action<string>? BeforeVersionedRollbackForTesting { get; set; }
    internal static Action<string>? BeforeCommittedCacheReadForTesting { get; set; }
    internal static bool PreserveSaveIntentForTesting { get; set; }
    public static bool IsAssetId(string? id) => id is { Length: 64 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static AssetResource[] ResolveResources(IEnumerable<string> ids, AssetLibrary assets) => ids.Distinct(StringComparer.Ordinal).Select(id =>
        assets.TryGet(id, out var resource) ? resource : throw new InvalidDataException(L.Format("Assets.MissingResource", id))).ToArray();

    public static async Task<ProjectFileVersion> SaveAsync<T>(string path, string kind, T value, AssetResource[] resources, CancellationToken ct, byte[]? previewPng = null, ProjectFileVersion? expectedVersion = null, string? applicationVersion = null)
    {
        ct.ThrowIfCancellationRequested(); ValidateCurrent(value);
        string writerVersion = PackageProvenance.ApplicationVersion(applicationVersion);
        value = value switch
        {
            SceneDocument document => (T)(object)document.Clone(),
            WorkspaceState workspace => (T)(object)workspace.Clone(),
            PrefabPackage package => (T)(object)package.Clone(),
            _ => throw new InvalidDataException(L.Get("Project.InvalidContentType"))
        };
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
        var pathGate = PathGates.GetOrAdd(path, _ => new(1, 1));
        await pathGate.WaitAsync(ct).ConfigureAwait(false);
        string? saveIntent = null;
        FileStream? writeLease = null;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (kind is "project" or "prefab")
            {
                // All profiles of this Windows user share the lease. Keep lock files
                // in app-owned storage and leave project directories alone on reads.
                string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
                string leasePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FloatCanvas", "package-save-leases", digest + ".lock");
                writeLease = await FileLease.AcquireAsync(leasePath, ct).ConfigureAwait(false);
            }
            await ReconcileSaveIntentAsync<T>(path, kind, ct).ConfigureAwait(false);
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
            string? preservedPrevious = null;
            if (expectedVersion is { Sha256: null })
            {
                // A null digest is the compare-and-swap expectation for "does not exist".
                // File.Replace requires an existing target, so create the path without
                // overwrite and treat a file appearing after validation as a conflict.
                BeforeVersionedReplaceForTesting?.Invoke(path);
                try { File.Move(temporary, path, overwrite: false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    throw new ProjectFileConflictException(File.Exists(path) ? path : null);
                }
                AfterVersionedReplaceForTesting?.Invoke(path);
                var installedNewVersion = await ReadPostCommitVersionAsync(path, path).ConfigureAwait(false);
                if (installedNewVersion != writtenVersion)
                    throw new ProjectFileConflictException(path);
            }
            else if (expectedVersion is not null)
            {
                // Preserve the replaced generation until its identity is verified. This also
                // retains an external write racing the final comparison instead of losing it.
                string previous = path + "." + Guid.NewGuid().ToString("N") + ".save-backup";
                preservedPrevious = previous;
                saveIntent = IntentPath(path);
                await WriteSaveIntentAsync(path, kind, expectedVersion, writtenVersion, previous).ConfigureAwait(false);
                BeforeVersionedReplaceForTesting?.Invoke(path);
                try { File.Replace(temporary, path, previous); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // The target can change after the version comparison but before
                    // Replace. Report that race as a project conflict, not a generic
                    // storage failure, so the caller can explain the external change.
                    throw new ProjectFileConflictException(File.Exists(previous) ? previous : null);
                }
                AfterVersionedReplaceForTesting?.Invoke(path);
                // Commit is a non-cancellable boundary. Once Replace succeeds, finish
                // verification and cleanup rather than reporting cancellation for a
                // save that is already visible on disk.
                var installedVersion = await ReadPostCommitVersionAsync(path, previous).ConfigureAwait(false);
                if (installedVersion != writtenVersion)
                {
                    // Keep a displaced valid generation when the external target is not a
                    // complete package. A valid newer package safely supersedes it.
                    bool externalValid = false;
                    try
                    {
                        if (!IsKnownValid(path, kind))
                            await LoadAsync<T>(path, kind, CancellationToken.None).ConfigureAwait(false);
                        externalValid = true;
                    }
                    catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or
                        JsonException or NotSupportedException or EndOfStreamException) { }
                    if (externalValid) TryDelete(previous);
                    throw new ProjectFileConflictException(externalValid ? null :
                        File.Exists(previous) ? previous : path);
                }
                var previousVersion = await ReadPostCommitVersionAsync(previous, previous).ConfigureAwait(false);
                if (previousVersion != expectedVersion)
                {
                    BeforeVersionedRollbackForTesting?.Invoke(path);
                    await RestoreExternalGenerationAsync(path, kind, expectedVersion, writtenVersion,
                        previous, previousVersion).ConfigureAwait(false);
                    throw new InvalidOperationException("External generation restoration must end in a conflict.");
                }
            }
            else File.Move(temporary, path, overwrite: expectedVersion is null);
            BeforeCommittedCacheReadForTesting?.Invoke(path);
            try
            {
                await using (var committed = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    string committedHash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(committed, CancellationToken.None).ConfigureAwait(false));
                    if (committedHash != writtenVersion.Sha256)
                        throw new ProjectFileConflictException(preservedPrevious is not null && File.Exists(preservedPrevious)
                            ? preservedPrevious : path);
                    ValidatedPackageCache.Remember(committed.SafeFileHandle, kind);
                }
            }
            catch (ProjectFileConflictException) { throw; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new ProjectFileConflictException(
                    preservedPrevious is not null && File.Exists(preservedPrevious) ? preservedPrevious : null);
            }
            if (preservedPrevious is not null) TryDelete(preservedPrevious);
            // The origin object is shared with editor/undo snapshots, but is excluded
            // from scene content. Failed writes never establish or alter its identity.
            Provenance(value).RecordSuccessfulSave(writerVersion);
            foreach (var document in Documents(value)) { document.Provenance.RecordSuccessfulSave(writerVersion); document.RecordResourceIdentityPersistence(); }
            if (value is PrefabPackage package) package.RecordResourceIdentityPersistence();
            return writtenVersion;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            if (saveIntent is not null && !PreserveSaveIntentForTesting) TryDelete(saveIntent);
            writeLease?.Dispose();
            pathGate.Release();
        }
    }
    private static async Task RestoreExternalGenerationAsync(string path, string kind,
        ProjectFileVersion expectedVersion, ProjectFileVersion writtenVersion, string pending, ProjectFileVersion pendingVersion)
    {
        var knownOlder = new HashSet<ProjectFileVersion> { pendingVersion };
        for (int attempt = 0; attempt < 4; attempt++)
        {
            string captured = path + "." + Guid.NewGuid().ToString("N") + ".save-rejected";
            await WriteSaveIntentAsync(path, kind, expectedVersion, writtenVersion, captured).ConfigureAwait(false);
            try { File.Replace(pending, path, captured); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw new ProjectFileConflictException(File.Exists(pending) ? pending : null); }
            var capturedVersion = await ReadPostCommitVersionAsync(captured, captured).ConfigureAwait(false);
            if (capturedVersion == writtenVersion || knownOlder.Contains(capturedVersion))
            {
                TryDelete(captured);
                throw new ProjectFileConflictException();
            }
            knownOlder.Add(pendingVersion);
            pending = captured;
            pendingVersion = capturedVersion;
        }
        // A writer that never yields a stable target leaves its latest captured
        // generation as an explicit recovery path instead of deleting it.
        throw new ProjectFileConflictException(pending);
    }
    private static string IntentPath(string path) => path + ".save-intent.json";
    private static async Task WriteSaveIntentAsync(string path, string kind, ProjectFileVersion expectedVersion,
        ProjectFileVersion writtenVersion, string sidecar)
    {
        if (expectedVersion.Sha256 is not { Length: 64 } expected || writtenVersion.Sha256 is not { Length: 64 } written)
            throw new InvalidOperationException("A versioned save intent requires both content identities.");
        string intentPath = IntentPath(path);
        var intent = new SaveIntent
        {
            Schema = "FloatCanvas.SaveIntent.v1",
            Kind = kind,
            TargetPath = path,
            ExpectedSha256 = expected,
            WrittenSha256 = written,
            SidecarPath = sidecar
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(intent, JsonOptions);
        string temporary = intentPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
                output.Flush(true);
            }
            File.Move(temporary, intentPath, true);
        }
        finally { TryDelete(temporary); }
    }
    private static async Task ReconcileSaveIntentAsync<T>(string path, string kind, CancellationToken ct)
    {
        string intentPath = IntentPath(path);
        if (!File.Exists(intentPath)) return;
        SaveIntent intent;
        try
        {
            var info = new FileInfo(intentPath);
            if (info.Length is <= 0 or > 16 * 1024) throw new InvalidDataException(L.Get("Project.InvalidManifest"));
            intent = JsonSerializer.Deserialize<SaveIntent>(await File.ReadAllBytesAsync(intentPath, ct).ConfigureAwait(false),
                JsonOptions) ?? throw new InvalidDataException(L.Get("Project.InvalidManifest"));
        }
        catch (JsonException error) { throw new InvalidDataException(L.Get("Project.InvalidManifest"), error); }
        if (intent.Schema != "FloatCanvas.SaveIntent.v1" || intent.Kind != kind ||
            !string.Equals(Path.GetFullPath(intent.TargetPath), path, StringComparison.OrdinalIgnoreCase) ||
            intent.ExpectedSha256 is not { Length: 64 } || intent.WrittenSha256 is not { Length: 64 })
            throw new InvalidDataException(L.Get("Project.InvalidManifest"));
        string? sidecar = intent.SidecarPath is { } candidate ? Path.GetFullPath(candidate) : null;
        if (sidecar is not null && (!string.Equals(Path.GetDirectoryName(sidecar), Path.GetDirectoryName(path), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(sidecar).StartsWith(Path.GetFileName(path) + ".", StringComparison.OrdinalIgnoreCase) ||
            !(sidecar.EndsWith(".save-backup", StringComparison.OrdinalIgnoreCase) ||
              sidecar.EndsWith(".save-rejected", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException(L.Get("Project.InvalidManifest"));
        var expected = new ProjectFileVersion(intent.ExpectedSha256);
        var written = new ProjectFileVersion(intent.WrittenSha256);
        var targetVersion = await ProjectFileVersion.ReadAsync(path, ct).ConfigureAwait(false);
        var sidecarVersion = sidecar is null ? new ProjectFileVersion(null) :
            await ProjectFileVersion.ReadAsync(sidecar, ct).ConfigureAwait(false);
        if (targetVersion == written)
        {
            if (sidecar is not null && sidecarVersion == expected)
            {
                TryDelete(sidecar);
                TryDelete(intentPath);
                return;
            }
            if (sidecar is not null && sidecarVersion.Sha256 is not null)
            {
                string captured = path + "." + Guid.NewGuid().ToString("N") + ".save-rejected";
                try { File.Replace(sidecar, path, captured); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { throw new ProjectFileConflictException(File.Exists(sidecar) ? sidecar : intentPath); }
                TryDelete(intentPath);
                throw new ProjectFileConflictException(File.Exists(captured) ? captured : intentPath);
            }
            TryDelete(intentPath);
            return;
        }
        if (targetVersion == expected)
        {
            if (sidecarVersion.Sha256 is null || sidecarVersion == expected)
            {
                if (sidecarVersion == expected) TryDelete(sidecar!);
                TryDelete(intentPath);
                return;
            }
            throw new ProjectFileConflictException(sidecar ?? intentPath);
        }
        bool targetReadable = false;
        if (targetVersion.Sha256 is not null)
        {
            try
            {
                if (!IsKnownValid(path, kind)) await LoadAsync<T>(path, kind, ct).ConfigureAwait(false);
                targetReadable = true;
            }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or
                JsonException or NotSupportedException or EndOfStreamException) { }
        }
        if (targetReadable)
        {
            // A valid independent generation superseded the interrupted attempt.
            // Preserve any displaced bytes that do not match our expected generation.
            if (sidecarVersion == expected) TryDelete(sidecar!);
            TryDelete(intentPath);
            return;
        }
        if (sidecarVersion == expected)
        {
            string? captured = null;
            if (targetVersion.Sha256 is null) File.Move(sidecar!, path, overwrite: false);
            else
            {
                captured = path + "." + Guid.NewGuid().ToString("N") + ".save-rejected";
                try { File.Replace(sidecar!, path, captured); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { throw new ProjectFileConflictException(File.Exists(sidecar) ? sidecar : intentPath); }
            }
            TryDelete(intentPath);
            if (captured is not null) throw new ProjectFileConflictException(captured);
            return;
        }
        throw new ProjectFileConflictException(sidecar is not null && File.Exists(sidecar) ? sidecar : intentPath);
    }
    public static async Task<(ProjectFileVersion Version, T Value, AssetLibrary Assets)> LoadVersionedAsync<T>(
        string path, string kind, CancellationToken ct)
    {
        path = Path.GetFullPath(path);
        var pathGate = PathGates.GetOrAdd(path, _ => new(1, 1));
        await pathGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var version = await ProjectFileVersion.ReadAsync(path, ct).ConfigureAwait(false);
            var result = await LoadAsync<T>(path, kind, ct).ConfigureAwait(false);
            if (version != await ProjectFileVersion.ReadAsync(path, ct).ConfigureAwait(false))
                throw new ProjectFileConflictException();
            return (version, result.Value, result.Assets);
        }
        finally { pathGate.Release(); }
    }
    private static async Task<ProjectFileVersion> ReadPostCommitVersionAsync(string path,string preserved)
    {
        try { return await ProjectFileVersion.ReadAsync(path, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ProjectFileConflictException(File.Exists(preserved) ? preserved : null);
        }
    }
    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
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
            bool skippedNodes = false;
            T value = await ReadJsonAsync<T>(RequiredEntry(archive, payload), JsonCapacity(payload), ct, skipped => skippedNodes = skipped, manifest.Version).ConfigureAwait(false);
            PrepareLoaded(value, manifest.Version, skippedNodes);
            var provenance = new DocumentProvenance(manifest.CreatedWith, manifest.LastSavedWith);
            foreach (var document in Documents(value)) { document.Provenance = provenance; if (manifest.Version >= 11) document.RecordResourceIdentityPersistence(); }
            if (value is PrefabPackage prefab) { prefab.Provenance = provenance; prefab.RecordResourceIdentityPersistence(); }
            if (!(skippedNodes ? References(value).ToHashSet(StringComparer.Ordinal).IsSubsetOf(manifest.Assets.Select(a => a.Id)) : References(value).ToHashSet(StringComparer.Ordinal).SetEquals(manifest.Assets.Select(a => a.Id)))) throw new InvalidDataException(L.Get("Project.AssetReferencesMismatch"));
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
            if (skippedNodes) assets.Prune(References(value));
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
    private static void NormalizeSkippedNavigation<T>(T value)
    {
        var documents = Documents(value).ToArray();
        var parents = documents.ToDictionary(document => document, document => document.Groups.ToDictionary(group => group.Id, group => group.ParentId));
        foreach (var document in documents) document.PruneGroups();
        if (value is not WorkspaceState workspace) return;
        foreach (var state in PrefabDraftState.Enumerate(workspace.PendingPrefabEdit))
        {
            Normalize(state.DraftDocument, state.DraftScopeId, state.DraftSelection, out var draftScope); state.DraftScopeId = draftScope;
            var owner = state.ParentEdit?.DraftDocument ?? documents.FirstOrDefault(document => document.ResourceId == state.OwnerResourceId);
            if (owner is not null) { Normalize(owner, state.ReturnScopeId, state.ReturnSelection, out var returnScope); state.ReturnScopeId = returnScope; }
        }
        void Normalize(SceneDocument document, Guid? scope, List<Guid> selection, out Guid? resolved)
        {
            var graph = new SceneGraph(document);
            while (scope is { } id && graph.FindGroup(id) is null)
                scope = parents[document].GetValueOrDefault(id);
            resolved = scope;
            selection.RemoveAll(id => graph.FindElement(id) is null && graph.FindGroup(id) is null && graph.FindInstance(id) is null);
        }
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
    private static void PrepareLoaded<T>(T value, int version, bool skippedNodes)
    {
        Validate(value);
        if (version < SceneDocument.CurrentVersion) UpgradeAndValidate(value);
        if (!skippedNodes) return;
        NormalizeSkippedNavigation(value);
        if (value is PrefabPackage prefab)
            foreach (var definition in prefab.Definitions) SceneGraph.PruneEmptyGroups(definition.Graph);
        Validate(value);
    }
    private static void UpgradeAndValidate<T>(T value)
    {
        if (value is PrefabPackage package) SceneFormatMigration.Upgrade(package);
        foreach (var document in Documents(value)) SceneFormatMigration.Upgrade(document);
        ValidateCurrent(value);
    }
    private static void ValidateCurrent<T>(T value)
    {
        if (value is PrefabPackage prefab && prefab.Version != SceneDocument.CurrentVersion)
            throw new InvalidDataException(L.Format("Scene.UnsupportedVersion", prefab.Version));
        foreach (var document in Documents(value))
        {
            if (document is null) throw new InvalidDataException(L.Get("Scene.MissingRequiredFields"));
            if (document.Version != SceneDocument.CurrentVersion)
                throw new InvalidDataException(L.Format("Scene.UnsupportedVersion", document.Version));
        }
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
    // The released v20 palette uses Foreground/Background. The working model uses
    // PrimaryColor/SecondaryColor under the same version until the next release.
    // Determine the contract while validating the payload, before any model is exposed.
    private sealed class SourceColorSchema
    {
        private bool? current;
        public bool UsesPublishedColors => current == false;
        public bool Observe(JsonElement style, int version)
        {
            bool hasCurrentNames = version >= 20 && (style.TryGetProperty("PrimaryColor", out _) || style.TryGetProperty("SecondaryColor", out _));
            if (current.HasValue && current != hasCurrentNames)
                throw new InvalidDataException("Mixed color field contracts in one payload.");
            current = hasCurrentNames;
            return hasCurrentNames;
        }
    }

    private static async Task<T> ReadJsonAsync<T>(ZipArchiveEntry entry, long maximumBytes, CancellationToken ct, Action<bool>? contentFiltered = null, int? sourceVersion = null)
    {
        byte[] bytes = await ReadBytesAsync(entry, maximumBytes, ct).ConfigureAwait(false);
        using var sourceJson = JsonDocument.Parse(bytes, new() { MaxDepth = 128 });
        RejectDuplicateProperties(sourceJson.RootElement);
        bool skipped = false;
        using var filtered = typeof(T) == typeof(Manifest) ? null : FilterUnsupportedContent(sourceJson.RootElement, out skipped);
        contentFiltered?.Invoke(skipped);
        var json = filtered ?? sourceJson;
        if (typeof(T) == typeof(Manifest) && json.RootElement.TryGetProperty("Version", out var packageVersion) && packageVersion.ValueKind == JsonValueKind.Number &&
            packageVersion.TryGetInt32(out int version) && version >= 9)
            RequireProperties(json.RootElement, "CreatedWith", "LastSavedWith");
        var colors = new SourceColorSchema();
        // Scene version and workspace document identity are mandatory, not inferred from C# defaults.
        if (typeof(T) == typeof(SceneDocument)) RequireSceneProperties(json.RootElement, colors, sourceVersion);
        if (typeof(T) == typeof(PrefabPackage)) RequirePrefabProperties(json.RootElement, colors, sourceVersion);
        if (typeof(T) == typeof(WorkspaceState))
        {
            if (!json.RootElement.TryGetProperty("LiveDocument", out var live) || !json.RootElement.TryGetProperty("CaptureOrigin", out _)) throw new InvalidDataException(L.Get("Recovery.MissingRequiredFields"));
            RequireSceneProperties(live, colors, sourceVersion);
            if (json.RootElement.TryGetProperty("CaptureDocument", out var capture) && capture.ValueKind != JsonValueKind.Null) RequireSceneProperties(capture, colors, sourceVersion);
            if (json.RootElement.TryGetProperty("PendingPrefabEdit", out var pending) && pending.ValueKind != JsonValueKind.Null)
            {
                if (ReadFormatVersion(live) < 11) throw new InvalidDataException("Legacy workspace cannot contain prefab edit state.");
                int depth = 0;
                while (pending.ValueKind != JsonValueKind.Null)
                {
                    if (++depth > SceneLimits.MaximumGroupDepth) throw new InvalidDataException("Nested prefab edit recovery is too deep.");
                    RequireProperties(pending, "OwnerResourceId", "DefinitionId", "ExpectedRevision", "DraftDocument", "ReturnScopeId", "ReturnSelection", "DraftScopeId", "DraftSelection", "IsSourceProject", "SourcePath", "SourceFileSha256");
                    RequireSceneProperties(pending.GetProperty("DraftDocument"), colors, sourceVersion);
                    if (pending.TryGetProperty("PreviewOrigin", out var previewOrigin) && previewOrigin.ValueKind != JsonValueKind.Null)
                    {
                        if (ReadFormatVersion(pending.GetProperty("DraftDocument")) < 17) throw new InvalidDataException("Subproject preview origins require scene format 17.");
                        RequireProperties(previewOrigin, "X", "Y");
                    }
                    if (!pending.TryGetProperty("ParentEdit", out pending)) break;
                }
            }
        }
        return SceneFormatMigration.ReadPayload<T>(json.RootElement, sourceVersion ?? SceneDocument.CurrentVersion, JsonOptions, colors.UsesPublishedColors);
    }
    private static JsonDocument? FilterUnsupportedContent(JsonElement source, out bool skipped)
    {
        skipped = false;
        if (source.ValueKind != JsonValueKind.Object) throw new InvalidDataException(L.Get("Scene.MissingRequiredFields"));
        if (!SceneContentCompatibility.RequiresFiltering(source)) return null;
        var compatible = System.Text.Json.Nodes.JsonObject.Create(source)!;
        skipped = SceneContentCompatibility.Filter(compatible);
        return JsonSerializer.SerializeToDocument(compatible, JsonOptions);
    }

    internal static void WriteEmbeddedDocument(Utf8JsonWriter writer, SceneDocument document)
    {
        if (document.Version != SceneDocument.CurrentVersion) throw new InvalidDataException(L.Format("Scene.UnsupportedVersion", document.Version));
        JsonSerializer.Serialize(writer, document, JsonOptions);
    }

    internal static SceneDocument ReadEmbeddedDocument(JsonElement source)
    {
        RejectDuplicateProperties(source);
        using var filtered = FilterUnsupportedContent(source, out bool skipped);
        var json = filtered?.RootElement ?? source;
        var colors = new SourceColorSchema();
        RequireSceneProperties(json, colors);
        int version = ReadFormatVersion(json);
        var document = SceneFormatMigration.ReadPayload<SceneDocument>(json, version, JsonOptions, colors.UsesPublishedColors);
        PrepareLoaded(document, version, skipped);
        return document;
    }
    private static void RequireSceneProperties(JsonElement json, SourceColorSchema colors, int? packageVersion = null)
    {
        if (json.ValueKind != JsonValueKind.Object || new[] { "Version", "Kind", "Elements", "Viewport", "LayerOpacity", "NextNumber" }.Any(p => !json.TryGetProperty(p, out _))) throw new InvalidDataException(L.Get("Scene.MissingRequiredFields"));
        int version = CheckSourceVersion(json, packageVersion);
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
                RequirePreviewProperties(pair.Value, version, colors);
            }
        }
        else if (json.TryGetProperty("RootPreviewCanvases", out _)) throw new InvalidDataException("Subproject canvases require scene format 17.");
        if (json.TryGetProperty("AuthoringReference", out var authoring) && authoring.ValueKind != JsonValueKind.Null)
        {
            if (version < 18) throw new InvalidDataException("Authored canvas references require scene format 18.");
            RequireProperties(authoring, "Frame", "ReferenceScreenBounds");
            RequireProperties(authoring.GetProperty("Frame"), "X", "Y", "Width", "Height");
            RequireProperties(authoring.GetProperty("ReferenceScreenBounds"), "X", "Y", "Width", "Height");
            if (authoring.TryGetProperty("ContentReferenceBounds", out var contentReference) && contentReference.ValueKind != JsonValueKind.Null)
                RequireProperties(contentReference, "X", "Y", "Width", "Height");
        }
        if (version >= 3)
        {
            RequireProperties(json, "Canvas");
            var canvas = json.GetProperty("Canvas");
            RequireCanvasProperties(canvas, version);
        }
        if (json.GetProperty("Elements").ValueKind == JsonValueKind.Array)
            foreach (var element in json.GetProperty("Elements").EnumerateArray()) { RequirePlacementProperties(element); RequireAppearanceProperties(element, version, colors); }
        int graphVersion = version;
        if (graphVersion >= 10)
        {
            RequireProperties(json, "Groups");
            if (json.GetProperty("Groups").ValueKind != JsonValueKind.Array) throw new InvalidDataException(L.Get("Scene.MissingRequiredFields"));
            foreach (var group in json.GetProperty("Groups").EnumerateArray())
            {
                RequireProperties(group, "Id", "ParentId", "Name", "Transform", "ElevationScale", "Visible", "Locked", "OrderMode");
                RequireProperties(group.GetProperty("Transform"), "M11", "M12", "M21", "M22", "OffsetX", "OffsetY");
                RequireOptionalPreview(group, version, colors);
            }
        }
        if (graphVersion >= 11)
        {
            RequireProperties(json, "ResourceId", "Instances", "Definitions");
            RequireGraphProperties(json, version, colors);
            RequireDefinitions(json.GetProperty("Definitions"), version, colors);
        }
        else if (json.TryGetProperty("Instances", out _) || json.TryGetProperty("Definitions", out _) || json.TryGetProperty("ResourceId", out _))
            throw new InvalidDataException("Prefab fields require scene format 11.");
        if (json.TryGetProperty("Background", out var background) && background.ValueKind != JsonValueKind.Null) { RequirePlacementProperties(background); RequireAppearanceProperties(background, version, colors); }
    }
    private static void RequireCanvasProperties(JsonElement canvas, int version)
    {
        RequireProperties(canvas, "BackgroundColor", "Projection", "Grid");
        if (version >= 5) RequireProperties(canvas, "DepthPriorityOcclusion");
        var projection = canvas.GetProperty("Projection");
        RequireProperties(projection, "Enabled", "Columns", "Rows", "Origin", "CellSize", "Quad", "Matrix", "AlgorithmVersion");
        if (version == 6) RequireProperties(projection, "LockedGridCellSize");
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
    private static void RequirePrefabProperties(JsonElement json, SourceColorSchema colors, int? packageVersion)
    {
        RequireProperties(json, "Version", "ResourceId", "EntryDefinitionId", "Definitions");
        int version = CheckSourceVersion(json, packageVersion);
        if (!SceneFormatMigration.SupportsPrefab(version)) throw new InvalidDataException("Unsupported prefab format.");
        RequireDefinitions(json.GetProperty("Definitions"), version, colors);
    }
    private static void RequireDefinitions(JsonElement definitions, int version, SourceColorSchema colors)
    {
        if (definitions.ValueKind != JsonValueKind.Array || definitions.GetArrayLength() > SceneLimits.MaximumDefinitions) throw new InvalidDataException("Invalid prefab definitions.");
        foreach (var definition in definitions.EnumerateArray())
        {
            RequireProperties(definition, "Id", "Revision", "Name", "Pivot", "Graph", "Source");
            RequireProperties(definition.GetProperty("Pivot"), "X", "Y");
            RequireOptionalPreview(definition, version, colors);
            RequireGraphProperties(definition.GetProperty("Graph"), version, colors);
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
    private static void RequireGraphProperties(JsonElement graph, int version, SourceColorSchema colors)
    {
        RequireProperties(graph, "Elements", "Groups", "Instances");
        if (graph.GetProperty("Elements").ValueKind != JsonValueKind.Array || graph.GetProperty("Groups").ValueKind != JsonValueKind.Array || graph.GetProperty("Instances").ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Invalid prefab graph collections.");
        foreach (var element in graph.GetProperty("Elements").EnumerateArray())
        {
            RequireProperties(element, "Id", "Kind", "GroupId", "Transform", "Points", "Width", "Height", "Style", "Text", "Effects", "Visible", "Locked");
            RequireProperties(element.GetProperty("Transform"), "M11", "M12", "M21", "M22", "OffsetX", "OffsetY");
            RequirePlacementProperties(element);
            RequireAppearanceProperties(element, version, colors);
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
            RequireOptionalPreview(group, version, colors);
        }
        foreach (var instance in graph.GetProperty("Instances").EnumerateArray())
        {
            RequireProperties(instance, "Id", "ParentId", "DefinitionId", "Name", "Transform", "ElevationScale", "Visible", "Locked");
            RequireProperties(instance.GetProperty("Transform"), "M11", "M12", "M21", "M22", "OffsetX", "OffsetY");
            RequireContainerPlacement(instance, version);
        }
    }
    private static void RequireOptionalPreview(JsonElement owner, int version, SourceColorSchema colors)
    {
        RequireContainerPlacement(owner, version);
        if (!owner.TryGetProperty("PreviewCanvas", out var preview)) return;
        if (version < 17) throw new InvalidDataException("Subproject canvases require scene format 17.");
        if (preview.ValueKind != JsonValueKind.Null) RequirePreviewProperties(preview, version, colors);
    }
    private static void RequireContainerPlacement(JsonElement owner, int version)
    {
        if (version >= 20 && owner.TryGetProperty("Transform", out _)) RequireProperties(owner, "Opacity");
        if (!owner.TryGetProperty("Billboard", out var placement) || placement.ValueKind == JsonValueKind.Null) return;
        if (version < 19) throw new InvalidDataException("Container billboards require scene format 19.");
        RequireProperties(placement, "ReferencePoint", "Height");
        RequireProperties(placement.GetProperty("ReferencePoint"), "X", "Y");
    }
    private static void RequirePreviewProperties(JsonElement preview, int version, SourceColorSchema colors)
    {
        RequireProperties(preview, "Origin", "Viewport", "Canvas", "LayerOpacity");
        RequireProperties(preview.GetProperty("Origin"), "X", "Y");
        RequireProperties(preview.GetProperty("Viewport"), "X", "Y", "Width", "Height");
        RequireCanvasProperties(preview.GetProperty("Canvas"), version);
        if (preview.TryGetProperty("Background", out var background) && background.ValueKind != JsonValueKind.Null)
        {
            RequireProperties(background, "Id", "Kind", "GroupId", "Transform", "Points", "Width", "Height", "Style", "Text", "Effects", "Visible", "Locked");
            RequireProperties(background.GetProperty("Transform"), "M11", "M12", "M21", "M22", "OffsetX", "OffsetY");
            RequirePlacementProperties(background); RequireAppearanceProperties(background, version, colors);
        }
    }
    private static void RequireAppearanceProperties(JsonElement element, int version, SourceColorSchema colors)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException(L.Get("Scene.MissingRequiredFields"));
        RequireWindowSourceProperties(element, version);
        RequirePresentationProperties(element, version);
        if (version >= 12)
        {
            RequireProperties(element, "Style", "NumberTextSizeMode");
            var style = element.GetProperty("Style");
            bool currentColors = colors.Observe(style, version);
            if (currentColors)
            {
                RequireProperties(style, "PrimaryColor", "SecondaryColor", "Variant");
                if (style.TryGetProperty("Foreground", out _) || style.TryGetProperty("Background", out _) ||
                    style.GetProperty("Variant") is { ValueKind: JsonValueKind.String } current && AppearanceMigration.IsLegacyNumberOutline(current.GetString()))
                    throw new InvalidDataException("Invalid primary/secondary color schema.");
            }
            else
            {
                RequireProperties(style, "Foreground", "Background", "AdditionalColors", "Variant");
                if (style.TryGetProperty("PrimaryColor", out _) || style.TryGetProperty("SecondaryColor", out _) ||
                    style.TryGetProperty(nameof(ElementStyle.HasTextOutlineColor), out _) ||
                    style.GetProperty("Variant") is { ValueKind: JsonValueKind.String } previous &&
                    previous.GetString() is ElementAppearance.NumberOutline or ElementAppearance.TextOutline or ElementAppearance.TextLabel)
                    throw new InvalidDataException("Unexpected fields or variant in the released foreground/background color contract.");
            }
            var primary = style.GetProperty(currentColors ? "PrimaryColor" : "Foreground");
            var secondary = style.GetProperty(currentColors ? "SecondaryColor" : "Background");
            if (primary.ValueKind != JsonValueKind.Number || !primary.TryGetUInt32(out _) ||
                secondary.ValueKind != JsonValueKind.Number || !secondary.TryGetUInt32(out _))
                throw new InvalidDataException("Invalid color value.");
            if (element.TryGetProperty("NumberUsesForeground", out _) || style.TryGetProperty("Stroke", out _) ||
                style.TryGetProperty("Fill", out _) || style.TryGetProperty("LinkColors", out _) ||
                style.GetProperty("Variant").ValueKind != JsonValueKind.String ||
                style.TryGetProperty("AdditionalColors", out var archived) && archived.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Invalid canonical appearance fields.");
        }
        else
        {
            if (element.TryGetProperty("NumberTextSizeMode", out _)) throw new InvalidDataException("Canonical appearance requires scene format 12.");
            if (element.TryGetProperty("NumberUsesForeground", out var sizing) && sizing.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("Invalid legacy number sizing flag.");
            if (!element.TryGetProperty("Style", out var style)) return;
            if (style.ValueKind != JsonValueKind.Object || style.TryGetProperty("Foreground", out _) || style.TryGetProperty("Background", out _) ||
                style.TryGetProperty("AdditionalColors", out _) || style.TryGetProperty(nameof(ElementStyle.HasTextOutlineColor), out _) || style.TryGetProperty("PrimaryColor", out _) ||
                style.TryGetProperty("SecondaryColor", out _)) throw new InvalidDataException("Invalid legacy appearance fields.");
            foreach (string color in new[] { "Stroke", "Fill" })
                if (style.TryGetProperty(color, out var value) && (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt32(out _)))
                    throw new InvalidDataException("Invalid legacy color value.");
            if (style.TryGetProperty("LinkColors", out var linked) && linked.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("Invalid legacy linked-colors flag.");
            if (style.TryGetProperty("Variant", out var variant) && variant.ValueKind != JsonValueKind.Null &&
                (variant.ValueKind != JsonValueKind.String || variant.GetString() is not
                    (ElementAppearance.ShapeIndependent or ElementAppearance.ShapeUniform or ElementAppearance.NumberFilled or AppearanceMigration.LegacyNumberOutline)))
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
    private static int CheckSourceVersion(JsonElement json, int? packageVersion)
    {
        int version = ReadFormatVersion(json);
        if (!SceneFormatMigration.Supports(version) || packageVersion.HasValue && packageVersion != version)
            throw new InvalidDataException(L.Format("Scene.UnsupportedVersion", version));
        return version;
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
