using FloatCanvas.Core;

namespace FloatCanvas.App;

internal sealed record GroupCanvasView(SceneDocument Document, PointD Origin, object Epoch,
    bool IsRootPreview, bool IsProjected, LayerPlacement? EditingPlacement = null);

/// <summary>A presentation of the current editing graph. Changing the presentation never publishes a draft.</summary>
internal sealed class GroupCanvasScene
{
    private sealed record OwnerState(PrefabEditContext Context, SceneDocument Document, long Revision, long ScopeVersion, Guid? Instance);
    private sealed record DraftState(PrefabEditContext Context, SceneDocument Document, long Revision);
    private EditorSession? session;
    private SceneDocument? document;
    private PrefabEditContext? context;
    private GroupCanvasMode mode;
    private long scopeVersion = -1, revision = -1;
    private PointD origin;
    private OwnerState[] owners = [];
    private DraftState[] drafts = [];
    private object epoch = new();
    private GroupCanvasView? view;

    internal GroupCanvasView Capture(EditorSession active, PrefabEditContext? editContext, GroupCanvasMode canvasMode,
        PointD screenOrigin, AssetLibrary assets)
    {
        var chain = editContext?.Chain().ToArray() ?? [];
        var currentOwners = chain.Select(item => new OwnerState(item, item.Owner.Document,
            item.Owner.PreviewRevision, item.Owner.EditScopeVersion, EditingInstance(item))).ToArray();
        var currentDrafts = chain.Select(item => new DraftState(item, item.Session.Document, item.Session.PreviewRevision)).ToArray();
        bool root = canvasMode == GroupCanvasMode.RootProject && (editContext is not null || active.EditScopeId is not null);
        var currentOrigin = root && chain.Length > 0 ? chain[^1].ReturnOrigin : screenOrigin;
        bool sameView = ReferenceEquals(session, active) && ReferenceEquals(document, active.Document) &&
            ReferenceEquals(context, editContext) && mode == canvasMode && scopeVersion == active.EditScopeVersion &&
            origin == currentOrigin && owners.SequenceEqual(currentOwners);
        if (!sameView) epoch = new();
        if (sameView && revision == active.PreviewRevision && drafts.SequenceEqual(currentDrafts) && view is not null) return view;

        SceneDocument source;
        if (root && editContext is not null) source = PrefabPreviewComposer.Capture(editContext, assets).Document;
        else if (!root && active.EditScopeId is { } scope) source = Isolate(active.Document, scope);
        else source = active.Document;
        var placement = root && editContext is not null ? EditingPlacement(chain) : null;
        var result = new GroupCanvasView(source, currentOrigin, epoch, root, !ReferenceEquals(source, active.Document), placement);
        session = active; document = active.Document; context = editContext; mode = canvasMode;
        scopeVersion = active.EditScopeVersion; revision = active.PreviewRevision; origin = currentOrigin;
        owners = currentOwners; drafts = currentDrafts; view = result;
        return result;
    }

    private static Guid? EditingInstance(PrefabEditContext context)
    {
        var graph = new SceneGraph(context.Owner.Document);
        bool Matches(Guid id) => graph.FindInstance(id)?.DefinitionId == context.DefinitionId;
        if (context.EnteredInstanceId is { } entered) return Matches(entered) ? entered : null;
        // Resource editing has no clicked occurrence. Prefer its retained selection,
        // then use the first visible occurrence in paint order as the editing anchor.
        foreach (var id in context.ReturnSelection) if (Matches(id)) return id;
        return graph.OrderedContentNodeIds.Where(id => Matches(id) && graph.IsVisible(id)).Select(id => (Guid?)id).FirstOrDefault();
    }

    private static LayerPlacement? EditingPlacement(IReadOnlyList<PrefabEditContext> chain)
    {
        var result = LayerPlacement.Identity;
        foreach (var context in chain)
        {
            if (EditingInstance(context) is not { } id) return null;
            var owner = context.Owner.Document;
            var graph = new SceneGraph(owner);
            var sourceOffset = default(PointD);
            if (context.Draft is null && owner.Definitions.Single(definition => definition.Id == context.DefinitionId).Source is { } source)
            {
                sourceOffset = new(-source.ImportOrigin.X, -source.ImportOrigin.Y);
                if (context.SourceEntryDefinitionId is { } entry)
                    sourceOffset += context.Session.Document.Definitions.Single(definition => definition.Id == entry).Pivot;
            }
            result = graph.NodeWorldPlacement(id).Compose(new(Affine2D.Translation(sourceOffset.X, sourceOffset.Y), 1)).Compose(result);
        }
        return result;
    }

    internal static SceneDocument Isolate(SceneDocument source, Guid scope)
    {
        // Keep the ancestor placements and real IDs: the controller continues to edit
        // the owning document in its existing world coordinate system.
        var isolated = source.CloneElements([scope]);
        isolated.Version = source.Version; isolated.Kind = source.Kind;
        isolated.Canvas = source.Canvas.Clone();
        isolated.DisplayLayout = source.DisplayLayout.Select(display => display.Clone()).ToList();
        isolated.Background = null;
        return isolated;
    }
}
