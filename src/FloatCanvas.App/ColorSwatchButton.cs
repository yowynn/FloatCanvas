using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FloatCanvas.App;

/// <summary>Square color face, with a separate outline even for transparent colors.</summary>
internal sealed class ColorSwatchButton : Button
{
    internal ColorSwatchButton(uint color)
    {
        Style = (Style)Application.Current.FindResource(typeof(Button));
        Resources["CanvasButtonCornerRadius"] = new CornerRadius(0);
        MinWidth = MinHeight = 0;
        Margin = Padding = new Thickness(0);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Content = ColorPickerPanel.CreateSwatch(color);
        SetResourceReference(BackgroundProperty, "CanvasSurface");
        UpdateOutline();
        UpdateThickness(System.Windows.Media.VisualTreeHelper.GetDpi(this));
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    { base.OnDpiChanged(oldDpi, newDpi); UpdateThickness(newDpi); }
    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); UpdateOutline(); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); UpdateOutline(); }
    protected override void OnIsKeyboardFocusedChanged(DependencyPropertyChangedEventArgs e)
    { base.OnIsKeyboardFocusedChanged(e); UpdateOutline(); }
    private void UpdateOutline() => SetResourceReference(BorderBrushProperty,
        IsMouseOver || IsKeyboardFocused ? "CanvasAccent" : "CanvasSwatchBorder");
    private void UpdateThickness(DpiScale dpi) => BorderThickness = new Thickness(
        Math.Max(1, Math.Round(dpi.DpiScaleX, MidpointRounding.AwayFromZero)) / dpi.DpiScaleX,
        Math.Max(1, Math.Round(dpi.DpiScaleY, MidpointRounding.AwayFromZero)) / dpi.DpiScaleY,
        Math.Max(1, Math.Round(dpi.DpiScaleX, MidpointRounding.AwayFromZero)) / dpi.DpiScaleX,
        Math.Max(1, Math.Round(dpi.DpiScaleY, MidpointRounding.AwayFromZero)) / dpi.DpiScaleY);
}
