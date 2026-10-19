using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FloatCanvas.App;

/// <summary>Local mouse reordering: no OLE drag, no accidental activation on release.</summary>
internal static class TileDragReorder
{
    internal readonly record struct DropTarget(Button Button, bool After);
    internal readonly record struct ItemDropTarget(FrameworkElement Tile, bool After,Rect Marker);
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
        private readonly HashSet<FrameworkElement> moving;
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
            moving=[tile];
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
                if (ResolveExternal(e) is { } external) feedback?.ShowExternalTarget(external.Element);
                else if (ResolveDropCore(gallery, tiles(), point,moving:moving) is { } drop)
                    feedback?.ShowInsertion(drop.Marker);
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
                target = external is null ? ResolveDropCore(gallery, tiles(), e.GetPosition(gallery),moving:moving) : null;
            }
            finally { Cancel(); }
            // Feedback and capture must be gone before callbacks rebuild their controls or throw.
            if (external is { } destination) destination.Dropped();
            else if (target is { } drop) dropped(drop.Tile, drop.After);
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

    internal static ItemDropTarget? ResolveDropCore(FrameworkElement gallery, IEnumerable<FrameworkElement> elements, Point point,
        bool vertical=false,IReadOnlySet<FrameworkElement>? moving=null)
    {
        FrameworkElement viewport = gallery;
        for (DependencyObject? parent = VisualTreeHelper.GetParent(gallery); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollContentPresenter presenter) { viewport = presenter; break; }
        if (gallery is ItemsControl && FindViewport(gallery) is { } innerViewport) viewport = innerViewport;
        var viewportPoint = gallery.TranslatePoint(point, viewport);
        if (!new Rect(new Point(), viewport.RenderSize).Contains(viewportPoint)) return null;
        var ordered=elements.Where(element=>element.Visibility==Visibility.Visible).ToArray();
        var tiles = ordered.Select((element,index)=>(Tile:element,Index:index)).Where(item=>gallery.IsAncestorOf(item.Tile))
            .Select(item => (item.Tile,item.Index, Bounds: new Rect(item.Tile.TranslatePoint(new Point(), gallery), item.Tile.RenderSize)))
            .Where(tile => tile.Bounds.Width > 0 && tile.Bounds.Height > 0).ToArray();
        if (tiles.Length == 0) return null;
        int gap=tiles.Length;
        for(int i=0;i<tiles.Length;i++)
        {
            var tile=tiles[i];
            if(vertical)
            {
                if(point.Y<tile.Bounds.Top+tile.Bounds.Height/2){gap=i;break;}
                continue;
            }
            if (tile.Bounds.Contains(point)){gap=i+(point.X>=tile.Bounds.Left+tile.Bounds.Width/2?1:0);break;}
            // The gap before a tile or row is an insertion point before that tile.
            if (point.Y < tile.Bounds.Top || point.Y <= tile.Bounds.Bottom && point.X < tile.Bounds.Left){gap=i;break;}
            if (point.Y <= tile.Bounds.Bottom && ReferenceEquals(tile.Tile, tiles.LastOrDefault(t => Math.Abs(t.Bounds.Top - tile.Bounds.Top) < 1).Tile))
            {gap=i+1;break;}
        }
        int logicalGap=gap<tiles.Length?tiles[gap].Index:tiles[^1].Index+1;
        var remaining=ordered.Where(item=>moving?.Contains(item)!=true).ToArray();
        if(remaining.Length==0)return null;
        int index=ordered.Take(logicalGap).Count(item=>moving?.Contains(item)!=true);
        if(moving is {Count:>0})
        {
            var reordered=remaining.Take(index).Concat(ordered.Where(item=>moving.Contains(item))).Concat(remaining.Skip(index));
            if(reordered.SequenceEqual(ordered))return null;
        }
        bool after=index==remaining.Length;
        var anchor=after?remaining[^1]:remaining[index];
        int anchorIndex=Array.IndexOf(ordered,anchor);
        int markerGap=anchorIndex+(after?1:0);
        var previous=tiles.Where(item=>item.Index<markerGap).Select(item=>(Rect?)item.Bounds).LastOrDefault();
        var next=tiles.Where(item=>item.Index>=markerGap).Select(item=>(Rect?)item.Bounds).FirstOrDefault();
        var visible=new Rect(viewport.TranslatePoint(new Point(),gallery),viewport.RenderSize);
        Rect marker;
        if(vertical)
        {
            double y=previous is { } before&&next is { } following?(before.Bottom+following.Top)/2:next?.Top??previous!.Value.Bottom;
            var row=next??previous!.Value;
            marker=new(Math.Max(row.Left,visible.Left),Math.Clamp(y,visible.Top,visible.Bottom),
                Math.Max(0,Math.Min(row.Right,visible.Right)-Math.Max(row.Left,visible.Left)),0);
        }
        else
        {
            Rect row=next??previous!.Value;
            double x=row.Left;
            if(previous is { } before&&next is { } following&&Math.Abs(before.Top-following.Top)<1)
                x=(before.Right+following.Left)/2;
            else if(next is null||!row.IntersectsWith(visible)&&previous is { } visiblePrevious&&visiblePrevious.IntersectsWith(visible))
            {row=previous!.Value;x=row.Right;}
            double top=Math.Clamp(row.Top,visible.Top,visible.Bottom),bottom=Math.Clamp(row.Bottom,visible.Top,visible.Bottom);
            marker=new(x,top,0,Math.Max(0,bottom-top));
        }
        return new(anchor,after,marker);
    }

    private static ScrollContentPresenter? FindViewport(DependencyObject root)
    {
        if (root is ScrollContentPresenter viewport) return viewport;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindViewport(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }
}
