using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Data;
using System.Windows.Media;

namespace FloatCanvas.App;

/// <summary>Shared usable width for property and tool parameter columns, excluding chrome and scrollbars.</summary>
internal static class PropertyPanelLayout
{
    internal const double ContentWidth = 440;
    internal const double TwoColumnMinimumWidth = 640;
    internal const double TwoColumnMinimumHeight = 420;
    internal const double LabelWidth = 96;
    internal const double LabelFontSize = 12;
    internal const double SectionFontSize = 13;
    internal static readonly FontFamily LabelFontFamily = new("Segoe UI, Microsoft YaHei UI");
    internal const double ControlHeight = 34;
    internal const double LabelGap = 8;
    internal const double PairGap = 36;
    internal const double PairMinimumWidth = 244;
    internal const double Padding = WindowLayout.ContentInset;
    internal static double WindowWidth => ContentWidth + Padding * 2 + 2;

    private static readonly DependencyProperty LabelMaximumProperty = DependencyProperty.RegisterAttached("LabelMaximum", typeof(double), typeof(PropertyPanelLayout), new FrameworkPropertyMetadata(LabelWidth, FrameworkPropertyMetadataOptions.Inherits));
    internal static void Scope(FrameworkElement panel, double maximum = LabelWidth)
    {
        Grid.SetIsSharedSizeScope(panel, true);
        panel.SetValue(LabelMaximumProperty, maximum);
    }

    internal static Grid Row(TextBlock label, FrameworkElement control)
    {
        var row = new Grid { Margin = new(0, 4, 0, 4) };
        if (control is ComboBox or ColorField or NumericValueField || control is TextBox { AcceptsReturn: false })
            control.Height = control.MinHeight = ControlHeight;
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto, SharedSizeGroup = "PropertyLabels" });
        row.ColumnDefinitions.Add(new());
        label.Width = double.NaN; label.Margin = new(0, 0, LabelGap, 0);
        label.SetBinding(FrameworkElement.MaxWidthProperty, new Binding { Path = new PropertyPath(LabelMaximumProperty), Source = row });
        label.FontSize = LabelFontSize; label.FontFamily = LabelFontFamily; label.FontWeight = FontWeights.Normal;
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label); Grid.SetColumn(control, 1); row.Children.Add(control);
        control.VerticalAlignment = VerticalAlignment.Center;
        if (control is SegmentedChoice choice)
        {
            row.RowDefinitions.Add(new() { Height = GridLength.Auto }); row.RowDefinitions.Add(new() { Height = GridLength.Auto });
            row.SizeChanged += (_, _) =>
            {
                bool stacked = row.ActualWidth < LabelWidth + LabelGap + choice.PreferredWidth;
                Grid.SetColumnSpan(label, stacked ? 2 : 1);
                Grid.SetRow(control, stacked ? 1 : 0); Grid.SetColumn(control, stacked ? 0 : 1); Grid.SetColumnSpan(control, stacked ? 2 : 1);
                label.Margin = stacked ? new(0, 0, 0, 4) : new(0, 0, LabelGap, 0);
                label.SetCurrentValue(FrameworkElement.MaxWidthProperty, stacked ? row.ActualWidth : (double)row.GetValue(LabelMaximumProperty));
            };
        }
        return row;
    }

    internal static TextBlock Label(string name) => new() { Text = name, FontSize = LabelFontSize, FontFamily = LabelFontFamily, FontWeight = FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center };

    internal static TextBlock SectionTitle(string name) => new() { Text = name, FontSize = SectionFontSize, FontFamily = LabelFontFamily, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };

    internal static Grid BooleanRow(string name, CheckBox control)
    {
        var row = new Grid { Margin = new(0, 4, 0, 4), MinHeight = ControlHeight };
        row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var label = new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap, FontSize = LabelFontSize, FontFamily = LabelFontFamily, FontWeight = FontWeights.Normal, Margin = new(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        control.Content = null; control.Padding = new(0); control.Margin = new(0); control.HorizontalAlignment = HorizontalAlignment.Right; control.VerticalAlignment = VerticalAlignment.Center;
        control.SetResourceReference(FrameworkElement.StyleProperty, "CanvasPropertySwitch");
        AutomationProperties.SetName(control, name); AutomationProperties.SetLabeledBy(control, label);
        row.Children.Add(label); Grid.SetColumn(control, 1); row.Children.Add(control);
        row.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(UIElement.Visibility)) { Source = control });
        return row;
    }

    // Related pairs share the same two columns. Narrow windows stack the pair
    // instead of compressing labels, units and editable values into tiny slots.
    internal static Grid Pair(FrameworkElement first, FrameworkElement second, FrameworkElement? middle = null)
    {
        var pair = new Grid();
        pair.ColumnDefinitions.Add(new()); pair.ColumnDefinitions.Add(new() { Width = new(PairGap) }); pair.ColumnDefinitions.Add(new());
        for (int i = 0; i < 3; i++) pair.RowDefinitions.Add(new() { Height = GridLength.Auto });
        pair.Children.Add(first); Grid.SetColumn(second, 2); pair.Children.Add(second);
        if (middle is not null) { Grid.SetColumn(middle, 1); pair.Children.Add(middle); }
        pair.SizeChanged += (_, _) =>
        {
            bool stacked = pair.ActualWidth < PairMinimumWidth;
            Grid.SetRow(second, stacked ? 2 : 0); Grid.SetColumn(second, stacked ? 0 : 2);
            Grid.SetColumnSpan(first, stacked ? 3 : 1); Grid.SetColumnSpan(second, stacked ? 3 : 1);
            if (middle is not null)
            { Grid.SetRow(middle, stacked ? 1 : 0); Grid.SetColumn(middle, stacked ? 0 : 1); Grid.SetColumnSpan(middle, stacked ? 3 : 1); }
        };
        return pair;
    }
}
