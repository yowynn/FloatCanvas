using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;

namespace FloatCanvas.MouseTests;

internal sealed record ObserverReply(string Command, ObserverSnapshot? Snapshot, string? Error = null);
internal sealed record RawMouseMessage(long Timestamp, uint NativeHeaderSize, uint NativePacketSize,
    ushort MotionFlags, int LastX, int LastY, ushort ButtonFlags, short ButtonData, uint RawButtons, uint Extra,
    uint ForegroundPid)
{
    public string ExtraHex => $"0x{Extra:X8}";
    public uint ExpectedTagLow32 => unchecked((uint)Native.TagValue);
    public bool Low32MatchesStimulusTag => Extra == ExpectedTagLow32;
}
internal sealed record ObserverSnapshot
{
    public int ProcessId { get; init; }
    public long WindowHandle { get; init; }
    public string Phase { get; init; } = "inactive";
    public bool RawInputRegistered { get; init; }
    public int RawError { get; init; }
    public int Samples { get; init; }
    public int ForeignForegroundSamples { get; init; }
    public int RawPackets { get; init; }
    public int RawMotionPackets { get; init; }
    public Dictionary<string, int> RawButtons { get; init; } = [];
    public RawMouseMessage[] RawMessages { get; init; } = [];
    public Dictionary<string, int> OrdinaryMessages { get; init; } = [];
    public Dictionary<int, int> ButtonHighSamples { get; init; } = [];
    public PixelPoint[] CursorSamples { get; init; } = [];
    public int CursorTransitions { get; init; }
    public string? Failure { get; init; }
    public string Role { get; init; } = "RawOnly";
    public DirectInputSnapshot? DirectInput { get; init; }
    public bool CancellationRequested { get; init; }
    public string RawRegistrationFieldMeaning { get; init; } = "Result of this fixture's explicit Raw API registration; DirectInput runtime-internal registrations are not enumerated.";
}

/// <summary>Separate process observes WM, background Raw Input and global polling independently of the hook.</summary>
internal sealed class Observer : IDisposable
{
    private readonly ProbeWindow _window;
    private readonly int _parentPid;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _poller;
    private readonly Dictionary<string, int> _rawButtons = [], _messages = [];
    private readonly Dictionary<int, int> _high = [];
    private readonly List<PixelPoint> _cursor = [];
    private readonly List<RawMouseMessage> _rawMessages = [];
    private string _phase = "inactive";
    private bool _active;
    private int _samples, _foreign, _rawPackets, _rawMotion, _transitions;
    private PixelPoint? _lastCursor;
    private Exception? _failure;
    private bool _rawRegistered;
    private int _rawError;

