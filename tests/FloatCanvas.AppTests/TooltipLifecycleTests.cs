using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;

internal static class TooltipLifecycleTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        TooltipLifecycle.Initialize();
        foreach (string theme in new[] { "light", "dark" })
        {
            AppTheme.Apply(theme);
            PickerAndHover(check, theme);
        }
        NativeAndOwnedPopups(check);
    }

    private static void PickerAndHover(Action<string, bool> check, string theme)
    {
        var root = new StackPanel();
        using var source = new HwndSource(new HwndSourceParameters("FloatCanvas.TooltipLifecycle")
        { PositionX = -10000, PositionY = -10000, Width = 500, Height = 400, WindowStyle = unchecked((int)0x80000000) });
        source.RootVisual = root;
        using var scope = new OverlayWindowScope(source.Handle, () => false);
        var picker = new GridCountPicker(() => (4, 3)); root.Children.Add(picker);
        var hint = new ToolTip { Content = "Pick column and row counts" }; picker.ToolTip = hint;
        Layout(root);
        try
        {
            picker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            check($"{theme}: production grid picker opens away from the pointer", picker.IsPickerOpen && !picker.IsMouseOver);
            HintEvent(picker); Pump();
            check($"{theme}: delayed hint cannot cover the open grid picker", !OverlayTransientControls.IsHintOpen(hint));
            SetHover(picker, true); HintEvent(picker);
            check($"{theme}: pointer remaining on the open trigger cannot show a hint either", !OverlayTransientControls.IsHintOpen(hint));
            picker.ClosePicker(); Pump(); SetHover(picker, true); HintEvent(picker);
            check($"{theme}: closing the grid picker restores ordinary hover hints", OverlayTransientControls.IsHintOpen(hint));
            int closed = 0; hint.Closed += (_, _) => closed++;
            picker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            check($"{theme}: opening grid picker dismisses an existing hint exactly once", picker.IsPickerOpen && !OverlayTransientControls.IsHintOpen(hint) && closed == 1);
            var inner = Descendants(picker.PickerContent).OfType<Button>().First();
            inner.ToolTip = new ToolTip { Content = "Child control" }; SetHover(inner, true); HintEvent(inner);
            check($"{theme}: popup content retains its own hover hints", OverlayTransientControls.IsHintOpen((ToolTip)inner.ToolTip));
            OverlayTransientControls.CloseHint((ToolTip)inner.ToolTip);
            picker.ClosePicker(); SetHover(picker, false); HintEvent(picker);
            check($"{theme}: returning focus to the trigger does not restart its hint", !OverlayTransientControls.IsHintOpen(hint));
            foreach (Control control in new Control[] { new Button(), new CheckBox(), new Slider(), new ComboBox(), new TextBox(), picker })
            {
                if (!ReferenceEquals(control, picker)) root.Children.Add(control);
                Layout(root);
                check($"{theme}: {control.GetType().Name} uses hover delay and disables keyboard hints",
                    ToolTipService.GetShowsToolTipOnKeyboardFocus(control) == false && ToolTipService.GetInitialShowDelay(control) == 700);
            }
            SetHover(picker, true); HintEvent(picker);
            check($"{theme}: enhanced presenter still displays a valid hover", OverlayTransientControls.IsHintOpen(hint));
            HintEvent(picker, opening: false);
            check($"{theme}: WPF tooltip closing also closes the enhanced presenter", !OverlayTransientControls.IsHintOpen(hint));
            HintEvent(picker); SetHover(picker, false);
            picker.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
            check($"{theme}: leaving the trigger closes the hint", !OverlayTransientControls.IsHintOpen(hint));
            HintEvent(picker);
            check($"{theme}: a late request after leave stays closed", !OverlayTransientControls.IsHintOpen(hint));
            SetHover(picker, true); HintEvent(picker); picker.Visibility = Visibility.Collapsed;
            check($"{theme}: hiding the trigger closes its hint", !OverlayTransientControls.IsHintOpen(hint));
            picker.Visibility = Visibility.Visible; Layout(root); SetHover(picker, true);
            picker.IsEnabled = false; HintEvent(picker);
            check($"{theme}: disabled targets follow their normal tooltip policy", !OverlayTransientControls.IsHintOpen(hint));
            ToolTipService.SetShowOnDisabled(picker, true); HintEvent(picker);
            check($"{theme}: disabled-control reason tips remain available on hover", OverlayTransientControls.IsHintOpen(hint));
            OverlayTransientControls.CloseHint(hint); picker.IsEnabled = true;
            SetHover(root, true); root.ToolTip = new ToolTip { Content = "Ancestor" }; HintEvent(root);
            using (var first = TooltipLifecycle.Suppress(picker))
            {
                check($"{theme}: child popup suppression closes ancestor hints", !OverlayTransientControls.IsHintOpen((ToolTip)root.ToolTip));
                SetHover(picker, true);
                var second = TooltipLifecycle.Suppress(picker); first.Dispose(); HintEvent(picker);
                check($"{theme}: nested suppression survives one popup closing", !OverlayTransientControls.IsHintOpen(hint));
                second.Dispose(); HintEvent(picker);
                check($"{theme}: final suppression release restores hover", OverlayTransientControls.IsHintOpen(hint));
            }
            root.Children.Remove(picker); Pump();
            check($"{theme}: unloading the trigger closes the hint", !OverlayTransientControls.IsHintOpen(hint));
        }
        finally { SetHover(picker, false); picker.ClosePicker(); scope.ClosePopups(); }
    }

    private static void NativeAndOwnedPopups(Action<string, bool> check)
    {
        AppTheme.Apply("light");
        var root = new StackPanel(); var trigger = new Button { Content = "Trigger" }; root.Children.Add(trigger);
        var combo = new ComboBox { ItemsSource = new[] { "One", "Two" } }; root.Children.Add(combo);
        var owner = new Window { Content = root, Width = 400, Height = 300, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        var tip = new ToolTip { Content = "Native hover", PlacementTarget = trigger }; trigger.ToolTip = tip;
        owner.Show(); Pump();
        void ShowNative() { SetHover(trigger, true); tip.IsOpen = true; Pump(); }
        try
        {
            ShowNative(); check("native hover tooltip still opens", tip.IsOpen);
            SetHover(trigger, false); trigger.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent }); Pump();
            check("native tooltip closes after leaving", !tip.IsOpen);
            tip.IsOpen = true; Pump();
            check("late native tooltip opening is rejected without hover", !tip.IsOpen);
            var enabled = new CheckBox { IsChecked = true };
            BindingOperations.SetBinding(trigger, ToolTipService.IsEnabledProperty, new Binding(nameof(CheckBox.IsChecked)) { Source = enabled });
            foreach (bool owned in new[] { false, true })
            {
                ShowNative();
                var handle = new PopupHandle(new Popup { PlacementTarget = trigger, Child = new Button { Content = "Child" }, StaysOpen = true }, owned ? "Options" : null);
                try
                {
                    handle.IsOpen = true; Pump();
                    check($"{(owned ? "owned window" : "native popup")}: opening dismisses trigger tooltip", handle.IsOpen && !tip.IsOpen);
                    SetHover(trigger, true); check("open popup rejects delayed opening even with hover", HintEvent(trigger).Handled);
                }
                finally { handle.IsOpen = false; Pump(); }
                check("popup close preserves tooltip bindings and content", BindingOperations.IsDataBound(trigger, ToolTipService.IsEnabledProperty) && ReferenceEquals(trigger.ToolTip, tip));
                ShowNative(); check("popup close releases suppression", tip.IsOpen); tip.IsOpen = false;
            }
            var menu = new ContextMenu { PlacementTarget = trigger }; menu.Items.Add(new MenuItem { Header = "Command" });
            bool menuClosed = false; menu.Closed += (_, _) => menuClosed = true;
            ShowNative(); menu.IsOpen = true; Pump(); SetHover(trigger, true);
            check("context menu closes and suppresses its trigger hint", !tip.IsOpen && HintEvent(trigger).Handled);
            menu.IsOpen = false; WaitUntil(() => menuClosed); ShowNative(); check("context menu close restores hover eligibility", tip.IsOpen); tip.IsOpen = false;
            SetHover(combo, true); combo.ToolTip = new ToolTip { Content = "Choose", PlacementTarget = combo };
            var comboTip = (ToolTip)combo.ToolTip; comboTip.IsOpen = true; Pump();
            combo.IsDropDownOpen = true; Pump(); SetHover(combo, true);
            check("expanded dropdown closes and suppresses its hint", !comboTip.IsOpen && HintEvent(combo).Handled);
            combo.IsDropDownOpen = false; Pump(); SetHover(combo, true);
            check("dropdown close restores hover eligibility", TooltipLifecycle.CanShow(combo));
            enabled.IsChecked = false; SetHover(trigger, true); check("existing tooltip enable binding continues to work", HintEvent(trigger).Handled);
        }
        finally { SetHover(trigger, false); tip.IsOpen = false; owner.Close(); Pump(); }
    }

    internal static ToolTipEventArgs HintEvent(FrameworkElement target, bool opening = true)
    {
        var args = (ToolTipEventArgs)Activator.CreateInstance(typeof(ToolTipEventArgs), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, [opening], null)!;
        target.RaiseEvent(args); return args;
    }
    // Replay WPF's hover transition (including reverse-inherited state), without moving the physical pointer.
    internal static void SetHover(UIElement target, bool value) => typeof(MouseDevice)
        .GetMethod("ChangeMouseOver", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(Mouse.PrimaryDevice, [value ? target : null, Environment.TickCount]);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static void Layout(FrameworkElement root) { root.Measure(new Size(500, 400)); root.Arrange(new Rect(0, 0, 500, 400)); root.UpdateLayout(); Pump(); }
    private static void WaitUntil(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (ready() || DateTime.UtcNow >= deadline) { timer.Stop(); frame.Continue = false; } };
        timer.Start(); Dispatcher.PushFrame(frame); Pump();
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
