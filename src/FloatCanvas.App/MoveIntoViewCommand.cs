using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Moves editable visible content into the visible document frame without changing its geometry.</summary>
internal static class MoveIntoViewCommand
{
    internal static bool Execute(EditorSession session, RectD primaryWorkArea, PointD screenOrigin)
    {
        var visible = new RectD(primaryWorkArea.Left - screenOrigin.X, primaryWorkArea.Top - screenOrigin.Y,
            primaryWorkArea.Width, primaryWorkArea.Height).Intersect(session.Document.Viewport);
        if (visible.IsEmpty) return false;
        var items = session.Document.Elements.Where(e => e.Visible && !e.Locked).ToArray();
        if (items.Length == 0) return false;
        var bounds = items.Select(e => ElementGeometry.Bounds(e)).Aggregate(RectD.Union);
        // Keep the usual margins only when the current crop has room for them.
        // Oversized groups remain unscaled with their top-left inside the visible frame.
        double left = visible.Left + Math.Min(40, Math.Max(0, visible.Width - bounds.Width));
        double top = visible.Top + Math.Min(100, Math.Max(0, visible.Height - bounds.Height));
        var delta = Affine2D.Translation(left - bounds.Left, top - bounds.Top);
        if (delta.OffsetX == 0 && delta.OffsetY == 0) return false;
        session.Execute(L.Get("History.MoveIntoView"), _ =>
        {
            foreach (var element in items) element.Transform = delta.Compose(element.Transform);
        });
        return true;
    }
}
