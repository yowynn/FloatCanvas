using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;
using Path = System.Windows.Shapes.Path;

namespace FloatCanvas.App;

internal static class SymbolIcon
{
    private static readonly ConditionalWeakTable<SymbolShape, StreamGeometry> geometries = new();

    internal static Path Create(SymbolShape shape, double size = 16)
    {
        var icon = new Path { Data = geometries.GetValue(shape, Build), Width = size, Height = size, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(Path.FillProperty, "CanvasText");
        return icon;
    }

    internal static ComboBoxItem Option(SymbolKind kind, SymbolShape? shape = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (kind != SymbolKind.Custom || shape is not null) content.Children.Add(Create(SymbolGeometry.Shape(kind, shape)));
        content.Children.Add(new TextBlock { Text = L.Get("Symbol." + kind), Margin = new(8, 0, 0, 0) });
        return new ComboBoxItem { Tag = kind, Content = content };
    }

    private static StreamGeometry Build(SymbolShape shape)
    {
        var geo = new StreamGeometry { FillRule = FillRule.EvenOdd };
        double x = 16 * Math.Min(1, shape.AspectRatio), y = 16 * Math.Min(1, 1 / shape.AspectRatio);
        Point Scale(PointD p) => new(p.X * x, p.Y * y);
        using (var ctx = geo.Open())
            foreach (var c in shape.Contours)
            {
                ctx.BeginFigure(Scale(c[0].Point), true, true);
                foreach (var v in c.AsSpan()[1..])
                    if (v.Control1 is { } a && v.Control2 is { } b) ctx.BezierTo(Scale(a), Scale(b), Scale(v.Point), true, false);
                    else ctx.LineTo(Scale(v.Point), true, false);
            }
        geo.Freeze(); return geo;
    }
}
