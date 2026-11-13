using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Core;

public sealed record LayerMoveResult(IReadOnlyList<Guid> Moved, IReadOnlyList<Guid> Unchanged, IReadOnlyList<SkippedLayer> Skipped);

/// <summary>Prepared off-session topology/placement, bound to the exact document, scope and selection.</summary>
public sealed class LayerMovePlan
{
    internal LayerMovePlan(EditorSession session, LayerOperationAnalysis analysis, SceneDocument? candidate, RootCanvasKind root, Guid? parent)
    {
        Owner = session; Source = session.Document; Generation = session.DocumentGeneration;
        Revision = session.PreviewRevision; Scope = session.EditScopeVersion; Selection = session.Selection.ToHashSet();
        Analysis = analysis; Candidate = candidate; Root = root; Parent = parent;
    }
    internal EditorSession Owner { get; }
    internal SceneDocument Source { get; }
    internal SceneDocument? Candidate { get; }
    internal long Generation { get; }
    internal long Revision { get; }
    internal long Scope { get; }
    internal HashSet<Guid> Selection { get; }
    internal bool Consumed { get; set; }
    public LayerOperationAnalysis Analysis { get; }
    public RootCanvasKind Root { get; }
    public Guid? Parent { get; }
    public bool HasChanges => Candidate is not null;
}

public sealed partial class EditorSession
{
    public void MoveLayerNodes(IEnumerable<Guid> ids, RootCanvasKind root, Guid? parent)
        => CommitLayerMove(PrepareLayerMove(ids, root, parent));

    public LayerMovePlan PrepareLayerMove(IEnumerable<Guid> ids, RootCanvasKind root, Guid? parent)
    {
        _ = SceneCanvasRoots.PaintRank(root);
        var graph = new SceneGraph(Document);
        var analysis = AnalyzeLayerOperation(LayerOperation.Move, ids, graph);
        if (analysis.BlockReason is { } blocked) throw new InvalidOperationException(L.Get(blocked));
        if (analysis.Eligible.Count == 0) return new(this, analysis, null, root, parent);
        var reason = analysis.MoveTargetReason(root, parent);
        if (reason == "Layers.CurrentTarget") return new(this, analysis, null, root, parent);
        if (reason is not null) throw new InvalidOperationException(L.Get(reason));
        var nodes = analysis.Eligible;
        var candidate = Document.CreatePlacementDraft(nodes.ToHashSet());
        var target = new SceneGraph(candidate);
        foreach (var id in nodes)
            if (target.FindGroup(id)?.OrderMode == GroupOrderMode.LegacyPassThrough) target.CollectLegacyGroup(id);
        target.ReparentPreservingWorld(nodes, parent);
        if (root != ActiveRootCanvas)
            foreach (var id in nodes) SceneCanvasRoots.Assign(candidate, id, root, target);
        if (SceneGraph.PruneEmptyGroups(candidate)) target = new(candidate);
        candidate.Elements = target.OrderedLeafIds.Select(id => target.FindElement(id)!).ToList();
        var mapping = CanvasMapping.Create(candidate, root);
        if (SceneEvaluation.ResolveHostNodes(candidate, nodes, target).Any(leaf => !mapping.IsElementSafe(leaf.RenderElement)))
            throw new InvalidOperationException(L.Get("Perspective.InputOutsidePlane"));
        // Counts cannot grow; depth/ownership/order were checked by the topology index above.
        // The normal commit still validates full capacity and rolls back on any failure.
        return new(this, analysis, candidate, root, parent);
    }

    public LayerMoveResult CommitLayerMove(LayerMovePlan plan)
    {
        if (plan.Consumed || !ReferenceEquals(plan.Owner, this) || !ReferenceEquals(plan.Source, Document) ||
            plan.Generation != DocumentGeneration || plan.Revision != PreviewRevision || plan.Scope != EditScopeVersion ||
            !Selection.SetEquals(plan.Selection) || IsTransactionActive)
            throw new InvalidOperationException(L.Get("Layers.StaleMove"));
        EnsureWritableParent(EditScopeId);
        plan.Consumed = true;
        if (plan.Candidate is not { } candidate) return new([], plan.Analysis.Eligible, plan.Analysis.Skipped);
        BeginTransaction(L.Get("Layers.MoveTo"));
        try
        {
            var document = Document;
            document.Elements = candidate.Elements; document.Groups = candidate.Groups; document.Instances = candidate.Instances;
            document.RootOrder = candidate.RootOrder; document.RootCanvasAssignments = candidate.RootCanvasAssignments;
            Selection.ExceptWith(plan.Analysis.Eligible);
            // A prepared move always changes at least one owner or root, and never edits a definition.
            CommitCore(settingsOnly: false, knownContentChange: true, knownDefinitionsUnchanged: true);
        }
        catch { Cancel(); throw; }
        return new(plan.Analysis.Eligible, [], plan.Analysis.Skipped);
    }
}
