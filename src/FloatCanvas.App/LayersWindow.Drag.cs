using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Primitives = System.Windows.Controls.Primitives;
using Input = System.Windows.Input;

namespace FloatCanvas.App;

internal sealed partial class LayersWindow
{
    private EditorSession? dragSession;
    private Guid[]? dragIds;
    private Guid? dropTarget, dropParent, dragSource;
    private bool dropBefore, dropInto;
    private long dragRevision, dragScopeVersion;
    private Wpf.Point dragStart, dragStartScreen;
    private TileDragFeedback? dragFeedback;
    private Primitives.Thumb? activeHandle;
    private readonly HashSet<Wpf.FrameworkElement> dragRows = [];

    internal bool BeginDrag(Guid id)
    {
        CommitRename(); var session = getSession();
        if (!CanWrite || session.IsTransactionActive || !ReferenceEquals(session, displayedSession)) return false;
        if (!rows.TryGetValue(id, out var visible) || visible.ParentId != session.EditScopeId) return false;
        var sourceGraph = new SceneGraph(session.Document);
        if (sourceGraph.FindElement(id) is null && sourceGraph.FindGroup(id) is null && sourceGraph.FindInstance(id) is null || sourceGraph.IsLocked(id)) return false;
        if (!session.Selection.Contains(id)) session.SetSelection([id]);
        dragSession = session; dragIds = session.Selection.Where(node => !session.IsNodeLocked(node)).ToArray();
        dragRevision = session.SceneRevision; dragScopeVersion = session.EditScopeVersion; dragSource = id; dragRows.Clear();
        foreach (var node in dragIds) if (Container(node) is { } container) dragRows.Add(container);
        return dragIds.Length > 0;
    }
    internal void StartDrag(Guid id, Primitives.Thumb handle, double x, double y)
    {
        if (!BeginDrag(id)) { handle.CancelDrag(); return; }
        activeHandle = handle;
        dragStart = handle.TranslatePoint(new Wpf.Point(x, y), layers); dragStartScreen = layers.PointToScreen(dragStart);
    }
    internal void CompleteDrag(double horizontal, double vertical, bool canceled)
    {
        if (!canceled) UpdateDropTarget(layers.PointFromScreen(dragStartScreen + new Wpf.Vector(horizontal, vertical)));
        FinishDrag(!canceled);
    }
    internal void UpdateDrag() => UpdateDropTarget(Input.Mouse.GetPosition(layers));
    private void UpdateDropTarget(Wpf.Point point)
    {
        if (dragIds is null) return;
        if (!CanWrite || !ReferenceEquals(dragSession, getSession()) || dragSession.IsTransactionActive || dragSession.SceneRevision != dragRevision || dragSession.EditScopeVersion != dragScopeVersion)
        { CancelDrag(); Refresh(); return; }
        ClearDropIndicator();
        if (dragFeedback is null && Math.Abs(point.X - dragStart.X) <= Wpf.SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - dragStart.Y) <= Wpf.SystemParameters.MinimumVerticalDragDistance) return;
        if (dragFeedback is null && dragSource is { } source && Container(source) is { } item)
            dragFeedback = TileDragFeedback.Create(item, layers, dragStart, dragRows);
        dragFeedback?.Move(point);
        var graph = new SceneGraph(dragSession.Document);
        if (point.Y < 0 && Wpf.PresentationSource.FromVisual(breadcrumbs) is not null)
        {
            var crumbPoint = breadcrumbs.PointFromScreen(layers.PointToScreen(point));
            foreach (var button in breadcrumbs.Children.OfType<Controls.Button>())
            {
                var bounds = new Wpf.Rect(button.TranslatePoint(new(), breadcrumbs), button.RenderSize);
                if (!bounds.Contains(crumbPoint) || button.Tag is not ScopeTarget target ||
                    target.Id is { } parent && dragIds.Any(id => id == parent || graph.IsAncestor(id, parent))) continue;
                dropParent = target.Id; dropInto = true; status.Text = L.Format("Layers.DropInto", button.Content); return;
            }
        }
        if (point.X < 0 || point.X > layers.ActualWidth || point.Y < 0 || point.Y > layers.ActualHeight) return;
        var candidates = visibleRows.Select(r => (Row: r, Container: Container(r.Id))).Where(p => p.Container is { ActualHeight: > 0 }).Select(p =>
                (p.Row, Item: p.Container!, Bounds: new Wpf.Rect(p.Container!.TranslatePoint(new(), layers), p.Container.RenderSize))).OrderBy(p => p.Bounds.Top).ToArray();
        if (candidates.Length == 0) return;
        var candidate = candidates.FirstOrDefault(p => point.Y < p.Bounds.Bottom - .5);
        if (candidate.Item is null) candidate = candidates[^1];
        var row = candidate.Row; var rect = candidate.Bounds;
        if (dragIds.Any(id => id == row.Id || graph.IsAncestor(id, row.Id))) return;
        dropTarget = row.Id; dropParent = row.ParentId;
        dropInto = row.IsGroup && point.Y > rect.Top + rect.Height * .25 && point.Y < rect.Bottom - rect.Height * .25 && point.X > row.Depth * 16 + 40;
        if (dropInto)
        {
            if (row.EffectiveLocked) { ClearDropIndicator(); return; }
            dropParent = row.Id; status.Text = L.Format("Layers.DropInto", row.Name);
            dragFeedback?.ShowInsertion(new Wpf.Rect(rect.X + 28 + row.Depth * 16, rect.Bottom - 4, Math.Max(10, rect.Width - 28 - row.Depth * 16), 0));
        }
        else
        {
            dropBefore = point.Y < rect.Top + rect.Height / 2;
            status.Text = row.IsLegacy ? L.Get("Layers.CollectHint") : "";
            dragFeedback?.ShowInsertion(new Wpf.Rect(rect.X, dropBefore ? rect.Top - 1 : rect.Bottom - 1, rect.Width, 0));
        }
    }
    private void FinishDrag(bool commit)
    {
        var session = dragSession; var ids = dragIds; var target = dropTarget; var parent = dropParent;
        bool before = dropBefore, into = dropInto; long revision = dragRevision, scopeVersion = dragScopeVersion;
        CancelDrag();
        if (!commit || session is null || ids is null || !CanWrite || !ReferenceEquals(session, getSession()) ||
            session.IsTransactionActive || session.SceneRevision != revision || session.EditScopeVersion != scopeVersion) { Refresh(); return; }
        try
        {
            if (into)
            {
                session.MoveNodes(ids, parent);
            }
            else if (target is { } id) ReorderLayers(ids, id, before);
        }
        catch (Exception ex) { status.Text = ex.Message; }
        Refresh();
    }
    private void ReorderLayers(Guid[] ids, Guid targetId, bool before)
    {
        var session = getSession();
        if (!CanWrite || session.IsTransactionActive || ids.Contains(targetId) || ids.Any(id => session.IsNodeLocked(id))) return;
        var graph = new SceneGraph(session.Document); var parent = graph.ParentId(targetId);
        var siblings = graph.EnumerateChildren(parent); int index = siblings.ToList().IndexOf(targetId); if (index < 0) return;
        // The list is front-to-back; the source ledger is back-to-front.
        Guid? beforeNode = before ? siblings.Skip(index + 1).FirstOrDefault(id => !ids.Contains(id)) : targetId;
        if (beforeNode == Guid.Empty) beforeNode = null;
        session.MoveNodes(ids, parent, beforeNode);
    }
    private void ClearDropIndicator() { dragFeedback?.ClearTarget(); dropTarget = dropParent = null; dropInto = false; }
    private void CancelDrag()
    {
        ClearDropIndicator(); dragIds = null; dragSession = null; dragSource = null; dragRows.Clear();
        dragFeedback?.Dispose(); dragFeedback = null;
        var handle = activeHandle; activeHandle = null;
        if (handle?.IsDragging == true) handle.CancelDrag();
    }
}
