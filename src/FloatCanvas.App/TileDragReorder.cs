using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FloatCanvas.App;

/// <summary>Local mouse reordering: no OLE drag, no accidental activation on release.</summary>
internal static class TileDragReorder
{
    internal readonly record struct DropTarget(Button Button, bool After);
    internal readonly record struct ItemDropTarget(FrameworkElement Tile, bool After);
    internal readonly record struct ExternalDropTarget(FrameworkElement Element, Action Dropped);

    internal static void Attach(Button button, Panel gallery, Action<Button, bool> dropped,
        Func<IEnumerable<ExternalDropTarget>>? externalTargets = null, Action? clicked = null) =>
        _ = new DragSession(button, gallery, () => gallery.Children.OfType<Button>(),
            (target, after) => dropped((Button)target, after), externalTargets, clicked);

    internal static void Attach(FrameworkElement tile, ItemsControl gallery, Action<FrameworkElement, bool> dropped,
        Func<IEnumerable<ExternalDropTarget>>? externalTargets = null, Action? clicked = null) =>
        _ = new DragSession(tile, gallery, () => gallery.Items.Cast<object>().Select(item =>
            gallery.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement ?? item as FrameworkElement).OfType<FrameworkElement>(), dropped, externalTargets, clicked);

    private sealed class DragSession
    {
        private readonly FrameworkElement tile, gallery;
        private readonly Func<IEnumerable<FrameworkElement>> tiles;
        private readonly Action<FrameworkElement, bool> dropped;
        private readonly Func<IEnumerable<ExternalDropTarget>>? externalTargets;
        private readonly Action? clicked;
        private Point start;
        private bool down, dragging, suppressRelease;
        private Window? window;
        private UIElement? inputScope;
        private bool capturing;
        private MouseDevice? mouse;
        private TileDragFeedback? feedback;

        internal DragSession(FrameworkElement tile, FrameworkElement gallery, Func<IEnumerable<FrameworkElement>> tiles,
            Action<FrameworkElement, bool> dropped, Func<IEnumerable<ExternalDropTarget>>? externalTargets, Action? clicked)
        {
            this.tile = tile; this.gallery = gallery; this.tiles = tiles; this.dropped = dropped; this.externalTargets = externalTargets; this.clicked = clicked;
            tile.PreviewMouseLeftButtonDown += Press;
            tile.PreviewMouseLeftButtonUp += Release;
            tile.LostMouseCapture += (_, _) => { if (!capturing && !tile.IsMouseCaptured) Cancel(); };
            tile.Unloaded += (_, _) => Cancel();
        }

