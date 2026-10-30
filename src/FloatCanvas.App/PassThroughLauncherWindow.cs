using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>The passive view of the session's shared launcher position.</summary>
internal sealed class PassThroughLauncherWindow : Window
{
    internal const double ArtworkSize = ToolbarLauncher.ArtworkSize;
    internal ToolbarLauncher Launcher { get; } = new(editing: false) { Focusable = false };
    internal event Action<OverlayPoint>? PositionChanged;
    internal event Action? EnterEditingRequested;
    internal event Action? ToggleVisibilityRequested;

    internal PassThroughLauncherWindow()
    {
        OverlayWindowScope.Prepare(this);
        Title = L.Get("Settings.PassThroughLauncher");
        Width = Height = ArtworkSize;
        var surface = new Grid();
        surface.Children.Add(Launcher);
        Content = surface;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; ShowActivated = false; Focusable = false; Topmost = true;
        SetEntryHint(null);
        Launcher.MenuRequested += () => EnterEditingRequested?.Invoke();
        Launcher.ToggleVisibilityRequested += () => ToggleVisibilityRequested?.Invoke();
        Launcher.DragRequested += () =>
        {
            var before = new Point(Left, Top);
            if(OverlayWindowDrag.TryStart(this,()=>CompleteDrag(before)))return;
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
        if (before == new Point(Left, Top)) return;
        var point = PointToScreen(new Point());
        PositionChanged?.Invoke(new(point.X, point.Y));
    }

    internal void ShowAt(OverlayPoint origin, bool canvasVisible)
    {
        Launcher.SetCanvasVisible(canvasVisible);
        new WindowInteropHelper(this).EnsureHandle();
        OverlayCoordinates.PositionToolWindow(new WindowInteropHelper(this).Handle, origin);
        if (!IsVisible) Show();
        // Showing on a different-DPI monitor can resize the native window; restore
        // the shared physical origin after WPF has applied that monitor's DPI.
        OverlayCoordinates.PositionToolWindow(new WindowInteropHelper(this).Handle, origin);
    }

    internal void SetEntryHint(string? message)
    {
        string hint = message ?? L.Get("Settings.PassThroughLauncherHint");
        Launcher.ToolTip = hint;
        AutomationProperties.SetHelpText(Launcher, hint);
        AutomationProperties.SetHelpText(this, hint);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}
