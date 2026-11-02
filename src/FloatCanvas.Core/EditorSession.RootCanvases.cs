using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Core;

public sealed partial class EditorSession
{
    public RootCanvasKind ActiveRootCanvas { get; private set; } = RootCanvasKind.Main;

    /// <summary>Changes editing navigation only. New documents always start on Main.</summary>
    public void SetRootCanvas(RootCanvasKind root)
    {
        _ = SceneCanvasRoots.PaintRank(root);
        if (IsTransactionActive) throw new InvalidOperationException(L.Get("History.TransactionAlreadyActive"));
        if (ActiveRootCanvas == root && editScopeId is null) return;
        ActiveRootCanvas = root; editScopeId = null; editScopeAncestors = [];
        Selection.Clear(); EditScopeVersion++; OnChanged();
    }

    private bool InActiveRoot(SceneGraph graph, Guid id) => SceneCanvasRoots.Resolve(Document, id, graph) == ActiveRootCanvas;

    /// <summary>Clears this root as one confirmed canvas operation, including locked root content.</summary>
    public bool ClearRootCanvas(string label)
    {
        EnsureWritableParent(null);
        var graph = new SceneGraph(Document);
        var roots = graph.EnumerateChildren(null).Where(id => InActiveRoot(graph, id)).ToArray();
        if (roots.Length == 0) return false;
        Execute(label, document => { new SceneGraph(document).RemoveNodes(roots); Selection.Clear(); });
        return true;
    }

    /// <summary>Moves owned subtrees between roots, retaining logical coordinates. Screen appearance can change.</summary>
    public void MoveNodesToRoot(IEnumerable<Guid> ids, RootCanvasKind root)
    {
        _ = SceneCanvasRoots.PaintRank(root); EnsureWritableParent(null);
        var graph = new SceneGraph(Document);
        var nodes = graph.NormalizeSelection(ids).Where(id => !graph.IsLocked(id)).ToArray();
        if (nodes.Length == 0 || nodes.All(id => graph.ParentId(id) is null && SceneCanvasRoots.Resolve(Document, id, graph) == root)) return;
        var candidate = Document.Clone(); var target = new SceneGraph(candidate);
        target.ReparentPreservingWorld(nodes, null);
        foreach (var id in nodes) SceneCanvasRoots.Assign(candidate, id, root, target);
        SceneLimits.ValidateCapacity(candidate);
        var mapping = CanvasMapping.Create(candidate, root); var evaluated = SceneEvaluation.Build(candidate);
        if (nodes.SelectMany(evaluated.OccurrencesOfHostNode).Any(leaf => !mapping.IsElementSafe(leaf.RenderElement)))
            throw new InvalidOperationException(L.Get("Perspective.InputOutsidePlane"));
        Execute(L.Get("History.ReorderElements"), _ => { Document = candidate; Selection.Clear(); });
    }
}
