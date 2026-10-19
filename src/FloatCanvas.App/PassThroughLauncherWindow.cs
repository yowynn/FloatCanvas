using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>One process-only position; it follows the toolbar until the user moves it.</summary>
internal sealed class PassThroughLauncherWindow : Window
{
    internal ToolbarLauncher Launcher { get; } = new() { Focusable = false };
    internal bool IsDetached { get; private set; }
    internal event Action? EnterEditingRequested;
    internal event Action? ToggleVisibilityRequested;

    internal PassThroughLauncherWindow()
    {
        Title = L.Get("Settings.PassThroughLauncher"); Content = Launcher;
        Width = Height = ToolbarLauncher.ArtworkSize;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; ShowActivated = false; Focusable = false; Topmost = true;
        Launcher.ToolTip = L.Get("Settings.PassThroughLauncherHint");
        Launcher.MenuRequested += () => EnterEditingRequested?.Invoke();
        Launcher.ToggleExpandedRequested += () => ToggleVisibilityRequested?.Invoke();
        Launcher.DragRequested += () =>
        {
            var before = new Point(Left, Top);
            if (Mouse.LeftButton == MouseButtonState.Pressed) DragMove();
            CompleteDrag(before);
        };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(handle, -20, GetWindowLongPtr(handle, -20) | (nint)0x08000080); // NOACTIVATE, TOOLWINDOW
            HwndSource.FromHwnd(handle)!.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
            {
                if (message == 0x21) { handled = true; return 3; } // MA_NOACTIVATE; the launcher still receives the click
                return 0;
            });
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible) Launcher.CancelPendingGestures(); };
        Closed += (_, _) => Launcher.CancelPendingGestures();
    }

    private void CompleteDrag(Point before)
    {
        if (before != new Point(Left, Top)) IsDetached = true;
        if (IsDetached) AppWindowChrome.EnsureVisibleBounds(this);
    }

    internal void ShowAt(OverlayPoint toolbarOrigin, bool canvasVisible)
    {
        Launcher.SetCanvasVisible(canvasVisible);
        new WindowInteropHelper(this).EnsureHandle();
        if (!IsDetached) OverlayCoordinates.PositionToolWindow(new WindowInteropHelper(this).Handle, toolbarOrigin);
        else AppWindowChrome.EnsureVisibleBounds(this);
        if (!IsVisible) Show();
        if (!IsDetached) OverlayCoordinates.PositionToolWindow(new WindowInteropHelper(this).Handle, toolbarOrigin);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}
