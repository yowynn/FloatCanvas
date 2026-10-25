using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FloatCanvas.App;

/// <summary>A fixed label slot; only overflowing text moves, never its hit area.</summary>
internal sealed class PropertyLabel : TextBlock
{
    static PropertyLabel() => TextProperty.OverrideMetadata(typeof(PropertyLabel), new FrameworkPropertyMetadata("", (target, change) => ((PropertyLabel)target).TextChanged(change)));
    private readonly TranslateTransform textOffset = new();
    internal bool IsScrolling { get; private set; }
    internal double OverflowWidth => Math.Max(0, new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection,
        new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground ?? Brushes.Black,
        VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace - ActualWidth);

    internal PropertyLabel(string text)
    {
        Text = text; FontSize = PropertyPanelLayout.LabelFontSize; FontFamily = PropertyPanelLayout.LabelFontFamily; FontWeight = FontWeights.Normal; Width = PropertyPanelLayout.LabelWidth;
        TextWrapping = TextWrapping.NoWrap; TextTrimming = TextTrimming.CharacterEllipsis; ClipToBounds = true;
        ToolTip = text;
        System.Windows.Automation.AutomationProperties.SetName(this, text);
        TextEffects = new TextEffectCollection { new TextEffect { Transform = textOffset, PositionStart = 0, PositionCount = int.MaxValue } };
        MouseEnter += (_, _) => StartScroll(); MouseLeave += (_, _) => StopScroll();
        PreviewMouseLeftButtonDown += (_, _) => StopScroll();
        PreviewMouseLeftButtonUp += (_, _) => Dispatcher.BeginInvoke(new Action(() => { if (IsMouseOver) StartScroll(); }));
        Unloaded += (_, _) => StopScroll(); SizeChanged += (_, _) => StopScroll();
        IsVisibleChanged += (_, _) => { if (!IsVisible) StopScroll(); };
    }

    internal void StartScroll()
    {
        StopScroll();
        if (OverflowWidth < 1 || Mouse.LeftButton == MouseButtonState.Pressed || !SystemParameters.ClientAreaAnimation) return;
        double end = .8 + OverflowWidth / 28;
        var animation = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(.8))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(-OverflowWidth, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(end))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(-OverflowWidth, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(end + 1))));
        TextTrimming = TextTrimming.None; IsScrolling = true;
        textOffset.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    internal void StopScroll()
    {
        textOffset.BeginAnimation(TranslateTransform.XProperty, null); textOffset.X = 0;
        IsScrolling = false; TextTrimming = TextTrimming.CharacterEllipsis;
    }

    private void TextChanged(DependencyPropertyChangedEventArgs e)
    {
        StopScroll();
        System.Windows.Automation.AutomationProperties.SetName(this, Text);
        if (ToolTip is null || Equals(ToolTip, e.OldValue)) ToolTip = Text;
    }
}
