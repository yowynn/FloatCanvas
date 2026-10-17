using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FloatCanvas.App;

/// <summary>Tiles share one device-pixel pitch instead of independently rounded margins.</summary>
internal sealed class PixelTilePanel(int columns, double tileSize, double gap = 2) : Panel
{
    internal int Columns { get; } = columns > 0 ? columns : throw new ArgumentOutOfRangeException(nameof(columns));
    internal double TileSize { get; } = tileSize;
    internal double Gap { get; } = gap;

    protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
    {
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);
        // The parent snaps the shared origin. Children must not re-round that pitch.
        UseLayoutRounding = true;
        if (visualAdded is FrameworkElement tile) tile.UseLayoutRounding = false;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (tile, spacing) = Metrics();
        int count = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(tile);
            if (child.Visibility != Visibility.Collapsed) count++;
        }
        int rows = (count + Columns - 1) / Columns;
        return new(Columns * tile.Width + (Columns - 1) * spacing.Width,
            rows == 0 ? 0 : rows * tile.Height + (rows - 1) * spacing.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (tile, spacing) = Metrics();
        int index = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Arrange(new Rect(new Point(index % Columns * (tile.Width + spacing.Width),
                index / Columns * (tile.Height + spacing.Height)), tile));
            index++;
        }
        return finalSize;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateMeasure();
    }

    private (Size Tile, Size Spacing) Metrics()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        static double Pixels(double value, double scale) => Math.Max(1, Math.Round(value * scale, MidpointRounding.AwayFromZero)) / scale;
        return (new(Pixels(TileSize, dpi.DpiScaleX), Pixels(TileSize, dpi.DpiScaleY)),
            new(Pixels(Gap, dpi.DpiScaleX), Pixels(Gap, dpi.DpiScaleY)));
    }
}
