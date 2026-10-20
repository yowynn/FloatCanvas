using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Shell;
using System.Windows.Threading;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>One floating surface for tool panels and dialogs, with native movement and sizing.</summary>
internal static class AppWindowChrome
{
    internal const double CaptionHeight = 36;
    internal const double PanelCornerRadius = 12;
    private static readonly DependencyProperty AppliedProperty = DependencyProperty.RegisterAttached(
        "Applied", typeof(bool), typeof(AppWindowChrome), new PropertyMetadata(false));
    private static ControlTemplate? template;

    public static void Apply(Window window) => ApplyCore(window);
    public static void ApplyDialog(Window window) => ApplyCore(window);
    public static void SetHeaderDetail(Window window, string detail) => window.Resources["CanvasWindowDetail"] = detail;
    public static void SetHeaderActions(Window window, FrameworkElement actions) => window.Resources["CanvasWindowActions"] = actions;
    public static void SetHelp(Window window, string title, string text)
    {
        window.Resources["CanvasWindowHelpTitle"] = title;
        window.Resources["CanvasWindowHelpText"] = text;
        window.Resources["CanvasWindowHelpVisibility"] = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void ApplyCore(Window window)
    {
        SessionWindowLayout.TrackOwned(window);
        SessionWindowCoordinator.TrackOwned(window);
        window.Dispatcher.VerifyAccess();
        window.WindowStyle = WindowStyle.None;
        window.UseLayoutRounding = true;
        window.SnapsToDevicePixels = true;
        window.SetResourceReference(Window.BackgroundProperty, "CanvasSurface");
        window.SetResourceReference(Window.ForegroundProperty, "CanvasText");
        window.Resources["CanvasWindowClose"] = L.Get("Window.Close");
        if (!window.Resources.Contains("CanvasWindowDetail")) SetHeaderDetail(window, "");
        if (!window.Resources.Contains("CanvasWindowHelpVisibility")) SetHelp(window, "", "");
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = CaptionHeight,
            ResizeBorderThickness = new Thickness(CanResize(window) ? 6 : 0),
            // A WPF region, even a rectangular one, prevents native DWM rounding.
            GlassFrameThickness = new Thickness(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ? 1 : 0),
            CornerRadius = new CornerRadius(PanelCornerRadius),
            UseAeroCaptionButtons = false,
            NonClientFrameEdges = NonClientFrameEdges.None
        });
        window.Template = template ??= CreateTemplate();
        if ((bool)window.GetValue(AppliedProperty)) return;
        window.SetValue(AppliedProperty, true);
        window.CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (_, _) => SessionWindowCoordinator.Close(window)));
        window.CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand, BlockCommand, DisableCommand));
        window.CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand, BlockCommand, DisableCommand));
        window.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, window.Template.FindName("Help", window))) return;
            var popup = (Popup)window.Template.FindName("HelpPopup", window);
            popup.CustomPopupPlacementCallback = (size, target, _) =>
            [
                new(new Point(target.Width - size.Width, target.Height + 4), PopupPrimaryAxis.Vertical),
                new(new Point(target.Width - size.Width, -size.Height - 4), PopupPrimaryAxis.Vertical)
            ];
            popup.IsOpen = !popup.IsOpen;
            e.Handled = true;
        }));
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && TryCloseHelp(window)) e.Handled = true;
        };
        window.StateChanged += (_, _) =>
        {
            if (window.WindowState == WindowState.Normal) return;
            window.WindowState = WindowState.Normal;
            EnsureVisibleBounds(window);
        };
        window.IsVisibleChanged += (_, _) =>
        {
            if (window.IsVisible) window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => EnsureVisibleBounds(window));
            else if (window.Template.FindName("HelpPopup", window) is Popup popup) popup.IsOpen = false;
        };
        if (new WindowInteropHelper(window).Handle != 0) AttachWindowBounds(window);
        else window.SourceInitialized += (_, _) => AttachWindowBounds(window);
    }

    internal static bool TryCloseHelp(Window window)
    {
        if (window.Template?.FindName("HelpPopup", window) is not Popup { IsOpen: true } popup) return false;
        popup.IsOpen = false; return true;
    }

    private static ControlTemplate CreateTemplate()
    {
        // DWM owns the Windows 11 outline and chooses its radius. A second, fixed-radius
        // client stroke exposes the backplate between the two arcs at the corners.
        // Keep the one-DIP inset so removing that stroke does not move panel content.
        bool nativeFrame = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
        return (ControlTemplate)XamlReader.Parse(TemplateMarkup
            .Replace("__WINDOW_EDGE_BRUSH__", nativeFrame ? "Transparent" : "{DynamicResource CanvasBorder}")
            .Replace("__WINDOW_EDGE_RADIUS__", nativeFrame ? "0" : "12"));
    }

    private static bool CanResize(Window window) => window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;
    private static void BlockCommand(object sender, ExecutedRoutedEventArgs e) => e.Handled = true;
    private static void DisableCommand(object sender, CanExecuteRoutedEventArgs e) { e.CanExecute = false; e.Handled = true; }

    private static void AttachWindowBounds(Window window)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(PanelWindowProc);
        _ = SetWindowLongPtr(handle, -16, (nint)((long)GetWindowLongPtr(handle, -16) & ~0x00030000L));
        nint menu = GetSystemMenu(handle, false);
        foreach (uint command in new uint[] { 0xF020, 0xF030, 0xF120 }) _ = DeleteMenu(menu, command, 0);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            int rounding = 2; // DWMWCP_ROUND; Windows chooses the native radius.
            _ = DwmSetWindowAttribute(handle, 33, ref rounding, sizeof(int));
        }
    }

    internal static void EnsureVisibleBounds(Window window)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || !window.IsVisible || !GetWindowRect(handle, out var rect)) return;
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor)) return;
        int width = Math.Min(rect.Right - rect.Left, monitor.Work.Right - monitor.Work.Left);
        int height = Math.Min(rect.Bottom - rect.Top, monitor.Work.Bottom - monitor.Work.Top);
        int left = Math.Clamp(rect.Left, monitor.Work.Left, monitor.Work.Right - width);
        int top = Math.Clamp(rect.Top, monitor.Work.Top, monitor.Work.Bottom - height);
        if (left != rect.Left || top != rect.Top || width != rect.Right - rect.Left || height != rect.Bottom - rect.Top)
            _ = SetWindowPos(handle, 0, left, top, width, height, 0x0014); // NOZORDER | NOACTIVATE
    }

    private static nint PanelWindowProc(nint handle, int message, nint wParam, nint lParam, ref bool handled)
    {
        if ((message == 0x00A3 && wParam == 2) ||
            (message == 0x0112 && ((long)wParam & 0xFFF0) is 0xF020 or 0xF030))
        {
            handled = true;
            return 0;
        }
        if (message == 0x007C && (int)wParam == -16 && lParam != 0)
        {
            var style = Marshal.PtrToStructure<StyleStruct>(lParam);
            style.New &= ~0x00030000U;
            Marshal.StructureToPtr(style, lParam, false);
        }
        if (message is not (0x0024 or 0x007E or 0x02E0 or 0x001A)) return 0;
        if (message == 0x001A && wParam != 0x002F) return 0; // SPI_SETWORKAREA, including a relocated taskbar
        if (message != 0x0024)
        {
            if (HwndSource.FromHwnd(handle)?.RootVisual is Window window)
                window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => EnsureVisibleBounds(window));
            return 0;
        }
        if (lParam == 0) return 0;
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor)) return 0;
        var bounds = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        bounds.MaxPosition = new NativePoint(monitor.Work.Left - monitor.Monitor.Left, monitor.Work.Top - monitor.Monitor.Top);
        bounds.MaxSize = new NativePoint(monitor.Work.Right - monitor.Work.Left, monitor.Work.Bottom - monitor.Work.Top);
        Marshal.StructureToPtr(bounds, lParam, false);
        return 0; // WPF still owns normal Min/Max track sizes.
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public int Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct StyleStruct { public uint Old, New; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint handle, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint handle, out NativeRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint handle, int index, nint value);
    [DllImport("user32.dll")] private static extern nint GetSystemMenu(nint handle, [MarshalAs(UnmanagedType.Bool)] bool revert);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteMenu(nint menu, uint position, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint handle, int attribute, ref int value, int size);

    private const string TemplateMarkup = """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:shell="clr-namespace:System.Windows.Shell;assembly=PresentationFramework" TargetType="Window">
          <Border x:Name="WindowBorder" Background="{TemplateBinding Background}" BorderBrush="__WINDOW_EDGE_BRUSH__" BorderThickness="1" CornerRadius="__WINDOW_EDGE_RADIUS__">
            <Grid>
              <Grid.RowDefinitions><RowDefinition Height="36"/><RowDefinition Height="*"/></Grid.RowDefinitions>
              <Grid x:Name="TitleBar" Margin="12,0,6,0" TextElement.Foreground="{TemplateBinding Foreground}">
                <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                <DockPanel Margin="0,0,4,0" IsHitTestVisible="False">
                  <TextBlock x:Name="HeaderDetail" DockPanel.Dock="Right" MaxWidth="160" Text="{DynamicResource CanvasWindowDetail}" Margin="8,0,0,0" FontSize="11" Foreground="{DynamicResource CanvasMuted}" VerticalAlignment="Center" TextTrimming="CharacterEllipsis"/>
                  <TextBlock Text="{TemplateBinding Title}" VerticalAlignment="Center" FontSize="13" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/>
                </DockPanel>
                <StackPanel Grid.Column="1" Orientation="Horizontal" VerticalAlignment="Center">
                  <ContentPresenter Content="{DynamicResource CanvasWindowActions}" shell:WindowChrome.IsHitTestVisibleInChrome="True"/>
                  <Button x:Name="Help" Style="{DynamicResource CanvasCaptionButton}" Width="28" Height="28" MinWidth="0" MinHeight="0" Padding="0" Margin="1,0" BorderThickness="0" shell:WindowChrome.IsHitTestVisibleInChrome="True"
                          Visibility="{DynamicResource CanvasWindowHelpVisibility}" ToolTip="{DynamicResource CanvasWindowHelpTitle}" AutomationProperties.Name="{DynamicResource CanvasWindowHelpTitle}">
                    <Path Width="16" Height="16" Data="M 8,1 A 7,7 0 1 1 8,15 A 7,7 0 1 1 8,1 M 6,5.5 C 6,3.5 10,3.5 10,5.5 C 10,7 8,7 8,9 M 8,11.5 L 8,11.6" Stroke="{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}" StrokeThickness="1.5" StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
                  </Button>
                  <Button x:Name="Close" Style="{DynamicResource CanvasCloseCaptionButton}" Width="28" Height="28" MinWidth="0" MinHeight="0" Padding="0" Margin="1,0" BorderThickness="0" shell:WindowChrome.IsHitTestVisibleInChrome="True"
                          Command="{x:Static SystemCommands.CloseWindowCommand}" CommandTarget="{Binding RelativeSource={RelativeSource TemplatedParent}}" ToolTip="{DynamicResource CanvasWindowClose}" AutomationProperties.Name="{DynamicResource CanvasWindowClose}" AutomationProperties.AccessKey="Alt+F4">
                    <Path Width="16" Height="16" Data="M 3,3 L 13,13 M 13,3 L 3,13" Stroke="{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}" StrokeThickness="1.5" StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
                  </Button>
                </StackPanel>
              </Grid>
              <AdornerDecorator Grid.Row="1"><ContentPresenter x:Name="WindowContent" ContentSource="Content"/></AdornerDecorator>
              <Border x:Name="ResizeGrip" Grid.RowSpan="2" Width="20" Height="20" Background="Transparent" HorizontalAlignment="Right" VerticalAlignment="Bottom" Cursor="SizeNWSE" shell:WindowChrome.ResizeGripDirection="BottomRight">
                <Path Data="M 1,11 L 11,1 M 6,11 L 11,6" Width="12" Height="12" Margin="0,0,3,3" HorizontalAlignment="Right" VerticalAlignment="Bottom" IsHitTestVisible="False" Stroke="{DynamicResource CanvasMuted}" StrokeThickness="1" Opacity="0.65"/>
              </Border>
              <Popup x:Name="HelpPopup" Placement="Custom" PlacementTarget="{Binding ElementName=Help}" AllowsTransparency="True" StaysOpen="False" Focusable="False">
                <Border Background="{DynamicResource CanvasSurface}" BorderBrush="{DynamicResource CanvasBorder}" BorderThickness="1" CornerRadius="10" Padding="12" MaxWidth="280">
                  <StackPanel>
                    <TextBlock Text="{DynamicResource CanvasWindowHelpTitle}" FontWeight="SemiBold" FontSize="13" Foreground="{DynamicResource CanvasText}" Margin="0,0,0,8"/>
                    <TextBlock Text="{DynamicResource CanvasWindowHelpText}" FontSize="12" Foreground="{DynamicResource CanvasText}" TextWrapping="Wrap"/>
                  </StackPanel>
                </Border>
              </Popup>
            </Grid>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="ResizeMode" Value="NoResize"><Setter TargetName="ResizeGrip" Property="Visibility" Value="Collapsed"/></Trigger>
            <Trigger Property="ResizeMode" Value="CanMinimize"><Setter TargetName="ResizeGrip" Property="Visibility" Value="Collapsed"/></Trigger>
            <Trigger Property="IsActive" Value="False"><Setter TargetName="TitleBar" Property="TextElement.Foreground" Value="{DynamicResource CanvasMuted}"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """;
}
