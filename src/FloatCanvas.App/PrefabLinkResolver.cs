using System.IO;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal enum PrefabLinkStatus { UpToDate, Changed, Missing }
internal sealed record PrefabLinkCandidate(EditorSession Owner, long OwnerRevision, long OwnerScopeVersion, Guid? DefinitionId, Guid? ExpectedDefinitionRevision,
    PrefabPackage Package, AssetLibrary Assets, ExternalPrefabSource Source, string Path, ProjectFileVersion FileVersion, LoadedProject? SourceProject);

/// <summary>Reads complete external snapshots. Evaluation and rendering never consult this module.</summary>
internal sealed class PrefabLinkResolver
{
    private readonly PrefabStore prefabs = new();
    private readonly ProjectStore projects = new();
    internal Func<CancellationToken, Task>? BeforeReadAsync { get; set; }

    internal static string ResolvePath(ExternalPrefabSource source, string? ownerPath)
    {
        if (!source.IsLinked) throw new InvalidOperationException(L.Get("Prefab.SourceMissing"));
        if (source.IsRelative && ownerPath is null) throw new InvalidOperationException(L.Get("Prefab.SourceMissing"));
        return Path.GetFullPath(source.IsRelative ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ownerPath!))!, source.Locator!) : source.Locator!);
    }
    internal static ExternalPrefabSource Locate(ExternalPrefabSource source, string path, string? ownerPath)
    {
        var result = source.Clone(); path = Path.GetFullPath(path);
        result.IsRelative = ownerPath is not null && string.Equals(Path.GetPathRoot(path), Path.GetPathRoot(Path.GetFullPath(ownerPath)), StringComparison.OrdinalIgnoreCase);
        result.Locator = result.IsRelative ? Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(ownerPath!))!, path) : path;
        return result;
    }
    internal async Task<PrefabLinkStatus> CheckAsync(ExternalPrefabSource source, string? ownerPath, CancellationToken ct = default)
    {
        var version = await ProjectFileVersion.ReadAsync(ResolvePath(source, ownerPath), ct);
        return version.Sha256 is null ? PrefabLinkStatus.Missing : string.Equals(version.Sha256, source.LastAcceptedFileSha256, StringComparison.OrdinalIgnoreCase)
            ? PrefabLinkStatus.UpToDate : PrefabLinkStatus.Changed;
    }
    internal async Task<PrefabLinkCandidate> PrepareAsync(EditorSession owner, string path, string? ownerPath,
        Guid? definitionId = null, bool linked = true, bool replaceIdentity = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner); path = Path.GetFullPath(path);
        long revision = owner.SceneRevision, scopeVersion = owner.EditScopeVersion;
        var previous = definitionId is { } id ? owner.Document.Definitions.Single(definition => definition.Id == id).Clone() : null;
        if (ownerPath is not null && string.Equals(path, Path.GetFullPath(ownerPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L.Get("Prefab.LinkCycle"));
        if (BeforeReadAsync is { } beforeRead) await beforeRead(ct);
        PrefabPackage package; AssetLibrary assets; ProjectFileVersion fileVersion; LoadedProject? sourceProject = null;
        PointD origin = default; bool persistent;
        var kind = string.Equals(Path.GetExtension(path), ".floatprefab", StringComparison.OrdinalIgnoreCase) ? PrefabSourceKind.PrefabDefinition : PrefabSourceKind.ProjectContent;
        if (kind == PrefabSourceKind.PrefabDefinition)
        {
            var loaded = await prefabs.LoadAsync(path, ct); package = loaded.Package; assets = loaded.Assets;
            fileVersion = loaded.FileVersion ?? await ProjectFileVersion.ReadAsync(path, ct);
            persistent = package.ResourceIdentityPersistent;
            origin = package.Definitions.Single(definition => definition.Id == package.EntryDefinitionId).Pivot;
        }
        else
        {
            if (!string.Equals(Path.GetExtension(path), ".floatcanvas", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(L.Get("Prefab.LinkFilter"));
            sourceProject = await projects.LoadAsync(path, ct); assets = sourceProject.Assets;
            fileVersion = sourceProject.FileVersion ?? await ProjectFileVersion.ReadAsync(path, ct);
            persistent = sourceProject.Document.ResourceIdentityPersistent;
            (package, origin) = ProjectContent(sourceProject, Path.GetFileNameWithoutExtension(path));
        }
        ct.ThrowIfCancellationRequested();
        if (linked && !persistent) throw new InvalidOperationException(L.Get("Prefab.LinkNeedsIdentity"));
        if (fileVersion.Sha256 is null) throw new FileNotFoundException(L.Get("Prefab.SourceMissing"), path);
        var entry = package.Definitions.Single(definition => definition.Id == package.EntryDefinitionId);
        var origins = package.Definitions.SelectMany(definition => definition.Source is { } source
            ? source.OriginClosure.Append(new(source.SourceResourceId, source.EntryKind, source.EntryDefinitionId)) : [])
            .Append(new(package.ResourceId, kind, kind == PrefabSourceKind.PrefabDefinition ? package.EntryDefinitionId : null))
            .Distinct().ToList();
        if (package.ResourceId == owner.Document.ResourceId || origins.Any(originReference => originReference.ResourceId == owner.Document.ResourceId))
            throw new InvalidOperationException(L.Get("Prefab.LinkCycle"));
        if (previous?.Source is { } old && old.SourceResourceId != Guid.Empty &&
            (old.SourceResourceId != package.ResourceId || old.EntryKind != kind || kind == PrefabSourceKind.PrefabDefinition && old.EntryDefinitionId != package.EntryDefinitionId) && !replaceIdentity)
            throw new InvalidOperationException(L.Get("Prefab.SourceIdentityChanged"));
        var metadata = new ExternalPrefabSource
        {
            SourceResourceId = package.ResourceId, EntryKind = kind,
            EntryDefinitionId = kind == PrefabSourceKind.PrefabDefinition ? package.EntryDefinitionId : null,
            LastAcceptedFileSha256 = fileVersion.Sha256, LastAcceptedContentRevision = entry.Revision, ImportOrigin = origin, OriginClosure = origins
        };
        if (linked) metadata = Locate(metadata, path, ownerPath);
        return new(owner, revision, scopeVersion, definitionId, previous?.Revision, package, assets, metadata, path, fileVersion, sourceProject);
    }
    internal static bool IsCurrent(PrefabLinkCandidate candidate, EditorSession owner) => ReferenceEquals(candidate.Owner, owner) &&
        owner.SceneRevision == candidate.OwnerRevision && owner.EditScopeVersion == candidate.OwnerScopeVersion && !owner.IsTransactionActive &&
        (candidate.DefinitionId is not { } id || owner.Document.Definitions.Any(definition => definition.Id == id && definition.Revision == candidate.ExpectedDefinitionRevision));

    internal static (PrefabPackage Package, PointD Origin) ProjectContent(LoadedProject project, string name)
    {
        // Keep SourceProject intact for editing/saving the original complete file.
        // The placed definition contains authored Main nodes only; the canvas
        // header remains preview metadata and is never turned into host content.
        var document = SceneCanvasRoots.ExtractRootContent(project.Document, RootCanvasKind.Main);
        var graph = new SceneGraph(document); graph.MaterializeOrder();
        // An unlocked wrapper includes hidden/locked roots without changing their flags.
        var roots = graph.EnumerateChildren(null).ToArray();
        var wrapper = graph.InsertGroup(new() { Name = name, OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] });
        graph.ReparentPreservingWorld(roots, wrapper.Id);
        var temporary = new EditorSession(document, project.Assets); temporary.SetSelection([wrapper.Id]);
        var bounds = temporary.CreateSelectionSnapshot(normalizeOrigin: false).Viewport;
        var created = temporary.CreatePrefabFromSelection(name);
        var package = temporary.ExportPrefab(created.DefinitionId); package.ResourceId = project.Document.ResourceId;
        if (project.Document.ResourceIdentityPersistent) package.RecordResourceIdentityPersistence();
        return (package, new(bounds.X, bounds.Y));
    }
}
