using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace FloatCanvas.App;

/// <summary>Fixed-size library tiles with continuous pixel scrolling and one row of prefetch.</summary>
internal sealed class VirtualizingTilePanel : VirtualizingPanel, IScrollInfo
{
    internal const double TileHeight = 150;
    private Size extent, viewport;
    private double offset;
    private int columns = 1;
    private double cellWidth = StampLibraryWindow.TileFootprint;
    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner is null) return new();
        double width = double.IsInfinity(availableSize.Width) ? StampLibraryWindow.TileFootprint : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? TileHeight * 4 : availableSize.Height;
        columns = Math.Max(1, (int)Math.Floor((width + .01) / StampLibraryWindow.TileFootprint));
        cellWidth = width / columns;
        viewport = new(width, height); extent = new(width, Math.Ceiling(owner.Items.Count / (double)columns) * TileHeight);
        offset = Math.Clamp(offset, 0, Math.Max(0, extent.Height - viewport.Height)); ScrollOwner?.InvalidateScrollInfo();
        int first = Math.Max(0, ((int)(offset / TileHeight) - 1) * columns);
        int last = Math.Min(owner.Items.Count - 1, ((int)Math.Ceiling((offset + height) / TileHeight) + 1) * columns - 1);
        _ = InternalChildren; // Ensure WPF connects the generator before the first measure.
        var generator = ItemContainerGenerator;
        for (int child = InternalChildren.Count - 1; child >= 0; child--)
        {
            var position = new GeneratorPosition(child, 0);
            int index = generator.IndexFromGeneratorPosition(position);
            if (index >= first && index <= last) continue;
            if (generator is IRecyclingItemContainerGenerator recycling) recycling.Recycle(position, 1); else generator.Remove(position, 1);
            RemoveInternalChildRange(child, 1);
        }
        if (last >= first)
        {
            var start = generator.GeneratorPositionFromIndex(first);
            int childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
            using (generator.StartAt(start, GeneratorDirection.Forward, true))
                for (int index = first; index <= last; index++, childIndex++)
                {
                    var child = (UIElement)generator.GenerateNext(out bool created);
                    // Recycling returns an existing container with created=false, but it was
                    // detached above and still needs insertion and preparation for its new item.
                    if (created || VisualTreeHelper.GetParent(child) != this)
                    {
                        if (childIndex >= InternalChildren.Count) AddInternalChild(child); else InsertInternalChild(childIndex, child);
                        generator.PrepareItemContainer(child);
                    }
                    child.Measure(new(cellWidth, TileHeight));
                }
        }
        return viewport;
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            int index = ItemContainerGenerator.IndexFromGeneratorPosition(new(i, 0));
            InternalChildren[i].Arrange(new Rect(index % columns * cellWidth,
                index / columns * TileHeight - offset, cellWidth, TileHeight));
        }
        return finalSize;
    }
    protected override void BringIndexIntoView(int index)
    {
        double top = index / columns * TileHeight;
        if (top < offset) SetVerticalOffset(top); else if (top + TileHeight > offset + viewport.Height) SetVerticalOffset(top + TileHeight - viewport.Height);
    }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => extent.Width;
    public double ExtentHeight => extent.Height;
    public double ViewportWidth => viewport.Width;
    public double ViewportHeight => viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => offset;
    public ScrollViewer? ScrollOwner { get; set; }
    public void LineUp() => SetVerticalOffset(offset - TileHeight / 3);
    public void LineDown() => SetVerticalOffset(offset + TileHeight / 3);
    public void MouseWheelUp() => SetVerticalOffset(offset - TileHeight);
    public void MouseWheelDown() => SetVerticalOffset(offset + TileHeight);
    public void PageUp() => SetVerticalOffset(offset - viewport.Height);
    public void PageDown() => SetVerticalOffset(offset + viewport.Height);
    public void LineLeft() { }
    public void LineRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void SetHorizontalOffset(double value) { }
    public void SetVerticalOffset(double value)
    {
        double next = Math.Clamp(value, 0, Math.Max(0, extent.Height - viewport.Height));
        if (Math.Abs(offset - next) < .01) return; offset = next; InvalidateMeasure(); ScrollOwner?.InvalidateScrollInfo();
    }
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual is null || visual == this) return rectangle;
        var bounds = visual.TransformToAncestor(this).TransformBounds(rectangle);
        if (bounds.Top < 0) SetVerticalOffset(offset + bounds.Top);
        else if (bounds.Bottom > viewport.Height) SetVerticalOffset(offset + bounds.Bottom - viewport.Height);
        return bounds;
    }
}
