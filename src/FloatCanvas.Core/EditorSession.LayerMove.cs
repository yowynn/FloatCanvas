using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Core;

public sealed partial class EditorSession
{
    /// <summary>Prepares all graph changes off-session so a failed move cannot partially mutate content.</summary>
    public void MoveLayerNodes(IEnumerable<Guid> ids, RootCanvasKind root, Guid? parent)
    {
        _ = SceneCanvasRoots.PaintRank(root);
        EnsureWritableParent(EditScopeId);
        if (IsTransactionActive) throw new InvalidOperationException(L.Get("History.TransactionAlreadyActive"));
        var graph = new SceneGraph(Document);
        var nodes = graph.NormalizeSelection(ids).Where(id => !graph.IsLocked(id)).ToArray();
        if (nodes.Length == 0) return;
        if (nodes.Any(id => SceneCanvasRoots.Resolve(Document, id, graph) != ActiveRootCanvas)) throw new InvalidOperationException(L.Get("Layers.StaleMove"));
        if (parent is { } destination && (graph.FindGroup(destination) is null || graph.IsLocked(destination) || SceneCanvasRoots.Resolve(Document, destination, graph) != root || nodes.Any(id => id == destination || graph.IsAncestor(id, destination))))
            throw new InvalidOperationException(L.Get("Layers.InvalidTarget"));
        if (root != ActiveRootCanvas && parent is not null) throw new InvalidOperationException(L.Get("Layers.InvalidTarget"));
        if (root == ActiveRootCanvas && nodes.All(id => graph.ParentId(id) == parent)) return;
        var candidate = new EditorSession(Document.Clone(), Assets);
        candidate.SetRootCanvas(ActiveRootCanvas);
        if (root == ActiveRootCanvas) candidate.MoveNodes(nodes, parent);
        else
        {
            foreach (var id in nodes.Where(id => new SceneGraph(candidate.Document).FindGroup(id)?.OrderMode == GroupOrderMode.LegacyPassThrough)) candidate.CollectLegacyGroup(id);
            candidate.MoveNodesToRoot(nodes, root);
        }
        var result = candidate.Document;
        SceneLimits.ValidateCapacity(result);
        var mapping = CanvasMapping.Create(result, root); var evaluated = SceneEvaluation.Build(result);
        if (nodes.SelectMany(evaluated.OccurrencesOfHostNode).Any(leaf => !mapping.IsElementSafe(leaf.RenderElement))) throw new InvalidOperationException(L.Get("Perspective.InputOutsidePlane"));
        Execute(L.Get("Layers.MoveTo"), _ => { Document = result; Selection.ExceptWith(nodes); });
    }
}
