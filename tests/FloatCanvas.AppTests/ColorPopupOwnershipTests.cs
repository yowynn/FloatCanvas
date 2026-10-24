using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Text.Json;
using System.Windows.Threading;
using FloatCanvas.App;

internal static class ColorPopupOwnershipTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Preview(string output, bool overlay)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        ApplicationPaths.Configure(["--profile", Path.Combine(output, "profile")]);
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        var owner = new Window { Title = "浮绘颜色窗口独立验收 · " + (overlay ? "增强呈现" : "普通呈现"),
            Width = 800, Height = 400, Left = 140, Top = 120, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        nint handle = new WindowInteropHelper(owner).EnsureHandle();
        using var scope = overlay ? new OverlayWindowScope(handle, () => false) : null;
        var root = new Canvas { Background = Brushes.WhiteSmoke };
        root.Children.Add(new TextBlock { Text = "独立颜色弹窗验收：拖动颜色标题；点击右侧输入框或按钮。", Margin = new Thickness(24, 20, 0, 0) });
        var field = new ColorField(ColorRole.Foreground, new ColorLibrary(Path.Combine(output, "profile", "colors.json"))) { Width = 360 };
        var display = (PopupHandle)typeof(ColorField).GetField("popupDisplay", Private)!.GetValue(field)!;
        var picker = (ColorPickerPanel)typeof(ColorField).GetField("picker", Private)!.GetValue(field)!;
        var ownerProperty = (DependencyProperty)typeof(PopupChildDialog).GetField("OwnerProperty", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Canvas.SetLeft(field, 24); Canvas.SetTop(field, 65); root.Children.Add(field);
        var input = new TextBox { Text = "父面板输入框", Width = 270, Height = 34 };
        System.Windows.Automation.AutomationProperties.SetName(input, "父面板输入框");
        Canvas.SetLeft(input, 470); Canvas.SetTop(input, 65); root.Children.Add(input);
        var parentButton = new Button { Content = "父面板按钮：0", Width = 180, Height = 34 };
        Canvas.SetLeft(parentButton, 470); Canvas.SetTop(parentButton, 115); root.Children.Add(parentButton);
        var status = new TextBlock { Width = 285, TextWrapping = TextWrapping.Wrap };
        Canvas.SetLeft(status, 470); Canvas.SetTop(status, 175); root.Children.Add(status);
        owner.Content = root;
        int parentClicks = 0, popupCloses = 0;
        parentButton.Click += (_, _) => { parentButton.Content = "父面板按钮：" + ++parentClicks; Snapshot(); };
        field.PickerClosed += () =>
        {
            popupCloses++;
            File.AppendAllText(Path.Combine(output, "close-traces.log"), DateTimeOffset.Now + "\n" + Environment.StackTrace + "\n\n");
            Snapshot();
        };
        field.ValueChanged += _ => Snapshot();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => Snapshot();
        owner.Loaded += (_, _) => { field.OpenPicker(); timer.Start(); Snapshot(); };
        owner.Closed += (_, _) => { timer.Stop(); field.ClosePicker(); Snapshot(); app.Shutdown(); };
        app.Run(owner);
        void Snapshot()
        {
            var source = PresentationSource.FromVisual((Visual)field.PickerRoot) as HwndSource;
            NativeRect? bounds = source is not null && GetWindowRect(source.Handle, out var rect) ? rect : null;
            var state = new { backend = overlay ? "overlay" : "desktop", open = field.IsPickerOpen, parentClicks, popupCloses,
                staysOpen = display.StaysOpen, pickerResolvesAttachedOwner = ReferenceEquals(picker.GetValue(ownerProperty), display.Key),
                mouseLeft = Mouse.LeftButton.ToString(), visibleDialogs = app.Windows.Cast<Window>().Where(window => window.IsVisible && !ReferenceEquals(window, owner)).Select(window => window.Title).ToArray(),
                focus = ReferenceEquals(Keyboard.FocusedElement, input) ? "parent-text" : Keyboard.FocusedElement?.GetType().Name,
                inputText = input.Text, color = field.Value.ToString("X8"),
                popup = bounds is { } b ? new { left = b.Left, top = b.Top, width = b.Right - b.Left, height = b.Bottom - b.Top } : null };
            status.Text = (field.IsPickerOpen ? "颜色窗口打开" : "颜色窗口已关闭") + "\n父面板按钮次数：" + parentClicks + "\n焦点：" + state.focus;
            File.WriteAllText(Path.Combine(output, "state.json"), JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        var owner = new Grid { Width = 600, Height = 400 };
        var trigger = new Button { Width = 120, Height = 32, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var outside = new Button { Margin = new Thickness(200, 20, 0, 0), Width = 80, Height = 32, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        owner.Children.Add(trigger); owner.Children.Add(outside);
        using var ownerSource = new HwndSource(new HwndSourceParameters("FloatCanvas popup owner test") { Width = 600, Height = 400, WindowStyle = unchecked((int)0x80000000) });
        ownerSource.RootVisual = owner; Layout(owner);
        using var scope = new OverlayWindowScope(ownerSource.Handle, () => false);
        var content = new Border { Child = new TextBox { Text = "inside", Width = 180, Height = 40 }, Background = Brushes.White };
        var popupOwner = new OverlayPopup { StaysOpen = false, IsOpen = true };
        PopupChildDialog.Attach(content, popupOwner);
        int closed = 0, parentPresses = 0; bool closedBeforeParent = false, isOpen = true;
        outside.PreviewMouseDown += (_, _) => { parentPresses++; closedBeforeParent = closed == 1; };
        using var popup = new OverlayPopupWindow(content, trigger, PlacementMode.Bottom, 0, 4, () => { closed++; isOpen = false; }, closeOnOutside: () => isOpen && !popupOwner.StaysOpen, targetPressToggles: true);
        check("popup owner fixture leaves native surfaces hidden", !IsWindowVisible(ownerSource.Handle) && !IsWindowVisible(((HwndSource)PresentationSource.FromVisual(content)).Handle));
        var sourceLess = Press(outside);
        check("parent click enters input staging without OriginalSource", sourceLess);
        check("a parent-panel first click closes the child before the parent handles it", closed == 1 && closedBeforeParent && parentPresses == 1);
        closed = 0; isOpen = true; Press((UIElement)content.Child);
        check("clicking a color popup editor preserves its popup", closed == 0);
        using (var retention = PopupChildDialog.RetainFor((UIElement)content.Child))
        {
            check("real child-dialog retention resolves the popup owner through its content", retention is not null && popupOwner.StaysOpen);
            Press(outside);
            check("a retained popup survives the child-dialog input stage", closed == 0);
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, ownerSource, Environment.TickCount, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = outside };
            InputManager.Current.ProcessInput(escape);
            check("a retained child dialog receives Escape without closing its color parent", closed == 0 && !escape.Handled);
        }
        check("child-dialog retention restores the original outside-click policy", !popupOwner.StaysOpen);
        Press(outside);
        check("outside dismissal resumes after child-dialog retention ends", closed == 1);
        closed = 0; isOpen = true; popupOwner.StaysOpen = true;
        var dragEscape = new KeyEventArgs(Keyboard.PrimaryDevice, ownerSource, Environment.TickCount, Key.Escape)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = content.Child };
        InputManager.Current.ProcessInput(dragEscape);
        check("temporary drag retention still lets Escape close the color popup", closed == 1 && dragEscape.Handled);
        popupOwner.StaysOpen = false;
        closed = 0; isOpen = true; Press(trigger);
        check("pressing the original color trigger leaves its toggle click in control", closed == 0);
        int menuClosed = 0;
        using var menu = new OverlayPopupWindow(new Border { Child = new TextBlock { Text = "menu" } }, trigger, PlacementMode.Bottom, 0, 4, () => menuClosed++);
        Press(trigger);
        check("ordinary menus still close when their placement target is pressed", menuClosed > 0);
        ColorActionBubbling(owner, check);
        ownerSource.RootVisual = null;
    }
    private static void ColorActionBubbling(Grid owner, Action<string, bool> check)
    {
        var field = new ColorField(ColorRole.Foreground, new ColorLibrary(Path.Combine(ApplicationPaths.ProfileDirectory, "popup-bubbling-colors.json")));
        owner.Children.Add(field); Layout(owner);
        var display = (PopupHandle)typeof(ColorField).GetField("popupDisplay", Private)!.GetValue(field)!;
        var popup = (OverlayPopup)display.Key;
        var trigger = (Button)typeof(ColorField).GetField("button", Private)!.GetValue(field)!;
        var content = (Border)popup.Child!; var original = content.Child;
        var action = new Button { Content = "Nested color action" }; content.Child = action;
        int routed = 0;
        trigger.AddHandler(Button.ClickEvent, new RoutedEventHandler((_, e) =>
        { if (ReferenceEquals(e.OriginalSource, action)) routed++; }), true);
        // The routed-event path is built before handlers run. Detach only its
        // presentation here so a regression cannot show a native popup in this fixture.
        action.Click += (_, _) => popup.Child = null;
        try
        {
            foreach (bool open in new[] { false, true })
            {
                popup.Child = null; popup.IsOpen = open; popup.Child = content;
                int before = routed;
                action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, action));
                check("nested color action really bubbles through the overlay placement target", routed == before + 1);
                check(open ? "returning from a nested color action leaves its owner popup open" :
                    "a color action cannot reopen an already-closed owner popup", field.IsPickerOpen == open);
            }
            popup.Child = null; popup.IsOpen = false;
            trigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, trigger));
            check("the actual color trigger still toggles its own picker", field.IsPickerOpen);
        }
        finally
        {
            field.ClosePicker(); content.Child = original; popup.Child = content; owner.Children.Remove(field);
        }
    }
    private static bool Press(UIElement target)
    {
        var factory = typeof(ToolOptionsInputFixture).GetMethod("ControlledMouse", BindingFlags.Static | BindingFlags.NonPublic)!;
        var mouse = (MouseDevice)factory.Invoke(null, [target])!;
        try
        {
            var source = (HwndSource)PresentationSource.FromVisual(target);
            typeof(MouseDevice).GetField("_inputSource", Private)!.SetValue(mouse, source);
            var point = target.TranslatePoint(new Point(4, 4), (UIElement)source.RootVisual);
            typeof(MouseDevice).GetField("_lastPosition", Private)!.SetValue(mouse, source.CompositionTarget.TransformToDevice.Transform(point));
            var press = new MouseButtonEventArgs(mouse, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent };
            bool sourceLess = press.OriginalSource is null;
            InputManager.Current.ProcessInput(press);
            return sourceLess;
        }
        finally { ToolOptionsInputFixture.ReleaseControlledMouse(mouse); }
    }
    private static void Layout(FrameworkElement root)
    { root.Measure(new Size(800, 800)); root.Arrange(new Rect(root.DesiredSize)); root.UpdateLayout(); }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct NativeRect{public int Left,Top,Right,Bottom;}
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle,out NativeRect rect);
}
