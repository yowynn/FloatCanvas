using FloatCanvas.Core;
using Wpf = System.Windows;

namespace FloatCanvas.App;

internal sealed partial class LayersWindow
{
    private EditorSession? locateSession;
    private SceneDocument? locateDocument;
    private readonly HashSet<Guid> locateSelectionIds = [];
    private Guid[] locateOrder = [];
    private int locateIndex = -1;
    private long locateRevision;
    private bool locatingSelection;

    private void RefreshLocateSelection(EditorSession session)
    {
        if (!locatingSelection && (!ReferenceEquals(locateSession, session) || !ReferenceEquals(locateDocument, session.Document) ||
            locateRevision != session.PreviewRevision || !locateSelectionIds.SetEquals(session.Selection)))
        {
            locateSession = session; locateDocument = session.Document;
            locateRevision = session.PreviewRevision;
            locateSelectionIds.Clear(); locateSelectionIds.UnionWith(session.Selection);
            locateOrder = []; locateIndex = -1;
        }
        locateSelection.Visibility = session.Selection.Count == 0 ? Wpf.Visibility.Collapsed : Wpf.Visibility.Visible;
        locateSelection.IsEnabled = canMutate() && !session.IsTransactionActive;
    }

    private void LocateSelection()
    {
        var session = getSession();
        if (!canMutate() || session.IsTransactionActive || session.Selection.Count == 0) return;
        CommitRename(); CancelDrag();
        RefreshLocateSelection(session);
        using var read = SceneGraph.BeginReadScope();
        var graph = SceneGraph.Read(session.Document);
        if (locateOrder.Length == 0)
        {
            // Top-to-bottom row order, also when the canvas selected a descendant
            // that is not in the current direct-child list.
            var ordered = new List<Guid>();
            void Visit(Guid? parent)
            {
                foreach (var id in graph.EnumerateChildren(parent).Reverse())
                {
                    if (session.Selection.Contains(id)) ordered.Add(id);
                    if (graph.FindGroup(id) is not null) Visit(id);
                }
            }
            Visit(null); locateOrder = ordered.ToArray();
        }
        if (locateOrder.Length == 0) return;
        locateIndex = (locateIndex + 1) % locateOrder.Length;
        Guid target = locateOrder[locateIndex];
        Guid? parent = graph.ParentId(target);
        if (session.EditScopeId != parent)
        {
            var selection = session.Selection.ToArray();
            locatingSelection = true;
            try { session.SetEditScope(parent); session.SetSelection(selection); }
            finally { locatingSelection = false; }
        }
        RefreshRows(session);
        if (rows.TryGetValue(target, out var row)) layers.ScrollIntoView(row);
    }
}
