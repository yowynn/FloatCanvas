using L = FloatCanvas.Localization.AppText;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

/// <summary>
/// Replays an isolated key through this UI thread's normal WPF/TSF message pipeline.
/// This is a local message dispatch, never an insertion into the global input stream.
/// The owner advances one packet at a time after TranslateMessage, so queued characters
/// can run before the next packet changes the thread-local keyboard state.
/// </summary>
internal sealed class PrivateKeyboardBridge
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    /// <summary>
    /// A non-activating WPF Popup can assign logical keyboard focus while Win32 still
    /// points at the canvas HWND. Synchronize only in response to its actual pointer
    /// press, never by pulling a remembered TextBox back into focus on every key.
    /// </summary>
    internal static bool SynchronizePointerFocus(DependencyObject source)
    {
        if (PresentationSource.FromDependencyObject(source) is not HwndSource target || target.IsDisposed || target.Handle == 0) return false;
        if (GetWindowThreadProcessId(target.Handle, out uint process) != GetCurrentThreadId() || process != Environment.ProcessId ||
            !IsOurWindow(GetForegroundWindow())) return false;
        if (GetFocus() == target.Handle) return true;
        // Routed MouseDown may already have focused an editor in this Popup. WM_SETFOCUS
        // can restore the PopupRoot instead; preserve only this press's connected target,
        // never a remembered editor from another surface or a later keyboard packet.
        var routedFocus = Keyboard.FocusedElement as DependencyObject;
        bool preserveRoutedFocus = CanFocus(routedFocus) && ReferenceEquals(PresentationSource.FromDependencyObject(routedFocus!), target);
        SetFocus(target.Handle);
        if (GetFocus() == target.Handle && IsOurWindow(GetForegroundWindow()) && preserveRoutedFocus &&
            !ReferenceEquals(Keyboard.FocusedElement, routedFocus) && CanFocus(routedFocus) &&
            ReferenceEquals(PresentationSource.FromDependencyObject(routedFocus!), target))
            Keyboard.Focus((IInputElement)routedFocus!);
        return GetFocus() == target.Handle;
    }

    private static bool CanFocus(DependencyObject? element) => element switch
    {
        UIElement control => control.Focusable && control.IsEnabled && control.IsVisible,
        ContentElement content => content.Focusable && content.IsEnabled,
        _ => false
    };

    public bool Process(KeyboardInputPacket packet)
        => ProcessCore(packet);

    /// <summary>
    /// A command can enter a native or WPF modal loop before key dispatch returns. The
    /// queued UI operation can run only if that stack pumps messages; it then releases the
    /// current slot. The regular path still completes after Translate/Dispatch, so generated
    /// character messages precede the next packet. There is no timer or background ack.
    /// </summary>
    public bool Process(KeyboardInputPacket packet, Action completePacket)
    {
        _dispatcher.VerifyAccess();
        bool completed = false;
        void CompleteOnce()
        {
            if (completed) return;
            completed = true;
            completePacket();
        }
        EventHandler enteringModal = (_, _) => CompleteOnce();
        ComponentDispatcher.EnterThreadModal += enteringModal;
        // MessageBox.Show does not PushModal, and EnterThreadModal is raised only at the
        // outermost WPF modal boundary. A real reentrant UI pump executes this queued turn.
        var modalCompletion = _dispatcher.InvokeAsync(CompleteOnce, DispatcherPriority.Send);
        try { return ProcessCore(packet); }
        finally
        {
            modalCompletion.Abort();
            ComponentDispatcher.EnterThreadModal -= enteringModal;
            CompleteOnce();
        }
    }

    private bool ProcessCore(KeyboardInputPacket packet)
    {
        _dispatcher.VerifyAccess();
        if (packet.Kind != KeyboardInputPacketKind.Key) return false;
        // Updating this thread's key-state table does not update system asynchronous state.
        // Apply balancing releases even if the user has already chosen another foreground.
        var state = packet.CopyKeyboardState();
        var current = new byte[256];
        if (!GetKeyboardState(current))
            throw new Win32Exception(Marshal.GetLastWin32Error(), L.Get("App.ReadKeyboardStateFailed"));
        // A keyboard snapshot must not undo a concurrent mouse press in this UI thread.
        foreach (int button in new[] { 1, 2, 4, 5, 6 }) state[button] = current[button];
        if (!SetKeyboardState(state))
            throw new Win32Exception(Marshal.GetLastWin32Error(), L.Get("App.UpdateKeyboardStateFailed"));

        var foreground = GetForegroundWindow();
        var focus = GetFocus();
        if (!IsOurWindow(foreground) || !IsOurWindow(focus)) return false;

        uint flags = packet.Flags;
        uint keyData = 1 | ((packet.ScanCode & 0xff) << 16) | ((flags & 1) << 24) |
            (((flags & 0x20) != 0 ? 1u : 0u) << 29) |
            ((packet.IsRepeat || packet.IsKeyUp ? 1u : 0u) << 30) |
            ((packet.IsKeyUp ? 1u : 0u) << 31);
        bool system = (flags & 0x20) != 0;
        var message = new MSG
        {
            hwnd = focus,
            message = packet.NativeMessage is 0x100 or 0x101 or 0x104 or 0x105
                ? (int)packet.NativeMessage
                : packet.IsKeyUp ? (system ? 0x105 : 0x101) : (system ? 0x104 : 0x100),
            wParam = (nint)packet.VirtualKey,
            lParam = unchecked((nint)(int)keyData),
            time = unchecked((int)packet.Time)
        };
        // A private DispatchMessage would bypass the native dialog manager's Tab/Enter,
        // Escape and mnemonic handling. Do not run this path for WPF windows or a foreign
        // HWND: only the actual foreground #32770 root on this same UI thread qualifies.
        if (TryGetOwnNativeDialog(foreground, focus, out var dialog) && IsDialogMessage(dialog, ref message))
            return true; // IsDialogMessage already translates/dispatches a handled message.

        // This is the same public preprocessing entry used by the WPF Dispatcher.
        // In particular, do not bypass TSF with synthesized TextInput/WM_CHAR events.
        if (!ComponentDispatcher.RaiseThreadMessage(ref message))
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
        return true;
    }

    private static bool IsOurWindow(nint window) => window != 0 &&
        GetWindowThreadProcessId(window, out uint processId) != 0 && processId == Environment.ProcessId;

    private static bool TryGetOwnNativeDialog(nint foreground, nint focus, out nint dialog)
    {
        dialog = 0;
        nint root = GetAncestor(focus, 2); // GA_ROOT; owner windows are not part of this chain.
        uint thread = GetCurrentThreadId();
        if (root == 0 || root != foreground || GetWindowThreadProcessId(root, out uint processId) != thread ||
            processId != Environment.ProcessId || GetWindowThreadProcessId(focus, out uint focusProcessId) != thread ||
            focusProcessId != Environment.ProcessId) return false;
        var name = new StringBuilder(32);
        if (GetClassName(root, name, name.Capacity) == 0 || name.ToString() != "#32770") return false;
        dialog = root; return true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKeyboardState([In] byte[] state);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState([Out] byte[] state);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder name, int capacity);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "IsDialogMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsDialogMessage(nint dialog, ref MSG message);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern nint DispatchMessage(ref MSG message);
}
