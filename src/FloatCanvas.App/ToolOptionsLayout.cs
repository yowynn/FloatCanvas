using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace FloatCanvas.App;

internal static class ToolOptionsLayout
{
    internal static Thickness ParameterMargin => new(WindowLayout.ContentInset, 0, WindowLayout.ContentInset, 0);

    internal static ScrollViewer ScrollWithReservedGutter() => new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ScrollViewer">
              <Grid>
                <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="12"/></Grid.ColumnDefinitions>
                <ScrollContentPresenter x:Name="PART_ScrollContentPresenter" Content="{TemplateBinding Content}" ContentTemplate="{TemplateBinding ContentTemplate}" CanContentScroll="{TemplateBinding CanContentScroll}"/>
                <ScrollBar x:Name="PART_VerticalScrollBar" Grid.Column="1" Orientation="Vertical" Maximum="{TemplateBinding ScrollableHeight}" ViewportSize="{TemplateBinding ViewportHeight}" Value="{Binding VerticalOffset, RelativeSource={RelativeSource TemplatedParent}, Mode=OneWay}" Visibility="{TemplateBinding ComputedVerticalScrollBarVisibility}"/>
              </Grid>
            </ControlTemplate>
            """)
    };

    internal static TextBlock Section(string key, bool first = false)
    {
        var title = PropertyPanelLayout.SectionTitle(FloatCanvas.Localization.AppText.Get(key));
        title.Margin = new(0, first ? 0 : 16, 0, 8);
        return title;
    }
}
