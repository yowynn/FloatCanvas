using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

internal enum WindowDismissReason { Close, CanvasPress, ToolbarPress, ExternalFocus, ModeExit, Shutdown }

/// <summary>One editing session owns window handoffs; closing a surface does not change its mode.</summary>
internal sealed class SessionWindowCoordinator : IDisposable
{
    private const string ResourceKey = "FloatCanvas.SessionWindowCoordinator";
    private readonly OverlayHost host;
    private readonly Window toolbar;
    private readonly Dictionary<Window, Registration> windows = [];
    private readonly Dictionary<Popup, Action> popups = [];
    private WindowDismissReason? destination;
    private long nextIntent, intentToken, intentSession;
    private bool disposed;

    internal SessionWindowCoordinator(OverlayHost host, Window toolbar)
    {
        this.host = host; this.toolbar = toolbar;
        toolbar.Resources[ResourceKey] = this;
    }

    internal bool IsDirectTransfer => !disposed && destination is not null && intentSession == host.EditingSessionVersion;
    internal bool IsInputMethodSwitchPending => !disposed && host.IsInputMethodSwitchPending;

    internal bool CanContinue(WindowSessionRequest request, object document) => !disposed &&
        request.IsCurrent(host.EditingSessionVersion, host.IsEditing, document) && (!request.Editing || OwnsForeground());

    internal static SessionWindowCoordinator? Find(Window? window)
    {
        for (var owner = window; owner is not null; owner = owner.Owner)
            if (owner.Resources.Contains(ResourceKey) && owner.Resources[ResourceKey] is SessionWindowCoordinator coordinator)
                return coordinator;
        return null;
    }

    internal static void TrackOwned(Window window) => Find(window)?.Register(window);

    // Native caption movement runs a Win32 modal loop rather than WPF input staging.
    // Renew only when this UI thread actually handles a move/size message.
    internal void NotifyNativeInputProgress()
    {
        if (!disposed) host.NotifyInputProgress();
    }

    internal void RegisterPopup(Popup popup, Action close) => popups[popup] = close;
    internal void UnregisterPopup(Popup popup) => popups.Remove(popup);

    internal void Register(Window window, Func<bool>? pinned = null, Action<WindowDismissReason>? dismissTransient = null)
    {
        if (ReferenceEquals(window, toolbar)) return;
        window.Resources[ResourceKey] = this;
        if (windows.TryGetValue(window, out var existing))
        {
            if (pinned is not null) existing.Pinned = pinned;
            if (dismissTransient is not null) existing.DismissTransient = dismissTransient;
            return;
        }
        var registration = new Registration { Window = window, Pinned = pinned, DismissTransient = dismissTransient };
        windows.Add(window, registration);
        window.Closing += OnClosing;
        window.Closed += OnClosed;
    }

    internal static void Show(Window window, bool activate = false)
    {
        var coordinator = Find(window);
        using var transfer = coordinator?.BeginTransition();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
        if (activate) window.Activate();
    }

    internal static void Hide(Window window, WindowDismissReason reason = WindowDismissReason.Close)
    {
        var coordinator = Find(window);
        using var transfer = coordinator?.BeginTransition();
        if (reason == WindowDismissReason.Close) coordinator?.ReturnBeforeDismiss(window);
        window.Hide();
    }

    internal static void Close(Window window)
    {
        var coordinator = Find(window);
        using var transfer = coordinator?.BeginTransition();
        bool wasActive = window.IsActive;
        coordinator?.ReturnBeforeDismiss(window);
        window.Close();
        // A handler may cancel Close without hiding the panel. Keep that valid surface active.
        if (coordinator is not null && wasActive && window.IsVisible && window.IsEnabled)
            coordinator.ActivateIfOwned(window);
    }

