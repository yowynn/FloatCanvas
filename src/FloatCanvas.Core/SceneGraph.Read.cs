namespace FloatCanvas.Core;

public sealed partial class SceneGraph
{
    [ThreadStatic] private static ReadScope? currentReadScope;

    /// <summary>Reuses indexes during one synchronous UI read. Never retain across an await or dispatcher turn.</summary>
    public static IDisposable BeginReadScope()
    {
        var scope = new ReadScope(currentReadScope);
        currentReadScope = scope;
        return scope;
    }

    /// <summary>A query-only index. Mutating commands must construct their own graph.</summary>
    public static SceneGraph Read(SceneNodeGraph document)
    {
        if (currentReadScope is not { } scope) return new(document);
        if (!scope.Graphs.TryGetValue(document, out var graph)) scope.Graphs.Add(document, graph = new(document));
        return graph;
    }

    internal static void InvalidateReads(SceneNodeGraph document) => currentReadScope?.Graphs.Remove(document);

    private sealed class ReadScope(ReadScope? parent) : IDisposable
    {
        private bool disposed;
        internal Dictionary<SceneNodeGraph, SceneGraph> Graphs { get; } = parent?.Graphs ?? new(ReferenceEqualityComparer.Instance);
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; currentReadScope = parent;
            if (parent is null) Graphs.Clear();
        }
    }
}
