using System.ComponentModel;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using static FloatCanvas.Windows.NativeMethods;

namespace FloatCanvas.Windows;

/// <summary>One application-owned passive Raw Input receiver. It never suppresses or injects input.</summary>
public sealed class PresentationInputObserver : IPresentationInputSource
{
    private const uint InputSink = 0x100, DeviceNotify = 0x2000, Remove = 1;
    private static readonly object registrationGate = new();
    private readonly PresentationInputBuffer buffer = new();
    private readonly Dictionary<long, PresentationDeviceKind> deviceKinds = [];
    private readonly bool keyboard, pointer;
    private readonly Thread thread;
    private readonly WindowProcedure callback;
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nint window;
    private int stopped, accepting, error;
    private PresentationObservedCursor? previousCursor;
    private uint lockState;
    private int watchdogTicks;
    public long Epoch { get; }
    public bool IsRunning => Volatile.Read(ref accepting) != 0;
    public int LastError => Volatile.Read(ref error);
    public Task Completion => completed.Task;
    public event Action? Available;

    private PresentationInputObserver(long epoch, bool keyboard, bool pointer)
    {
        if (!keyboard && !pointer) throw new ArgumentException("No presentation input was requested.");
        Epoch = epoch; this.keyboard = keyboard; this.pointer = pointer; callback = Dispatch;
        thread = new Thread(Loop) { IsBackground = true, Name = "FloatCanvas presentation input" };
    }
    public static async Task<PresentationInputObserver> StartAsync(long epoch, bool keyboard, bool pointer,
        CancellationToken cancellationToken = default)
    {
        var source = new PresentationInputObserver(epoch, keyboard, pointer);
        try { source.thread.Start(); await source.started.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false); return source; }
        catch { source.Stop(); throw; }
    }
    public PresentationObservedBatch Drain() => buffer.Drain(Epoch);
    public bool Stop()
    {
        Volatile.Write(ref stopped, 1); Volatile.Write(ref accepting, 0); buffer.Close();
        bool removed;
        lock (registrationGate) removed = RemoveOwned();
        nint target = Volatile.Read(ref window); if (target != 0) PostMessageW(target, 0x10, 0, 0);
        return removed;
    }
    public void Dispose() => Stop();

    private void Loop()
    {
        string className = "FloatCanvas.PresentationInput." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N");
        nint module = GetModuleHandleW(null); bool registeredClass = false; nuint timer = 0;
        try
        {
            var klass = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = callback, Instance = module, ClassName = className };
            if (RegisterClassExW(ref klass) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            registeredClass = true;
            nint created = CreateWindowExW(0, className, "", 0, 0, 0, 0, 0, new(-3), 0, module, 0);
            if (created == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            Volatile.Write(ref window, created);
            lock (registrationGate)
            {
                if (Volatile.Read(ref stopped) != 0) { started.TrySetCanceled(); return; }
                var existing = Registrations();
                if (existing.Any(item => item.Page == 1 &&
                    (pointer && item.Usage == 2 || keyboard && item.Usage == 6 || keyboard && item.Usage == 0 && (item.Flags & 0x20) != 0)))
                    throw new InvalidOperationException("Another component already receives raw keyboard or mouse input.");
                var requested = Requested(created, InputSink | DeviceNotify);
                if (!Native.RegisterRawInputDevices(requested, (uint)requested.Length, (uint)Marshal.SizeOf<RawDevice>())) throw new Win32Exception(Marshal.GetLastWin32Error());
                Volatile.Write(ref accepting, 1);
            }
            PublishLockStates(Environment.TickCount64, force: true);
            timer = Native.SetTimer(created, 1, 33, 0); if (timer == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            started.TrySetResult();
            while (Volatile.Read(ref stopped) == 0)
            {
                int result = Native.GetMessage(out var message, 0, 0, 0);
                if (result == 0) break; if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                Native.TranslateMessage(ref message); Native.DispatchMessage(ref message);
            }
        }
        catch (Exception exception)
        {
            Volatile.Write(ref error, exception is Win32Exception win32 ? win32.NativeErrorCode : -1);
            started.TrySetException(exception); Signal();
        }
        finally
        {
            Volatile.Write(ref accepting, 0); Volatile.Write(ref stopped, 1); buffer.Close();
            lock (registrationGate) RemoveOwned();
            nint target = Interlocked.Exchange(ref window, 0);
            if (timer != 0 && target != 0) Native.KillTimer(target, timer);
            if (target != 0) DestroyWindow(target);
            if (registeredClass) Native.UnregisterClass(className, module);
            started.TrySetCanceled(); completed.TrySetResult(); Signal();
        }
    }

    private nint Dispatch(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (message == 0x10) { Native.PostQuitMessage(0); return 0; }
            if (Volatile.Read(ref accepting) != 0)
            {
                if (message == 0xFF) ReadInput(lParam);
                else if (message == 0xFE && wParam == 2)
                {
                    long device = lParam.ToInt64();
                    var kind = DeviceKind(lParam);
                    deviceKinds.Remove(device);
                    Add(new(PresentationObservedKind.DeviceRemoved, Environment.TickCount64, device, DeviceKind: kind));
                }
                else if (message == 0x113)
                {
                    int timerTick = ++watchdogTicks;
                    if (timerTick % 15 == 0 && !PresentationDesktopAvailability.IsCurrentInputDesktop())
                    { Volatile.Write(ref error, 5); Stop(); Signal(); }
                    else
                    {
                        if (keyboard && timerTick % 6 == 0) PublishLockStates(Environment.TickCount64);
                        if (pointer && PresentationDesktopAvailability.IsCurrentInputDesktop() && Native.GetPhysicalCursorPos(out var point))
                        {
                            var position = new PresentationObservedCursor(point.X, point.Y, Environment.TickCount64);
                            if (previousCursor is not { } prior || prior.X != point.X || prior.Y != point.Y)
                            { previousCursor = position; if (buffer.Move(position)) Signal(); }
                        }
                    }
                }
            }
        }
        catch (Exception exception)
        {
            Volatile.Write(ref error, exception is Win32Exception win32 ? win32.NativeErrorCode : -1); Stop(); Signal();
        }
        // Required for WM_INPUT cleanup as well as ordinary window messages.
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }
    private unsafe void ReadInput(nint raw)
    {
        uint size = 0; uint header = (uint)(IntPtr.Size == 8 ? 24 : 16);
        if (Native.GetRawInputData(raw, 0x10000003, null, ref size, header) == uint.MaxValue || size is < 32 or > 1024) return;
        byte* data = stackalloc byte[(int)size]; uint copied = Native.GetRawInputData(raw, 0x10000003, data, ref size, header);
        if (copied == uint.MaxValue || copied != size) return;
        var bytes = new ReadOnlySpan<byte>(data, (int)size);
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        long rawDevice = IntPtr.Size == 8
            ? BinaryPrimitives.ReadInt64LittleEndian(bytes[8..])
            : BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        if (type == 0) deviceKinds[rawDevice] = PresentationDeviceKind.Mouse;
        else if (type == 1) deviceKinds[rawDevice] = PresentationDeviceKind.Keyboard;
        long now = ExpandMessageTime(unchecked((uint)Native.GetMessageTime()), Environment.TickCount64);
        if (type == 1)
        {
            PublishLockStates(now);
            // The UI can use device-backed events when its ordinary editing lease
            // did not see the same edge through the hook or the native window.
            if (keyboard && rawDevice != 0 && GetWindowThreadProcessId(GetForegroundWindow(), out uint owner) != 0 &&
                owner == Environment.ProcessId && PresentationRawInputDecoder.TryKeyboard(bytes, IntPtr.Size, out var key))
                Add(new(key.Released ? PresentationObservedKind.RawKeyUp : PresentationObservedKind.RawKeyDown,
                    now, VirtualKey: key.VirtualKey, ScanCode: key.ScanCode, KeyFlags: key.Flags,
                    InputOwner: PresentationInputOwner.FloatCanvas));
        }
        if (PresentationRawInputDecoder.TryMouse(bytes, IntPtr.Size, out long device, out ushort buttons, out short wheel) && buttons != 0)
        {
            bool located = Native.GetPhysicalCursorPos(out var point);
            for (int button = 0; button < 5; button++)
            {
                if ((buttons & (1 << (button * 2))) != 0) Add(new(PresentationObservedKind.ButtonDown, now, device, X: point.X, Y: point.Y, HasPosition: located, Button: button, DeviceKind: PresentationDeviceKind.Mouse));
                if ((buttons & (2 << (button * 2))) != 0) Add(new(PresentationObservedKind.ButtonUp, now, device, X: point.X, Y: point.Y, HasPosition: located, Button: button, DeviceKind: PresentationDeviceKind.Mouse));
            }
            if (wheel != 0)
                Add(new(PresentationObservedKind.Wheel, now, device, Button: wheel > 0 ? 5 : 6, DeviceKind: PresentationDeviceKind.Mouse));
        }
    }
    private void PublishLockStates(long milliseconds, bool force = false)
    {
        uint current = 0;
        if (IsLocked(0x14)) current |= 1;
        if (IsLocked(0x90)) current |= 2;
        if (IsLocked(0x91)) current |= 4;
        foreach (var (virtualKey, bit) in LockKeys)
        {
            bool was = (lockState & bit) != 0, isLocked = (current & bit) != 0;
            if (force || was != isLocked)
                Add(new(PresentationObservedKind.LockStateChanged, milliseconds, VirtualKey: virtualKey, IsLocked: isLocked));
        }
        lockState = current;
    }
    // The low bit is the persistent toggle state for lock keys. GetAsyncKeyState's
    // corresponding bit answers "pressed since the previous query" and is not a
    // substitute for Caps/Num/Scroll lock state.
    private static bool IsLocked(int virtualKey) => (Native.GetKeyState(virtualKey) & 1) != 0;
    private static readonly (int VirtualKey, uint Bit)[] LockKeys = [(0x14, 1), (0x90, 2), (0x91, 4)];
    private void Add(PresentationObservedInput packet) { if (buffer.Add(packet)) Signal(); }
    private PresentationDeviceKind DeviceKind(nint device)
    {
        uint size = 0;
        uint result = Native.GetRawInputDeviceInfo(device, 0x2000000B, null, ref size);
        int error = Marshal.GetLastWin32Error();
        if (size is < 4 or > 4096 || result == uint.MaxValue && error != 122) return deviceKinds.GetValueOrDefault(device);
        var data = new byte[(int)size];
        if (Native.GetRawInputDeviceInfo(device, 0x2000000B, data, ref size) == uint.MaxValue) return deviceKinds.GetValueOrDefault(device);
        return DecodeDeviceType(data);
    }
    internal static PresentationDeviceKind DecodeDeviceType(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) return PresentationDeviceKind.Unknown;
        return BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4)) switch
        {
            0 => PresentationDeviceKind.Mouse,
            1 => PresentationDeviceKind.Keyboard,
            _ => PresentationDeviceKind.Unknown
        };
    }
    internal static long ExpandMessageTime(uint messageTime, long now) =>
        Math.Max(0, now - unchecked((uint)now - messageTime));
    private void Signal() { try { Available?.Invoke(); } catch { /* A detached UI subscriber cannot change input delivery. */ } }
    private RawDevice[] Requested(nint target, uint flags)
    {
        var devices = new List<RawDevice>(2);
        if (keyboard) devices.Add(new(1, 6, flags, target));
        if (pointer) devices.Add(new(1, 2, flags, target));
        return [.. devices];
    }
    private bool RemoveOwned()
    {
        nint target = Volatile.Read(ref window); if (target == 0) return true;
        try
        {
            var owned = Registrations().Where(item => item.Page == 1 && item.Usage is 2 or 6 && item.Target == target)
                .Select(item => new RawDevice(item.Page, item.Usage, Remove, 0)).ToArray();
            if (owned.Length == 0) return true;
            bool success = Native.RegisterRawInputDevices(owned, (uint)owned.Length, (uint)Marshal.SizeOf<RawDevice>());
            if (!success) Volatile.Write(ref error, Marshal.GetLastWin32Error()); return success;
        }
        catch (Exception exception) { Volatile.Write(ref error, exception is Win32Exception win32 ? win32.NativeErrorCode : -1); return false; }
    }
    private static RawDevice[] Registrations()
    {
        uint count = 0; uint size = (uint)Marshal.SizeOf<RawDevice>();
        uint initial = Native.GetRegisteredRawInputDevices(null, ref count, size);
        if (initial == uint.MaxValue && Marshal.GetLastWin32Error() != 122) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (count > 4096) throw new InvalidOperationException("Too many raw-input registrations.");
        if (count == 0) return [];
        var devices = new RawDevice[count]; uint received = Native.GetRegisteredRawInputDevices(devices, ref count, size);
        if (received == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error()); return devices.Take((int)received).ToArray();
    }
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct RawDevice(ushort Page, ushort Usage, uint Flags, nint Target);
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Message { internal nint Window; internal uint Id; internal nuint WParam; internal nint LParam; internal uint Time; internal Point Point; internal uint Private; }
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RegisterRawInputDevices([In] RawDevice[] devices, uint count, uint size);
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint GetRegisteredRawInputDevices([Out] RawDevice[]? devices, ref uint count, uint size);
        [DllImport("user32.dll", SetLastError = true)] internal static extern unsafe uint GetRawInputData(nint input, uint command, void* data, ref uint size, uint headerSize);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetPhysicalCursorPos(out Point point);
        [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
        internal static extern uint GetRawInputDeviceInfo(nint device, uint command, byte[]? data, ref uint size);
        [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)] internal static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] internal static extern nint DispatchMessage(ref Message message);
        [DllImport("user32.dll")] internal static extern void PostQuitMessage(int code);
        [DllImport("user32.dll")] internal static extern int GetMessageTime();
        [DllImport("user32.dll")] internal static extern short GetKeyState(int virtualKey);
        [DllImport("user32.dll", SetLastError = true)] internal static extern nuint SetTimer(nint window, nuint id, uint elapsed, nint procedure);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool KillTimer(nint window, nuint timer);
        [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnregisterClass(string name, nint module);
    }
}
