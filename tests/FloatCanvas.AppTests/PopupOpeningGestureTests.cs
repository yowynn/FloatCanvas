using System.Reflection;
using System.Reflection.Emit;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FloatCanvas.App;

/// <summary>Screen-edge popups that cover their anchor: the opening press never completes on an item.</summary>
/// <remarks>Uses a controlled WPF mouse device and a scripted cursor; no desktop input is injected.</remarks>
internal static class PopupOpeningGestureTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("dark");
        var previousCursor = OverlayPopupWindow.Cursor;
        var previousButtons = OverlayPopupWindow.IsButtonPressed;
        var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        using var owner = new HwndSource(new HwndSourceParameters("FloatCanvas.OpeningGesture")
        { Width = 10, Height = 10, PositionX = -10000, PositionY = -10000, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x080000A0 });
        using var scope = new OverlayWindowScope(owner.Handle, () => true);
        AppTheme.Apply("dark");
        var combo = new OverlayComboBox { Width = 220, MaxDropDownHeight = 600 };
        foreach (int number in Enumerable.Range(0, 20)) combo.Items.Add(new ComboBoxItem { Content = "Choice " + number, Height = 30 });
        combo.SelectedIndex = 5;
        var window = new Window { Content = combo, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None, ShowActivated = false,
            ShowInTaskbar = false, Opacity = 0, Topmost = true };
        OverlayWindowScope.Prepare(window);
        window.Show(); Pump();
        var dpi = VisualTreeHelper.GetDpi(window);
        // The anchor's bottom edge sits on the work area's bottom edge.
        window.Left = work.Left / dpi.DpiScaleX + 40;
        window.Top = work.Bottom / dpi.DpiScaleY - window.ActualHeight;
        Pump(); combo.ApplyTemplate();
        var anchor = (FrameworkElement)combo.Template.FindName("DropDownToggle", combo);
        Point anchorCenter = anchor.PointToScreen(new(anchor.ActualWidth / 2, anchor.ActualHeight / 2));
        Rect anchorBounds = new(anchor.PointToScreen(new()), anchor.PointToScreen(new(anchor.ActualWidth, anchor.ActualHeight)));
        Point cursor = anchorCenter;
        bool pressed = true;
        OverlayPopupWindow.Cursor = () => cursor;
        OverlayPopupWindow.IsButtonPressed = button => pressed && button == MouseButton.Left;
        try
        {
            // Opening press over the toggle (ClickMode=Press).
            combo.IsDropDownOpen = true; Pump();
            var popupWindow = OpenPopup(combo);
            Rect bounds = popupWindow.ScreenBounds;
            check("a bottom-edge dropdown flips above its anchor instead of being pushed over it",
                bounds.Bottom <= anchorBounds.Top + 1 && bounds.Height > 0);
            combo.IsDropDownOpen = false; Pump();

            // Press, drag onto an item and release is one gesture and still chooses it.
            combo.SelectedIndex = 0;
            cursor = anchorCenter; pressed = true;
            combo.IsDropDownOpen = true; Pump();
            popupWindow = OpenPopup(combo);
            var dragTarget = combo.Items.Cast<ComboBoxItem>().First(item => item.IsVisible && item != combo.SelectedItem);
            cursor = Center(dragTarget); pressed = false;
            Release(dragTarget, MouseButton.Left);
            check("pressing, dragging onto an item and releasing still chooses it in one gesture",
                !combo.IsDropDownOpen && ReferenceEquals(combo.SelectedItem, dragTarget));

            combo.SelectedIndex = 0;
            cursor = anchorCenter; pressed = false;
            combo.IsDropDownOpen = true; Pump();
            popupWindow = OpenPopup(combo);
            var plainTarget = combo.Items.Cast<ComboBoxItem>().First(item => item.IsVisible);
            cursor = Center(plainTarget);
            Release(plainTarget, MouseButton.Left);
            check("keyboard or programmatic opening does not swallow the first release", !combo.IsDropDownOpen && ReferenceEquals(combo.SelectedItem, plainTarget));

            // A context menu opened by a right press under the cursor ignores only that release.
            var item = new MenuItem { Header = "Menu choice" };
            int clicked = 0; item.Click += (_, _) => clicked++;
            // A custom placement can put the menu over its own anchor (the case the guard exists for).
            var menu = new OverlayContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Custom, Items = { item },
                CustomPopupPlacementCallback = (_, _, _) => [new CustomPopupPlacement(new Point(0, -8), PopupPrimaryAxis.None)] };
            OverlayPopupWindow.IsButtonPressed = button => pressed && button == MouseButton.Right;
            cursor = anchorCenter; pressed = true;
            menu.IsOpen = true; Pump();
            cursor = Center(item); pressed = false;
            var opened = (OverlayPopupWindow)typeof(OverlayContextMenu).GetField("window", Private)!.GetValue(menu)!;
            check("a context menu at the cursor records its opening right press", opened.IsOpeningButton(MouseButton.Right));
            cursor = anchorCenter;
            check("the menu item lies under the opening press's anchor", ScreenRect(item).Contains(cursor) && anchorBounds.Contains(cursor));
            Release(item, MouseButton.Right);
            check("the opening right release does not invoke the menu item under it", clicked == 0 && menu.IsOpen);
            cursor = Center(item);
            Press(item, MouseButton.Left);
            var rightNow = (OverlayPopupWindow)typeof(OverlayContextMenu).GetField("window", Private)!.GetValue(menu)!;
            check("a new press clears the opening gesture so the next release is a normal click",
                !rightNow.IsOpeningButton(MouseButton.Right) && clicked == 0);
            Release(item, MouseButton.Left);
            check("the menu stays open for that ordinary click", menu.IsOpen);
            menu.IsOpen = false; Pump();

            // A menu is measured before its items have templates; the window must still grow to them.
            var longMenu = new OverlayContextMenu { PlacementTarget = anchor, Placement = PlacementMode.MousePoint };
            var longItem = new MenuItem { Header = "窗口源（实验性）这是一段很长很长的菜单文字" };
            longMenu.Items.Add(longItem);
            var gestureItem = new MenuItem { Header = "short" };
            longMenu.Items.Add(gestureItem);
            // The real launcher menu fills shortcut text when it opens, after being measured.
            longMenu.Opened += (_, _) => gestureItem.InputGestureText = "Ctrl+Shift+Alt+Q";
            cursor = anchorCenter; pressed = false;
            longMenu.IsOpen = true; Pump();
            var longWindow = (OverlayPopupWindow)typeof(OverlayContextMenu).GetField("window", Private)!.GetValue(longMenu)!;
            int windowWidth = (int)longWindow.ScreenBounds.Width;
            int contentWidth = (int)Math.Ceiling(longMenu.ActualWidth * dpi.DpiScaleX);
            // The natural width of the realized menu, independent of the window it was given.
            longMenu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            int naturalWidth = (int)Math.Ceiling(longMenu.DesiredSize.Width * dpi.DpiScaleX);
            check("a menu window grows to its realized content instead of clipping long labels",
                windowWidth >= contentWidth - 1 && windowWidth >= naturalWidth - 1);
            longMenu.IsOpen = false; Pump();
            menu.IsOpen = false;
        }
        finally
        {
            combo.IsDropDownOpen = false;
            OverlayPopupWindow.Cursor = previousCursor;
            OverlayPopupWindow.IsButtonPressed = previousButtons;
            window.Close(); Pump();
        }
    }

    private static OverlayPopupWindow OpenPopup(ComboBox combo)
    {
        var popup = (OverlayPopup)combo.Template.FindName("PART_Popup", combo);
        return (OverlayPopupWindow)typeof(OverlayPopup).GetField("window", Private)!.GetValue(popup)!;
    }
    private static Rect ScreenRect(FrameworkElement element) =>
        new(element.PointToScreen(new()), element.PointToScreen(new(element.ActualWidth, element.ActualHeight)));
    private static Point Center(FrameworkElement element) => element.PointToScreen(new(element.ActualWidth / 2, element.ActualHeight / 2));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static void Press(IInputElement target, MouseButton button) => Send(target, button, Mouse.PreviewMouseDownEvent, MouseButtonState.Pressed);
    private static void Release(IInputElement target, MouseButton button) => Send(target, button, Mouse.PreviewMouseUpEvent, MouseButtonState.Released);
    private static void Send(IInputElement target, MouseButton button, RoutedEvent routed, MouseButtonState state)
    {
        var device = (MouseDevice)Activator.CreateInstance(MouseType.Value, InputManager.Current, target, state)!;
        try { InputManager.Current.ProcessInput(new MouseButtonEventArgs(device, Environment.TickCount, button) { RoutedEvent = routed }); Pump(); }
        finally { ToolOptionsInputFixture.ReleaseControlledMouse(device); }
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    // A controlled MouseDevice: fixed target and button state, without SendInput or cursor movement.
    private static readonly Lazy<Type> MouseType = new(() =>
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("FloatCanvasOpeningGestureInput"), AssemblyBuilderAccess.Run);
        assembly.SetCustomAttribute(new CustomAttributeBuilder(typeof(System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute).GetConstructor([typeof(string)])!, ["PresentationCore"]));
        var module = assembly.DefineDynamicModule("OpeningGestureInput");
        var type = module.DefineType("OpeningGestureMouse", TypeAttributes.Public, typeof(MouseDevice));
        var target = type.DefineField("target", typeof(IInputElement), FieldAttributes.Public);
        var state = type.DefineField("state", typeof(MouseButtonState), FieldAttributes.Public);
        var ctor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [typeof(InputManager), typeof(IInputElement), typeof(MouseButtonState)]);
        var il = ctor.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, typeof(MouseDevice).GetConstructors(Private).Single());
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Stfld, target);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_3); il.Emit(OpCodes.Stfld, state); il.Emit(OpCodes.Ret);
        var method = typeof(MouseDevice).GetMethod("GetButtonStateFromSystem", Private)!;
        var getter = type.DefineMethod(method.Name, MethodAttributes.Assembly | MethodAttributes.Virtual, typeof(MouseButtonState), [typeof(MouseButton)]);
        il = getter.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, state); il.Emit(OpCodes.Ret); type.DefineMethodOverride(getter, method);
        method = typeof(MouseDevice).GetProperty("Target")!.GetMethod!;
        getter = type.DefineMethod(method.Name, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName, typeof(IInputElement), Type.EmptyTypes);
        il = getter.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, target); il.Emit(OpCodes.Ret); type.DefineMethodOverride(getter, method);
        return type.CreateType()!;
    });
}
