using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FloatCanvas.MouseTests;

internal sealed class ProbeInitializationException(string message, object diagnostic, Exception inner) : Exception(message, inner)
{
    public object Diagnostic { get; } = diagnostic;
}

internal sealed record DirectInputCall(long Timestamp, string Method, int HResult, object? Detail = null)
{
    public string Hex => $"0x{unchecked((uint)HResult):X8}";
}
internal sealed record DirectInputSample(long Timestamp, int X, int Y, int Z, byte[] Buttons);
internal sealed record DirectInputSnapshot
{
    public string Phase { get; init; } = "inactive";
    public bool Acquired { get; init; }
    public uint CooperativeFlags { get; init; }
    public bool WatchdogFired { get; init; }
    public bool ThreadAlive { get; init; }
    public double? LeaseMilliseconds { get; init; }
    public string? ReleaseReason { get; init; }
    public int Samples { get; init; }
    public int ForeignForegroundSamples { get; init; }
    public int XChanges { get; init; }
    public int YChanges { get; init; }
    public int PositiveWheel { get; init; }
    public int NegativeWheel { get; init; }
    public int[] ButtonDown { get; init; } = new int[8];
    public int[] ButtonUp { get; init; } = new int[8];
    public DirectInputSample[] Data { get; init; } = [];
    public DirectInputCall[] Calls { get; init; } = [];
    public string? Failure { get; init; }
}

/// <summary>All native device calls and release run on one private thread, independent of WPF's Dispatcher.</summary>
internal sealed class DirectInputWorker : IDisposable
{
    private readonly nint _window;
    private readonly HashSet<uint> _ownPids;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly ConcurrentQueue<Action<DirectInputNative>> _commands = new();
    private readonly ConcurrentQueue<(long Epoch, int Reason)> _leaseStops = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private readonly List<DirectInputCall> _calls = [];
    private readonly List<DirectInputSample> _data = [];
    private readonly int[] _down = new int[8], _up = new int[8];
    private readonly byte[] _lastButtons = new byte[8];
    private Timer? _watchdog;
    private Timer? _hardWatchdog;
    private long _leaseEpoch;
    private int _exclusiveIntent;
    private int _stop, _disposed;
    private bool _acquired, _capture, _watchdogFired;
    private uint _flags;
    private long _leaseStart, _leaseEnd;
    private string? _reason, _failure;
    private string _phase = "inactive";
    private int _samples, _foreign, _x, _y, _positiveWheel, _negativeWheel;

