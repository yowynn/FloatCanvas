using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

namespace FloatCanvas.App;

/// <summary>Shared neutral palette and native WPF control styles. Registry access is read-only.</summary>
internal static class AppTheme
{
    private static ResourceDictionary? styles;

    public static void Apply(string mode)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;
        app.Dispatcher.VerifyAccess();
        bool dark = mode?.ToLowerInvariant() switch { "dark" => true, "light" => false, _ => SystemUsesDarkTheme() };
        var resources = app.Resources;
        SetBrush(resources, "CanvasSurface", dark ? "#202322" : "#F7F7F5");
        SetBrush(resources, "CanvasText", dark ? "#E9ECE9" : "#202320");
        SetBrush(resources, "CanvasBorder", dark ? "#414641" : "#DADCD7");
        SetBrush(resources, "CanvasHover", dark ? "#303630" : "#EAEDE7");
        SetBrush(resources, "CanvasAccent", dark ? "#66AEF5" : "#2383E2");
        SetBrush(resources, "CanvasMuted", dark ? "#A6AEA5" : "#6F786E");
        SetBrush(resources, "CanvasInput", dark ? "#272C27" : "#FFFFFF");

        // Standard WPF popups, lists and checkboxes also use the same palette.
        resources[SystemColors.WindowBrushKey] = resources["CanvasInput"];
        resources[SystemColors.WindowTextBrushKey] = resources["CanvasText"];
        resources[SystemColors.ControlBrushKey] = resources["CanvasSurface"];
        resources[SystemColors.ControlTextBrushKey] = resources["CanvasText"];
        resources[SystemColors.GrayTextBrushKey] = resources["CanvasMuted"];
        resources[SystemColors.HighlightBrushKey] = resources["CanvasAccent"];
        resources[SystemColors.HighlightTextBrushKey] = Brushes.White;

