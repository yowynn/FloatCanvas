using System.Text.Json.Serialization;

namespace FloatCanvas.Core;

public sealed class PrefabInstance
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ParentId { get; set; }
    public Guid DefinitionId { get; set; }
    public string Name { get; set; } = "";
    public Affine2D Transform { get; set; } = Affine2D.Identity;
    public double ElevationScale { get; set; } = 1;
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public PrefabInstance Clone() => (PrefabInstance)MemberwiseClone();
}

public enum PrefabSourceKind { ProjectContent, PrefabDefinition }
public sealed record PrefabOriginReference(Guid ResourceId, PrefabSourceKind EntryKind, Guid? EntryDefinitionId = null);

public sealed class ExternalPrefabSource
{
    /// <summary>Null retains portable origin information without an active external update binding.</summary>
    public string? Locator { get; set; }
    public bool IsRelative { get; set; }
    public Guid SourceResourceId { get; set; }
    public PrefabSourceKind EntryKind { get; set; }
    public Guid? EntryDefinitionId { get; set; }
    public string? LastAcceptedFileSha256 { get; set; }
    public Guid? LastAcceptedContentRevision { get; set; }
    public PointD ImportOrigin { get; set; }
    public List<PrefabOriginReference> OriginClosure { get; set; } = [];
    public Dictionary<Guid, Guid> ImportedDefinitionIds { get; set; } = [];
    [JsonIgnore] public bool IsLinked => !string.IsNullOrWhiteSpace(Locator);
    public ExternalPrefabSource Clone()
    {
        var result = (ExternalPrefabSource)MemberwiseClone(); result.OriginClosure = [.. OriginClosure];
        result.ImportedDefinitionIds = new(ImportedDefinitionIds); return result;
    }
}

public sealed class PrefabDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid Revision { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public PointD Pivot { get; set; }
    public SceneNodeGraph Graph { get; set; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SubprojectCanvasSettings? PreviewCanvas { get; set; }
    public ExternalPrefabSource? Source { get; set; }
    public PrefabDefinition Clone(IReadOnlyDictionary<string, string>? assetIds = null) => new()
    {
        Id = Id, Revision = Revision, Name = Name, Pivot = Pivot,
        Graph = SceneGraphClone.CloneGraph(Graph, assetIds: assetIds).Graph, Source = Source?.Clone(), PreviewCanvas = PreviewCanvas?.Clone(assetIds)
    };
}

/// <summary>Persistence metadata shared with snapshots, never part of editable graph content.</summary>
internal sealed class SceneResourceIdentity(Guid id)
{
    internal Guid Id { get; } = id;
    internal bool Persistent { get; set; }
}

public sealed class PrefabPackage
{
    private SceneResourceIdentity identity = new(Guid.NewGuid());
    public int Version { get; set; } = SceneDocument.CurrentVersion;
    public Guid ResourceId { get => identity.Id; set => identity = new(value); }
    public Guid EntryDefinitionId { get; set; }
    public List<PrefabDefinition> Definitions { get; set; } = [];
    [JsonIgnore] public DocumentProvenance Provenance { get; set; } = new();
    [JsonIgnore] public bool ResourceIdentityPersistent => identity.Persistent;
    [JsonIgnore] public IEnumerable<string> ReferencedAssets => Definitions.SelectMany(definition => definition.Graph.ReferencedAssetIds
        .Concat(definition.PreviewCanvas?.ReferencedAssetIds ?? [])).Distinct(StringComparer.Ordinal);
    public void RecordResourceIdentityPersistence() => identity.Persistent = true;
    public PrefabPackage Clone()
    {
        var result = new PrefabPackage { Version = Version, EntryDefinitionId = EntryDefinitionId, Definitions = Definitions.Select(definition => definition.Clone()).ToList(), Provenance = Provenance };
        result.identity = identity; return result;
    }
}

public enum PrefabImportIdentity { WithinOwner, CrossDocument }
public sealed record PrefabImportPlan(SceneNodeGraph Graph, IReadOnlyList<PrefabDefinition> DefinitionsToAdd,
    IReadOnlyDictionary<Guid, Guid> NodeIdMap, IReadOnlyDictionary<Guid, Guid> DefinitionIdMap, IReadOnlySet<string> ReferencedAssetIds);
public sealed record PrefabExpansionBudget(long SourceElements, long SourceGroups, long SourceInstances, int Definitions,
    long ExpandedLeaves, long ExpandedNodes, long ExpandedPathPoints, long ExpandedEffects, int MaximumDepth);
