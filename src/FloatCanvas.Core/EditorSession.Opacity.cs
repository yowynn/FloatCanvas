using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Core;

public sealed partial class EditorSession
{
    public bool TrySetSelectionOpacity(double opacity)
    {
        if (IsReadOnly || !double.IsFinite(opacity) || opacity is < 0 or > 1) return false;
        var graph = new SceneGraph(Document);
        var ids = graph.NormalizeSelection(Selection).ToArray();
        if (ids.Length == 0 || ids.Any(graph.IsLocked)) return false;
        void Apply()
        {
            foreach (var id in ids)
            {
                if (graph.FindGroup(id) is { } group) group.Opacity = opacity;
                else if (graph.FindInstance(id) is { } instance) instance.Opacity = opacity;
                else graph.FindElement(id)!.Style.Opacity = opacity;
            }
        }
        if (IsTransactionActive)
        {
            Apply();
            NotifyContainerPlacementChanged(ids.Where(id => graph.FindElement(id) is null), ids.Where(id => graph.FindElement(id) is not null));
        }
        else Execute(L.Get("History.UpdateProperties"), _ => Apply());
        return true;
    }
}
