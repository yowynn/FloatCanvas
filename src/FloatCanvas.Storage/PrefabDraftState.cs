using FloatCanvas.Core;
using System.Text.Json.Serialization;

namespace FloatCanvas.Storage;

/// <summary>Recoverable source editing state. Runtime occurrence addresses never enter this DTO.</summary>
public sealed class PrefabDraftState
{
    public Guid OwnerResourceId { get; set; }
    public Guid DefinitionId { get; set; }
    public Guid ExpectedRevision { get; set; }
    public SceneDocument DraftDocument { get; set; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PointD? PreviewOrigin { get; set; }
    public Guid? ReturnScopeId { get; set; }
    public List<Guid> ReturnSelection { get; set; } = [];
    public Guid? DraftScopeId { get; set; }
    public List<Guid> DraftSelection { get; set; } = [];
    public bool IsSourceProject { get; set; }
    public string? SourcePath { get; set; }
    public string? SourceFileSha256 { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PrefabDraftState? ParentEdit { get; set; }
    public PrefabDraftState Clone()
    {
        PrefabDraftState? parent = null;
        foreach (var state in Enumerate(this).Reverse())
            parent = new()
            {
                OwnerResourceId = state.OwnerResourceId, DefinitionId = state.DefinitionId, ExpectedRevision = state.ExpectedRevision,
                DraftDocument = state.DraftDocument.Clone(), PreviewOrigin = state.PreviewOrigin, ReturnScopeId = state.ReturnScopeId, ReturnSelection = [.. state.ReturnSelection],
                DraftScopeId = state.DraftScopeId, DraftSelection = [.. state.DraftSelection], IsSourceProject = state.IsSourceProject,
                SourcePath = state.SourcePath, SourceFileSha256 = state.SourceFileSha256, ParentEdit = parent
            };
        return parent!;
    }
    public static IReadOnlyList<PrefabDraftState> Enumerate(PrefabDraftState? state)
    {
        var result = new List<PrefabDraftState>(); var seen = new HashSet<PrefabDraftState>(ReferenceEqualityComparer.Instance);
        while (state is not null)
        {
            if (result.Count >= SceneLimits.MaximumGroupDepth || !seen.Add(state)) throw new InvalidDataException("Invalid nested prefab edit recovery state.");
            result.Add(state); state = state.ParentEdit;
        }
        return result;
    }
}
