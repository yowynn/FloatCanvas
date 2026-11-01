using FloatCanvas.Core;
using FloatCanvas.Rendering;

namespace FloatCanvas.App;

internal sealed partial class FrozenSceneEvaluation
{
    private SceneGraph? screenDraftGraph;
    private readonly Dictionary<Guid, (int Index, bool Visible)> screenDraftSlots = [];

    /// <summary>Runs on the consumer, never during UI capture. Appending to a legacy group
    /// appends to its nearest atomic paint context, rather than gathering scattered children.</summary>
    internal bool TryScreenDraftSlot(Guid? scopeId, out int index, out bool visible)
    {
        var evaluation = value.Value;
        index = evaluation.Scene.Leaves.Count; visible = true;
        if (scopeId is not { } scope) return true;
        if (screenDraftSlots.TryGetValue(scope, out var cached))
        { index = cached.Index; visible = cached.Visible; return true; }
        var graph = screenDraftGraph ??= new SceneGraph(source);
        if (graph.FindGroup(scope) is null) return false;
        visible = graph.IsVisible(scope);
        Guid? context = scope;
        while (context is { } current && graph.FindGroup(current)!.OrderMode != GroupOrderMode.AtomicBlock)
            context = graph.ParentId(current);
        if (context is { } atomic) index = EndOfContext(atomic);
        screenDraftSlots.Add(scope, (index, visible)); return true;

        int EndOfContext(Guid id)
        {
            var leaves = evaluation.Scene.OccurrencesOfHostNode(id);
            if (leaves.Count > 0) return leaves.Max(leaf => evaluation.Indices[leaf.RuntimeElementId]) + 1;
            // Empty folders have a real slot, even though they have no occurrence. Search
            // following siblings, then following siblings of their enclosing paint context.
            Guid? current = id;
            while (current is { } token)
            {
                var parent = graph.PaintContext(token);
                var order = graph.PaintOrder(parent);
                int at = -1;
                for (int position = 0; position < order.Count; position++) if (order[position] == token) { at = position; break; }
                if (at < 0) throw new InvalidOperationException("An atomic folder is missing its paint slot.");
                for (int position = at + 1; position < order.Count; position++)
                {
                    var following = evaluation.Scene.OccurrencesOfHostNode(order[position]);
                    if (following.Count > 0) return following.Min(leaf => evaluation.Indices[leaf.RuntimeElementId]);
                }
                current = parent;
            }
            return evaluation.Scene.Leaves.Count;
        }
    }
}
