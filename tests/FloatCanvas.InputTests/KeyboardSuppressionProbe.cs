using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FloatCanvas.InputTests;

/// <summary>
/// Temporary X-only experiment, not an application input router or an IME solution.
/// The caller must supply a fast, thread-safe editing predicate and dispose the scope.
/// Posted messages target only the validated native input HWND; no input is reinjected.
/// </summary>
internal sealed class KeyboardSuppressionProbe : IDisposable
{
    private const int KeyboardLl = 13;
    private const uint KeyDown = 0x100, KeyUp = 0x101, SysKeyDown = 0x104, SysKeyUp = 0x105;
    private const uint Quit = 0x12, VkX = 0x58;
    private readonly nint _nativeInput;
    private readonly Func<bool> _canSuppress;
    private readonly HashSet<nint> _allowedForeground;
    private readonly bool _relay;
    private readonly HookProcedure _callback;
    private readonly Thread _thread;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nint _hook;
    private uint _threadId;
    private int _enabled = 1, _disposed, _lastHookError;
    private bool _suppressedXDown;
    private uint _lastScanCode, _lastFlags;
    private long _suppressedDown, _suppressedUp, _relayedDown, _relayedUp, _skippedOutsideScope;
    private Exception? _failure;

    private KeyboardSuppressionProbe(nint nativeInput, Func<bool> canSuppress, bool relay,
        IEnumerable<nint>? allowedForegroundWindows)
    {
        ArgumentNullException.ThrowIfNull(canSuppress);
        RequireOwnWindow(nativeInput);
        _nativeInput = nativeInput;
        _canSuppress = canSuppress;
        _relay = relay;
        _allowedForeground = allowedForegroundWindows is null ? [] : [.. allowedForegroundWindows];
        _allowedForeground.Add(nativeInput);
        foreach (var window in _allowedForeground) RequireOwnWindow(window);
        if ((GetAsyncKeyState((int)VkX) & 0x8000) != 0)
            throw new InvalidOperationException("Release X before starting the suppression probe.");
        _callback = OnKeyboard;
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "FloatCanvas X-only suppression probe" };
    }

    public static async Task<KeyboardSuppressionProbe> StartAsync(nint nativeInput, Func<bool> canSuppress,
        bool relayToNativeInput = true, IEnumerable<nint>? allowedForegroundWindows = null,
        CancellationToken cancellationToken = default)
    {
        var probe = new KeyboardSuppressionProbe(nativeInput, canSuppress, relayToNativeInput, allowedForegroundWindows);
        try
        {
            probe._thread.Start();
            await probe._started.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            return probe;
        }
        catch { probe.Dispose(); throw; }
    }

    public long SuppressedDown => Interlocked.Read(ref _suppressedDown);
    public long SuppressedUp => Interlocked.Read(ref _suppressedUp);
    public long RelayedDown => Interlocked.Read(ref _relayedDown);
    public long RelayedUp => Interlocked.Read(ref _relayedUp);
    public long SkippedOutsideScope => Interlocked.Read(ref _skippedOutsideScope);
    public int LastHookError => Volatile.Read(ref _lastHookError);
    public Exception? Failure => Volatile.Read(ref _failure);
    public bool IsThreadAlive => _thread.IsAlive;

    private static void RequireOwnWindow(nint window)
    {
        if (window == 0 || !IsWindow(window) || GetWindowThreadProcessId(window, out uint pid) == 0 || pid != Environment.ProcessId)
            throw new ArgumentException("The suppression probe accepts only live HWNDs owned by this test process.", nameof(window));
    }

    private void MessageLoop()
    {
        try
        {
            // Establish the thread queue before publishing its ID or accepting disposal.
            PeekMessage(out _, 0, 0, 0, 0);
            Volatile.Write(ref _threadId, GetCurrentThreadId());
            if (Volatile.Read(ref _enabled) == 0) { _started.TrySetCanceled(); return; }
            nint installed = SetWindowsHookEx(KeyboardLl, _callback, GetModuleHandle(null), 0);
            if (installed == 0)
            {
                int error = Marshal.GetLastWin32Error();
                Volatile.Write(ref _lastHookError, error);
                throw new Win32Exception(error, "The temporary X-only hook could not be installed.");
            }
            Interlocked.Exchange(ref _hook, installed);
            if (Volatile.Read(ref _enabled) == 0) { _started.TrySetCanceled(); return; }
            _started.TrySetResult();
            while (Volatile.Read(ref _enabled) != 0)
            {
                int result = GetMessage(out var message, 0, 0, 0);
                if (result == 0) break;
                if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "The suppression probe message loop failed.");
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception error)
        {
            Volatile.Write(ref _failure, error);
            _started.TrySetException(error);
        }
        finally
        {
            Volatile.Write(ref _enabled, 0);
            RemoveHook();
            BalanceLocalKeyUp();
            _started.TrySetCanceled();
            GC.KeepAlive(_callback);
        }
    }

    private nint OnKeyboard(int code, nuint message, nint data)
    {
        // Never inspect lParam when the hook chain requires unconditional forwarding.
        if (code != 0 || Volatile.Read(ref _enabled) == 0) return CallNextHookEx(0, code, message, data);
        if (message != KeyDown && message != KeyUp && message != SysKeyDown && message != SysKeyUp)
            return CallNextHookEx(0, code, message, data);
        try
        {
            var key = Marshal.PtrToStructure<KeyboardEvent>(data);
            if (key.VirtualKey != VkX) return CallNextHookEx(0, code, message, data);
            nint foreground = GetForegroundWindow();
            bool ownScope = _allowedForeground.Contains(foreground) &&
                GetWindowThreadProcessId(foreground, out uint pid) != 0 && pid == Environment.ProcessId &&
                IsWindow(_nativeInput) && GetWindowThreadProcessId(_nativeInput, out uint targetPid) != 0 &&
                targetPid == Environment.ProcessId && _canSuppress();
            if (!ownScope)
            {
                Interlocked.Increment(ref _skippedOutsideScope);
                BalanceLocalKeyUp();
                return CallNextHookEx(0, code, message, data);
            }
            bool up = message == KeyUp || message == SysKeyUp;
            // A key pressed before entering the scope must retain its physical release.
            if (up && !_suppressedXDown) return CallNextHookEx(0, code, message, data);
            bool previousDown = _suppressedXDown;
            if (_relay && !PostLocalMessage((uint)message, key.ScanCode, key.Flags, previousDown, up))
            {
                FailOpen(null); // Never swallow after relay failure.
                return CallNextHookEx(0, code, message, data);
            }
            _lastScanCode = key.ScanCode; _lastFlags = key.Flags;
            _suppressedXDown = !up;
            if (up) Interlocked.Increment(ref _suppressedUp); else Interlocked.Increment(ref _suppressedDown);
            return 1;
        }
        catch (Exception error)
        {
            FailOpen(error);
            return CallNextHookEx(0, code, message, data);
        }
    }

    private void FailOpen(Exception? error)
    {
        if (error is not null) Volatile.Write(ref _failure, error);
        Volatile.Write(ref _enabled, 0);
        // Wake GetMessage even when the failure happened inside a sent hook callback.
        uint thread = Volatile.Read(ref _threadId);
        if (thread != 0) PostThreadMessage(thread, Quit, 0, 0);
    }

    private bool PostLocalMessage(uint message, uint scanCode, uint flags, bool previousDown, bool up)
    {
        // Standard native key-message layout; this deliberately does not update any global key state.
        uint keyData = 1 | ((scanCode & 0xff) << 16) | ((flags & 1) << 24) |
            (((flags & 0x20) != 0 ? 1u : 0u) << 29) | ((previousDown || up ? 1u : 0u) << 30) | ((up ? 1u : 0u) << 31);
        if (!PostMessage(_nativeInput, message, VkX, unchecked((nint)(int)keyData)))
        {
            Volatile.Write(ref _lastHookError, Marshal.GetLastWin32Error());
            return false;
        }
        if (up) Interlocked.Increment(ref _relayedUp); else Interlocked.Increment(ref _relayedDown);
        return true;
    }

    private void BalanceLocalKeyUp()
    {
        if (!_suppressedXDown) return;
        _suppressedXDown = false;
        // Only balance the posted local stream. Never SendInput a release into another application.
        if (_relay && IsWindow(_nativeInput) && GetWindowThreadProcessId(_nativeInput, out uint pid) != 0 && pid == Environment.ProcessId)
            PostLocalMessage((_lastFlags & 0x20) != 0 ? SysKeyUp : KeyUp, _lastScanCode, _lastFlags, true, true);
    }

    private void RemoveHook()
    {
        nint hook = Interlocked.Exchange(ref _hook, 0);
        if (hook != 0 && !UnhookWindowsHookEx(hook)) Volatile.Write(ref _lastHookError, Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Volatile.Write(ref _enabled, 0); // Stop suppression even if posting quit or joining fails.
        RemoveHook();
        uint thread = Volatile.Read(ref _threadId);
        if (thread != 0 && _thread.IsAlive && !PostThreadMessage(thread, Quit, 0, 0))
            Volatile.Write(ref _lastHookError, Marshal.GetLastWin32Error());
        if (Thread.CurrentThread != _thread && _thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
        GC.KeepAlive(_callback);
    }

    [StructLayout(LayoutKind.Sequential)] private struct KeyboardEvent
    { public uint VirtualKey, ScanCode, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct Message
    { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint HookProcedure(int code, nuint message, nint data);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static extern nint SetWindowsHookEx(int type, HookProcedure callback, nint instance, uint threadId);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
