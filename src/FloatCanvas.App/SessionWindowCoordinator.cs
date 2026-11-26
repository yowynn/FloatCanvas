using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Controls;
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
    private const string ModalResourceKey = ResourceKey + ".Modal";
    [ThreadStatic] private static SessionWindowCoordinator? current;
    private readonly SessionWindowCoordinator? previous;
    private readonly OverlayHost host;
    private readonly Window toolbar;
    private readonly Func<IDisposable?> beginNativeDialog;
    private readonly Dictionary<Window, Registration> windows = [];
    private readonly Dictionary<object, Action> popups = [];
    private readonly HashSet<Window> suspendedWindows = [];
    private readonly List<Window> modalWindows = [];
    private readonly List<Action> deferredOwnerRestores = [];
    private int nativeModalDepth;
    internal static bool HasModalInput => current is { disposed: false } value && (value.nativeModalDepth > 0 || value.modalWindows.Count > 0);
    internal static bool HasModalChild(Window owner)
    {
        if (Find(owner) is not { } coordinator) return false;
        foreach (var dialog in coordinator.modalWindows)
            for (var parent = dialog.Owner; parent is not null; parent = parent.Owner)
                if (ReferenceEquals(parent, owner)) return true;
        return false;
    }
    internal event Action? ModalEntering;
    internal bool OwnsKeyboardSource(DependencyObject? source) => source is not null &&
        EditorShortcutPolicy.Ancestors(source).Any(value => value is Window window && ReferenceEquals(Find(window), this));
    internal bool IsSuspendingWindows { get; private set; }
    private WindowDismissReason? destination;
    private long nextIntent, intentToken, intentSession;
    private bool disposed;
    private long activationOrder;

    internal SessionWindowCoordinator(OverlayHost host, Window toolbar, Func<IDisposable?>? beginNativeDialog = null)
    {
        this.host = host; this.toolbar = toolbar;
        this.beginNativeDialog = beginNativeDialog ?? (() => host.BeginNativeDialog());
        previous = current; current = this;
        toolbar.Resources[ResourceKey] = this;
    }

    internal bool IsDirectTransfer => !disposed && destination is not null && intentSession == host.EditingSessionVersion;
    internal bool IsInputMethodSwitchPending => !disposed && host.IsInputMethodSwitchPending;
    internal Action<int>? HistoryCommand { get; set; }

    internal bool CanContinue(WindowSessionRequest request, object document) => !disposed &&
        request.IsCurrent(host.EditingSessionVersion, host.IsEditing, document) && (!request.Editing || OwnsEditingInput());

    internal static SessionWindowCoordinator? Find(Window? window)
    {
        for (var owner = window; owner is not null; owner = owner.Owner)
            if (owner.Resources.Contains(ResourceKey) && owner.Resources[ResourceKey] is SessionWindowCoordinator coordinator)
                return coordinator;
        return null;
    }

    internal static void TrackOwned(Window window) => Find(window)?.Register(window);
    internal static bool IsEditingWindow(Window? window) => Find(window)?.host.IsEditing ?? OverlayWindowScope.Current?.IsInputActive == true;

    internal IDisposable SuspendHotkeysForRecording()
    {
        bool previous = host.ApplicationHotkeysSuspended;
        host.SuspendApplicationHotkeys(true);
        return new Scope(() => { if (!disposed) host.SuspendApplicationHotkeys(previous); });
    }

    // Native caption movement runs a Win32 modal loop rather than WPF input staging.
    // Renew only when this UI thread actually handles a move/size message.
    internal void NotifyNativeInputProgress()
    {
        if (!disposed) host.NotifyInputProgress();
    }

    internal void RegisterPopup(object popup, Action close) => popups[popup] = close;
    internal void UnregisterPopup(object popup) => popups.Remove(popup);

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
        window.Activated += OnActivated;
        window.GotKeyboardFocus += OnKeyboardFocus;
    }

    internal static void Show(Window window, bool activate = false, Action? revealed = null)
    {
        WindowTitles.Apply(window);
        OverlayWindowScope.Prepare(window);
        var coordinator = Find(window);
        using var transfer = coordinator?.BeginTransition();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        SessionWindowLayout.PrepareForShow(window);
        nint handle = new WindowInteropHelper(window).EnsureHandle();
        // Modal ownership can leave the native HWND disabled after WPF has already
        // restored Window.IsEnabled. Repair that stale state only outside an active
        // modal, where the disabled HWND is intentional.
        bool modalActive = coordinator is not null && (coordinator.nativeModalDepth > 0 || coordinator.modalWindows.Count > 0);
        if (!modalActive && window.IsEnabled && handle != 0 && IsWindow(handle) && !IsWindowEnabled(handle))
            EnableWindow(handle, true);
        // A reused window may have changed while hidden; never expose its retained frame.
        // Activation must follow the actual reveal. A cloaked HWND cannot become the
        // editing input destination, and an early focus request is lost on handoff.
        WindowRevealGate.Show(window, activate ? () =>
        {
            if (coordinator is not null && coordinator.host.UsesSystemInput && coordinator.host.IsEditing)
                coordinator.host.TryFocusEditingWindow(new WindowInteropHelper(window).Handle);
            else window.Activate();
            revealed?.Invoke();
        } : revealed);
    }

    internal static nint CurrentForeground => GetForegroundWindow();

    // Enhanced input can belong to this app while another process remains the
    // Win32 foreground. Only a direct user request may transfer it to a normal
    // window, and only while that request's source foreground is still current.
    internal static bool ActivateRegularWindowForUserAction(Window window, nint expectedForeground)
    {
        if (!window.IsVisible || !OverlayWindowScope.IsRegularWindow(window)) return false;
        nint handle = new WindowInteropHelper(window).Handle;
        if (GetForegroundWindow() == handle) return true;
        if (GetForegroundWindow() != expectedForeground) return false;
        window.Activate();
        if (GetForegroundWindow() == handle) return true;
        uint thread = GetCurrentThreadId(), foregroundThread = GetWindowThreadProcessId(expectedForeground, out _);
        if (foregroundThread == 0 || foregroundThread == thread) return false;
        bool attached = AttachThreadInput(thread, foregroundThread, true);
        if (!attached) return false;
        try
        {
            if (GetForegroundWindow() != expectedForeground) return false;
            SetForegroundWindow(handle);
            if (GetForegroundWindow() != handle) return false;
            SetFocus(handle); return true;
        }
        finally { AttachThreadInput(thread, foregroundThread, false); }
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
        OverlayWindowScope.Prepare(window);
        WindowTitles.Apply(window);
        var coordinator = Find(window);
        coordinator?.Register(window);
        using var modalScope = coordinator?.EnterModal(window);
        long version = coordinator?.host.EditingSessionVersion ?? 0;
        var owner = window.Owner;
        // The native transition expires at the next control turn, even while ShowDialog pumps.
        using var entering = coordinator?.BeginTransition();
        try
        {
            SessionWindowLayout.PrepareForShow(window);
            return coordinator is not null && coordinator.host.UsesSystemInput && coordinator.host.IsEditing
                ? coordinator.ShowOverlayModal(window) : window.ShowDialog();
        }
        finally
        {
            modalScope?.Dispose();
            if (coordinator is not null && coordinator.host.EditingSessionVersion == version)
            {
                using var returning = coordinator.BeginTransition();
                coordinator.ReturnToOwner(owner);
                coordinator.RestoreKeyboardDestination();
            }
        }
    }

    private IDisposable EnterModal(Window window)
    {
        ModalEntering?.Invoke();
        modalWindows.Add(window);
        return new Scope(() =>
        {
            modalWindows.Remove(window);
            FlushDeferredOwnerRestores();
        });
    }

    private void FlushDeferredOwnerRestores()
    {
        if (disposed || modalWindows.Count > 0 || nativeModalDepth > 0) return;
        foreach (var restore in deferredOwnerRestores.ToArray())
        {
            deferredOwnerRestores.Remove(restore);
            restore();
        }
    }

    internal void CloseEditingModals(WindowDismissReason reason = WindowDismissReason.ModeExit)
    {
        if (modalWindows.Count == 0) return;
        using var intent = DirectTransfer(reason);
        using var transition = BeginTransition();
        // Nested ShowDialog/DispatcherFrame loops own disabled HWNDs. Close children
        // first so every finally can unwind and restore exactly its disabled owners.
        foreach (var window in modalWindows.ToArray().Reverse())
            if (window.IsVisible) window.Close();
    }

    internal static void SetModalResult(Window window, bool result)
    {
        if (window.Resources[ModalResourceKey] is OverlayModal modal)
        {
            modal.Result = result;
            Close(window);
            if (!modal.Closed) modal.Result = null;
        }
        else window.DialogResult = result;
    }

    // ShowDialog disables every HWND on its thread. Input infrastructure must
    // stay enabled to isolate the desktop; only WPF editing surfaces are modal.
    private bool? ShowOverlayModal(Window window)
    {
        var modal = new OverlayModal();
        var disabled = new List<HwndSource>();
        var owner = window.Owner;
        long version = host.EditingSessionVersion;
        CancelEventArgs? closeRequest = null;
        bool returningFocus = false;
        HwndSource? dialogSource = null;
        window.Resources[ModalResourceKey] = modal;
        void Closed(object? sender, EventArgs args) { modal.Closed = true; modal.Frame.Continue = false; }
        void Closing(object? sender, CancelEventArgs args)
        {
            closeRequest = args;
            returningFocus = !IsDirectTransfer && OwnsEditingInput() && ContainsForeground(window);
        }
        void RestoreOwners()
        {
            // Nested dialogs restore only the windows they disabled themselves.
            void RestoreEnabledOwners()
            {
                foreach (var source in disabled.ToArray())
                    if (!source.IsDisposed && IsWindow(source.Handle))
                    {
                        EnableWindow(source.Handle, true);
                        disabled.Remove(source);
                    }
            }
            void RestoreWhenNoOtherModal()
            {
                if (disposed || disabled.Count == 0) return;
                bool anotherModal = modalWindows.Any(other => !ReferenceEquals(other, window)) || nativeModalDepth > 0;
                if (anotherModal)
                {
                    // Closing a nested child returns control to its direct owner while
                    // ancestors and sibling modals remain disabled.
                    foreach (var source in disabled.ToArray())
                        if (owner is not null && ReferenceEquals(source.RootVisual, owner) &&
                            !source.IsDisposed && IsWindow(source.Handle))
                        {
                            EnableWindow(source.Handle, true);
                            disabled.Remove(source);
                        }
                    if (disabled.Count == 0) return;
                    if (!deferredOwnerRestores.Contains(RestoreWhenNoOtherModal))
                        deferredOwnerRestores.Add(RestoreWhenNoOtherModal);
                    return;
                }
                deferredOwnerRestores.Remove(RestoreWhenNoOtherModal);
                RestoreEnabledOwners();
            }
            RestoreWhenNoOtherModal();
            // WPF may publish IsEnabled=true after the modal's finally block. Recheck
            // once the current dispatch turn has completed so no native owner stays
            // permanently disabled while its WPF surface looks interactive.
            if (disabled.Count > 0 && !window.Dispatcher.HasShutdownStarted)
                window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RestoreWhenNoOtherModal));
        }
        nint BeforeHide(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
        {
            // WM_WINDOWPOSCHANGING with SWP_HIDEWINDOW follows cancellable Closing and
            // precedes hiding the focused HWND. Waiting for Closed leaves no enabled owner.
            if (message == 0x0046 && lParam != 0 && closeRequest is { Cancel: false } &&
                (Marshal.PtrToStructure<WindowPosition>(lParam).Flags & 0x0080) != 0)
            {
                closeRequest = null;
                RestoreOwners();
                if (returningFocus && host.EditingSessionVersion == version) ReturnToOwner(owner);
            }
            return 0;
        }
        void AttachCloseHook(object? sender, EventArgs args)
        {
            dialogSource = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            dialogSource?.AddHook(BeforeHide);
        }
        void Loaded(object sender, RoutedEventArgs args)
        {
            if (!modal.Closed && host.HasEditingInput) host.TryFocusEditingWindow(new WindowInteropHelper(window).Handle);
        }
        void KeyDown(object sender, KeyEventArgs args)
        {
            if (args.Handled || args.Key != Key.Escape) return;
            args.Handled = true; SetModalResult(window, false);
        }
        void ButtonClick(object sender, RoutedEventArgs args)
        {
            if (args.OriginalSource is Button { IsCancel: true }) SetModalResult(window, false);
        }
        var click = new RoutedEventHandler(ButtonClick);
        window.Closing += Closing; window.Closed += Closed; window.Loaded += Loaded; window.KeyDown += KeyDown;
        window.SourceInitialized += AttachCloseHook;
        if (new WindowInteropHelper(window).Handle != 0) AttachCloseHook(null, EventArgs.Empty);
        window.AddHandler(ButtonBase.ClickEvent, click, true);
        ComponentDispatcher.PushModal();
        try
        {
            foreach (var source in PresentationSource.CurrentSources.OfType<HwndSource>().ToArray())
            {
                if (source.Dispatcher != window.Dispatcher || source.RootVisual is null || ReferenceEquals(source.RootVisual, window)) continue;
                var handle = source.Handle;
                if (handle == 0 || !IsWindowVisible(handle) || !IsWindowEnabled(handle)) continue;
                disabled.Add(source); EnableWindow(handle, false);
            }
            Show(window, activate: true);
            if (!modal.Closed) Dispatcher.PushFrame(modal.Frame);
            return modal.Result;
        }
        finally
        {
            window.Closing -= Closing; window.Closed -= Closed; window.Loaded -= Loaded; window.KeyDown -= KeyDown;
            window.SourceInitialized -= AttachCloseHook;
            if (dialogSource is { IsDisposed: false }) dialogSource.RemoveHook(BeforeHide);
            window.RemoveHandler(ButtonBase.ClickEvent, click);
            window.Resources.Remove(ModalResourceKey);
            RestoreOwners();
            ComponentDispatcher.PopModal();
        }
    }

    private sealed class OverlayModal
    {
        internal DispatcherFrame Frame { get; } = new();
        internal bool? Result;
        internal bool Closed;
    }

    internal static bool? ShowNativeModal(Window owner, Func<bool?> show)
        => RunNativeModal(owner, show);

    internal static T RunNativeModal<T>(Window? owner, Func<T> show)
    {
        // A fallback may have no usable WPF owner because constructing that owner
        // failed. The active thread's coordinator still owns the input handoff.
        var coordinator = Find(owner) ?? (current is { disposed: false } ? current : null);
        long version = coordinator?.host.EditingSessionVersion ?? 0;
        if (coordinator is not null) { coordinator.ModalEntering?.Invoke(); coordinator.nativeModalDepth++; }
        try
        {
            using var native = coordinator?.beginNativeDialog();
            using var entering = coordinator?.BeginTransition();
            return show();
        }
        finally
        {
            if (coordinator is not null) coordinator.nativeModalDepth--;
            coordinator?.FlushDeferredOwnerRestores();
            if (coordinator is not null && coordinator.host.EditingSessionVersion == version)
            {
                using var returning = coordinator.BeginTransition();
                coordinator.ActivateIfOwned(owner ?? coordinator.toolbar);
                coordinator.RestoreKeyboardDestination();
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

    internal void SuspendEditingWindows()
    {
        using var intent = DirectTransfer(WindowDismissReason.ModeExit);
        IsSuspendingWindows = true;
        try
        {
            CloseEditingModals();
            foreach (var registration in windows.Values.ToArray())
            {
                if (!registration.Window.IsVisible || registration.Pinned is null) continue;
                if (registration.Pinned?.Invoke() == true) suspendedWindows.Add(registration.Window);
                else registration.DismissTransient?.Invoke(WindowDismissReason.ModeExit);
                registration.Window.Hide();
            }
        }
        finally { IsSuspendingWindows = false; }
    }

    internal void HideTemporarily(IEnumerable<Window> targets)
    {
        using var intent = DirectTransfer(WindowDismissReason.CanvasPress);
        using var transition = BeginTransition();
        bool previous = IsSuspendingWindows;
        IsSuspendingWindows = true;
        try
        {
            foreach (var window in targets)
                if (window.IsVisible) Hide(window, WindowDismissReason.CanvasPress);
        }
        finally { IsSuspendingWindows = previous; }
    }

    internal void RememberPinnedForResume(IEnumerable<Window> targets)
    {
        foreach (var window in targets)
            if (windows.TryGetValue(window, out var registration) && registration.Pinned?.Invoke() == true)
                suspendedWindows.Add(window);
    }

    internal void ResumeEditingWindows()
    {
        foreach (var window in suspendedWindows.ToArray())
            if (windows.ContainsKey(window)) Show(window);
        suspendedWindows.Clear();
    }

    internal void ReconcileInteractionSurface()
    {
        if (disposed || !host.IsEditing || IsDirectTransfer || HasModalInput ||
            ComponentDispatcher.IsThreadModal) return;
        foreach (var window in windows.Keys.Append(toolbar).Distinct())
        {
            if (!window.IsVisible || !window.IsEnabled) continue;
            nint handle = new WindowInteropHelper(window).Handle;
            if (handle != 0 && IsWindow(handle) && IsWindowVisible(handle) && !IsWindowEnabled(handle))
                EnableWindow(handle, true);
        }
        RestoreKeyboardDestination();
    }

    internal IEnumerable<Window> OrderForRaise(IEnumerable<Window> candidates) => candidates
        .OrderBy(window => windows.TryGetValue(window, out var item) ? item.ActivationOrder : 0)
        .ThenBy(OwnerDepth);

    private static int OwnerDepth(Window window)
    {
        int depth = 0;
        for (var owner = window.Owner; owner is not null; owner = owner.Owner) depth++;
        return depth;
    }

    private void OnActivated(object? sender, EventArgs args) { if (sender is Window window) RememberActivation(window); }
    private void OnKeyboardFocus(object sender, KeyboardFocusChangedEventArgs args) { if (sender is Window window) RememberActivation(window); }
    private void RememberActivation(Window window)
    {
        long order = ++activationOrder;
        for (Window? current = window; current is not null && !ReferenceEquals(current, toolbar); current = current.Owner)
            if (windows.TryGetValue(current, out var item)) item.ActivationOrder = order;
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
        // An enhanced modal hands off after Closing is accepted, with its owners enabled.
        if (source.Resources[ModalResourceKey] is OverlayModal) return;
        if (disposed || !host.IsEditing || IsDirectTransfer || !OwnsEditingInput() || !ContainsForeground(source)) return;
        // WPF ShowDialog may still own a disabled parent. Its finally repeats this
        // handoff once WPF has finished reenabling the owner.
        ReturnToOwner(source.Owner);
    }

    private void ReturnToOwner(Window? firstOwner)
    {
        if (disposed || !host.IsEditing || IsDirectTransfer || !OwnsEditingInput()) return;
        for (var owner = firstOwner; owner is not null; owner = owner.Owner)
        {
            if (ActivateIfOwned(owner)) return;
        }
        if (!ActivateIfOwned(toolbar)) host.TryFocusEditingCanvas();
    }

    // WPF may restore native focus to the previously active property window even
    // after that window has hidden itself, or while the modal owner was disabled
    // by an asynchronous command. Reconcile again once the command reenables it.
    internal void RestoreKeyboardDestination()
    {
        if (disposed || !host.IsEditing || IsDirectTransfer || HasModalInput || ComponentDispatcher.IsThreadModal || !OwnsEditingInput()) return;
        nint handle = host.UsesSystemInput ? GetFocus() : GetForegroundWindow();
        if (FindKeyboardWindow(handle) is not { } window) return;
        if (!CanActivate(window))
        {
            using var transition = BeginTransition();
            ReturnToOwner(window.Owner);
            handle = GetFocus();
            if (FindKeyboardWindow(handle) is not { } destination) return;
            window = destination;
        }
        if (!CanActivate(window) || GetFocus() != new WindowInteropHelper(window).Handle) return;
        if (Keyboard.FocusedElement is DependencyObject focused && ReferenceEquals(Window.GetWindow(focused), window) &&
            (focused is UIElement { IsVisible: true, IsEnabled: true } || focused is ContentElement { IsEnabled: true })) return;
        // Restore command routing on the already active surface, without reviving
        // a remembered hidden editor or changing an existing live text selection.
        Keyboard.Focus(window);
    }

    // Focus can legitimately be absent while a native/modal window is closing.
    // HwndSource.FromHwnd throws for zero rather than returning null.
    internal Window? FindKeyboardWindow(nint handle) =>
        handle != 0 && HwndSource.FromHwnd(handle)?.RootVisual is Window window && ReferenceEquals(Find(window), this) ? window : null;

    private bool ActivateIfOwned(Window target)
    {
        if (disposed || !host.IsEditing || IsDirectTransfer || !CanActivate(target) || !OwnsEditingInput()) return false;
        if(host.UsesSystemInput) return host.TryFocusEditingWindow(new WindowInteropHelper(target).Handle);
        return target.Activate() && GetForegroundWindow() == new WindowInteropHelper(target).Handle;
    }

    private static bool CanActivate(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        return window.IsVisible && window.IsEnabled && window.WindowState != WindowState.Minimized &&
            handle != 0 && IsWindow(handle) && IsWindowVisible(handle) && IsWindowEnabled(handle);
    }

    private bool OwnsEditingInput()
    {
        if(host.UsesSystemInput) return host.HasEditingInput;
        var foreground = GetForegroundWindow();
        return foreground != 0 && GetWindowThreadProcessId(foreground, out var process) != 0 && process == Environment.ProcessId;
    }

    private bool ContainsForeground(Window source)
    {
        var sourceHandle = new WindowInteropHelper(source).Handle;
        if (sourceHandle == 0) return false;
        for (var handle = host.UsesSystemInput?GetFocus():GetForegroundWindow(); handle != 0; handle = GetWindow(handle, 4))
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
        suspendedWindows.Remove(window);
        window.Closing -= OnClosing; window.Closed -= OnClosed;
        window.Activated -= OnActivated; window.GotKeyboardFocus -= OnKeyboardFocus;
        window.Resources.Remove(ResourceKey);
    }

    public void Dispose()
    {
        if (disposed) return;
        CloseEditingModals(WindowDismissReason.Shutdown);
        disposed = true; ++nextIntent; intentToken = 0; destination = null;
        foreach (var window in windows.Keys.ToArray())
        {
            window.Closing -= OnClosing; window.Closed -= OnClosed;
            window.Activated -= OnActivated; window.GotKeyboardFocus -= OnKeyboardFocus;
            window.Resources.Remove(ResourceKey);
        }
        windows.Clear(); popups.Clear(); suspendedWindows.Clear(); modalWindows.Clear(); toolbar.Resources.Remove(ResourceKey);
        deferredOwnerRestores.Clear();
        if (ReferenceEquals(current, this)) current = previous;
    }

    private sealed class Registration
    {
        internal required Window Window;
        internal Func<bool>? Pinned;
        internal long ActivationOrder;
        internal Action<WindowDismissReason>? DismissTransient;
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        private Action? action = dispose;
        public void Dispose() { var current = action; action = null; current?.Invoke(); }
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct PixelPoint(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct WindowPosition
    {
        public nint Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }
    [DllImport("user32.dll")] private static extern nint WindowFromPhysicalPoint(PixelPoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint handle, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint first, uint second, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnableWindow(nint window, bool enabled);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint process);
}
