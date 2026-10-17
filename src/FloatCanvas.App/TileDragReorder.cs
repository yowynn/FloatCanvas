using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FloatCanvas.App;

/// <summary>Local mouse reordering: no OLE drag, no accidental activation on release.</summary>
internal static class TileDragReorder
{
    internal readonly record struct DropTarget(Button Button, bool After);
    internal static void Attach(Button button, Panel gallery, Action<Button, bool> dropped)
    {
        Point start = default; bool dragging = false, down = false;
        button.PreviewMouseLeftButtonDown += (_, e) => { start = e.GetPosition(gallery); down = true; dragging = false; };
        button.PreviewMouseMove += (_, e) =>
        {
            if (!down || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(gallery);
            if (!dragging && (Math.Abs(point.X - start.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(point.Y - start.Y) > SystemParameters.MinimumVerticalDragDistance))
            { dragging = true; button.CaptureMouse(); }
        };
        button.PreviewMouseLeftButtonUp += (_, e) =>
        {
            down = false; if (!dragging) return;
            var target = ResolveDrop(gallery, e.GetPosition(gallery));
            dragging = false; e.Handled = true; button.ReleaseMouseCapture();
            if (target is { } drop && drop.Button != button) dropped(drop.Button, drop.After);
        };
        button.LostMouseCapture += (_, _) => { down = false; dragging = false; };
    }

    /// <summary>Hit the arranged tiles inside the visible scroll viewport, including blank space after the last tile.</summary>
    internal static DropTarget? ResolveDrop(Panel gallery, Point point)
    {
        FrameworkElement viewport = gallery;
        for (DependencyObject? parent = VisualTreeHelper.GetParent(gallery); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollContentPresenter presenter) { viewport = presenter; break; }
        var viewportPoint = gallery.TranslatePoint(point, viewport);
        if (!new Rect(new Point(), viewport.RenderSize).Contains(viewportPoint)) return null;
        var tiles = gallery.Children.OfType<Button>().Where(button => button.Visibility == Visibility.Visible)
            .Select(button => (Button: button, Bounds: new Rect(button.TranslatePoint(new Point(), gallery), button.RenderSize)))
            .Where(tile => tile.Bounds.Width > 0 && tile.Bounds.Height > 0).ToArray();
        if (tiles.Length == 0) return null;
        foreach (var tile in tiles)
        {
            if (tile.Bounds.Contains(point)) return new(tile.Button, point.X >= tile.Bounds.Left + tile.Bounds.Width / 2);
            // The gap before a tile or row is an insertion point before that tile.
            if (point.Y < tile.Bounds.Top || point.Y <= tile.Bounds.Bottom && point.X < tile.Bounds.Left) return new(tile.Button, false);
            if (point.Y <= tile.Bounds.Bottom && ReferenceEquals(tile.Button, tiles.LastOrDefault(t => Math.Abs(t.Bounds.Top - tile.Bounds.Top) < 1).Button))
                return new(tile.Button, true);
        }
        return new(tiles[^1].Button, true);
    }
}
