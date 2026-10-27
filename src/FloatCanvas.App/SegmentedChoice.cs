using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FloatCanvas.App;

/// <summary>Named, mutually exclusive choices with native radio semantics and no popup.</summary>
internal sealed class SegmentedChoice : Grid
{
    private static readonly Style OptionStyle = (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="RadioButton">
          <Setter Property="Foreground" Value="{DynamicResource CanvasText}"/>
          <Setter Property="FontSize" Value="12"/><Setter Property="MinHeight" Value="26"/>
          <Setter Property="Padding" Value="2,3"/>
          <Setter Property="HorizontalContentAlignment" Value="Center"/>
          <Setter Property="VerticalContentAlignment" Value="Center"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RadioButton">
            <Border Name="Frame" Background="Transparent" BorderThickness="1" BorderBrush="Transparent" CornerRadius="6" Padding="{TemplateBinding Padding}">
              <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True"><Setter Property="Foreground" Value="{DynamicResource CanvasAccent}"/></Trigger>
              <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource CanvasAccent}"/></Trigger>
              <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate></Setter.Value></Setter>
        </Style>
        """);
    private readonly UniformGrid options = new() { Rows = 1 };
    private readonly Grid track = new();
    private readonly Border indicator = new() { CornerRadius = new(6), BorderThickness = new(1), HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly TranslateTransform slide = new();
    private readonly List<RadioButton> items = [];
    private readonly string groupName = "Segmented_" + Guid.NewGuid().ToString("N");
    private bool synchronizing;
    private int selectedIndex = -1;
    private double optionWidth, targetX = double.NaN, targetWidth = double.NaN;

    internal IReadOnlyList<RadioButton> Items => items;
    internal double PreferredWidth => items.Count * optionWidth + 8;
    internal event EventHandler? SelectionChanged;

    internal int SelectedIndex
    {
        get => selectedIndex;
        set
        {
            if (value < -1 || value >= items.Count) throw new ArgumentOutOfRangeException(nameof(value));
            if (selectedIndex == value) return;
            selectedIndex = value;
            synchronizing = true;
            try { for (int index = 0; index < items.Count; index++) items[index].IsChecked = index == value; }
            finally { synchronizing = false; }
            UpdateIndicator(animate: true);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal RadioButton? SelectedItem { get => selectedIndex < 0 ? null : items[selectedIndex]; set => SelectedIndex = value is null ? -1 : items.IndexOf(value); }
    internal object? SelectedValue { get => SelectedItem?.Tag; set => SelectedIndex = items.FindIndex(item => Equals(item.Tag, value)); }

    internal SegmentedChoice()
    {
        MinHeight = PropertyPanelLayout.ControlHeight;
        var border = new Border { CornerRadius = new(9), BorderThickness = new(1), Padding = new(3), Child = track };
        border.SetResourceReference(Border.BackgroundProperty, "CanvasInput");
        border.SetResourceReference(Border.BorderBrushProperty, "CanvasBorder");
        indicator.SetResourceReference(Border.BackgroundProperty, "CanvasHover");
        indicator.SetResourceReference(Border.BorderBrushProperty, "CanvasBorder");
        indicator.RenderTransform = slide;
        track.Children.Add(indicator); track.Children.Add(options); Children.Add(border);
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Once);
        Unloaded += (_, _) => slide.BeginAnimation(TranslateTransform.XProperty, null);
    }

    internal void Add(string label, object value, FrameworkElement icon)
    {
        icon.Width = icon.Height = 18; icon.Margin = new(0, 0, 5, 0); icon.IsHitTestVisible = false;
        var caption = new TextBlock { Text = label, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        caption.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        optionWidth = Math.Max(optionWidth, caption.DesiredSize.Width + 18 + 5 + 6);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(icon); content.Children.Add(caption);
        var option = new RadioButton { Content = content, Tag = value, GroupName = groupName, Style = OptionStyle, ToolTip = label };
        AutomationProperties.SetName(option, label);
        int index = items.Count;
        option.Checked += (_, _) => { if (!synchronizing) SelectedIndex = index; };
        items.Add(option); options.Children.Add(option);
        InvalidateMeasure();
    }

    internal void SetIcon(int index, FrameworkElement icon)
    {
        var content = (StackPanel)items[index].Content;
        icon.Width = icon.Height = 18; icon.IsHitTestVisible = false;
        content.Children.RemoveAt(0); content.Children.Insert(0, icon); InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size constraint)
    {
        bool vertical = constraint.Width < PreferredWidth - .5;
        foreach (var item in items)
        {
            var content = (StackPanel)item.Content;
            content.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
            ((FrameworkElement)content.Children[0]).Margin = vertical ? new(0, 0, 0, 2) : new(0, 0, 5, 0);
            ((TextBlock)content.Children[1]).TextAlignment = vertical ? System.Windows.TextAlignment.Center : System.Windows.TextAlignment.Left;
        }
        return base.MeasureOverride(constraint);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var result = base.ArrangeOverride(arrangeSize);
        UpdateIndicator(animate: false);
        return result;
    }

    private void UpdateIndicator(bool animate)
    {
        if (SelectedItem is not { ActualWidth: > 0 } item) { indicator.Visibility = Visibility.Collapsed; return; }
        double x = item.TranslatePoint(new(), track).X;
        if (indicator.Visibility == Visibility.Visible && Math.Abs(x - targetX) < .01 && Math.Abs(item.ActualWidth - targetWidth) < .01) return;
        double previous = slide.X;
        bool move = animate && indicator.Visibility == Visibility.Visible && IsVisible && IsLoaded && SystemParameters.ClientAreaAnimation;
        slide.BeginAnimation(TranslateTransform.XProperty, null);
        targetX = x; targetWidth = item.ActualWidth;
        indicator.Width = targetWidth; slide.X = x; indicator.Visibility = Visibility.Visible;
        if (move) slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(previous, x, TimeSpan.FromMilliseconds(160))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.None && items.Count > 0 && e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)
        {
            SelectedIndex = e.Key switch
            {
                Key.Home => 0, Key.End => items.Count - 1,
                Key.Left or Key.Up => selectedIndex < 0 ? items.Count - 1 : (selectedIndex + items.Count - 1) % items.Count,
                _ => (selectedIndex + 1) % items.Count
            };
            SelectedItem!.Focus(); e.Handled = true; return;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ChoicePeer(this);

    private sealed class ChoicePeer(SegmentedChoice owner) : FrameworkElementAutomationPeer(owner), ISelectionProvider
    {
        protected override string GetClassNameCore() => nameof(SegmentedChoice);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.Selection ? this : base.GetPattern(patternInterface);
        bool ISelectionProvider.CanSelectMultiple => false;
        bool ISelectionProvider.IsSelectionRequired => false; // Mixed selections intentionally have no selected segment.
        IRawElementProviderSimple[] ISelectionProvider.GetSelection() => owner.SelectedItem is { } selected && UIElementAutomationPeer.CreatePeerForElement(selected) is { } peer ? [ProviderFromPeer(peer)] : [];
    }
}
