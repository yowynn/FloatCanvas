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
    private static string currentMode = "system";
    private static bool observingSystemColors;
    private static ResourceDictionary? styles;
    private static ResourceDictionary? overlayStyles;

    // The primary process calls this before constructing DesktopSession: its field
    // initializers can fail before the session applies the saved theme itself.
    internal static void InitializeStartup()
    {
        var settings = UserSettings.Load();
        FloatCanvas.Localization.AppText.Initialize(settings.Language);
        Apply(settings.Theme);
    }

    public static void Apply(string mode)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;
        app.Dispatcher.VerifyAccess();
        AppInputPolicy.Initialize();
        currentMode = mode;
        if (!observingSystemColors)
        {
            observingSystemColors = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category is UserPreferenceCategory.Accessibility or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.General)
                    app.Dispatcher.BeginInvoke(() => Apply(currentMode));
            };
            SystemParameters.StaticPropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(SystemParameters.HighContrast) or "")
                    app.Dispatcher.BeginInvoke(() => Apply(currentMode));
            };
        }
        bool dark = mode?.ToLowerInvariant() switch { "dark" => true, "light" => false, _ => SystemUsesDarkTheme() };
        var resources = app.Resources;
        ApplyPalette(resources, dark);
        ResourceDictionary selected;
        if(OverlayWindowScope.Current is not null)
        {
            overlayStyles??=(ResourceDictionary)XamlReader.Parse(StyleMarkup
                .Replace("<ResourceDictionary ","<ResourceDictionary xmlns:overlay=\"clr-namespace:FloatCanvas.App;assembly=FloatCanvas\" ")
                .Replace("<Style x:Key=\"CanvasControlBase\" TargetType=\"Control\">",
                    "<Style x:Key=\"CanvasControlBase\" TargetType=\"Control\"><Setter Property=\"InputMethod.IsInputMethodEnabled\" Value=\"False\"/>")
                .Replace("<Style TargetType=\"TextBox\" BasedOn=\"{StaticResource CanvasControlBase}\">",
                    "<Style TargetType=\"TextBox\" BasedOn=\"{StaticResource CanvasControlBase}\"><Setter Property=\"InputMethod.IsInputMethodEnabled\" Value=\"True\"/>")
                .Replace("<Popup ","<overlay:OverlayPopup ").Replace("</Popup>","</overlay:OverlayPopup>"));
            selected=overlayStyles;
        }
        else selected=styles??=(ResourceDictionary)XamlReader.Parse(StyleMarkup);
        if(styles is not null && !ReferenceEquals(selected,styles)) resources.MergedDictionaries.Remove(styles);
        if(overlayStyles is not null && !ReferenceEquals(selected,overlayStyles)) resources.MergedDictionaries.Remove(overlayStyles);
        if (!resources.MergedDictionaries.Contains(selected)) resources.MergedDictionaries.Add(selected);
    }

    // Early argument/forwarding errors have no Application; a failed bootstrap may
    // also leave only part of its palette. Never combine that with default text.
    internal static void EnsureDialogPalette(Window window)
    {
        string[] required = ["CanvasSurface", "CanvasText", "CanvasBorder", "CanvasButton",
            "CanvasButtonHover", "CanvasPressed", "CanvasAccent", "CanvasMuted", "CanvasInput",
            "CanvasCloseHover", "CanvasCloseForeground"];
        if (required.All(key => window.TryFindResource(key) is SolidColorBrush { Color.A: 255, Opacity: 1 }) &&
            Contrast(((SolidColorBrush)window.TryFindResource("CanvasSurface")).Color,
                ((SolidColorBrush)window.TryFindResource("CanvasText")).Color) >= 4.5) return;
        ApplyPalette(window.Resources, false);
    }

    private static double Contrast(Color first, Color second)
    {
        static double Channel(byte value) { double c = value / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        static double Luminance(Color color) => .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    internal static void ApplyPalette(ResourceDictionary resources, bool dark, bool? highContrast = null)
    {
        SetBrush(resources, "CanvasSurface", dark ? "#24272C" : "#F8F9FB");
        SetBrush(resources, "CanvasFileBubble", dark ? "#29384B" : "#EDF3FB");
        SetBrush(resources, "CanvasText", dark ? "#E9ECF1" : "#252B36");
        SetBrush(resources, "CanvasBorder", dark ? "#3A3F48" : "#DDE2EA");
        SetBrush(resources, "CanvasSwatchBorder", dark ? "#40FFFFFF" : "#380F172A");
        SetBrush(resources, "CanvasHover", dark ? "#343A44" : "#E9EDF4");
        SetBrush(resources, "CanvasButtonHover", dark ? "#46515F" : "#D7E2F2");
        SetBrush(resources, "CanvasAccent", dark ? "#66AEF5" : "#2383E2");
        SetBrush(resources, "CanvasMuted", dark ? "#A3ADBA" : "#697586");
        SetBrush(resources, "CanvasInput", dark ? "#2D323B" : "#FFFFFF");
        SetBrush(resources, "CanvasLcdSurface", dark ? "#332E24" : "#C4CCAE");
        SetBrush(resources, "CanvasLcdText", dark ? "#E4DDC0" : "#30382B");
        var lcdEdge = new LinearGradientBrush(dark ? Color.FromRgb(0x19, 0x17, 0x0F) : Color.FromRgb(0x7D, 0x87, 0x6E),
            dark ? Color.FromRgb(0x6B, 0x5F, 0x43) : Color.FromRgb(0xDF, 0xE5, 0xCE), new Point(0, 0), new Point(0, 1));
        lcdEdge.Freeze(); resources["CanvasLcdBorder"] = lcdEdge;
        SetBrush(resources, "CanvasButton", dark ? "#303640" : "#E6EAF1");
        SetBrush(resources, "CanvasPressed", dark ? "#3B4655" : "#C5D3E7");
        SetBrush(resources, "CanvasHiddenSurface", dark ? "#4B3B20" : "#FFF0CC");
        SetBrush(resources, "CanvasHiddenText", dark ? "#FFD077" : "#9A5800");
        SetBrush(resources, "CanvasHiddenBorder", dark ? "#9A7735" : "#D6A343");
        SetBrush(resources, "CanvasPrimary", "#2376CC");
        SetBrush(resources, "CanvasPrimaryHover", "#1E66B3");
        SetBrush(resources, "CanvasPrimaryPressed", "#185394");
        SetBrush(resources, "CanvasDanger", "#C9343D");
        SetBrush(resources, "CanvasDangerHover", "#B92B35");
        SetBrush(resources, "CanvasDangerPressed", "#A4242E");
        SetBrush(resources, "CanvasCodeText", dark ? "#FFADB4" : "#B62335");
        SetBrush(resources, "CanvasCodeSurface", dark ? "#3B2E35" : "#F9E9EC");
        SetBrush(resources, "CanvasCloseHover", dark ? "#6A3B45" : "#F7D2D7");
        SetBrush(resources, "CanvasCloseForeground", dark ? "#FFADB4" : "#B62335");

        SetBrush(resources, "CanvasErrorText", dark ? "#FFADB4" : "#A7192C");
        resources["CanvasOnAccent"] = Brushes.White;
        if (highContrast ?? SystemParameters.HighContrast)
        {
            foreach (var key in resources.Keys.OfType<string>().Where(key => key.StartsWith("Canvas", StringComparison.Ordinal)).ToArray())
                resources[key] = SystemColors.WindowBrush;
            foreach (var key in new[] { "CanvasText", "CanvasMuted", "CanvasErrorText", "CanvasBorder", "CanvasSwatchBorder", "CanvasLcdText", "CanvasLcdBorder", "CanvasHiddenText", "CanvasHiddenBorder", "CanvasCodeText", "CanvasCloseForeground" })
                resources[key] = SystemColors.WindowTextBrush;
            foreach (var key in new[] { "CanvasAccent", "CanvasPrimary", "CanvasPrimaryHover", "CanvasPrimaryPressed", "CanvasDanger", "CanvasDangerHover", "CanvasDangerPressed" })
                resources[key] = SystemColors.HighlightBrush;
            resources["CanvasOnAccent"] = SystemColors.HighlightTextBrush;
        }

        // Standard WPF popups, lists and checkboxes also use the same palette.
        resources[SystemColors.WindowBrushKey] = resources["CanvasInput"];
        resources[SystemColors.WindowTextBrushKey] = resources["CanvasText"];
        resources[SystemColors.ControlBrushKey] = resources["CanvasSurface"];
        resources[SystemColors.ControlTextBrushKey] = resources["CanvasText"];
        resources[SystemColors.GrayTextBrushKey] = resources["CanvasMuted"];
        resources[SystemColors.HighlightBrushKey] = resources["CanvasAccent"];
        resources[SystemColors.HighlightTextBrushKey] = resources["CanvasOnAccent"];

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
          <CornerRadius x:Key="CanvasButtonCornerRadius">6</CornerRadius>
          <Style x:Key="CanvasControlBase" TargetType="Control">
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="KeyboardNavigation.TabNavigation" Value="None"/>
            <Setter Property="FontSize" Value="14"/>
            <Setter Property="FontFamily" Value="Segoe UI, Microsoft YaHei UI"/>
            <Setter Property="Foreground" Value="{DynamicResource CanvasText}"/>
            <Setter Property="ToolTipService.InitialShowDelay" Value="700"/>
            <Setter Property="ToolTipService.ShowDuration" Value="8000"/>
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
            <Setter Property="Background" Value="{DynamicResource CanvasButton}"/>
            <Setter Property="BorderBrush" Value="Transparent"/>
            <Setter Property="BorderThickness" Value="0"/>
            <Setter Property="HorizontalContentAlignment" Value="Center"/>
            <Setter Property="VerticalContentAlignment" Value="Center"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="Button">
                <Border x:Name="Chrome" CornerRadius="{DynamicResource CanvasButtonCornerRadius}" Background="{TemplateBinding Background}"
                        BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
                  <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}"
                                    RecognizesAccessKey="True" ContentSource="Content"/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{DynamicResource CanvasButtonHover}"/></Trigger>
                  <Trigger Property="IsPressed" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{DynamicResource CanvasPressed}"/></Trigger>
                  <Trigger Property="IsDefaulted" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="{DynamicResource CanvasAccent}"/></Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Chrome" Property="Opacity" Value="0.45"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value></Setter>
          </Style>
          <ControlTemplate x:Key="CanvasSelectionTileButtonTemplate" TargetType="Button">
            <Grid x:Name="TileChrome">
              <Border x:Name="Chrome" CornerRadius="{DynamicResource CanvasButtonCornerRadius}" Background="{TemplateBinding Background}"
                      BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
                <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}"
                                  RecognizesAccessKey="True" ContentSource="Content"/>
              </Border>
              <Ellipse x:Name="ModifiedDot" Width="8" Height="8" HorizontalAlignment="Right" VerticalAlignment="Top" Margin="0,-3,0,0"
                       Fill="{DynamicResource CanvasAccent}" Visibility="Collapsed" IsHitTestVisible="False" UseLayoutRounding="True"/>
            </Grid>
            <ControlTemplate.Triggers>
              <DataTrigger Binding="{Binding IsModified, RelativeSource={RelativeSource Self}}" Value="True">
                <Setter TargetName="ModifiedDot" Property="Visibility" Value="Visible"/>
              </DataTrigger>
              <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{DynamicResource CanvasButtonHover}"/></Trigger>
              <Trigger Property="IsPressed" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{DynamicResource CanvasPressed}"/></Trigger>
              <Trigger Property="IsDefaulted" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="{DynamicResource CanvasAccent}"/></Trigger>
              <Trigger Property="IsEnabled" Value="False"><Setter TargetName="TileChrome" Property="Opacity" Value="0.45"/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
          <Style x:Key="CanvasPrimaryButton" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
            <Setter Property="Background" Value="{DynamicResource CanvasPrimary}"/>
            <Setter Property="Foreground" Value="{DynamicResource CanvasOnAccent}"/>
            <Setter Property="FontWeight" Value="SemiBold"/>
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="Button">
                <Border x:Name="Chrome" CornerRadius="6" Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}">
                  <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True"/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{DynamicResource CanvasPrimaryHover}"/></Trigger>
                  <Trigger Property="IsPressed" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{DynamicResource CanvasPrimaryPressed}"/></Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Chrome" Property="Opacity" Value="0.45"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value></Setter>
          </Style>
          <Style x:Key="CanvasDangerButton" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
            <Setter Property="Background" Value="{DynamicResource CanvasDanger}"/>
            <Setter Property="Foreground" Value="{DynamicResource CanvasOnAccent}"/>
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="Button">
                <Border x:Name="Chrome" CornerRadius="{DynamicResource CanvasButtonCornerRadius}" Background="{TemplateBinding Background}"
                        BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
                  <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{DynamicResource CanvasDangerHover}"/></Trigger>
                  <Trigger Property="IsPressed" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{DynamicResource CanvasDangerPressed}"/></Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Chrome" Property="Opacity" Value="0.45"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value></Setter>
          </Style>
          <Style x:Key="CanvasCaptionButton" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
            <Setter Property="Background" Value="{DynamicResource CanvasButton}"/>
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="Button">
                <Border x:Name="Chrome" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="7"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{DynamicResource CanvasButtonHover}"/></Trigger>
                  <Trigger Property="IsPressed" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{DynamicResource CanvasPressed}"/></Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Chrome" Property="Opacity" Value="0.45"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value></Setter>
          </Style>
          <Style x:Key="CanvasCloseCaptionButton" TargetType="Button" BasedOn="{StaticResource CanvasCaptionButton}">
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="Button">
                <Border x:Name="Chrome" Background="{TemplateBinding Background}" CornerRadius="7"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{DynamicResource CanvasCloseHover}"/><Setter Property="Foreground" Value="{DynamicResource CanvasCloseForeground}"/></Trigger>
                  <Trigger Property="IsPressed" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{DynamicResource CanvasCloseHover}"/><Setter Property="Foreground" Value="{DynamicResource CanvasCloseForeground}"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value></Setter>
          </Style>
          <Style TargetType="CheckBox" BasedOn="{StaticResource CanvasControlBase}">
            <Setter Property="MinHeight" Value="28"/>
            <Setter Property="Padding" Value="8,3,0,3"/>
            <Setter Property="Background" Value="{DynamicResource CanvasInput}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource CanvasBorder}"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="VerticalContentAlignment" Value="Center"/>
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="CheckBox">
                <Grid Background="Transparent">
                  <Grid.ColumnDefinitions><ColumnDefinition Width="18"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                  <Border x:Name="CheckBorder" Width="18" Height="18" VerticalAlignment="Center" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="4">
                    <Grid><Path x:Name="Check" Visibility="Collapsed" Data="M 2,7 L 6,11 L 13,3" Stroke="{DynamicResource CanvasAccent}" StrokeThickness="1.8"/>
                      <Border x:Name="Mixed" Visibility="Collapsed" Margin="3,7" Background="{DynamicResource CanvasAccent}" CornerRadius="1"/>
                    </Grid>
                  </Border>
                  <ContentPresenter Grid.Column="1" Margin="{TemplateBinding Padding}" RecognizesAccessKey="True" VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
                </Grid>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsChecked" Value="True"><Setter TargetName="Check" Property="Visibility" Value="Visible"/></Trigger>
                  <Trigger Property="IsChecked" Value="{x:Null}"><Setter TargetName="Mixed" Property="Visibility" Value="Visible"/></Trigger>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="CheckBorder" Property="BorderBrush" Value="{DynamicResource CanvasAccent}"/></Trigger>
                  <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="CheckBorder" Property="BorderBrush" Value="{DynamicResource CanvasAccent}"/></Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value></Setter>
          </Style>
          <Style x:Key="CanvasScrollPageButton" TargetType="RepeatButton">
            <Setter Property="Focusable" Value="False"/><Setter Property="IsTabStop" Value="False"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style x:Key="CanvasScrollThumb" TargetType="Thumb">
            <Setter Property="MinHeight" Value="20"/>
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="Thumb">
                <Border x:Name="Thumb" Background="{DynamicResource CanvasMuted}" Opacity="0.6" CornerRadius="4" Margin="3"/>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Thumb" Property="Opacity" Value="0.85"/></Trigger>
                  <Trigger Property="IsDragging" Value="True"><Setter TargetName="Thumb" Property="Background" Value="{DynamicResource CanvasAccent}"/><Setter TargetName="Thumb" Property="Opacity" Value="1"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value></Setter>
          </Style>
          <Style TargetType="ScrollBar">
            <Setter Property="Width" Value="12"/><Setter Property="MinWidth" Value="12"/>
            <Setter Property="Background" Value="Transparent"/><Setter Property="Focusable" Value="False"/>
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="ScrollBar">
                <Grid Background="{TemplateBinding Background}">
                  <Track x:Name="PART_Track" Orientation="{TemplateBinding Orientation}" IsDirectionReversed="True"
                         Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" ViewportSize="{TemplateBinding ViewportSize}"
                         Value="{Binding Value, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
                    <Track.DecreaseRepeatButton><RepeatButton x:Name="Decrease" Style="{StaticResource CanvasScrollPageButton}" Command="{x:Static ScrollBar.PageUpCommand}"/></Track.DecreaseRepeatButton>
                    <Track.Thumb><Thumb x:Name="ScrollThumb" Style="{StaticResource CanvasScrollThumb}"/></Track.Thumb>
                    <Track.IncreaseRepeatButton><RepeatButton x:Name="Increase" Style="{StaticResource CanvasScrollPageButton}" Command="{x:Static ScrollBar.PageDownCommand}"/></Track.IncreaseRepeatButton>
                  </Track>
                </Grid>
                <ControlTemplate.Triggers>
                  <Trigger Property="Orientation" Value="Horizontal">
                    <Setter TargetName="PART_Track" Property="IsDirectionReversed" Value="False"/>
                    <Setter TargetName="ScrollThumb" Property="MinWidth" Value="20"/><Setter TargetName="ScrollThumb" Property="MinHeight" Value="0"/>
                    <Setter TargetName="Decrease" Property="Command" Value="{x:Static ScrollBar.PageLeftCommand}"/><Setter TargetName="Increase" Property="Command" Value="{x:Static ScrollBar.PageRightCommand}"/>
                  </Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value></Setter>
            <Style.Triggers><Trigger Property="Orientation" Value="Horizontal"><Setter Property="Width" Value="Auto"/><Setter Property="Height" Value="12"/><Setter Property="MinHeight" Value="12"/><Setter Property="MinWidth" Value="0"/></Trigger></Style.Triggers>
          </Style>
          <Style TargetType="ScrollViewer">
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="ScrollViewer">
                <Grid Background="{TemplateBinding Background}">
                  <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                  <Grid.RowDefinitions><RowDefinition Height="*"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
                  <ScrollContentPresenter x:Name="PART_ScrollContentPresenter" Margin="{TemplateBinding Padding}" Content="{TemplateBinding Content}"
                                          ContentTemplate="{TemplateBinding ContentTemplate}" CanContentScroll="{TemplateBinding CanContentScroll}"/>
                  <ScrollBar x:Name="PART_VerticalScrollBar" Grid.Column="1" Orientation="Vertical" Maximum="{TemplateBinding ScrollableHeight}"
                             ViewportSize="{TemplateBinding ViewportHeight}" Value="{TemplateBinding VerticalOffset}" Visibility="{TemplateBinding ComputedVerticalScrollBarVisibility}"/>
                  <ScrollBar x:Name="PART_HorizontalScrollBar" Grid.Row="1" Orientation="Horizontal" Maximum="{TemplateBinding ScrollableWidth}"
                             ViewportSize="{TemplateBinding ViewportWidth}" Value="{TemplateBinding HorizontalOffset}" Visibility="{TemplateBinding ComputedHorizontalScrollBarVisibility}"/>
                </Grid>
              </ControlTemplate>
            </Setter.Value></Setter>
          </Style>
          <Style x:Key="CanvasSliderPageButton" TargetType="RepeatButton">
            <Setter Property="Focusable" Value="False"/><Setter Property="IsTabStop" Value="False"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RepeatButton"><Border Height="4" Background="Transparent"/></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="Slider" BasedOn="{StaticResource CanvasControlBase}">
            <Setter Property="MinHeight" Value="24"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="Slider">
                <Grid x:Name="SliderRoot" Background="Transparent">
                  <Border x:Name="Rail" Height="4" Background="{DynamicResource CanvasBorder}" CornerRadius="2" VerticalAlignment="Center"/>
                  <Track x:Name="PART_Track" Orientation="{TemplateBinding Orientation}" IsDirectionReversed="{TemplateBinding IsDirectionReversed}">
                    <Track.DecreaseRepeatButton><RepeatButton Style="{StaticResource CanvasSliderPageButton}" Command="{x:Static Slider.DecreaseLarge}"/></Track.DecreaseRepeatButton>
                    <Track.Thumb><Thumb Width="14" Height="14"><Thumb.Template><ControlTemplate TargetType="Thumb"><Border x:Name="Handle" CornerRadius="7" Background="{DynamicResource CanvasAccent}" BorderBrush="{DynamicResource CanvasInput}" BorderThickness="2"/><ControlTemplate.Triggers><Trigger Property="IsDragging" Value="True"><Setter TargetName="Handle" Property="BorderBrush" Value="{DynamicResource CanvasText}"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
                    <Track.IncreaseRepeatButton><RepeatButton Style="{StaticResource CanvasSliderPageButton}" Command="{x:Static Slider.IncreaseLarge}"/></Track.IncreaseRepeatButton>
                  </Track>
                </Grid>
                <ControlTemplate.Triggers>
                  <Trigger Property="Orientation" Value="Vertical"><Setter TargetName="Rail" Property="Width" Value="4"/><Setter TargetName="Rail" Property="Height" Value="Auto"/><Setter TargetName="Rail" Property="VerticalAlignment" Value="Stretch"/></Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
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
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
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
                <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}" CornerRadius="8" Grid.IsSharedSizeScope="True">
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
                <Grid>
                <Border x:Name="Chrome" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}" CornerRadius="4">
                  <Grid>
                    <Grid.ColumnDefinitions><ColumnDefinition Width="18"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto" SharedSizeGroup="Gesture"/><ColumnDefinition Width="12"/></Grid.ColumnDefinitions>
                    <ContentPresenter x:Name="Icon" Content="{TemplateBinding Icon}" VerticalAlignment="Center"/>
                    <Path x:Name="Check" Visibility="Collapsed" Data="M 2 7 L 6 11 L 13 3" Stroke="{DynamicResource CanvasAccent}" StrokeThickness="1.8" VerticalAlignment="Center"/>
                    <ContentPresenter Grid.Column="1" Margin="6,0,12,0" ContentSource="Header" RecognizesAccessKey="True" VerticalAlignment="Center"/>
                    <TextBlock Grid.Column="2" HorizontalAlignment="Right" Text="{TemplateBinding InputGestureText}" Foreground="{DynamicResource CanvasMuted}" FontSize="12" VerticalAlignment="Center"/>
                    <Path x:Name="SubmenuArrow" Grid.Column="3" Data="M 0,0 L 4,4 L 0,8" Stroke="{DynamicResource CanvasMuted}" StrokeThickness="1.3" HorizontalAlignment="Right" VerticalAlignment="Center" Visibility="Collapsed"/>
                  </Grid>
                </Border>
                <Popup x:Name="PART_Popup" Placement="Right" PlacementTarget="{Binding ElementName=Chrome}" HorizontalOffset="-2" VerticalOffset="-4"
                       IsOpen="{Binding IsSubmenuOpen, RelativeSource={RelativeSource TemplatedParent}}" AllowsTransparency="True" Focusable="False" PopupAnimation="None">
                  <Border Background="{DynamicResource CanvasInput}" BorderBrush="{DynamicResource CanvasBorder}" BorderThickness="1" CornerRadius="8" Padding="4" MinWidth="180" Grid.IsSharedSizeScope="True">
                    <ScrollViewer CanContentScroll="True" VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled"><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Cycle"/></ScrollViewer>
                  </Border>
                </Popup>
                </Grid>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{DynamicResource CanvasHover}"/></Trigger>
                  <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="{DynamicResource CanvasAccent}"/></Trigger>
                  <Trigger Property="IsChecked" Value="True"><Setter TargetName="Check" Property="Visibility" Value="Visible"/><Setter TargetName="Icon" Property="Visibility" Value="Collapsed"/></Trigger>
                  <Trigger Property="HasItems" Value="True"><Setter TargetName="SubmenuArrow" Property="Visibility" Value="Visible"/></Trigger>
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
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
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
