using L = FloatCanvas.Localization.AppText;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FloatCanvas.Windows;

/// <summary>
/// Suppresses keyboard input during an explicitly maintained editing lease. No SendInput,
/// target-process injection, WPF dispatch or foreground activation is performed here.
/// A second read-only hook records exact mouse-press modifiers on this same input thread;
/// it always passes the original mouse event unchanged.
/// A matching held-key tail remains suppressed until its release or two seconds of stream silence.
/// Only matching observed hook releases (VK/scan/extended flag) clear a tail early; the zero
/// global async state of a suppressed key is not evidence that the physical key was released.
/// Windows chords pass to the OS; actual external foreground and Alt+Tab/etc end the lease.
/// Already-suppressed modifiers are never reconstructed globally,
/// so this alone does not promise a working OS Alt+Tab chord. Ctrl+Alt+Esc releases the lease
/// on the hook thread. Hook/native/notification failures unconditionally fail open, including tails.
/// </summary>
public sealed class KeyboardInputIsolation : IDisposable
{
    private readonly object gate = new();
    private readonly KeyboardSuppressionState state;
    private readonly KeyboardModifierHistory modifierHistory;
    private readonly IsolatedPointerPresses pointerPresses;
    private readonly CaptureInputObservationBuffer inputObservations = new();
    private readonly nint notificationWindow;
    private readonly uint notificationThreadId;
    private readonly uint notificationMessage;
    private readonly long heartbeatMilliseconds;
    private readonly Native.HookProcedure callback;
    private readonly Native.HookProcedure pointerCallback;
    private readonly Thread thread;
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nint hook, pointerHook;
    private uint threadId;
    private int stopped, disposed, lastWin32Error;
    private long lastHeartbeat, externalInputVersion;
    private Exception? failure;
    private bool notificationFailed;