    internal Observer(int parentPid, int x, int y, int width, int height, bool activate = true)
    {
        _parentPid = parentPid;
        _window = new ProbeWindow("浮绘：独立鼠标观察窗口", "Mouse isolation observer\nPositive controls receive native messages here.\nThis process also observes background Raw Input and global button/cursor state.", x, y, width, height, activate);
        _window.Message += OnMessage;
        _rawRegistered = Native.RegisterRawInputDevices([new Native.RawDevice(1, 2, 0x100, _window.Handle)], 1, (uint)Marshal.SizeOf<Native.RawDevice>());
        if (!_rawRegistered) { _rawError = Marshal.GetLastWin32Error(); throw new Win32Exception(_rawError, "Mouse RIDEV_INPUTSINK registration failed."); }
        _poller = new Thread(Poll) { IsBackground = true, Name = "Independent mouse state observer" }; _poller.Start();
    }
    private bool IsOwnForeground()
    {
        uint pid = Native.Pid(Native.GetForegroundWindow());
        return pid == _parentPid || pid == Environment.ProcessId;
    }
    private void Poll()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                lock (_gate)
                {
                    if (_active)
                    {
                        if (!IsOwnForeground()) _foreign++;
                        else
                        {
                            _samples++;
                            foreach (int key in Native.ButtonKeys)
                                if ((Native.GetAsyncKeyState(key) & 0x8000) != 0) _high[key] = _high.GetValueOrDefault(key) + 1;
                            PixelPoint p = Native.Cursor();
                            if (_lastCursor != p)
                            {
                                if (_lastCursor is not null) _transitions++;
                                _lastCursor = p;
                                if (_cursor.Count < 256) _cursor.Add(p);
                            }
                        }
                    }
                }
            }
            catch (Exception error) { lock (_gate) _failure ??= error; }
            _stop.Token.WaitHandle.WaitOne(2);
        }
    }
    private void OnMessage(uint id, nuint wParam, nint lParam)
    {
        lock (_gate)
        {
            if (!_active || !IsOwnForeground()) return;
            if (Native.IsMouse(id)) ProbeWindow.Increment(_messages, Native.Name(id, unchecked((uint)wParam)));
            if (id != 0xFF) return;
            uint size = 0, headerSize = (uint)Marshal.SizeOf<Native.RawHeader>();
            if (Native.GetRawInputData(lParam, 0x10000003, 0, ref size, headerSize) == uint.MaxValue)
            { _failure ??= new Win32Exception(Marshal.GetLastWin32Error(), "Raw input size query failed."); return; }
            if (size < headerSize || size > 1024 * 1024) { _failure ??= new InvalidDataException("Unexpected Raw Input packet size."); return; }
            nint buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (Native.GetRawInputData(lParam, 0x10000003, buffer, ref size, headerSize) == uint.MaxValue)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Raw input read failed.");
                var header = Marshal.PtrToStructure<Native.RawHeader>(buffer);
                if (header.Type != 0) return;
                if (size < headerSize + Marshal.SizeOf<Native.RawMouse>()) throw new InvalidDataException("Truncated RAWMOUSE packet.");
                var mouse = Marshal.PtrToStructure<Native.RawMouse>(buffer + (int)headerSize);
                _rawPackets++;
                if (_rawMessages.Count < 512)
                    _rawMessages.Add(new(Stopwatch.GetTimestamp(), headerSize, size, mouse.Flags, mouse.LastX, mouse.LastY,
                        (ushort)mouse.Buttons, unchecked((short)(mouse.Buttons >> 16)), mouse.RawButtons, mouse.Extra,
                        Native.Pid(Native.GetForegroundWindow())));
                if (mouse.LastX != 0 || mouse.LastY != 0) _rawMotion++;
                ushort buttons = (ushort)mouse.Buttons;
                string[] names = ["LeftDown", "LeftUp", "RightDown", "RightUp", "MiddleDown", "MiddleUp", "X1Down", "X1Up", "X2Down", "X2Up", "Wheel", "HWheel"];
                for (int i = 0; i < names.Length; i++) if ((buttons & (1 << i)) != 0) ProbeWindow.Increment(_rawButtons, names[i]);
            }
            catch (Exception error) { _failure ??= error; }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }
    private void Reset(string phase)
    {
        lock (_gate)
        {
            _phase = phase; _active = true;
            _samples = _foreign = _rawPackets = _rawMotion = _transitions = 0;
            _lastCursor = null; _cursor.Clear(); _rawButtons.Clear(); _rawMessages.Clear(); _messages.Clear(); _high.Clear();
        }
    }
    internal ObserverSnapshot Snapshot(bool stop = false)
    {
        lock (_gate)
        {
            if (stop) _active = false;
            return new ObserverSnapshot { ProcessId = Environment.ProcessId, WindowHandle = _window.Handle.ToInt64(), Phase = _phase,
                RawInputRegistered = _rawRegistered, RawError = _rawError, Samples = _samples, ForeignForegroundSamples = _foreign,
                RawPackets = _rawPackets, RawMotionPackets = _rawMotion, RawButtons = new(_rawButtons), OrdinaryMessages = new(_messages),
                RawMessages = [.. _rawMessages],
                ButtonHighSamples = new(_high), CursorSamples = [.. _cursor], CursorTransitions = _transitions,
                Failure = (_failure ?? _window.CallbackFailure)?.ToString(), CancellationRequested = _window.CancellationRequested };
        }
    }
    internal ObserverReply Command(string command)
    {
        if (command.StartsWith("reset:", StringComparison.Ordinal)) { Reset(command[6..]); return new(command, Snapshot()); }
        switch (command)
        {
            case "info": return new(command, Snapshot());
            case "snapshot": return new(command, Snapshot(true));
            case "activate":
                Native.ShowWindow(_window.Handle, 5);
                Native.SetWindowPos(_window.Handle, 0, 0, 0, 0, 0, 0x13); // raise own window without changing its geometry
                Native.SetForegroundWindow(_window.Handle);
                Native.AllowSetForegroundWindow((uint)_parentPid);
                return new(command, Snapshot());
            case "stop": return new(command, Snapshot(true));
            default: return new(command, null, "Unknown private observer command.");
        }
    }
    public void Dispose()
    {
        _stop.Cancel(); _poller.Join(2000);
        if (_rawRegistered)
        {
            Native.RegisterRawInputDevices([new Native.RawDevice(1, 2, 1, 0)], 1, (uint)Marshal.SizeOf<Native.RawDevice>());
            _rawRegistered = false;
        }
        _window.Dispose(); _stop.Dispose();
    }

    internal static async Task RunChildAsync(string pipeName, int parentPid, int x, int y, int width, int height, Dispatcher dispatcher, bool activate = true)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10000);
        if (!Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint actualPid) || actualPid != parentPid)
            throw new InvalidOperationException("Observer pipe server does not match its declared parent.");
        using var observer = new Observer(parentPid, x, y, width, height, activate);
        using var reader = new StreamReader(pipe); using var writer = new StreamWriter(pipe) { AutoFlush = true };
        // This async method resumes on the owning Dispatcher; only this private pipe can activate/close its own HWND.
        while (true)
        {
            string? line = await reader.ReadLineAsync();
            if (line is null) break;
            string command = JsonSerializer.Deserialize<string>(line) ?? throw new InvalidDataException("Invalid observer command.");
            var reply = observer.Command(command);
            await writer.WriteLineAsync(JsonSerializer.Serialize(reply));
            if (command == "stop") break;
        }
    }
}
