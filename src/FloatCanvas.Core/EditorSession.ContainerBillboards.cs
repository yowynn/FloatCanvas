using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Core;

public sealed partial class EditorSession
{
    private SceneEvaluationCache placementDefinitions = new();
    private long placementDefinitionsRevision = -1;
    private SceneDocument? placementDocument;
    private SceneEvaluationCache PlacementDefinitions()
    {
        // The session owns these DTOs; every unknown/definition change invalidates this bounded cache.
        if (!ReferenceEquals(placementDocument, Document) || placementDefinitionsRevision != PreviewDefinitionsRevision)
        { placementDocument = Document; placementDefinitionsRevision = PreviewDefinitionsRevision; placementDefinitions = new(); }
        return placementDefinitions;
    }
    public void SetContainerBillboard(Guid id, bool enabled)
    {
        var graph = new SceneGraph(Document);
        if (graph.FindGroup(id) is null && graph.FindInstance(id) is null) throw new InvalidOperationException(L.Get("Inspector.LayerMissing"));
        if ((graph.ContainerBillboard(id) is not null) == enabled) return;
        RequireWritableScope();
        if (graph.IsLocked(id)) throw new InvalidOperationException(L.Get("GroupCanvas.ReadOnly"));
        var bounds = enabled ? ContainerBillboardGeometry.LocalBounds(Document, id) : default;
        Execute(L.Get("Billboard.Placement"), document =>
        {
            SetContainerBillboardCore(document, id, enabled, bounds);
            EnsureSafeInsertion(SceneEvaluation.Build(document).OccurrencesOfHostNode(id).Select(leaf => leaf.RenderElement));
        });
    }

    internal static void SetContainerBillboardCore(SceneDocument document, Guid id, bool enabled, RectD bounds)
    {
        var graph = new SceneGraph(document);
        var group = graph.FindGroup(id); var instance = graph.FindInstance(id);
        var transform = group?.Transform ?? instance!.Transform;
        double elevation = group?.ElevationScale ?? instance!.ElevationScale;
        var old = group?.Billboard ?? instance?.Billboard;
        ContainerBillboard? next = null;
        if (enabled)
        {
            if (bounds.IsEmpty || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height)) throw new InvalidOperationException(L.Get("Billboard.InvalidLogicalPlacement"));
            var center = transform.TransformPoint(bounds.Center); var frame = transform.TransformBounds(bounds);
            next = new(bounds.Center, (frame.Bottom - center.Y) / elevation);
            transform = transform with { OffsetX = center.X, OffsetY = frame.Bottom };
            if (group is not null) graph.CollectLegacyGroup(id);
        }
        else if (old is not null) transform = old.FlatPlacement(new(transform, elevation));
        if (group is not null) { group.Transform = transform; group.Billboard = next; }
        else { instance!.Transform = transform; instance.Billboard = next; }
    }

    public void SetContainerBillboardHeight(Guid id, double height)
    {
        RequireWritableScope(); var graph = new SceneGraph(Document);
        if (!double.IsFinite(height) || graph.ContainerBillboard(id) is not { } billboard || graph.IsLocked(id))
            throw new InvalidOperationException(L.Get("Billboard.InvalidLogicalPlacement"));
        if (billboard.Height == height) return;
        Execute(L.Get("Billboard.Height"), document =>
        {
            var current = new SceneGraph(document);
            if (current.FindGroup(id) is { } group) group.Billboard = billboard with { Height = height };
            else current.FindInstance(id)!.Billboard = billboard with { Height = height };
            EnsureSafeInsertion(SceneEvaluation.Build(document).OccurrencesOfHostNode(id).Select(leaf => leaf.RenderElement));
        });
    }

    public bool TryUpdateContainerBillboardHeight(Guid id, double height)
    {
        if (!IsTransactionActive || !double.IsFinite(height)) return false;
        RequireWritableScope(); var graph = new SceneGraph(Document);
        if (graph.IsLocked(id) || graph.ContainerBillboard(id) is not { } previous) return false;
        var group = graph.FindGroup(id); var instance = graph.FindInstance(id);
        void Set(ContainerBillboard value) { if (group is not null) group.Billboard = value; else instance!.Billboard = value; }
        Set(previous with { Height = height });
        try { EnsureSafeInsertion(SceneEvaluation.ResolveHostNodes(Document, [id], definitions: PlacementDefinitions()).Select(leaf => leaf.RenderElement)); }
        catch (InvalidOperationException) { Set(previous); return false; }
        NotifyContainerPlacementChanged([id]); return true;
    }
}