    private KeyboardInputIsolation(nint window, uint message, int queueCapacity, TimeSpan timeout)
    {
        RequireOwnWindow(window);
        if (message is < 0x8000 or > 0xBFFF) throw new ArgumentOutOfRangeException(nameof(message), L.Get("Keyboard.PrivateMessageRequired"));
        if (timeout < TimeSpan.FromMilliseconds(250) || timeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        notificationWindow = window; notificationMessage = message; heartbeatMilliseconds = (long)timeout.TotalMilliseconds;
        notificationThreadId = Native.GetWindowThreadProcessId(window, out _);
        state = new(queueCapacity, 2000);
        modifierHistory = new(queueCapacity + 1);
        pointerPresses = new(queueCapacity);
        callback = OnKeyboard;
        pointerCallback = OnPointer;
        thread = new Thread(MessageLoop) { Name = "FloatCanvas private keyboard input", IsBackground = true };
    }

    public static async Task<KeyboardInputIsolation> StartAsync(nint notificationWindow, uint notificationMessage,
        int queueCapacity = 256, TimeSpan? heartbeatTimeout = null, CancellationToken cancellationToken = default)
    {
        var service = new KeyboardInputIsolation(notificationWindow, notificationMessage, queueCapacity,
            heartbeatTimeout ?? TimeSpan.FromMilliseconds(1200));
        try
        {
            service.thread.Start();
            await service.started.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            return service;
        }
        catch { service.Dispose(); throw; }
    }

    public bool IsLeaseActive { get { lock (gate) return state.IsActive; } }
    public long CurrentLeaseId { get { lock (gate) return state.LeaseId; } }
    public int QueuedPacketCount { get { lock (gate) return state.QueuedCount; } }
    public long SuppressedCount { get { lock (gate) return state.SuppressedCount; } }
    public long TailSuppressedCount { get { lock (gate) return state.TailSuppressedCount; } }
    public KeyboardLeaseEndReason? LastEndReason { get { lock (gate) return state.LastEndReason; } }
    public int LastWin32Error => Volatile.Read(ref lastWin32Error);
    public Exception? Failure => Volatile.Read(ref failure);
    public bool IsThreadAlive => thread.IsAlive;
    // The hooks remain installed during clean capture, but never suppress external input.
    internal long ExternalInputVersion => Interlocked.Read(ref externalInputVersion);
    // The UI takes a bounded immutable copy only when publishing a capture boundary.
    internal IReadOnlyList<CaptureInputObservation> GetCaptureInputObservationsAfter(long version, uint? startTime = null)
    { lock (gate) return inputObservations.SnapshotAfter(version, startTime); }
    internal bool TryGetModifiers(uint? messageTime, out OverlayModifiers modifiers)
    {
        lock (gate)
        {
            modifiers = messageTime is { } time ? modifierHistory.At(time, Environment.TickCount64) : state.CurrentModifiers;
            return state.IsActive;
        }
    }
    internal bool TryGetPointerModifiers(uint messageTime, OverlayPoint position, OverlayMouseButton button,
        bool isPress, out OverlayModifiers modifiers)
    {
        lock (gate)
        {
            if (!state.IsActive) { modifiers = OverlayModifiers.None; return false; }
            if (!isPress || !pointerPresses.TryTake(messageTime, position, button, out modifiers))
                modifiers = modifierHistory.At(messageTime, Environment.TickCount64);
            return true;
        }
    }
    /// <summary>Tail idle timeout. Continuing matching repeats renew it until the observed keyup.</summary>
    public TimeSpan TailQuarantineTimeout => TimeSpan.FromSeconds(2);

    /// <summary>
    /// Call on the UI thread after verifying editing entry. Does not wait for the hook thread.
    /// Keys already globally down retain their repeat/release stream; they are not isolated.
    /// Synchronously resets this thread's private keyboard state for the new lease, preserving
    /// current mouse-button bits. Stale old-lease balances can then safely be ignored by the UI.
    /// </summary>
    public long BeginLease(HotkeyBinding toggle, HotkeyBinding hide)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Volatile.Read(ref stopped) != 0 || Failure is not null) throw new InvalidOperationException(L.Get("Keyboard.IsolationUnavailable"), Failure);
        ValidateHotkey(toggle); ValidateHotkey(hide);
        if (!OwnsWindow(Native.GetForegroundWindow())) throw new InvalidOperationException(L.Get("Keyboard.ForegroundRequired"));
        lock (gate)
        {
            if (Volatile.Read(ref stopped) != 0) throw new InvalidOperationException(L.Get("Keyboard.IsolationStopped"));
            return BeginLeaseCore(toggle, hide);
        }
    }

    /// <summary>
    /// A caller-owned short internal transition may replace exactly one foreground-ended
    /// lease. Rechecks reason, identity and real foreground under the hook-state lock;
    /// no window activation, focus change or global input reconstruction occurs.
    /// </summary>
    internal bool TryResumeForegroundLease(long endedLease, HotkeyBinding toggle, HotkeyBinding hide, out long lease)
    {
        lease = 0;
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0 || Volatile.Read(ref stopped) != 0 || Failure is not null ||
                state.LeaseId != endedLease || !state.CanResumeForegroundLoss ||
                !OwnsWindow(Native.GetForegroundWindow()) || !IsInputDesktopAvailable()) return false;
            lease = BeginLeaseCore(toggle, hide);
            return true;
        }
    }

    internal bool TryGetInputMethodSwitch(long expectedLease, out nint origin)
    {
        lock (gate)
        {
            origin = 0;
            return Failure is null && Volatile.Read(ref stopped) == 0 &&
                state.TryGetInputMethodSwitch(expectedLease, Environment.TickCount64, out origin) && OwnsWindow(origin);
        }
    }

    internal void CancelInputMethodSwitch() { lock (gate) state.CancelInputMethodSwitch(); }

    internal bool TryResumeInputMethodLease(long endedLease, HotkeyBinding toggle, HotkeyBinding hide, out long lease)
    {
        lock (gate)
        {
            lease = 0;
            if (Volatile.Read(ref disposed) != 0 || Volatile.Read(ref stopped) != 0 || Failure is not null ||
                !state.CanResumeForegroundLoss || !state.TryGetInputMethodSwitch(endedLease, Environment.TickCount64, out var origin) ||
                Native.GetForegroundWindow() != origin || !OwnsWindow(origin) || !IsInputDesktopAvailable()) return false;
            lease = BeginLeaseCore(toggle, hide);
            return true;
        }
    }

    private long BeginLeaseCore(HotkeyBinding toggle, HotkeyBinding hide)
    {
        var initial = new byte[256];
        if (!Native.GetKeyboardState(initial)) throw new Win32Exception(Marshal.GetLastWin32Error());
        // Serialize this brief snapshot with hook-state updates. No UI callback/wait occurs.
        // Never infer inherited keys from the relay's possibly still-down local state.
        for (int i = 1; i < 255; i++)
            if (i is not (1 or 2 or 4 or 5 or 6))
                initial[i] = (byte)((initial[i] & 0x7F) | (Native.GetAsyncKeyState(i) < 0 ? 0x80 : 0));
        long now = Environment.TickCount64;
        Volatile.Write(ref lastHeartbeat, now);
        long lease = state.Begin(initial, toggle, hide, now);
        modifierHistory.Reset(now, state.CurrentModifiers);
        pointerPresses.Clear();
        if (!Native.SetKeyboardState(state.CopyLocalState()))
        {
            int error = Marshal.GetLastWin32Error();
            Volatile.Write(ref lastWin32Error, error);
            state.End(KeyboardLeaseEndReason.HookFailure, now); NotifyLocked();
            throw new Win32Exception(error, L.Get("Keyboard.LocalStateInitializationFailed"));
        }
        NotifyLocked();
        if (!state.IsActive || Volatile.Read(ref stopped) != 0)
            throw new InvalidOperationException(L.Get("Keyboard.UiNotificationFailed"), Failure);
        return lease;
    }

    public void EndLease(KeyboardLeaseEndReason reason = KeyboardLeaseEndReason.Explicit)
    { lock (gate) { state.End(reason, Environment.TickCount64); pointerPresses.Clear(); NotifyLocked(); } }
    /// <summary>Atomically replaces both private command bindings without restarting an active lease.</summary>
    public void UpdateHotkeys(HotkeyBinding toggle, HotkeyBinding hide)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ValidateHotkey(toggle); ValidateHotkey(hide);
        if (toggle == hide) throw new ArgumentException(L.Get("Hotkeys.DuplicateBindings"));
        lock (gate) state.UpdateHotkeys(toggle, hide);
    }
    /// <summary>Call regularly from the UI dispatcher, including while static and in modal loops.</summary>
    public void Heartbeat() => Volatile.Write(ref lastHeartbeat, Environment.TickCount64);

    /// <summary>
    /// Claims one packet. Until CompletePacket, no following packet notification is posted.
    /// Keep that packet's local keyboard state through the resulting character/TSF processing.
    /// </summary>
    public bool TryDequeue(out KeyboardInputPacket? packet)
    { lock (gate) return state.TryDequeue(out packet); }
    /// <summary>Call after local TranslateMessage/dispatch; queued WM_CHAR must precede the next wake.</summary>
    public void CompletePacket(long sequence)
    { lock (gate) { if (state.Complete(sequence)) NotifyLocked(); } }

    private void NotifyLocked()
    {
        if (notificationFailed || !state.NeedsNotification) return;
        bool own = OwnsWindow(notificationWindow);
        if (!own || !Native.PostMessage(notificationWindow, notificationMessage, 0, 0))
        {
            int error = own ? Marshal.GetLastWin32Error() : 1400;
            Volatile.Write(ref lastWin32Error, error);
            Volatile.Write(ref failure, new Win32Exception(error, "The private keyboard queue could not notify its UI."));
            notificationFailed = true;
            state.End(KeyboardLeaseEndReason.NotificationFailed, Environment.TickCount64);
            // There is no functioning UI sink to balance: stop all suppression and remove the
            // hook through its own pump. Passing the current event must also pass its future up.
            Volatile.Write(ref stopped, 1);
            uint id = Volatile.Read(ref threadId);
            if (id != 0) Native.PostThreadMessage(id, 0x12, 0, 0);
            return;
        }
        state.MarkNotificationPosted();
    }

    private nint OnKeyboard(int code, nuint message, nint data)
    {
        if (code != 0 || Volatile.Read(ref stopped) != 0 || message is not (0x100 or 0x101 or 0x104 or 0x105))
            return Native.CallNextHookEx(0, code, message, data);
        try
        {
            var key = Marshal.PtrToStructure<Native.KeyboardEvent>(data);
            long now = Environment.TickCount64;
            bool suppress;
            lock (gate)
            {
                // Sample the foreground in the same critical section as the lease identity.
                // An old outside snapshot must not end a lease the UI has just replaced.
                nint foreground = Native.GetForegroundWindow();
                bool own = OwnsWindow(foreground);
                long tailsBefore = state.TailSuppressedCount;
                bool shiftKey=key.VirtualKey is 0x10 or 0xA0 or 0xA1;
                OverlayPoint? pointerPosition=shiftKey&&Native.GetPhysicalCursorPos(out var cursor)?new(cursor.X,cursor.Y):null;
                bool? pointerLeftDown=shiftKey?Native.GetAsyncKeyState(1)<0:null;
                suppress = state.Process(key.VirtualKey, key.ScanCode, key.Flags, key.Time, message is 0x101 or 0x105,
                    (uint)message, foreground, own, now - Volatile.Read(ref lastHeartbeat) <= heartbeatMilliseconds, now,pointerPosition,pointerLeftDown);
                if (!own && message is 0x100 or 0x104)
                {
                    long observedVersion = CaptureInputIntent.CountsKeyboard(own, suppress)
                        ? Interlocked.Increment(ref externalInputVersion) : ExternalInputVersion;
                    inputObservations.Record(new(observedVersion, CaptureInputSource.KeyboardDown, key.Time, key.Flags,
                        foreground, 0, suppress, state.TailSuppressedCount != tailsBefore));
                }
                if (state.IsActive) modifierHistory.Record(key.Time, now, state.CurrentModifiers);
                NotifyLocked();
            }
            return suppress && Volatile.Read(ref stopped) == 0 ? 1 : Native.CallNextHookEx(0, code, message, data);
        }
        catch (Exception error)
        {
            Fail(error);
            return Native.CallNextHookEx(0, code, message, data);
        }
    }

    private nint OnPointer(int code, nuint message, nint data)
    {
        if (code == 0 && Volatile.Read(ref stopped) == 0 && IsCapturePointerInteraction(message))
        {
            try
            {
                var press = Marshal.PtrToStructure<Native.MouseEvent>(data);
                nint foreground = Native.GetForegroundWindow();
                bool own = OwnsWindow(foreground);
                nint target = Native.WindowFromPoint(new(press.X, press.Y));
                bool externalTarget = target != 0 && !OwnsWindow(target);
                // WindowFromPoint is not the recipient while our foreground UI thread
                // owns mouse capture (including the legacy offscreen input carrier).
                var info = new Native.GuiThreadInfo { Size = (uint)Marshal.SizeOf<Native.GuiThreadInfo>() };
                bool ownCapture = externalTarget && own && Native.GetGUIThreadInfo(notificationThreadId, ref info) && OwnsWindow(info.Capture);
                long now = Environment.TickCount64;
                lock (gate)
                {
                    if (CaptureInputIntent.CountsPointer(externalTarget, own, ownCapture))
                    {
                        long observedVersion = Interlocked.Increment(ref externalInputVersion);
                        inputObservations.Record(new(observedVersion,
                            message is 0x20A or 0x20E ? CaptureInputSource.PointerWheel : CaptureInputSource.PointerPress,
                            press.Time, press.Flags, foreground, target, false, false));
                        state.ObserveExternalPointerPress(now);
                        pointerPresses.Clear();
                        NotifyLocked();
                    }
                    if (message is 0x201 or 0x204 or 0x207 && own && state.IsActive &&
                        now - Volatile.Read(ref lastHeartbeat) <= heartbeatMilliseconds)
                        pointerPresses.Record((uint)message, press.Time, new(press.X, press.Y), state.CurrentModifiers);
                }
            }
            catch (Exception error) { Fail(error); }
        }
        // Observation only: this hook never blocks, redirects, or reconstructs mouse input.
        return Native.CallNextHookEx(0, code, message, data);
    }

    // A wheel/X-button action is explicit input even if it leaves foreground on the
    // same external HWND. Merely moving the pointer or releasing a button is not.
    internal static bool IsCapturePointerInteraction(nuint message) => message is
        0x201 or 0x204 or 0x207 or 0x20A or 0x20B or 0x20D or 0x20E;

    private void Watchdog()
    {
        long now = Environment.TickCount64;
        long observedLease;
        lock (gate)
        {
            if (!state.IsActive) { state.ExpireTails(now); NotifyLocked(); return; }
            observedLease = state.LeaseId;
        }
        bool own = OwnsWindow(Native.GetForegroundWindow()) && IsInputDesktopAvailable();
        lock (gate)
        {
            state.ExpireTails(now);
            if (state.IsActive && state.LeaseId == observedLease)
            {
                if (!own) state.End(KeyboardLeaseEndReason.ForegroundLost, now);
                else if (now - Volatile.Read(ref lastHeartbeat) > heartbeatMilliseconds) state.End(KeyboardLeaseEndReason.HeartbeatExpired, now);
            }
            NotifyLocked();
        }
    }

    private void MessageLoop()
    {
        nuint timer = 0;
        try
        {
            Native.PeekMessage(out _, 0, 0, 0, 0);
            Volatile.Write(ref threadId, Native.GetCurrentThreadId());
            if (Volatile.Read(ref stopped) != 0) { started.TrySetCanceled(); return; }
            nint installed = Native.SetWindowsHookEx(13, callback, Native.GetModuleHandle(null), 0);
            if (installed == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), L.Get("Keyboard.HookInstallationFailed"));
            Interlocked.Exchange(ref hook, installed);
            nint installedPointer = Native.SetWindowsHookEx(14, pointerCallback, Native.GetModuleHandle(null), 0);
            if (installedPointer == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), L.Get("Keyboard.HookInstallationFailed"));
            Interlocked.Exchange(ref pointerHook, installedPointer);
            timer = Native.SetTimer(0, 0, 100, 0);
            if (timer == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), L.Get("Keyboard.WatchdogStartFailed"));
            if (Volatile.Read(ref stopped) != 0) { started.TrySetCanceled(); return; }
            started.TrySetResult();
            while (Volatile.Read(ref stopped) == 0)
            {
                int result = Native.GetMessage(out var message, 0, 0, 0);
                if (result == 0) break;
                if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (message.Id == 0x113 && message.WParam == timer) Watchdog();
                else { Native.TranslateMessage(ref message); Native.DispatchMessage(ref message); }
            }
        }
        catch (Exception error) { Fail(error); started.TrySetException(error); }
        finally
        {
            Volatile.Write(ref stopped, 1);
            lock (gate) { state.End(KeyboardLeaseEndReason.HookFailure, Environment.TickCount64); NotifyLocked(); }
            if (timer != 0) Native.KillTimer(0, timer);
            RemoveHook(); started.TrySetCanceled(); GC.KeepAlive(callback); GC.KeepAlive(pointerCallback);
        }
    }

    private void Fail(Exception error)
    {
        Volatile.Write(ref failure, error);
        lock (gate) { state.End(KeyboardLeaseEndReason.HookFailure, Environment.TickCount64); NotifyLocked(); }
        // A native/callback failure is unconditionally fail-open, including held-key tails.
        Volatile.Write(ref stopped, 1);
        uint id = Volatile.Read(ref threadId);
        if (id != 0) Native.PostThreadMessage(id, 0x12, 0, 0);
    }
    private void RemoveHook()
    {
        nint installed = Interlocked.Exchange(ref hook, 0);
        if (installed != 0 && !Native.UnhookWindowsHookEx(installed)) Volatile.Write(ref lastWin32Error, Marshal.GetLastWin32Error());
        nint installedPointer = Interlocked.Exchange(ref pointerHook, 0);
        if (installedPointer != 0 && !Native.UnhookWindowsHookEx(installedPointer)) Volatile.Write(ref lastWin32Error, Marshal.GetLastWin32Error());
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        EndLease(KeyboardLeaseEndReason.Disposed);
        Volatile.Write(ref stopped, 1); RemoveHook();
        uint id = Volatile.Read(ref threadId);
        if (id != 0 && thread.IsAlive) Native.PostThreadMessage(id, 0x12, 0, 0);
        if (Thread.CurrentThread != thread && thread.IsAlive) thread.Join(TimeSpan.FromSeconds(2));
        GC.KeepAlive(callback); GC.KeepAlive(pointerCallback);
    }

    private static bool OwnsWindow(nint window) => window != 0 && Native.IsWindow(window) &&
        Native.GetWindowThreadProcessId(window, out uint pid) != 0 && pid == Environment.ProcessId;
    private static void RequireOwnWindow(nint window)
    { if (!OwnsWindow(window)) throw new ArgumentException(L.Get("Keyboard.OwnNotificationWindowRequired"), nameof(window)); }
    /// <summary>Validates the input-isolation contract without native calls or changing any saved setting.</summary>
    public static void ValidateHotkey(HotkeyBinding binding) => KeyboardSuppressionState.ValidateHotkey(binding);
    private static bool IsInputDesktopAvailable()
    {
        nint desktop = Native.OpenInputDesktop(0, false, 1);
        if (desktop == 0) return false;
        try
        {
            nint current = Native.GetThreadDesktop(Native.GetCurrentThreadId());
            // Desktop names are local to the current window station. Fixed stack buffers avoid
            // allocations; this native check runs only on the watchdog, never inside the hook.
            return current != 0 && DesktopNamesEqual(desktop, current);
        }
        finally { Native.CloseDesktop(desktop); }
    }
    private static unsafe bool DesktopNamesEqual(nint input, nint current)
    {
        const int capacity = 256;
        char* inputName = stackalloc char[capacity]; char* currentName = stackalloc char[capacity];
        if (!Native.GetUserObjectInformation(input, 2, inputName, capacity * sizeof(char), out uint inputLength) ||
            !Native.GetUserObjectInformation(current, 2, currentName, capacity * sizeof(char), out uint currentLength) ||
            inputLength == 0 || inputLength != currentLength || inputLength > capacity * sizeof(char)) return false;
        return new ReadOnlySpan<char>(inputName, (int)inputLength / sizeof(char)).Equals(
            new ReadOnlySpan<char>(currentName, (int)currentLength / sizeof(char)), StringComparison.OrdinalIgnoreCase);
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct KeyboardEvent
        { public uint VirtualKey, ScanCode, Flags, Time; public nuint ExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] internal struct MouseEvent
        { public int X, Y; public uint Data, Flags, Time; public nuint ExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] internal readonly struct Point(int x, int y)
        { internal readonly int X = x, Y = y; }
        [StructLayout(LayoutKind.Sequential)] internal struct Message
        { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
        [StructLayout(LayoutKind.Sequential)] internal struct GuiThreadInfo
        {
            public uint Size, Flags;
            public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
            public int CaretLeft, CaretTop, CaretRight, CaretBottom;
        }
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate nint HookProcedure(int code, nuint message, nint data);
        [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] internal static extern nint SetWindowsHookEx(int type, HookProcedure callback, nint instance, uint thread);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWindowsHookEx(nint hook);
        [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
        [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)] internal static extern int GetMessage(out Message message, nint window, uint min, uint max);
        [DllImport("user32.dll", EntryPoint = "PeekMessageW")] internal static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
        [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] internal static extern nint DispatchMessage(ref Message message);
        [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)] internal static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
        [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)] internal static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetPhysicalCursorPos(out Point point);
        [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
        [DllImport("user32.dll")] internal static extern bool IsWindow(nint window);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetKeyboardState([Out] byte[] state);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetKeyboardState([In] byte[] state);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", SetLastError = true)] internal static extern nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);
        [DllImport("user32.dll")] internal static extern bool KillTimer(nint window, nuint id);
        [DllImport("user32.dll", SetLastError = true)] internal static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
        [DllImport("user32.dll")] internal static extern bool CloseDesktop(nint desktop);
        [DllImport("user32.dll")] internal static extern nint GetThreadDesktop(uint thread);
        [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)] internal static extern unsafe bool GetUserObjectInformation(nint handle, int index, void* information, uint length, out uint required);
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
    }
}
