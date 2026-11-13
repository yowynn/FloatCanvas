namespace FloatCanvas.Core;

public enum LayerOperation { Delete, Duplicate, Group, Ungroup, Reorder, Move, Show, Hide, Lock, Unlock }
public sealed record SkippedLayer(Guid Id, string Reason);

/// <summary>A read-only qualification snapshot. Execution must analyze again against the live session.</summary>
public sealed class LayerOperationAnalysis
{
    private readonly SceneGraph graph;
    private readonly RootCanvasKind root;
    private readonly HashSet<Guid> forbiddenTargets = [];
    private readonly HashSet<Guid?> sourceParents = [];
    internal LayerOperationAnalysis(SceneGraph graph, RootCanvasKind root, Guid[] requested, Guid[] eligible,
        Guid[] unchanged, SkippedLayer[] skipped, string? blocked)
    {
        this.graph = graph; this.root = root;
        Requested = Array.AsReadOnly(requested); Eligible = Array.AsReadOnly(eligible);
        Unchanged = Array.AsReadOnly(unchanged); Skipped = Array.AsReadOnly(skipped); BlockReason = blocked;
        foreach (var id in eligible)
        {
            sourceParents.Add(graph.ParentId(id));
            if (graph.FindGroup(id) is not null) forbiddenTargets.UnionWith(graph.DescendantNodeIds(id));
        }
    }
    public IReadOnlyList<Guid> Requested { get; }
    public IReadOnlyList<Guid> Eligible { get; }
    public IReadOnlyList<Guid> Unchanged { get; }
    public IReadOnlyList<SkippedLayer> Skipped { get; }
    public string? BlockReason { get; }
    public bool CanExecute => BlockReason is null && Eligible.Count > 0;

    public string? MoveTargetReason(RootCanvasKind destinationRoot, Guid? parent)
    {
        if (BlockReason is not null) return BlockReason;
        if (Eligible.Count == 0) return "Layers.InvalidTarget";
        if (parent is { } id)
        {
            if (destinationRoot != root || graph.FindGroup(id) is null || graph.Document is not SceneDocument document ||
                SceneCanvasRoots.Resolve(document, id, graph) != destinationRoot) return "Layers.InvalidTarget";
            if (forbiddenTargets.Contains(id)) return "LayerExperience.CyclicTarget";
            if (graph.IsLocked(id)) return "LayerExperience.LockedTarget";
        }
        return destinationRoot == root && sourceParents.Count == 1 && sourceParents.Contains(parent) ? "Layers.CurrentTarget" : null;
    }
}

public sealed partial class EditorSession
{
    public LayerOperationAnalysis AnalyzeLayerOperation(LayerOperation operation, IEnumerable<Guid>? ids = null)
        => AnalyzeLayerOperation(operation, ids ?? Selection, SceneGraph.Read(Document));

    private LayerOperationAnalysis AnalyzeLayerOperation(LayerOperation operation, IEnumerable<Guid> ids, SceneGraph graph)
    {
        var input = ids.Distinct().ToArray(); var requested = graph.NormalizeSelection(input).ToArray();
        string? blocked = IsReadOnly ? "GroupCanvas.ReadOnly" : IsTransactionActive ? "History.TransactionAlreadyActive" :
            !CanInsertInEditScope ? "Inspector.LockedSuffix" : null;
        if (input.Any(id => graph.FindElement(id) is null && graph.FindGroup(id) is null && graph.FindInstance(id) is null) ||
            requested.Any(id => !InActiveRoot(graph, id))) blocked = "Layers.StaleMove";
        var eligible = new List<Guid>(); var unchanged = new List<Guid>(); var skipped = new List<SkippedLayer>();
        bool flags = operation is LayerOperation.Show or LayerOperation.Hide or LayerOperation.Lock or LayerOperation.Unlock;
        foreach (var id in requested)
        {
            bool ancestorLocked = graph.ParentId(id) is { } parent && graph.IsLocked(parent);
            if (operation != LayerOperation.Duplicate && (flags ? ancestorLocked : graph.IsLocked(id)))
            { skipped.Add(new(id, "Inspector.LockedSuffix")); continue; }
            if (operation == LayerOperation.Ungroup && graph.FindGroup(id) is null) { unchanged.Add(id); continue; }
            if (operation == LayerOperation.Ungroup && graph.FindGroup(id)?.Billboard is not null) blocked = "Billboard.UngroupFirstFlat";
            if (flags)
            {
                bool visibility = operation is LayerOperation.Show or LayerOperation.Hide;
                bool desired = operation is LayerOperation.Show or LayerOperation.Lock;
                var group = graph.FindGroup(id); var instance = graph.FindInstance(id); var element = graph.FindElement(id);
                bool current = visibility ? group?.Visible ?? instance?.Visible ?? element!.Visible : group?.Locked ?? instance?.Locked ?? element!.Locked;
                if (current == desired) { unchanged.Add(id); continue; }
            }
            eligible.Add(id);
        }
        return new(graph, ActiveRootCanvas, requested, eligible.ToArray(), unchanged.ToArray(), skipped.ToArray(), blocked);
    }

    public bool CanBrowseNode(Guid id) => !IsTransactionActive &&
        (Document.Groups.Any(group => group.Id == id) || Document.Instances.Any(instance => instance.Id == id));
}
