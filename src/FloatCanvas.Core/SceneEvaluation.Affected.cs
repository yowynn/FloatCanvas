namespace FloatCanvas.Core;

public static partial class SceneEvaluation
{
    /// <summary>Resolve only owned subtrees for validation. No full-world occurrence maps are built.</summary>
    public static IReadOnlyList<ResolvedLeaf> ResolveHostNodes(SceneDocument source, IEnumerable<Guid> nodeIds,
        SceneGraph? graph = null, SceneEvaluationCache? definitions = null, EvaluatedScene? identities = null)
    {
        graph ??= new(source); definitions ??= new();
        var result = new List<ResolvedLeaf>();
        foreach (var id in graph.NormalizeSelection(nodeIds)) Visit(id);
        return result;

        void Visit(Guid id)
        {
            if (graph.FindGroup(id) is not null)
            { foreach (var child in graph.EnumerateChildren(id)) Visit(child); return; }
            var root = SceneCanvasRoots.Resolve(source, id, graph);
            if (graph.FindElement(id) is { } element)
            {
                var container = graph.ScopeContainerPlacement(element.GroupId);
                var world = container.Resolve(element); world.GroupId = null;
                world.Visible = graph.IsVisible(id); world.Locked = graph.IsLocked(id);
                world.Style.Opacity *= graph.InheritedOpacity(id);
                result.Add(new(identities?.FindOccurrence(id)?.OccurrencePath ?? new(id.ToString("N")), new(null, id), world, container.Placement, id, root));
                return;
            }
            var instance = graph.FindInstance(id)!;
            var definition = source.Definitions.First(d => d.Id == instance.DefinitionId);
            var context = graph.ScopeContainerPlacement(instance.ParentId).Enter(instance.Transform, instance.ElevationScale, instance.Billboard);
            var local = definitions.Definition(definition, source.Definitions);
            string prefix = "i:" + id.ToString("N") + "/d:" + definition.Id.ToString("N") + "/";
            bool shown = graph.IsVisible(id), locked = graph.IsLocked(id);
            var previous = identities?.OccurrencesOfHostNode(id);
            for (int i = 0; i < local.Leaves.Count; i++)
            {
                var leaf = local.Leaves[i];
                var occurrence = previous is not null && previous.Count == local.Leaves.Count ? previous[i].OccurrencePath :
                    new OccurrencePath(prefix + (leaf.Source.DefinitionId is null ? "e:" : "") + leaf.OccurrencePath.Value);
                var world = context.ResolveWorld(leaf.RenderElement);
                world.Id = previous is not null && previous.Count == local.Leaves.Count ? previous[i].RuntimeElementId : RuntimeId(occurrence);
                world.GroupId = null; world.Visible &= shown; world.Locked |= locked;
                world.Style.Opacity *= graph.InheritedOpacity(id) * instance.Opacity;
                result.Add(new(occurrence, leaf.Source with { DefinitionId = leaf.Source.DefinitionId ?? definition.Id }, world,
                    context.Placement.Compose(leaf.ParentWorld), id, root));
            }
        }
    }

    /// <summary>Update placement and inherited appearance with unchanged topology and definition identities.</summary>
    public static EvaluatedScene UpdatePlacements(SceneDocument source, EvaluatedScene previous, IEnumerable<Guid> changedContainers,
        SceneEvaluationCache definitions)
    {
        var graph = new SceneGraph(source);
        var nodes = graph.NormalizeSelection(changedContainers);
        var leaves = previous.Leaves.ToArray();
        foreach (var leaf in ResolveHostNodes(source, nodes, graph, definitions, previous))
        {
            int index = previous.IndexOf(leaf.RuntimeElementId);
            if (index < 0) throw new InvalidOperationException("A placement update cannot change occurrence identity.");
            leaves[index] = leaf;
        }
        var old = previous.RenderDocument;
        var render = new SceneDocument
        {
            Version = source.Version, Kind = source.Kind, ResourceId = source.ResourceId, Provenance = source.Provenance,
            IsRuntimeEvaluated = true, Elements = leaves.Select(leaf => leaf.RenderElement).ToList(),
            RootCanvasAssignments = old.RootCanvasAssignments, Background = old.Background, DisplayLayout = old.DisplayLayout,
            Canvas = source.Canvas.Clone(), Viewport = source.Viewport, AuthoringReference = source.AuthoringReference,
            LayerOpacity = source.LayerOpacity, NextNumber = source.NextNumber
        };
        var result = new EvaluatedScene(render, previous, Array.AsReadOnly(leaves), graph, nodes.SelectMany(graph.DescendantNodeIds));
        runtimeViews.Add(render, result); return result;
    }
}
