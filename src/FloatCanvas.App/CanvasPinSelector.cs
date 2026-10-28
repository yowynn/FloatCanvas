using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Spatial single choice for the reference-plane pin, with two-dimensional keyboard navigation.</summary>
internal sealed class CanvasPinSelector : StackPanel
{
    private readonly ToggleButton[] points = new ToggleButton[9];
    private readonly TextBlock caption = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new(12, 0, 0, 0) };
    private CanvasPinLocation selected = CanvasPinLocation.BottomLeft;
    public event Action<CanvasPinLocation>? SelectionChanged;
    public CanvasPinLocation SelectedValue
    {
        get => selected;
        set
        {
            selected = value;
            for (int i = 0; i < points.Length; i++)
            {
                points[i].IsChecked = i == (int)value;
                ((Border)points[i].Content).SetResourceReference(Border.BackgroundProperty, i == (int)value ? "CanvasAccent" : "CanvasText");
            }
            caption.Text = L.Get("Canvas.Pin." + value);
        }
    }
    public CanvasPinSelector()
    {
        Orientation = Orientation.Horizontal;
        var grid = new Grid { Width = 96, Height = 96 };
        var pointStyle = (Style)XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ToggleButton">
              <Setter Property="Template"><Setter.Value>
                <ControlTemplate TargetType="ToggleButton">
                  <Border x:Name="Chrome" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Background="Transparent" BorderBrush="Transparent" BorderThickness="1" CornerRadius="4">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{DynamicResource CanvasHover}"/></Trigger>
                    <Trigger Property="IsChecked" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="{DynamicResource CanvasAccent}"/><Setter TargetName="Chrome" Property="Background" Value="{DynamicResource CanvasHover}"/></Trigger>
                    <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="{DynamicResource CanvasAccent}"/><Setter TargetName="Chrome" Property="BorderThickness" Value="2"/></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.5"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value></Setter>
            </Style>
            """);
        for (int i = 0; i < 3; i++) { grid.RowDefinitions.Add(new()); grid.ColumnDefinitions.Add(new()); }
        var outline = new Border { Margin = new(16), BorderThickness = new(1), IsHitTestVisible = false };
        outline.SetResourceReference(Border.BorderBrushProperty, "CanvasBorder");
        Grid.SetRowSpan(outline, 3); Grid.SetColumnSpan(outline, 3); grid.Children.Add(outline);
        for (int i = 0; i < points.Length; i++)
        {
            int index = i;
            var dot = new Border { Width = 8, Height = 8, CornerRadius = new(1), IsHitTestVisible = false };
            var button = points[i] = new ToggleButton { Content = dot, Style = pointStyle, Padding = new(0), MinWidth = 0, MinHeight = 0,
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = L.Get("Canvas.Pin." + (CanvasPinLocation)i) };
            System.Windows.Automation.AutomationProperties.SetName(button, (string)button.ToolTip);
            button.Click += (_, _) => Choose(index);
            button.PreviewKeyDown += (_, e) =>
            {
                int row = index / 3, column = index % 3;
                switch (e.Key)
                {
                    case Key.Left: column = Math.Max(0, column - 1); break;
                    case Key.Right: column = Math.Min(2, column + 1); break;
                    case Key.Up: row = Math.Max(0, row - 1); break;
                    case Key.Down: row = Math.Min(2, row + 1); break;
                    default: return;
                }
                int next = row * 3 + column; points[next].Focus(); Choose(next); e.Handled = true;
            };
            Grid.SetRow(button, i / 3); Grid.SetColumn(button, i % 3); grid.Children.Add(button);
        }
        Children.Add(grid); Children.Add(caption); SelectedValue = selected;
    }
    private void Choose(int index)
    {
        bool changed = selected != (CanvasPinLocation)index;
        SelectedValue = (CanvasPinLocation)index;
        if (changed) SelectionChanged?.Invoke(selected);
    }
}
