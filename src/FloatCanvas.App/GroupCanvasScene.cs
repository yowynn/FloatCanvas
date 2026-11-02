using FloatCanvas.Core;

namespace FloatCanvas.App;

internal sealed record GroupCanvasView(SceneDocument Document, PointD Origin, object Epoch,
    bool IsRootPreview, bool IsProjected, LayerPlacement? EditingPlacement = null, RootCanvasKind EditingRootCanvas = RootCanvasKind.Main);

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
    private RectD? editingViewport;
    private OwnerState[] owners = [];
    private DraftState[] drafts = [];
    private object epoch = new();
    private GroupCanvasView? view;

    internal GroupCanvasView Capture(EditorSession active, PrefabEditContext? editContext, GroupCanvasMode canvasMode,
        PointD screenOrigin, AssetLibrary assets, RectD? editingViewport = null)
    {
        var chain = editContext?.Chain().ToArray() ?? [];
        var currentOwners = chain.Select(item => new OwnerState(item, item.Owner.Document,
            item.Owner.PreviewRevision, item.Owner.EditScopeVersion, EditingInstance(item))).ToArray();
        var currentDrafts = chain.Select(item => new DraftState(item, item.Session.Document, item.Session.PreviewRevision)).ToArray();
        bool root = canvasMode == GroupCanvasMode.RootProject && (editContext is not null || active.EditScopeId is not null);
        var currentOrigin = root && chain.Length > 0 ? chain[^1].ReturnOrigin : screenOrigin;
        var extent = canvasMode == GroupCanvasMode.Temporary ? editingViewport ?? active.Document.Viewport : (RectD?)null;
        bool sameView = ReferenceEquals(session, active) && ReferenceEquals(document, active.Document) &&
            ReferenceEquals(context, editContext) && mode == canvasMode && scopeVersion == active.EditScopeVersion &&
            origin == currentOrigin && this.editingViewport == extent && owners.SequenceEqual(currentOwners);
        if (!sameView) epoch = new();
        if (sameView && revision == active.PreviewRevision && drafts.SequenceEqual(currentDrafts) && view is not null) return view;

        SceneDocument source;
        if (root && editContext is not null) source = PrefabPreviewComposer.Capture(editContext, assets).Document;
        else if (canvasMode == GroupCanvasMode.Temporary) source = Isolate(active);
        else source = active.Document;
        if (extent is { } bounds && source.Viewport != bounds)
        {
            if (ReferenceEquals(source, active.Document)) source = source.Clone();
            source.Canvas = ResolveEditingCanvas(source.Canvas, source.Viewport);
            source.Viewport = bounds;
        }
        var placement = root && editContext is not null ? EditingPlacement(chain) :
            canvasMode == GroupCanvasMode.Temporary ? active.GetEditingSubprojectPlacement() : (LayerPlacement?)null;
        var editingRoot = canvasMode == GroupCanvasMode.Temporary ? RootCanvasKind.Main : root && chain.Length > 0 && EditingInstance(chain[^1]) is { } host
            ? SceneCanvasRoots.Resolve(chain[^1].Owner.Document, host) : active.ActiveRootCanvas;
        var result = new GroupCanvasView(source, currentOrigin, epoch, root, !ReferenceEquals(source, active.Document), placement, editingRoot);
        session = active; document = active.Document; context = editContext; mode = canvasMode;
        scopeVersion = active.EditScopeVersion; revision = active.PreviewRevision; origin = currentOrigin;
        this.editingViewport = extent;
        owners = currentOwners; drafts = currentDrafts; view = result;
        return result;
    }

    // The saved viewport defines implicit calibration, not the extent of the editor's screen.
    internal static CanvasSettings ResolveEditingCanvas(CanvasSettings settings, RectD reference)
    {
        var result = settings.Clone();
        double cell = CanvasGridGeometry.ResolveCellSize(settings, reference);
        result.Grid.FlatOrigin ??= reference.Center;
        result.Grid.FlatCellSize ??= cell;
        if (settings.Projection.Enabled)
        {
            if (settings.Projection.Quad.Count == 0)
            {
                result.Projection = CanvasCalibration.CreateInitial(reference, settings.Projection.Columns, settings.Projection.Rows);
                result.Projection.Enabled = true;
            }
            result.Grid.PlaneOrigin ??= result.Projection.PinPoint;
            result.Grid.PlaneCellSize ??= cell;
        }
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

    private static SceneDocument Isolate(EditorSession session)
    {
        var source = session.Document;
        if (session.EditScopeId is null && session.ActiveRootCanvas == RootCanvasKind.Main && source.RootCanvasAssignments.Count == 0 &&
            session.GetEditingSubprojectCanvas().Origin == default) return source;
        var isolated = session.EditScopeId is { } scope ? Isolate(source, scope) : SceneCanvasRoots.ExtractRootContent(source, session.ActiveRootCanvas);
        var preview = session.GetEditingSubprojectCanvas(); preview.ApplyTo(isolated);
        TranslateRoots(isolated, new(-preview.Origin.X, -preview.Origin.Y));
        return isolated;
    }

    internal static SceneDocument Isolate(SceneDocument source, Guid scope)
    {
        var graph = new SceneGraph(source); var group = graph.FindGroup(scope) ?? throw new InvalidOperationException("Missing editing group.");
        var retained = graph.DescendantNodeIds(scope).Where(id => id != scope).ToHashSet();
        var isolated = new SceneDocument
        {
            Version = source.Version, Kind = source.Kind, ResourceId = source.ResourceId,
            Elements = source.Elements.Where(element => retained.Contains(element.Id)).Select(element => element.Clone()).ToList(),
            Groups = source.Groups.Where(child => retained.Contains(child.Id)).Select(child => child.Clone()).ToList(),
            Instances = source.Instances.Where(instance => retained.Contains(instance.Id)).Select(instance => instance.Clone()).ToList(),
            RootOrder = graph.PaintOrder(group.OrderMode == GroupOrderMode.AtomicBlock ? scope : graph.PaintContext(scope)).Where(retained.Contains).ToList()
        };
        foreach (var element in isolated.Elements.Where(element => element.GroupId == scope)) element.GroupId = null;
        foreach (var child in isolated.Groups.Where(child => child.ParentId == scope)) child.ParentId = null;
        foreach (var instance in isolated.Instances.Where(instance => instance.ParentId == scope)) instance.ParentId = null;
        isolated.Definitions = PrefabClosure.ReachableDefinitions(isolated, source.Definitions).Select(definition => definition.Clone()).ToList();
        (group.PreviewCanvas ?? new()).ApplyTo(isolated);
        return isolated;
    }

    private static void TranslateRoots(SceneDocument document, PointD offset)
    {
        var transform = Affine2D.Translation(offset.X, offset.Y);
        foreach (var element in document.Elements.Where(element => element.GroupId is null)) SceneGraph.ApplyPlacement(element, new(transform, 1));
        foreach (var group in document.Groups.Where(group => group.ParentId is null)) group.Transform = transform.Compose(group.Transform);
        foreach (var instance in document.Instances.Where(instance => instance.ParentId is null)) instance.Transform = transform.Compose(instance.Transform);
    }
}
