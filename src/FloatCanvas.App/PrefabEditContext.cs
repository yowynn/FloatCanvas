using FloatCanvas.Core;

namespace FloatCanvas.App;

/// <summary>A real editable graph, with an explicit return location and one publishing owner.</summary>
internal sealed class PrefabEditContext
{
    internal EditorSession Owner { get; }
    internal EditorSession Session { get; }
    internal Guid DefinitionId { get; }
    internal Guid ExpectedRevision { get; }
    internal PrefabEditDraft? Draft { get; }
    internal string? SourcePath { get; set; }
    internal Guid? SourceEntryDefinitionId { get; }
    internal bool IsOfflineSnapshot => Session.IsReadOnly;
    internal bool IsSource => SourcePath is not null && !IsOfflineSnapshot;
    internal bool IsPrefabSource => SourceEntryDefinitionId is not null;
    internal PrefabEditContext? Parent { get; }
    internal Guid? EnteredInstanceId { get; set; }
    internal PointD ReturnOrigin { get; }
    internal EditorTool ReturnTool { get; }
    internal Guid? ReturnScopeId { get; }
    internal Guid[] ReturnSelection { get; }
    internal long InitialContentVersion { get; }
    internal bool Restored { get; set; }
    internal bool HasChanges => Restored || Session.ContentVersion != InitialContentVersion || Session.HasPendingChanges;
    internal string Name => Owner.Document.Definitions.FirstOrDefault(definition => definition.Id == DefinitionId)?.Name ?? "";
    internal string DisplayName => EnteredInstanceId is { } id && new SceneGraph(Owner.Document).FindInstance(id) is { Name.Length: > 0 } instance
        ? instance.Name : Name;

    internal PrefabEditContext(EditorSession owner, PrefabEditDraft draft, PointD returnOrigin, EditorTool returnTool, PrefabEditContext? parent = null)
        : this(owner, draft.Session, draft.DefinitionId, draft.ExpectedRevision, returnOrigin, returnTool, parent)
    { Draft = draft; }
    internal PrefabEditContext(EditorSession owner, EditorSession session, Guid definitionId, Guid expectedRevision,
        PointD returnOrigin, EditorTool returnTool, PrefabEditContext? parent = null, string? sourcePath = null, Guid? sourceEntryDefinitionId = null)
    {
        Owner = owner; Session = session; DefinitionId = definitionId; ExpectedRevision = expectedRevision;
        ReturnOrigin = returnOrigin; ReturnTool = returnTool; Parent = parent; SourcePath = sourcePath; SourceEntryDefinitionId = sourceEntryDefinitionId;
        ReturnScopeId = owner.EditScopeId; ReturnSelection = owner.Selection.ToArray(); InitialContentVersion = session.ContentVersion;
    }
    internal IEnumerable<PrefabEditContext> Chain()
    {
        for (PrefabEditContext? context = this; context is not null; context = context.Parent) yield return context;
    }
    internal bool Contains(EditorSession owner, Guid definitionId) => Chain().Any(context => context.Owner.Document.ResourceId == owner.Document.ResourceId && context.DefinitionId == definitionId);
    internal IEnumerable<string> RetainedAssets() => Chain().SelectMany(context => context.Owner.ReferencedAssetsIncludingHistory()
        .Concat(context.Session.ReferencedAssetsIncludingHistory())).Distinct(StringComparer.Ordinal);
}
