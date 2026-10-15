using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FloatCanvas.MouseTests;

internal sealed record MousePacket(long Sequence, long Timestamp, string Kind, PixelPoint Point, uint MouseData,
    uint Flags, uint NativeTime, ulong ActualExtra, ulong ExpectedTag, bool IsStimulus, string[] HeldButtons)
{
    public string ActualExtraHex => $"0x{ActualExtra:X16}";
    public string ExpectedTagHex => $"0x{ExpectedTag:X16}";
    // Diagnostic only: IsStimulus and the identity assertion continue to require the complete ULONG_PTR value.
    public bool Low32MatchesExpectedTag => unchecked((uint)ActualExtra) == unchecked((uint)ExpectedTag);
}

/// <summary>
/// Temporary, exact-own-HWND, synthetic-stimulus experiment. Never a production mouse router.
/// The callback only validates its lease, updates a small ledger and queues private immutable data.
/// No UI invocation, synchronous waits, global reinjection or WPF mouse emulation occur here.
/// </summary>
internal sealed class MouseSuppressionScope : IDisposable
{
    private readonly nint _target;
    private readonly Native.HookProcedure _procedure;
    private readonly Thread _thread;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<MousePacket> _packets = new();
    private readonly HashSet<string> _held = [];
    private nint _hook;
    private uint _threadId;
    private int _enabled = 1, _disposed, _lastError, _scopeLost;
    private long _sequence, _forwarded, _suppressed;
    private Exception? _failure;
    public long Suppressed => Interlocked.Read(ref _suppressed);
    public long ForwardedOutsideLease => Interlocked.Read(ref _forwarded);
    public int LastError => Volatile.Read(ref _lastError);
    public bool ScopeLost => Volatile.Read(ref _scopeLost) != 0;
    public bool IsThreadAlive => _thread.IsAlive;
    public bool UnhookSucceeded { get; private set; }
    public string[] CanceledHeldButtons { get; private set; } = [];
    public Exception? Failure => Volatile.Read(ref _failure);

    private MouseSuppressionScope(nint target)
    {
        if (!Native.IsWindow(target) || Native.Pid(target) != Environment.ProcessId)
            throw new ArgumentException("Only a live native window belonging to this test process is allowed.", nameof(target));
        if (Native.ButtonKeys.Any(key => (Native.GetAsyncKeyState(key) & 0x8000) != 0))
            throw new InvalidOperationException("Release all mouse buttons before installing the test scope.");
        if (Native.GetForegroundWindow() != target) throw new InvalidOperationException("The own target must be foreground at scope entry.");
        _target = target; _procedure = Callback;
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "FloatCanvas test-only mouse hook" };
    }
    public static async Task<MouseSuppressionScope> StartAsync(nint target)
    {
        var scope = new MouseSuppressionScope(target);
        try { scope._thread.Start(); await scope._started.Task.WaitAsync(TimeSpan.FromSeconds(3)); return scope; }
        catch { scope.Dispose(); throw; }
    }
    public int DrainTo(ProbeWindow target)
    {
        int count = 0;
        while (_packets.TryDequeue(out var packet)) { target.AcceptPrivate(packet); count++; }
        return count;
    }
    private void MessageLoop()
    {
        try
        {
            Native.PeekMessage(out _, 0, 0, 0, 0);
            Volatile.Write(ref _threadId, Native.GetCurrentThreadId());
            if (Volatile.Read(ref _enabled) == 0) { _started.TrySetCanceled(); return; }
            nint hook = Native.SetWindowsHookEx(14, _procedure, Native.GetModuleHandle(null), 0);
            if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "WH_MOUSE_LL installation failed.");
            Interlocked.Exchange(ref _hook, hook); _started.TrySetResult();
            while (Volatile.Read(ref _enabled) != 0)
            {
                int status = Native.GetMessage(out var message, 0, 0, 0);
                if (status == 0) break;
                if (status < 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Mouse hook message loop failed.");
                Native.TranslateMessage(ref message); Native.DispatchMessage(ref message);
            }
        }
        catch (Exception error) { Volatile.Write(ref _failure, error); _started.TrySetException(error); }
        finally
        {
            Volatile.Write(ref _enabled, 0); RemoveHook();
            // Swallowed Down never entered global state: cancel private held state, never synthesize a global Up here.
            CanceledHeldButtons = [.. _held]; _held.Clear();
            _started.TrySetCanceled(); GC.KeepAlive(_procedure);
        }
    }
    private nint Callback(int code, nuint message, nint data)
    {
        if (code != 0 || Volatile.Read(ref _enabled) == 0 || !Native.IsMouse((uint)message))
            return Native.CallNextHookEx(0, code, message, data);
        try
        {
            if (Native.GetForegroundWindow() != _target || Native.Pid(_target) != Environment.ProcessId)
            {
                Interlocked.Increment(ref _forwarded); Volatile.Write(ref _scopeLost, 1); Volatile.Write(ref _enabled, 0);
                Native.PostThreadMessage(_threadId, 0x12, 0, 0);
                return Native.CallNextHookEx(0, code, message, data);
            }
            var value = Marshal.PtrToStructure<Native.MouseHookData>(data);
            uint id = (uint)message; string? button = Native.Button(id, value.MouseData);
            if (button is not null)
            {
                if (Native.IsDown(id)) _held.Add(button);
                else if (Native.IsUp(id)) _held.Remove(button);
            }
            _packets.Enqueue(new MousePacket(Interlocked.Increment(ref _sequence), Stopwatch.GetTimestamp(),
                Native.Name(id, value.MouseData), value.Position.Value, value.MouseData, value.Flags, value.Time,
                (ulong)value.Extra, Native.TagValue, value.Extra == Native.StimulusTag, [.. _held]));
            Interlocked.Increment(ref _suppressed);
            return 1;
        }
        catch (Exception error)
        {
            Volatile.Write(ref _failure, error); Volatile.Write(ref _enabled, 0);
            Native.PostThreadMessage(_threadId, 0x12, 0, 0);
            return Native.CallNextHookEx(0, code, message, data);
        }
    }
    private void RemoveHook()
    {
        nint hook = Interlocked.Exchange(ref _hook, 0);
        if (hook == 0) return;
        UnhookSucceeded = Native.UnhookWindowsHookEx(hook);
        if (!UnhookSucceeded) Volatile.Write(ref _lastError, Marshal.GetLastWin32Error());
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Volatile.Write(ref _enabled, 0);
        uint threadId = Volatile.Read(ref _threadId);
        if (threadId != 0) Native.PostThreadMessage(threadId, 0x12, 0, 0);
        if (_thread.IsAlive && !_thread.Join(2000))
        {
            // Bound shutdown; remove the hook even if its private message loop failed to exit.
            RemoveHook(); Volatile.Write(ref _failure, new TimeoutException("Mouse hook thread did not terminate in two seconds."));
        }
    }
}
