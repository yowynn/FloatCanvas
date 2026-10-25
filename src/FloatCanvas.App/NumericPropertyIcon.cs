using System.Windows;
using System.Windows.Media;

namespace FloatCanvas.App;

internal enum NumericPropertyKind { Number, X, Y, Width, Height, Opacity, Angle, StrokeWidth, CornerRadius, FontSize, Rows, Columns, Length, Strength, Step }

/// <summary>A single 16 DIP, rounded monoline family for every numeric drag anchor.</summary>
internal sealed class NumericPropertyIcon : FrameworkElement
{
    private static readonly DependencyProperty InkProperty = DependencyProperty.Register(nameof(Ink), typeof(Brush), typeof(NumericPropertyIcon), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    private Brush Ink => (Brush)GetValue(InkProperty);
    internal NumericPropertyKind Kind { get; }
    private readonly Geometry geometry;

    internal NumericPropertyIcon(NumericPropertyKind kind)
    {
        Kind = kind;
        Width = Height = 16; IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Center; VerticalAlignment = VerticalAlignment.Center;
        SetResourceReference(InkProperty, "CanvasMuted");
        geometry = Geometry.Parse(kind switch
        {
            NumericPropertyKind.X => "M4 4 L12 12 M12 4 L4 12",
            NumericPropertyKind.Y => "M4 4 L8 8 L12 4 M8 8 V12",
            NumericPropertyKind.Width => "M2.5 4 L5 12 L8 6 L11 12 L13.5 4",
            NumericPropertyKind.Height => "M4 4 V12 M12 4 V12 M4 8 H12",
            NumericPropertyKind.Opacity => "M8 2 A6 6 0 1 1 8 14 A6 6 0 1 1 8 2 M8 2 V14 M5.5 3 V13 M3.5 4.5 V11.5",
            NumericPropertyKind.Angle => "M3 3 V13 H13 M3 8 A5 5 0 0 1 8 13",
            NumericPropertyKind.StrokeWidth => "M2 3 H14 M2 13 H14 M8 5 V11 M6 7 L8 5 L10 7 M6 9 L8 11 L10 9",
            NumericPropertyKind.CornerRadius => "M3 13 V8 A5 5 0 0 1 8 3 H13",
            NumericPropertyKind.FontSize => "M3 5 V3 H13 V5 M8 3 V13 M5.5 13 H10.5",
            NumericPropertyKind.Rows => "M3 3 H13 V13 H3 Z M3 6.3 H13 M3 9.7 H13",
            NumericPropertyKind.Columns => "M3 3 H13 V13 H3 Z M6.3 3 V13 M9.7 3 V13",
            NumericPropertyKind.Length => "M3 4 V12 M13 4 V12 M3 8 H13 M5 6 L3 8 L5 10 M11 6 L13 8 L11 10",
            NumericPropertyKind.Strength => "M3 12 V9 M8 12 V6 M13 12 V3",
            NumericPropertyKind.Step => "M2 12 H6 V8 H10 V4 H14",
            _ => "M6 3 L4 13 M12 3 L10 13 M3 6 H14 M2 10 H13"
        });
        geometry.Freeze();
    }

    protected override void OnRender(DrawingContext dc) => dc.DrawGeometry(null,
        new Pen(Ink, 1.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, geometry);

    internal static NumericPropertyKind ForProperty(string key) => key switch
    {
        "X" => NumericPropertyKind.X, "Y" => NumericPropertyKind.Y,
        "W" or "DefaultWidth" => NumericPropertyKind.Width,
        "H" or "DefaultHeight" or "BillboardHeight" => NumericPropertyKind.Height,
        "Opacity" or "Color.Alpha" => NumericPropertyKind.Opacity,
        "Angle" => NumericPropertyKind.Angle,
        "StrokeWidth" or "CaptureBorderWidth" or "Canvas.GridWidth" => NumericPropertyKind.StrokeWidth,
        "CornerRadius" => NumericPropertyKind.CornerRadius, "FontSize" => NumericPropertyKind.FontSize,
        "R" => NumericPropertyKind.Rows, "C" => NumericPropertyKind.Columns,
        "ArrowHeadSize" or "Canvas.GridSize" => NumericPropertyKind.Length,
        "EffectStrength" => NumericPropertyKind.Strength, "NumberStep" => NumericPropertyKind.Step,
        _ => NumericPropertyKind.Number
    };
}