public static class ContainerBillboardGeometry
{
    /// <summary>Only authored local content; the container's preview canvas and outer placement are excluded.</summary>
    public static SceneDocument LocalContent(SceneDocument document, Guid id)
    {
        var graph = new SceneGraph(document);
        if (graph.FindInstance(id) is { } instance)
        {
            var definition = document.Definitions.Single(item => item.Id == instance.DefinitionId);
            return new() { Elements = definition.Graph.Elements, Groups = definition.Graph.Groups, Instances = definition.Graph.Instances,
                RootOrder = definition.Graph.RootOrder, Definitions = document.Definitions };
        }
        var group = graph.FindGroup(id) ?? throw new InvalidOperationException(L.Get("Inspector.LayerMissing"));
        var retained = graph.DescendantNodeIds(id).Where(node => node != id).ToHashSet();
        var local = new SceneDocument
        {
            Elements = document.Elements.Where(item => retained.Contains(item.Id)).Select(item => item.Clone()).ToList(),
            Groups = document.Groups.Where(item => retained.Contains(item.Id)).Select(item => item.Clone()).ToList(),
            Instances = document.Instances.Where(item => retained.Contains(item.Id)).Select(item => item.Clone()).ToList(),
            RootOrder = graph.PaintOrder(group.OrderMode == GroupOrderMode.AtomicBlock ? id : graph.PaintContext(id)).Where(retained.Contains).ToList(),
            Definitions = document.Definitions
        };
        foreach (var item in local.Elements) if (item.GroupId == id) item.GroupId = null;
        foreach (var item in local.Groups) if (item.ParentId == id) item.ParentId = null;
        foreach (var item in local.Instances) if (item.ParentId == id) item.ParentId = null;
        return local;
    }

    public static RectD LocalBounds(SceneDocument document, Guid id)
    {
        var local = SceneEvaluation.Build(LocalContent(document, id));
        return local.Leaves.Select(leaf => ElementGeometry.LocalToPlane(leaf.RenderElement)
            .TransformBounds(ElementGeometry.StrokedLocalBounds(leaf.RenderElement))).DefaultIfEmpty().Aggregate(RectD.Union);
    }
}

/// <summary>One-shot insertion intent; mapping and candidate never enter the file format.</summary>
public sealed record ContainerBillboardInsertion(CanvasMapping Mapping, PointD ScreenCenter, BillboardDepthLine? Depth = null)
{
    public CanvasElement ResolveCandidate(RectD bounds)
    {
        if (bounds.IsEmpty) throw new InvalidOperationException(L.Get("Billboard.InvalidLogicalPlacement"));
        var proxy = new CanvasElement { Kind = ElementKind.Rectangle, Width = bounds.Width, Height = bounds.Height };
        proxy.Style.StrokeWidth = 0;
        CanvasElement candidate;
        if (Depth is { } line)
        {
            if (!BillboardGeometry.TryAnchor(Mapping, line, ScreenCenter, out var anchor, out double height))
                throw new InvalidOperationException(L.Get("Billboard.InvalidLogicalPlacement"));
            candidate = proxy; candidate.IsBillboard = true; candidate.BillboardAnchor = anchor; candidate.BillboardHeight = height;
        }
        else if (!BillboardCreationGeometry.TryPlaceLogical(proxy, Mapping, ElementGeometry.LocalBounds(proxy).Center, ScreenCenter, out candidate, out _))
            throw new InvalidOperationException(L.Get("Billboard.InvalidLogicalPlacement"));
        return candidate;
    }
    public void Apply(SceneDocument document, Guid id)
    {
        var bounds = ContainerBillboardGeometry.LocalBounds(document, id);
        var candidate = ResolveCandidate(bounds);
        var graph = new SceneGraph(document);
        var local = graph.ParentWorldPlacement(id).Inverse().Compose(new(Affine2D.Translation(candidate.BillboardAnchor.X, candidate.BillboardAnchor.Y), 1));
        var card = new ContainerBillboard(bounds.Center, candidate.BillboardHeight);
        if (graph.FindGroup(id) is { } group)
        {
            graph.CollectLegacyGroup(id); group.Transform = local.PlaneTransform; group.ElevationScale = local.ElevationScale; group.Billboard = card;
        }
        else { var instance = graph.FindInstance(id)!; instance.Transform = local.PlaneTransform; instance.ElevationScale = local.ElevationScale; instance.Billboard = card; }
    }
}
