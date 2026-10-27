using System.Windows;
using System.Windows.Media;
using FloatCanvas.Core;

namespace FloatCanvas.App;

/// <summary>A four-by-four canvas sample; perspective changes its frame, not its calibration.</summary>
internal sealed class CanvasParameterIcon : FrameworkElement
{
    private static readonly DependencyProperty LightProperty = DependencyProperty.Register("Light", typeof(Brush), typeof(CanvasParameterIcon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty DarkProperty = DependencyProperty.Register("Dark", typeof(Brush), typeof(CanvasParameterIcon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty EdgeProperty = DependencyProperty.Register("Edge", typeof(Brush), typeof(CanvasParameterIcon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    private readonly Geometry[] cells;
    private readonly Geometry outline, grid;
    private readonly Brush background;
    private readonly Pen? gridPen;

    internal CanvasParameterIcon(CanvasPresetParameters parameters, double size = 24)
    {
        Width = Height = size; IsHitTestVisible = false;
        SetResourceReference(LightProperty, "CanvasInput"); SetResourceReference(DarkProperty, "CanvasBorder"); SetResourceReference(EdgeProperty, "CanvasMuted");
        Point Map(double x, double y) => new(.5 + (parameters.ProjectionEnabled ? 3.5 * (1 - y) : 0) + x * (23 - (parameters.ProjectionEnabled ? 7 * (1 - y) : 0)), .5 + y * 23);
        Geometry Polygon(double left, double top, double right, double bottom)
        {
            var shape = new StreamGeometry();
            using (var context = shape.Open()) { context.BeginFigure(Map(left, top), true, true); context.LineTo(Map(right, top), true, false); context.LineTo(Map(right, bottom), true, false); context.LineTo(Map(left, bottom), true, false); }
            shape.Freeze(); return shape;
        }
        cells = Enumerable.Range(0, 16).Select(i => Polygon((i % 4) / 4d, (i / 4) / 4d, (i % 4 + 1) / 4d, (i / 4 + 1) / 4d)).ToArray();
        outline = Polygon(0, 0, 1, 1);
        var lines = new StreamGeometry();
        using (var context = lines.Open()) for (int i = 1; i < 4; i++)
        { context.BeginFigure(Map(i / 4d, 0), false, false); context.LineTo(Map(i / 4d, 1), true, false); context.BeginFigure(Map(0, i / 4d), false, false); context.LineTo(Map(1, i / 4d), true, false); }
        lines.Freeze(); grid = lines;
        background = new SolidColorBrush(ColorValue(parameters.BackgroundColor)); background.Freeze();
        if (parameters.GridVisible)
        {
            gridPen = new Pen(new SolidColorBrush(ColorValue(parameters.GridColor)), Math.Min(2, parameters.GridWidth))
            { DashStyle = parameters.GridPattern switch { StrokePattern.Dashed => DashStyles.Dash, StrokePattern.Dotted => DashStyles.Dot, _ => DashStyles.Solid } };
            gridPen.Freeze();
        }
    }

    protected override void OnRender(DrawingContext drawing)
    {
        drawing.PushTransform(new ScaleTransform(ActualWidth / 24, ActualHeight / 24));
        for (int i = 0; i < cells.Length; i++) drawing.DrawGeometry((Brush)GetValue((i % 4 + i / 4) % 2 == 0 ? LightProperty : DarkProperty), null, cells[i]);
        drawing.DrawGeometry(background, null, outline);
        if (gridPen is not null) drawing.DrawGeometry(null, gridPen, grid);
        drawing.DrawGeometry(null, new Pen((Brush)GetValue(EdgeProperty), .75), outline);
        drawing.Pop();
    }

    private static Color ColorValue(uint color) => Color.FromArgb((byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color);
}