    internal static bool? ShowModal(Window window)
    {
        var coordinator = Find(window);
        coordinator?.Register(window);
        long version = coordinator?.host.EditingSessionVersion ?? 0;
        var owner = window.Owner;
        // The native transition expires at the next control turn, even while ShowDialog pumps.
        using var entering = coordinator?.BeginTransition();
        try { return window.ShowDialog(); }
        finally
        {
            if (coordinator is not null && coordinator.host.EditingSessionVersion == version)
            {
                using var returning = coordinator.BeginTransition();
                coordinator.ReturnToOwner(owner);
            }
        }
    }

    internal static bool? ShowNativeModal(Window owner, Func<bool?> show)
    {
        var coordinator = Find(owner);
        long version = coordinator?.host.EditingSessionVersion ?? 0;
        using var entering = coordinator?.BeginTransition();
        try { return show(); }
        finally
        {
            if (coordinator is not null && coordinator.host.EditingSessionVersion == version)
            {
                using var returning = coordinator.BeginTransition();
                coordinator.ActivateIfOwned(owner);
            }
        }
    }

    internal IDisposable DirectTransfer(WindowDismissReason reason)
    {
        var previous = IsDirectTransfer ? destination : null;
        long previousToken = IsDirectTransfer ? intentToken : 0;
        long version = host.EditingSessionVersion;
        long token = ++nextIntent;
        destination = reason;
        intentToken = token; intentSession = version;
        return new Scope(() =>
        {
            if (disposed || intentToken != token) return;
            bool currentSession = host.EditingSessionVersion == version;
            destination = currentSession ? previous : null;
            intentToken = currentSession ? previousToken : 0;
        });
    }

    internal void DismissTransient(WindowDismissReason reason)
    {
        using var intent = DirectTransfer(reason);
        using var transfer = BeginTransition();
        foreach (var registration in windows.Values.ToArray())
            if (registration.Window.IsVisible && registration.Pinned?.Invoke() != true)
                registration.DismissTransient?.Invoke(reason);
    }