        private void Press(object sender, MouseButtonEventArgs e)
        {
            Cancel(); suppressRelease = false; start = e.GetPosition(gallery); down = true; mouse = e.MouseDevice;
            window = Window.GetWindow(tile);
            if (window is not null) window.Deactivated += Deactivate;
            // Popup children can inherit their owner's Window service while input
            // is routed through a separate PopupRoot presentation source.
            inputScope = PresentationSource.FromVisual(tile)?.RootVisual as UIElement ?? window;
            inputScope?.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(KeyDown), true);
            // ListBox captures its parent subtree on press. A fast first move can
            // already be outside this tile, before our own capture has started.
            inputScope?.AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(Move), true);
            inputScope?.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(Release), true);
        }

        private void Move(object sender, MouseEventArgs e)
        {
            if (!down || capturing || !ReferenceEquals(e.MouseDevice, mouse)) return;
            if (e.LeftButton != MouseButtonState.Pressed) { Cancel(); return; }
            var point = e.GetPosition(gallery);
            if (!dragging && (Math.Abs(point.X - start.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(point.Y - start.Y) > SystemParameters.MinimumVerticalDragDistance))
            {
                capturing = suppressRelease = true;
                bool captured;
                try { captured = tile.CaptureMouse(); }
                catch { Cancel(); throw; }
                finally { capturing = false; }
                if (!captured) { Cancel(); return; }
                dragging = true;
                try
                {
                    var created = TileDragFeedback.Create(tile, gallery, start);
                    if (!down || !dragging || !tile.IsVisible || !tile.IsMouseCaptured) { created?.Dispose(); Cancel(); return; }
                    feedback = created;
                }
                catch { Cancel(); throw; }
            }
            if (!dragging) return;
            try
            {
                feedback?.Move(point);
                if (ResolveExternal(e) is { } external) feedback?.ShowTarget(external.Element, false, true);
                else if (ResolveDropCore(gallery, tiles(), point) is { } drop && !ReferenceEquals(drop.Tile, tile))
                    feedback?.ShowTarget(drop.Tile, drop.After, false);
                else feedback?.ClearTarget();
            }
            catch { Cancel(); throw; }
        }

        private void Release(object sender, MouseButtonEventArgs e)
        {
            if (down && !ReferenceEquals(e.MouseDevice, mouse)) return;
            bool swallow = suppressRelease; suppressRelease = false;
            if (!dragging)
            {
                bool click = down && ReferenceEquals(e.MouseDevice, mouse) && tile.IsVisible && tile.IsEnabled &&
                    new Rect(tile.RenderSize).Contains(e.GetPosition(tile));
                Cancel(); e.Handled |= swallow || clicked is not null;
                if (click && !swallow) clicked?.Invoke();
                return;
            }
            e.Handled = true;
            ExternalDropTarget? external;
            ItemDropTarget? target;
            try
            {
                external = ResolveExternal(e);
                target = external is null ? ResolveDropCore(gallery, tiles(), e.GetPosition(gallery)) : null;
            }
            finally { Cancel(); }
            // Feedback and capture must be gone before callbacks rebuild their controls or throw.
            if (external is { } destination) destination.Dropped();
            else if (target is { } drop && !ReferenceEquals(drop.Tile, tile)) dropped(drop.Tile, drop.After);
        }

        private ExternalDropTarget? ResolveExternal(MouseEventArgs e)
        {
            foreach (var target in externalTargets?.Invoke() ?? [])
                if (target.Element.IsVisible && target.Element.IsEnabled && target.Element.IsHitTestVisible &&
                    PresentationSource.FromVisual(target.Element) == PresentationSource.FromVisual(tile) &&
                    new Rect(target.Element.RenderSize).Contains(e.GetPosition(target.Element))) return target;
            return null;
        }

        private void Deactivate(object? sender, EventArgs e) => Cancel();
        private void KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || !down) return;
            bool wasDragging = dragging; Cancel();
            if (wasDragging) e.Handled = true;
        }
        private void Cancel()
        {
            bool release = dragging || capturing; down = dragging = false; mouse = null;
            if (window is not null) { window.Deactivated -= Deactivate; window = null; }
            if (inputScope is not null)
            {
                inputScope.RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(KeyDown));
                inputScope.RemoveHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(Move));
                inputScope.RemoveHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(Release));
                inputScope = null;
            }
            feedback?.Dispose(); feedback = null;
            if (release && tile.IsMouseCaptured) tile.ReleaseMouseCapture();
        }
    }

    /// <summary>Hit the arranged tiles inside the visible scroll viewport, including blank space after the last tile.</summary>
    internal static DropTarget? ResolveDrop(Panel gallery, Point point)
    {
        var target = ResolveDropCore(gallery, gallery.Children.OfType<Button>(), point);
        return target is { } drop ? new DropTarget((Button)drop.Tile, drop.After) : null;
    }

    private static ItemDropTarget? ResolveDropCore(FrameworkElement gallery, IEnumerable<FrameworkElement> elements, Point point)
    {
        FrameworkElement viewport = gallery;
        for (DependencyObject? parent = VisualTreeHelper.GetParent(gallery); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollContentPresenter presenter) { viewport = presenter; break; }
        if (gallery is ItemsControl && FindViewport(gallery) is { } innerViewport) viewport = innerViewport;
        var viewportPoint = gallery.TranslatePoint(point, viewport);
        if (!new Rect(new Point(), viewport.RenderSize).Contains(viewportPoint)) return null;
        var tiles = elements.Where(element => element.Visibility == Visibility.Visible)
            .Select(element => (Tile: element, Bounds: new Rect(element.TranslatePoint(new Point(), gallery), element.RenderSize)))
            .Where(tile => tile.Bounds.Width > 0 && tile.Bounds.Height > 0).ToArray();
        if (tiles.Length == 0) return null;
        foreach (var tile in tiles)
        {
            if (tile.Bounds.Contains(point)) return new(tile.Tile, point.X >= tile.Bounds.Left + tile.Bounds.Width / 2);
            // The gap before a tile or row is an insertion point before that tile.
            if (point.Y < tile.Bounds.Top || point.Y <= tile.Bounds.Bottom && point.X < tile.Bounds.Left) return new(tile.Tile, false);
            if (point.Y <= tile.Bounds.Bottom && ReferenceEquals(tile.Tile, tiles.LastOrDefault(t => Math.Abs(t.Bounds.Top - tile.Bounds.Top) < 1).Tile))
                return new(tile.Tile, true);
        }
        return new(tiles[^1].Tile, true);
    }

    private static ScrollContentPresenter? FindViewport(DependencyObject root)
    {
        if (root is ScrollContentPresenter viewport) return viewport;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindViewport(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }
}
