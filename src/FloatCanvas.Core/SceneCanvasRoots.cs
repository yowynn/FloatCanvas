namespace FloatCanvas.Core;

public enum RootCanvasKind { Main = 0, Foreground = 1, Background = 2 }

/// <summary>Document roots are coordinate spaces, separate from ordinary ownership groups.</summary>
public static class SceneCanvasRoots
{
    /// <summary>Local contents and independent canvas for one root, without importing the other root planes.</summary>
    public static SceneDocument ExtractRootContent(SceneDocument document, RootCanvasKind root)
    {
        _ = PaintRank(root);
        var graph = new SceneGraph(document);
        var selected = graph.EnumerateChildren(null).Where(id => Resolve(document, id, graph) == root).ToArray();
        var result = document.CloneElements(selected);
        var preview = root == RootCanvasKind.Main ? SubprojectCanvasSettings.FromDocument(document) :
            document.RootPreviewCanvases.GetValueOrDefault(root)?.Clone() ?? new SubprojectCanvasSettings();
        preview.ApplyTo(result);
        result.RootCanvasAssignments.Clear(); result.RootPreviewCanvases.Clear();
        result.Version = document.Version; result.Kind = document.Kind;
        return result;
    }

    public static int PaintRank(RootCanvasKind root) => root switch
    {
        RootCanvasKind.Background => 0, RootCanvasKind.Main => 1, RootCanvasKind.Foreground => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(root))
    };

    public static Guid TopLevelOwner(SceneGraph graph, Guid nodeId)
    {
        while (graph.ParentId(nodeId) is { } parent) nodeId = parent;
        return nodeId;
    }

    public static RootCanvasKind Resolve(SceneDocument document, Guid nodeId, SceneGraph? graph = null)
    {
        if (document.RootCanvasAssignments.Count == 0) return RootCanvasKind.Main;
        if (document.RootCanvasAssignments.TryGetValue(nodeId, out var direct)) return direct;
        if (document.Groups.Count == 0) return RootCanvasKind.Main;
        return document.RootCanvasAssignments.GetValueOrDefault(TopLevelOwner(graph ?? new SceneGraph(document), nodeId));
    }

    public static void Assign(SceneDocument document, Guid rootNodeId, RootCanvasKind root, SceneGraph? graph = null)
    {
        _ = PaintRank(root);
        graph ??= new SceneGraph(document);
        if (graph.ParentId(rootNodeId) is not null) throw new InvalidDataException("A root canvas assignment requires a top-level node.");
        if (root == RootCanvasKind.Main) document.RootCanvasAssignments.Remove(rootNodeId);
        else document.RootCanvasAssignments[rootNodeId] = root;
    }

    public static void Prune(SceneDocument document)
    {
        if (document.RootCanvasAssignments.Count == 0) return;
        var roots = new SceneGraph(document).EnumerateChildren(null).ToHashSet();
        foreach (var id in document.RootCanvasAssignments.Where(pair => pair.Value == RootCanvasKind.Main || !roots.Contains(pair.Key)).Select(pair => pair.Key).ToArray())
            document.RootCanvasAssignments.Remove(id);
    }

    public static void Validate(SceneDocument document)
    {
        if (document.RootCanvasAssignments is null) throw new InvalidDataException("Missing root canvas assignments.");
        if (document.RootCanvasAssignments.Count == 0) return;
        var roots = new SceneGraph(document).EnumerateChildren(null).ToHashSet();
        foreach (var pair in document.RootCanvasAssignments)
            if (!roots.Contains(pair.Key) || pair.Value is not (RootCanvasKind.Background or RootCanvasKind.Foreground))
                throw new InvalidDataException("Invalid root canvas assignment.");
    }
}