    // Capture the physical destination before a Popup's close/commit detaches its visual tree.
    // The context lasts through the complete WPF staging turn, including other PostProcess handlers.
    internal void ObservePointerPress(MouseButtonEventArgs mouse)
    {
        // WPF has not performed hit testing at PreProcessInput: OriginalSource can
        // still be null while the mouse device already targets the captured PopupRoot.
        var source = mouse.OriginalSource as DependencyObject ?? mouse.MouseDevice.Target as DependencyObject ?? Mouse.Captured as DependencyObject;
        if (disposed || !host.IsEditing || source is null ||
            PresentationSource.FromDependencyObject(source) is not HwndSource { RootVisual: UIElement root }) return;
        var point = root.PointToScreen(mouse.GetPosition(root));
        var pixel = new PixelPoint((int)Math.Round(point.X), (int)Math.Round(point.Y));
        var handle = GetAncestor(WindowFromPhysicalPoint(pixel), 2);
        WindowDismissReason? reason = handle == new WindowInteropHelper(toolbar).Handle ? WindowDismissReason.ToolbarPress :
            host.Diagnostics.Any(pair => pair.InputHandle == handle) ? WindowDismissReason.CanvasPress : null;
        if (reason is null) return;
        destination = reason;
        long token = intentToken = ++nextIntent;
        intentSession = host.EditingSessionVersion;
        toolbar.Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
        {
            if (intentToken == token) { destination = null; intentToken = 0; }
        });
        if (reason != WindowDismissReason.CanvasPress || Mouse.Captured is not DependencyObject captured ||
            PresentationSource.FromDependencyObject(captured) is not HwndSource capturedSource ||
            capturedSource.RootVisual is Window || GetCapture() != capturedSource.Handle) return;
        var button = mouse.ChangedButton switch
        {
            MouseButton.Left => OverlayMouseButton.Left,
            MouseButton.Right => OverlayMouseButton.Right,
            MouseButton.Middle => OverlayMouseButton.Middle,
            _ => OverlayMouseButton.None
        };
        if (button == OverlayMouseButton.None) return;
        using var transfer = DirectTransfer(WindowDismissReason.CanvasPress);
        // Commit drafts before starting the new canvas transaction. Popup.Closed can
        // arrive later, when DestroyWindow runs, and must not cancel that first gesture.
        foreach (var close in popups.Values.ToArray()) close();
        // WPF delivers an outside press to the captured Popup, not the canvas HWND.
        // Release only that verified Popup capture, then transfer this original press
        // synchronously. The native host owns the following real move/up messages.
        Mouse.Capture(null);
        if (host.TryForwardCapturedCanvasPress(new(pixel.X, pixel.Y), button, unchecked((uint)mouse.Timestamp), mouse.ClickCount))
            mouse.Handled = true;
    }

    private IDisposable? BeginTransition() => !disposed && host.IsEditing ? host.BeginInternalWindowTransition() : null;

    private void ReturnBeforeDismiss(Window source)
    {
        if (disposed || !host.IsEditing || IsDirectTransfer || !OwnsForeground() || !ContainsForeground(source)) return;
        // A visible modal owner remains disabled until ShowDialog has unwound. Its finally
        // block will repeat this handoff after WPF reenables it, without focusing the canvas.
        ReturnToOwner(source.Owner);
    }

    private void ReturnToOwner(Window? firstOwner)
    {
        if (disposed || !host.IsEditing || IsDirectTransfer || !OwnsForeground()) return;
        for (var owner = firstOwner; owner is not null; owner = owner.Owner)
        {
            if (ActivateIfOwned(owner)) return;
        }
        if (!ActivateIfOwned(toolbar)) host.TryFocusEditingCanvas();
    }

    private bool ActivateIfOwned(Window target)
    {
        if (disposed || !host.IsEditing || IsDirectTransfer || !CanActivate(target) || !OwnsForeground()) return false;
        return target.Activate() && GetForegroundWindow() == new WindowInteropHelper(target).Handle;
    }

    private static bool CanActivate(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        return window.IsVisible && window.IsEnabled && window.WindowState != WindowState.Minimized &&
            handle != 0 && IsWindow(handle) && IsWindowVisible(handle) && IsWindowEnabled(handle);
    }

    private static bool OwnsForeground()
    {
        var foreground = GetForegroundWindow();
        return foreground != 0 && GetWindowThreadProcessId(foreground, out var process) != 0 && process == Environment.ProcessId;
    }

    private static bool ContainsForeground(Window source)
    {
        var sourceHandle = new WindowInteropHelper(source).Handle;
        if (sourceHandle == 0) return false;
        for (var handle = GetForegroundWindow(); handle != 0; handle = GetWindow(handle, 4))
            if (handle == sourceHandle) return true;
        return false;
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (sender is not Window window || disposed) return;
        using var transfer = BeginTransition();
        bool wasActive = window.IsActive;
        long version = host.EditingSessionVersion;
        if (!args.Cancel) ReturnBeforeDismiss(window);
        window.Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
        {
            if (args.Cancel && wasActive && window.IsVisible && host.EditingSessionVersion == version)
                ActivateIfOwned(window);
        });
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        if (sender is not Window window) return;
        windows.Remove(window);
        window.Closing -= OnClosing; window.Closed -= OnClosed;
        window.Resources.Remove(ResourceKey);
    }

    public void Dispose()
    {
        disposed = true; ++nextIntent; intentToken = 0; destination = null;
        foreach (var window in windows.Keys.ToArray())
        {
            window.Closing -= OnClosing; window.Closed -= OnClosed;
            window.Resources.Remove(ResourceKey);
        }
        windows.Clear(); popups.Clear(); toolbar.Resources.Remove(ResourceKey);
    }

    private sealed class Registration
    {
        internal required Window Window;
        internal Func<bool>? Pinned;
        internal Action<WindowDismissReason>? DismissTransient;
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        private Action? action = dispose;
        public void Dispose() { var current = action; action = null; current?.Invoke(); }
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct PixelPoint(int X, int Y);
    [DllImport("user32.dll")] private static extern nint WindowFromPhysicalPoint(PixelPoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint handle, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint process);
}
