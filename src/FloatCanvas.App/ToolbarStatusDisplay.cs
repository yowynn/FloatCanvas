using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FloatCanvas.App;

/// <summary>A compact status readout; long messages scroll only while being inspected.</summary>
internal sealed class ToolbarStatusDisplay : Border
{
    private readonly TextBlock text;
    private readonly TranslateTransform scroll = new();
    private string observed = "";
    internal ToolbarStatusDisplay(TextBlock text)
    {
        this.text = text;
        Height = 26; Margin = new Thickness(2, 0, 2, 0); CornerRadius = new CornerRadius(3);
        SetResourceReference(BackgroundProperty,"CanvasLcdSurface");
        SetResourceReference(BorderBrushProperty,"CanvasLcdBorder");BorderThickness=new Thickness(1);
        Padding = new Thickness(6, 2, 6, 2); ClipToBounds = true;
        text.MaxWidth = double.PositiveInfinity; text.Margin = new Thickness(0); text.FontSize = 11;
        text.FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI");
        text.SetResourceReference(TextBlock.ForegroundProperty,"CanvasLcdText");
        text.VerticalAlignment = VerticalAlignment.Center;
        text.TextTrimming = TextTrimming.CharacterEllipsis; text.RenderTransform = scroll;
        Child = text;
        MouseEnter += (_, _) => StartScroll(); MouseLeave += (_, _) => StopScroll();
        Unloaded += (_, _) => StopScroll();
    }
    internal void Refresh()
    {
        if (observed == text.Text) return;
        observed = text.Text;
        if (IsMouseOver) StartScroll();
    }
    private void StartScroll()
    {
        StopScroll();
        var formatted = new FormattedText(text.Text, System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
            text.FontSize, text.Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double overflow = formatted.WidthIncludingTrailingWhitespace - Math.Max(1, ActualWidth - Padding.Left - Padding.Right - BorderThickness.Left - BorderThickness.Right);
        if (overflow <= 0) return;
        text.TextTrimming = TextTrimming.None;
        text.Width = formatted.WidthIncludingTrailingWhitespace;
        scroll.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -overflow,
            TimeSpan.FromSeconds(Math.Max(2, overflow / 35)))
        { BeginTime = TimeSpan.FromMilliseconds(500), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
    }
    private void StopScroll()
    {
        scroll.BeginAnimation(TranslateTransform.XProperty, null); scroll.X = 0;
        text.Width = double.NaN; text.TextTrimming = TextTrimming.CharacterEllipsis;
    }
}