    private DirectInputWorker(nint window, IEnumerable<uint> ownPids)
    {
        if (!Native.IsWindow(window) || Native.Pid(window) != Environment.ProcessId) throw new ArgumentException("Device HWND must belong to this fixture.");
        _window = window; _ownPids = [.. ownPids]; _ownPids.Add((uint)Environment.ProcessId);
        _thread = new Thread(Loop) { IsBackground = true, Name = "FloatCanvas test DirectInput device owner" };
    }
    public static async Task<DirectInputWorker> CreateAsync(nint window, IEnumerable<uint> ownPids)
    {
        var worker = new DirectInputWorker(window, ownPids);
        try { worker._thread.Start(); await worker._started.Task.WaitAsync(TimeSpan.FromSeconds(5)); return worker; }
        catch (Exception error) { worker.Dispose(); throw new ProbeInitializationException("DirectInput device initialization failed.", worker.Snapshot(), error); }
    }
    public Task StartAsync(uint flags, string phase, int exclusiveWatchdogMilliseconds = 4000) => Queue(device =>
    {
        if (Program.Cancellation.IsCancellationRequested || ProbeWindow.IsCancellationRequested(_window)) throw new OperationCanceledException("Own DirectInput fixture cancellation requested before Acquire.");
        if ((flags & 1) != 0 && exclusiveWatchdogMilliseconds is < 100 or > 4000) throw new ArgumentOutOfRangeException(nameof(exclusiveWatchdogMilliseconds));
        Release(device, "Reconfigure");
        if ((flags & 4) != 0 && Native.GetForegroundWindow() != _window) throw new InvalidOperationException("Foreground DirectInput lease requires the exact own target HWND.");
        if (Native.ButtonKeys.Any(k => (Native.GetAsyncKeyState(k) & 0x8000) != 0)) throw new InvalidOperationException("Mouse buttons must be released before Acquire.");
        device.Cooperate(_window, flags);
        long epoch;
        lock (_gate)
        {
            epoch = ++_leaseEpoch; _exclusiveIntent = (flags & 1) != 0 ? 1 : 0;
            _flags = flags; _leaseStart = Stopwatch.GetTimestamp(); _leaseEnd = 0; _reason = null; _watchdogFired = false;
            ResetCore(phase); _capture = true;
        }
        if ((flags & 1) != 0)
        {
            // Independent timer only signals; the device owner releases it, without waiting for Dispatcher work.
            _watchdog = new Timer(_ => { _leaseStops.Enqueue((epoch, 1)); SignalOwner(); }, null, exclusiveWatchdogMilliseconds, Timeout.Infinite);
            // If even Acquire/GetDeviceState/Unacquire hangs, cap this test's ownership by terminating THIS process.
            // Only our authenticated pipe children exist; EOF closes their observers. Never terminate a foreign PID.
            _hardWatchdog = new Timer(_ =>
            {
                lock (_gate)
                    if (_leaseEpoch == epoch && _exclusiveIntent != 0) Process.GetCurrentProcess().Kill();
            }, null, 4800, Timeout.Infinite);
        }
        try { device.Acquire(); lock (_gate) _acquired = true; }
        catch { CancelTimersAfterRelease(); throw; }
    });
    public Task StopAsync(string reason = "Requested") => Queue(device => Release(device, reason));
    public void RequestStop() { if (Volatile.Read(ref _disposed) != 0) return; _leaseStops.Enqueue((Interlocked.Read(ref _leaseEpoch), 2)); SignalOwner(); }
    public Task ResetAsync(string phase) => Queue(_ => { lock (_gate) { ResetCore(phase); _capture = true; } });
    public Task EndCaptureAsync() => Queue(_ => { lock (_gate) _capture = false; });
    public DirectInputSnapshot Snapshot()
    {
        lock (_gate) return new DirectInputSnapshot { Phase = _phase, Acquired = _acquired, CooperativeFlags = _flags,
            WatchdogFired = _watchdogFired, ThreadAlive = _thread.IsAlive, ReleaseReason = _reason,
            LeaseMilliseconds = _leaseStart == 0 ? null : Stopwatch.GetElapsedTime(_leaseStart, _leaseEnd == 0 ? Stopwatch.GetTimestamp() : _leaseEnd).TotalMilliseconds,
            Samples = _samples, ForeignForegroundSamples = _foreign, XChanges = _x, YChanges = _y,
            PositiveWheel = _positiveWheel, NegativeWheel = _negativeWheel, ButtonDown = [.. _down], ButtonUp = [.. _up], Data = [.. _data], Calls = [.. _calls], Failure = _failure };
    }
    private void ResetCore(string phase)
    {
        _phase = phase; _samples = _foreign = _x = _y = _positiveWheel = _negativeWheel = 0;
        Array.Clear(_down); Array.Clear(_up); Array.Clear(_lastButtons); _data.Clear();
    }
    private Task Queue(Action<DirectInputNative> action)
    {
        if (Volatile.Read(ref _disposed) != 0 || !_thread.IsAlive) return Task.FromException(new ObjectDisposedException(nameof(DirectInputWorker)));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Enqueue(device => { try { action(device); completion.TrySetResult(); } catch (Exception error) { completion.TrySetException(error); } });
        SignalOwner(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
    private void SignalOwner()
    {
        // Timer.Dispose does not join an already-running callback; a late callback must never tear down the test process.
        try { _wake.Set(); } catch (ObjectDisposedException) { }
    }
    private void Log(DirectInputCall call) { lock (_gate) _calls.Add(call); }
    private void Release(DirectInputNative device, string reason)
    {
        _watchdog?.Dispose(); _watchdog = null;
        if (!_acquired) { CancelTimersAfterRelease(); return; }
        device.Unacquire();
        lock (_gate) { _acquired = false; _exclusiveIntent = 0; _leaseEnd = Stopwatch.GetTimestamp(); _reason = reason; }
        CancelTimersAfterRelease();
    }
    private void CancelTimersAfterRelease()
    {
        lock (_gate) _exclusiveIntent = 0;
        _watchdog?.Dispose(); _watchdog = null; _hardWatchdog?.Dispose(); _hardWatchdog = null;
    }
    private void Loop()
    {
        DirectInputNative? device = null;
        try
        {
            device = new DirectInputNative(Log); _started.TrySetResult();
            while (Volatile.Read(ref _stop) == 0)
            {
                while (_commands.TryDequeue(out var command)) command(device);
                while (_leaseStops.TryDequeue(out var requested))
                {
                    if (requested.Epoch != Interlocked.Read(ref _leaseEpoch)) continue;
                    lock (_gate) _watchdogFired |= requested.Reason == 1;
                    Release(device, requested.Reason == 1 ? "IndependentWatchdog" : "OwnWindowBoundary");
                }
                if (_acquired)
                {
                    if ((_flags & 4) != 0 && Native.GetForegroundWindow() != _window) Release(device, "ForegroundLost");
                    else
                    {
                        int hr = device.Read(out var state);
                        if (hr < 0) Release(device, "GetDeviceStateFailed"); // deliberately no automatic reacquire loop
                        else lock (_gate)
                        {
                            if (_capture)
                            {
                                if (!_ownPids.Contains(Native.Pid(Native.GetForegroundWindow()))) _foreign++;
                                else
                                {
                                    _samples++;
                                    if (state.X != 0) _x++; if (state.Y != 0) _y++;
                                    if (state.Z > 0) _positiveWheel++; if (state.Z < 0) _negativeWheel++;
                                    for (int i = 0; i < 8; i++)
                                    {
                                        bool was = (_lastButtons[i] & 0x80) != 0, now = (state.Buttons[i] & 0x80) != 0;
                                        if (now && !was) _down[i]++; if (was && !now) _up[i]++; _lastButtons[i] = state.Buttons[i];
                                    }
                                    if (_data.Count < 2048 && (state.X != 0 || state.Y != 0 || state.Z != 0 || state.Buttons.Any(b => b != 0)))
                                        _data.Add(new(Stopwatch.GetTimestamp(), state.X, state.Y, state.Z, [.. state.Buttons]));
                                }
                            }
                        }
                    }
                }
                _wake.WaitOne(2);
            }
        }
        catch (Exception error) { lock (_gate) _failure = error.ToString(); _started.TrySetException(error); }
        finally
        {
            if (device is not null)
            {
                try { Release(device, "Dispose"); }
                catch (Exception error) { lock (_gate) _failure ??= error.ToString(); }
                // Interface release is still required when Unacquire reports failure. Keep the emergency deadline armed
                // until the native device release returns; the exclusive interface may otherwise remain held.
                try
                {
                    device.Dispose();
                    lock (_gate) { _acquired = false; _exclusiveIntent = 0; }
                    CancelTimersAfterRelease();
                }
                catch (Exception error) { lock (_gate) _failure ??= error.ToString(); }
            }
            _started.TrySetCanceled();
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Volatile.Write(ref _stop, 1); SignalOwner();
        if (_thread.IsAlive && !_thread.Join(2500)) { lock (_gate) _failure ??= "DirectInput owner thread did not exit in 2.5 seconds."; }
        _watchdog?.Dispose();
        // A still-stuck exclusive native call retains the 4.8s emergency deadline until process termination.
        if (!_thread.IsAlive && Volatile.Read(ref _exclusiveIntent) == 0) _hardWatchdog?.Dispose();
        if (!_thread.IsAlive) _wake.Dispose();
    }
}

/// <summary>Small SDK-verified dinput8 vtable projection; no driver or external DirectInput package.</summary>
internal sealed class DirectInputNative : IDisposable
{
    internal static object LayoutEvidence() => new { HeaderBaseline = "Windows SDK 10.0.22621.0 um/dinput.h", DataFormatBytes = Marshal.SizeOf<DataFormat>(),
        ObjectFormatBytes = Marshal.SizeOf<ObjectFormat>(), MouseStateBytes = Marshal.SizeOf<MouseState>(), CapabilitiesBytes = Marshal.SizeOf<Capabilities>(),
        DataFormat = "Exact managed projection of SDK c_dfDIMouse2 object definitions (ANYINSTANCE, null button GUIDs, standard optional bits); not a linked runtime export",
        ObjectsPointerOffset = Marshal.OffsetOf<DataFormat>(nameof(DataFormat.Objects)).ToInt32() };
    internal sealed record MouseObjectDefinition(uint Offset, uint Type, uint Flags, string? Guid)
    {
        public string TypeHex => $"0x{Type:X8}";
    }
    internal static MouseObjectDefinition[] StandardMouseObjects() =>
    [
        new(0, 0x00FFFF03, 0, "A36D02E0-C9F3-11CF-BFC7-444553540000"),
        new(4, 0x00FFFF03, 0, "A36D02E1-C9F3-11CF-BFC7-444553540000"),
        new(8, 0x80FFFF03, 0, "A36D02E2-C9F3-11CF-BFC7-444553540000"),
        new(12, 0x00FFFF0C, 0, null), new(13, 0x00FFFF0C, 0, null),
        new(14, 0x80FFFF0C, 0, null), new(15, 0x80FFFF0C, 0, null),
        new(16, 0x80FFFF0C, 0, null), new(17, 0x80FFFF0C, 0, null),
        new(18, 0x80FFFF0C, 0, null), new(19, 0x80FFFF0C, 0, null)
    ];
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateDevice(nint self, in Guid guid, out nint device, nint outer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int NoArgs(nint self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ReleaseReference(nint self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetFormat(nint self, in DataFormat format);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Cooperative(nint self, nint window, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReadState(nint self, uint size, nint state);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetCapabilities(nint self, ref Capabilities caps);
    [StructLayout(LayoutKind.Sequential)] private struct ObjectFormat { public nint Guid; public uint Offset, Type, Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct DataFormat { public uint Size, ObjectSize, Flags, DataSize, Count; public nint Objects; }
    [StructLayout(LayoutKind.Sequential)] private struct Capabilities
    {
        public uint Size, Flags, DeviceType, Axes, Buttons, Povs, FfSamplePeriod, FfMinimumResolution, FirmwareRevision, HardwareRevision, FfDriverVersion;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseState
    {
        public int X, Y, Z;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] Buttons;
    }
    private nint _directInput, _device, _state;
    private static readonly byte[] EmptyState = new byte[20];
    private readonly Action<DirectInputCall> _log;
    public DirectInputNative(Action<DirectInputCall> log)
    {
        _log = log;
        try
        {
            Guid iid = new("BF798031-483A-4DA2-AA99-5D64ED369700");
            Must("DirectInput8Create", DirectInput8Create(Native.GetModuleHandle(null), 0x800, in iid, out _directInput, 0));
            Guid mouse = new("6F1D2B60-D5A0-11CF-BFC7-444553540000");
            Must("CreateDevice(SysMouse)", Function<CreateDevice>(_directInput, 3)(_directInput, in mouse, out _device, 0));
            SetMouseFormat(); _state = Marshal.AllocHGlobal(20);
        }
        catch { Dispose(); throw; }
    }
    private int Record(string method, int hr, object? detail = null) { _log(new(Stopwatch.GetTimestamp(), method, hr, detail)); return hr; }
    private void Must(string method, int hr, object? detail = null) { Record(method, hr, detail); if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    public void Cooperate(nint window, uint flags) => Must($"SetCooperativeLevel(0x{flags:X})", Function<Cooperative>(_device, 13)(_device, window, flags));
    public void Acquire() => Must("Acquire", Function<NoArgs>(_device, 7)(_device));
    public void Unacquire() => Must("Unacquire", Function<NoArgs>(_device, 8)(_device));
    public int Read(out MouseState state)
    {
        // Unmapped slots (e.g. absent extra buttons) must remain zero, never uninitialized native memory.
        Marshal.Copy(EmptyState, 0, _state, EmptyState.Length);
        int hr = Record("GetDeviceState", Function<ReadState>(_device, 9)(_device, 20, _state));
        state = hr >= 0 ? Marshal.PtrToStructure<MouseState>(_state) : new MouseState { Buttons = new byte[8] }; return hr;
    }
    private void SetMouseFormat()
    {
        // Exact object definitions read from the SDK's own c_dfDIMouse2 static constant by tools/DumpSdkMouseFormat.cpp.
        // Capabilities describe counts, not valid instance identifiers. Never synthesize those from a count.
        MouseObjectDefinition[] definitions = StandardMouseObjects();
        var caps = new Capabilities { Size = (uint)Marshal.SizeOf<Capabilities>() };
        int capsHr = Function<GetCapabilities>(_device, 3)(_device, ref caps);
        _log(new(Stopwatch.GetTimestamp(), "GetCapabilities", capsHr, new { caps.Axes, caps.Buttons, caps.DeviceType, caps.Flags }));
        if (capsHr < 0) Marshal.ThrowExceptionForHR(capsHr);
        nint guidBuffer = Marshal.AllocHGlobal(16 * definitions.Length), objects = Marshal.AllocHGlobal(Marshal.SizeOf<ObjectFormat>() * definitions.Length);
        try
        {
            for (int i = 0; i < definitions.Length; i++)
            {
                MouseObjectDefinition definition = definitions[i];
                nint guidPointer = 0;
                if (definition.Guid is not null)
                {
                    guidPointer = guidBuffer + i * 16;
                    Marshal.StructureToPtr(new Guid(definition.Guid), guidPointer, false);
                }
                var item = new ObjectFormat { Guid = guidPointer, Offset = definition.Offset, Type = definition.Type, Flags = definition.Flags };
                Marshal.StructureToPtr(item, objects + i * Marshal.SizeOf<ObjectFormat>(), false);
            }
            var format = new DataFormat { Size = (uint)Marshal.SizeOf<DataFormat>(), ObjectSize = (uint)Marshal.SizeOf<ObjectFormat>(),
                Flags = 2, DataSize = 20, Count = (uint)definitions.Length, Objects = objects };
            Must("SetDataFormat(SdkDIMouse2Definition)", Function<SetFormat>(_device, 11)(_device, in format),
                new { format.Size, format.ObjectSize, format.Flags, format.DataSize, format.Count, Objects = definitions });
        }
        finally { Marshal.FreeHGlobal(objects); Marshal.FreeHGlobal(guidBuffer); }
    }
    private static T Function<T>(nint self, int index) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(self), index * IntPtr.Size));
    public void Dispose()
    {
        if (_device != 0) { Function<ReleaseReference>(_device, 2)(_device); _device = 0; }
        if (_directInput != 0) { Function<ReleaseReference>(_directInput, 2)(_directInput); _directInput = 0; }
        if (_state != 0) { Marshal.FreeHGlobal(_state); _state = 0; }
    }
    [DllImport("dinput8.dll", ExactSpelling = true)] private static extern int DirectInput8Create(nint instance, uint version, in Guid iid, out nint directInput, nint outer);
}