        if (styles is null) styles = (ResourceDictionary)XamlReader.Parse(StyleMarkup);
        if (!resources.MergedDictionaries.Contains(styles)) resources.MergedDictionaries.Add(styles);
    }

    private static void SetBrush(ResourceDictionary resources, string key, string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze(); resources[key] = brush;
    }

    private static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { return false; }
    }

    private const string StyleMarkup = """
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style x:Key="CanvasControlBase" TargetType="Control">
            <Setter Property="FontSize" Value="14"/>
            <Setter Property="FontFamily" Value="Segoe UI, Microsoft YaHei UI"/>
            <Setter Property="Foreground" Value="{DynamicResource CanvasText}"/>
            <Setter Property="ToolTipService.InitialShowDelay" Value="700"/>
            <Setter Property="ToolTipService.ShowDuration" Value="8000"/>
          </Style>
          <Style x:Key="CanvasKeyboardFocus" TargetType="Control">
            <Setter Property="Template">
              <Setter.Value><ControlTemplate TargetType="Control">
                <Border Margin="2" BorderBrush="{DynamicResource CanvasAccent}" BorderThickness="2" CornerRadius="5" IsHitTestVisible="False"/>
              </ControlTemplate></Setter.Value>
            </Setter>
          </Style>
          <Style TargetType="Window" BasedOn="{StaticResource CanvasControlBase}">
            <Setter Property="Icon" Value="pack://application:,,,/FloatCanvas;component/Assets/FloatCanvas.ico"/>
            <Setter Property="Background" Value="{DynamicResource CanvasSurface}"/>
            <Setter Property="Foreground" Value="{DynamicResource CanvasText}"/>
            <Setter Property="UseLayoutRounding" Value="True"/>
          </Style>
          <Style TargetType="TextBlock">
            <Setter Property="FontSize" Value="14"/>
            <Setter Property="FontFamily" Value="Segoe UI, Microsoft YaHei UI"/>
            <Setter Property="ToolTipService.InitialShowDelay" Value="700"/>
          </Style>
          <Style TargetType="Button" BasedOn="{StaticResource CanvasControlBase}">
            <Setter Property="MinHeight" Value="34"/>
            <Setter Property="MinWidth" Value="34"/>
            <Setter Property="Padding" Value="10,5"/>
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="BorderBrush" Value="Transparent"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="HorizontalContentAlignment" Value="Center"/>
            <Setter Property="VerticalContentAlignment" Value="Center"/>
            <Setter Property="FocusVisualStyle" Value="{StaticResource CanvasKeyboardFocus}"/>
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="Button">
                <Border x:Name="Chrome" CornerRadius="6" Background="{TemplateBinding Background}"
                        BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
                  <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}"
                                    RecognizesAccessKey="True" ContentSource="Content"/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{DynamicResource CanvasHover}"/></Trigger>
                  <Trigger Property="IsPressed" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{DynamicResource CanvasHover}"/><Setter TargetName="Chrome" Property="BorderBrush" Value="{DynamicResource CanvasBorder}"/></Trigger>
                  <Trigger Property="IsDefaulted" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="{DynamicResource CanvasAccent}"/></Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Chrome" Property="Opacity" Value="0.45"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value></Setter>
          </Style>
          <Style TargetType="TextBox" BasedOn="{StaticResource CanvasControlBase}">
            <Setter Property="MinHeight" Value="34"/>
            <Setter Property="Padding" Value="8,5"/>
            <Setter Property="Background" Value="{DynamicResource CanvasInput}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource CanvasBorder}"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="CaretBrush" Value="{DynamicResource CanvasText}"/>
            <Setter Property="SelectionBrush" Value="{DynamicResource CanvasAccent}"/>
            <Setter Property="SelectionOpacity" Value="0.35"/>
            <Setter Property="VerticalContentAlignment" Value="Center"/>
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="TextBox">
                <Border x:Name="Chrome" CornerRadius="6" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                        BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}">
                  <ScrollViewer x:Name="PART_ContentHost" Focusable="False" HorizontalScrollBarVisibility="{TemplateBinding ScrollViewer.HorizontalScrollBarVisibility}" VerticalScrollBarVisibility="{TemplateBinding ScrollViewer.VerticalScrollBarVisibility}"/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="{DynamicResource CanvasAccent}"/></Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Chrome" Property="Opacity" Value="0.45"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value></Setter>
            <Style.Triggers><Trigger Property="AcceptsReturn" Value="True"><Setter Property="VerticalContentAlignment" Value="Top"/></Trigger></Style.Triggers>
          </Style>
          <Style TargetType="ListBox" BasedOn="{StaticResource CanvasControlBase}">
            <Setter Property="Background" Value="{DynamicResource CanvasInput}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource CanvasBorder}"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Padding" Value="4"/>
            <Setter Property="ScrollViewer.HorizontalScrollBarVisibility" Value="Auto"/>
            <Setter Property="ScrollViewer.VerticalScrollBarVisibility" Value="Auto"/>
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="ListBox">
                <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="6" Padding="{TemplateBinding Padding}">
                  <ScrollViewer x:Name="PART_ScrollViewer" Focusable="False" CanContentScroll="True" HorizontalScrollBarVisibility="{TemplateBinding ScrollViewer.HorizontalScrollBarVisibility}" VerticalScrollBarVisibility="{TemplateBinding ScrollViewer.VerticalScrollBarVisibility}">
                    <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/>
                  </ScrollViewer>
                </Border>
              </ControlTemplate>
            </Setter.Value></Setter>
          </Style>
          <Style TargetType="ListBoxItem" BasedOn="{StaticResource CanvasControlBase}">
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="BorderBrush" Value="Transparent"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Padding" Value="6,5"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="FocusVisualStyle" Value="{StaticResource CanvasKeyboardFocus}"/>
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="ListBoxItem">
                <Border x:Name="Chrome" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="4" Padding="{TemplateBinding Padding}">
                  <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center"/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{DynamicResource CanvasHover}"/></Trigger>
                  <Trigger Property="IsSelected" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{DynamicResource CanvasHover}"/><Setter TargetName="Chrome" Property="BorderBrush" Value="{DynamicResource CanvasAccent}"/></Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Chrome" Property="Opacity" Value="0.45"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value></Setter>
          </Style>
          <Style TargetType="ContextMenu" BasedOn="{StaticResource CanvasControlBase}">
            <Setter Property="Background" Value="{DynamicResource CanvasInput}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource CanvasBorder}"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Padding" Value="4"/>
            <Setter Property="MinWidth" Value="180"/>
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="ContextMenu">
                <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}" CornerRadius="8">
                  <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Cycle"/>
                </Border>
              </ControlTemplate>
            </Setter.Value></Setter>
          </Style>
          <Style TargetType="MenuItem" BasedOn="{StaticResource CanvasControlBase}">
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="BorderBrush" Value="Transparent"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Padding" Value="7,7"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="MenuItem">
                <Border x:Name="Chrome" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}" CornerRadius="4">
                  <Grid>
                    <Grid.ColumnDefinitions><ColumnDefinition Width="18"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                    <ContentPresenter x:Name="Icon" Content="{TemplateBinding Icon}" VerticalAlignment="Center"/>
                    <Path x:Name="Check" Visibility="Collapsed" Data="M 2 7 L 6 11 L 13 3" Stroke="{DynamicResource CanvasAccent}" StrokeThickness="1.8" VerticalAlignment="Center"/>
                    <ContentPresenter Grid.Column="1" Margin="6,0,12,0" ContentSource="Header" RecognizesAccessKey="True" VerticalAlignment="Center"/>
                    <TextBlock Grid.Column="2" Text="{TemplateBinding InputGestureText}" Foreground="{DynamicResource CanvasMuted}" FontSize="12" VerticalAlignment="Center"/>
                  </Grid>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{DynamicResource CanvasHover}"/></Trigger>
                  <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="{DynamicResource CanvasAccent}"/></Trigger>
                  <Trigger Property="IsChecked" Value="True"><Setter TargetName="Check" Property="Visibility" Value="Visible"/><Setter TargetName="Icon" Property="Visibility" Value="Collapsed"/></Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Chrome" Property="Opacity" Value="0.45"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value></Setter>
          </Style>
          <Style TargetType="Separator">
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Separator"><Border Height="1" Margin="8,5" Background="{DynamicResource CanvasBorder}"/></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="ComboBoxItem" BasedOn="{StaticResource CanvasControlBase}">
            <Setter Property="Padding" Value="10,7"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="ComboBoxItem">
                <Border x:Name="Chrome" Padding="{TemplateBinding Padding}" CornerRadius="4" Background="{TemplateBinding Background}">
                  <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center"/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{DynamicResource CanvasHover}"/></Trigger>
                  <Trigger Property="IsSelected" Value="True"><Setter Property="Foreground" Value="{DynamicResource CanvasAccent}"/></Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Chrome" Property="Opacity" Value="0.45"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value></Setter>
          </Style>
          <Style TargetType="ComboBox" BasedOn="{StaticResource CanvasControlBase}">
            <Setter Property="MinHeight" Value="34"/>
            <Setter Property="Padding" Value="8,5"/>
            <Setter Property="Background" Value="{DynamicResource CanvasInput}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource CanvasBorder}"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="HorizontalContentAlignment" Value="Left"/>
            <Setter Property="VerticalContentAlignment" Value="Center"/>
            <Setter Property="FocusVisualStyle" Value="{StaticResource CanvasKeyboardFocus}"/>
            <Setter Property="ScrollViewer.HorizontalScrollBarVisibility" Value="Auto"/>
            <Setter Property="ScrollViewer.VerticalScrollBarVisibility" Value="Auto"/>
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="ComboBox">
                <Grid>
                  <Border x:Name="Chrome" CornerRadius="6" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"/>
                  <ToggleButton x:Name="DropDownToggle" Focusable="False" ClickMode="Press" Background="Transparent"
                                IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
                    <ToggleButton.Template><ControlTemplate TargetType="ToggleButton">
                      <Border Background="{TemplateBinding Background}"><Path Data="M 0 0 L 4 4 L 8 0" StrokeThickness="1.3" Stroke="{DynamicResource CanvasMuted}" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,11,0"/></Border>
                    </ControlTemplate></ToggleButton.Template>
                  </ToggleButton>
                  <ContentPresenter x:Name="Selection" Margin="8,5,30,5" IsHitTestVisible="False" VerticalAlignment="Center" HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                    Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}" ContentTemplateSelector="{TemplateBinding ItemTemplateSelector}"/>
                  <TextBox x:Name="PART_EditableTextBox" Visibility="Hidden" Margin="2,2,26,2" Background="Transparent" BorderThickness="0" MinHeight="0"
                           IsReadOnly="{TemplateBinding IsReadOnly}" VerticalContentAlignment="Center"/>
                  <Popup x:Name="PART_Popup" Placement="Bottom" AllowsTransparency="True" Focusable="False"
                         IsOpen="{TemplateBinding IsDropDownOpen}" PopupAnimation="None">
                    <Border Background="{DynamicResource CanvasInput}" BorderBrush="{DynamicResource CanvasBorder}" BorderThickness="1" CornerRadius="6" Padding="4"
                            MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}" MaxHeight="{TemplateBinding MaxDropDownHeight}">
                      <ScrollViewer CanContentScroll="True" HorizontalScrollBarVisibility="Auto" VerticalScrollBarVisibility="Auto">
                        <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/>
                      </ScrollViewer>
                    </Border>
                  </Popup>
                </Grid>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="{DynamicResource CanvasMuted}"/></Trigger>
                  <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="{DynamicResource CanvasAccent}"/></Trigger>
                  <Trigger Property="IsDropDownOpen" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="{DynamicResource CanvasAccent}"/></Trigger>
                  <Trigger Property="IsEditable" Value="True"><Setter TargetName="Selection" Property="Visibility" Value="Hidden"/><Setter TargetName="PART_EditableTextBox" Property="Visibility" Value="Visible"/></Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Chrome" Property="Opacity" Value="0.45"/><Setter TargetName="Selection" Property="Opacity" Value="0.45"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value></Setter>
          </Style>
          <Style TargetType="ToolTip" BasedOn="{StaticResource CanvasControlBase}">
            <Setter Property="Background" Value="{DynamicResource CanvasInput}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource CanvasBorder}"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Padding" Value="10,7"/>
            <Setter Property="FontSize" Value="12"/>
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="ToolTip"><Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="6" Padding="{TemplateBinding Padding}"><ContentPresenter/></Border></ControlTemplate>
            </Setter.Value></Setter>
          </Style>
        </ResourceDictionary>
        """;
}
