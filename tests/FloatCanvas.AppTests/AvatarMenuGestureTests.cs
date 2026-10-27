using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using FloatCanvas.App;

internal static class AvatarMenuGestureTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        nint foreground=GetForegroundWindow();
        var desktop=System.Windows.Forms.SystemInformation.VirtualScreen;
        var launcher = new ToolbarLauncher();
        using var source = new HwndSource(new HwndSourceParameters("FloatCanvas.AvatarGestureTests")
        { Width = 94, Height = 94, PositionX = desktop.Left-10000, PositionY = desktop.Top-10000,
            WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle=0x08000080 });
        source.RootVisual = launcher;
        launcher.Measure(new Size(94, 94)); launcher.Arrange(new Rect(0, 0, 94, 94));launcher.UpdateLayout();
        // CaptureMouse synchronously reports native input. Complete this source's
        // Loaded/input initialization before driving its protected press handler;
        // a hidden HWND with pending initialization can correctly lose capture
        // and cancel the gesture being asserted here.
        var frame=new DispatcherFrame();
        source.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,()=>frame.Continue=false);
        Dispatcher.PushFrame(frame);
        check("avatar gesture fixture uses an initialized nonactivating offscreen HWND",IsWindowVisible(source.Handle)&&launcher.IsLoaded&&GetForegroundWindow()==foreground);
        bool menuOpen = true; int closes = 0, opens = 0;
        launcher.CloseOpenMenu = () => { if (!menuOpen) return false; menuOpen = false; closes++; return true; };
        launcher.MenuRequested += () => opens++;
        var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent };
        typeof(ToolbarLauncher).GetMethod("OnPreviewMouseLeftButtonDown", Private)!.Invoke(launcher, [down]);
        var gestures = (LauncherGestureState)typeof(ToolbarLauncher).GetField("gestures", Private)!.GetValue(launcher)!;
        Console.WriteLine($"AVATAR_PRESS closes={closes} menu={menuOpen} pointerDown={gestures.IsPointerDown} opens={opens} captured={Mouse.Captured?.GetType().Name ?? "none"} nativeCapture={GetCapture()==source.Handle} nativeVisible={IsWindowVisible(source.Handle)}");
        check("avatar gesture fixture captures only its own offscreen input surface",ReferenceEquals(Mouse.Captured,launcher)&&GetCapture()==source.Handle);
        check("closing an open menu retains the same press as a double-click or drag candidate", closes == 1 && !menuOpen && gestures.IsPointerDown && opens == 0);
        launcher.CancelPendingGestures();

        var state = NewState();
        state.PointerDown(10, 20, 100, suppressMenu: true); state.PointerUp(10, 20, 120);
        check("one menu-dismissal click never reopens the menu at its timeout", state.AdvanceTime(750) == LauncherGestureAction.None && !state.HasPendingClick);

        state.PointerDown(10, 20, 1000, suppressMenu: true); state.PointerUp(10, 20, 1020);
        state.PointerDown(10, 20, 1100);
        check("double-click starting with menu dismissal still toggles the eyes", state.PointerUp(10, 20, 1120) == LauncherGestureAction.ToggleVisibility && state.AdvanceTime(2000) == LauncherGestureAction.None);

        state.PointerDown(10, 20, 3000, suppressMenu: true);
        check("the menu-dismissal press can immediately become a drag", state.PointerMove(18, 20, 3010) == LauncherGestureAction.BeginDrag);
        check("menu-dismissal drag cannot reopen the menu or toggle visibility", state.PointerUp(18, 20, 3020) == LauncherGestureAction.None && state.AdvanceTime(4000) == LauncherGestureAction.None);

        state.PointerDown(10, 20, 5000, suppressMenu: true); state.PointerUp(10, 20, 5020);
        state.PointerDown(10, 20, 5100);
        check("dragging the second press takes priority over the menu-dismissal double-click", state.PointerMove(18, 20, 5110) == LauncherGestureAction.BeginDrag && state.PointerUp(18, 20, 5120) == LauncherGestureAction.None);

        state.PointerDown(10, 20, 6000, suppressMenu: true);
        check("holding a menu-dismissal press beyond the interval cannot open the menu", state.AdvanceTime(7000) == LauncherGestureAction.None && state.PointerUp(10, 20, 7000) == LauncherGestureAction.None);
        state.PointerDown(10, 20, 8000); state.PointerUp(10, 20, 8020);
        check("menu suppression does not leak into a later ordinary click", state.AdvanceTime(8650) == LauncherGestureAction.OpenMenu);

        launcher.DismissMenuPress(new Point(20, 20));
        check("a press forwarded from a captured menu enters the same gesture state", ((LauncherGestureState)typeof(ToolbarLauncher).GetField("gestures", Private)!.GetValue(launcher)!).IsPointerDown);
        var lostMenuCapture = new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.LostMouseCaptureEvent, Source = new System.Windows.Controls.ContextMenu() };
        typeof(ToolbarLauncher).GetMethod("OnLostMouseCapture", Private)!.Invoke(launcher, [lostMenuCapture]);
        check("a dismissed menu's bubbling capture loss cannot cancel the new launcher press", ((LauncherGestureState)typeof(ToolbarLauncher).GetField("gestures", Private)!.GetValue(launcher)!).IsPointerDown);
        launcher.ReleaseMouseCapture();
        var lostOwnCapture = new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.LostMouseCaptureEvent, Source = launcher };
        typeof(ToolbarLauncher).GetMethod("OnLostMouseCapture", Private)!.Invoke(launcher, [lostOwnCapture]);
        check("losing the launcher's own capture still cancels an interrupted press", !((LauncherGestureState)typeof(ToolbarLauncher).GetField("gestures", Private)!.GetValue(launcher)!).IsPointerDown);
        launcher.CancelPendingGestures();
        nint handle=source.Handle;source.Dispose();
        check("avatar gesture fixture releases capture and destroys its offscreen HWND",GetCapture()!=handle&&!IsWindow(handle));
        check("avatar gesture fixture preserves the user's foreground window",GetForegroundWindow()==foreground);
    }

    private static LauncherGestureState NewState() => new(650, 7, 11, 12, 20);
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(nint handle);
}
