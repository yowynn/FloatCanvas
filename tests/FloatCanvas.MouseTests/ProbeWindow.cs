using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FloatCanvas.MouseTests;

/// <summary>Self-owned native test surface, with no controls belonging to user applications.</summary>
internal sealed class ProbeWindow : IDisposable
{
    private static readonly ConcurrentDictionary<nint, WeakReference<ProbeWindow>> OwnWindows = new();
    private readonly string _className = $"FloatCanvas.MouseProbe.{Guid.NewGuid():N}";
    private readonly Native.WindowProcedure _procedure;
    private readonly nint _instance = Native.GetModuleHandle(null);
    private readonly string _description;
    private readonly List<PixelPoint> _privatePoints = [];
    private Exception? _callbackFailure;
    public nint Handle { get; private set; }
    public event Action<uint, nuint, nint>? Message;
    public event Action<MousePacket>? PrivatePacket;
    public Dictionary<string, int> OrdinaryMessages { get; } = [];
    public List<OrdinaryMouseMessage> OrdinaryMessageDetails { get; } = [];
    public Dictionary<string, int> PrivateMessages { get; } = [];
    public HashSet<string> PrivateHeldButtons { get; } = [];
    public IReadOnlyList<PixelPoint> PrivatePoints => _privatePoints;
    public Exception? CallbackFailure => _callbackFailure;
    public bool CancellationRequested { get; private set; }
    internal static bool IsCancellationRequested(nint window) => OwnWindows.TryGetValue(window, out var weak) && weak.TryGetTarget(out var target) && target.CancellationRequested;

    public ProbeWindow(string title, string description, int x = 120, int y = 120, int width = 760, int height = 480, bool activate = true)
    {
        _description = description; _procedure = OnMessage;
        var wc = new Native.WindowClass { Size = (uint)Marshal.SizeOf<Native.WindowClass>(), Instance = _instance,
            ClassName = _className, Procedure = Marshal.GetFunctionPointerForDelegate(_procedure), Background = 6 };
        if (Native.RegisterClassEx(ref wc) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        Handle = Native.CreateWindowEx(0, _className, title, 0x00CF0000, x, y, width, height, 0, 0, _instance, 0);
        if (Handle == 0) { Native.UnregisterClass(_className, _instance); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        OwnWindows[Handle] = new(this);
        Native.ShowWindow(Handle, activate ? 5 : 4); Native.UpdateWindow(Handle);
    }
    public void Reset()
    {
        OrdinaryMessages.Clear(); OrdinaryMessageDetails.Clear(); PrivateMessages.Clear(); _privatePoints.Clear(); PrivateHeldButtons.Clear();
        Native.InvalidateRect(Handle, 0, true);
    }
    public void AcceptPrivate(MousePacket packet)
    {
        Increment(PrivateMessages, packet.Kind);
        PrivateHeldButtons.Clear(); PrivateHeldButtons.UnionWith(packet.HeldButtons);
        if (packet.Kind == "Move") _privatePoints.Add(packet.Point);
        PrivatePacket?.Invoke(packet);
        Native.InvalidateRect(Handle, 0, true);
    }
    public void CancelPrivateHeld()
    {
        PrivateHeldButtons.Clear(); Increment(PrivateMessages, "Cancel");
    }
    public static void Increment(Dictionary<string, int> counts, string key)
        => counts[key] = counts.GetValueOrDefault(key) + 1;
    private nint OnMessage(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (Native.IsMouse(message))
            {
                Increment(OrdinaryMessages, Native.Name(message, unchecked((uint)wParam)));
                // Capture while this message is current, before nested callbacks/paint can change thread message metadata.
                OrdinaryMessageDetails.Add(OrdinaryMouseMessage.Capture(window, message, wParam, lParam));
            }
            Message?.Invoke(message, wParam, lParam);
            if (message == 0xF)
            {
                nint dc = Native.BeginPaint(window, out var paint);
                try
                {
                    Native.GetClientRect(window, out var rect); rect.Left += 20; rect.Top += 20;
                    Native.DrawText(dc, _description, -1, ref rect, 0x10);
                    var origin = new Native.Point(0, 0); Native.ClientToScreen(window, ref origin);
                    foreach (var point in _privatePoints.TakeLast(100))
                        Native.Ellipse(dc, point.X - origin.X - 3, point.Y - origin.Y - 3, point.X - origin.X + 3, point.Y - origin.Y + 3);
                }
                finally { Native.EndPaint(window, ref paint); }
                return 0;
            }
            // The runner owns lifetime. A user closing the fixture invalidates guards rather than leaving an orphan UI loop.
            // Keep the own release surface visible until the runner balances known synthetic buttons.
            if (message == 0x10 || (message == 0x100 && wParam == 27)) { CancellationRequested = true; return 0; }
            // Do not let side buttons become shell/browser app commands. This native target only counts stimuli.
            if (Native.IsMouse(message)) return message is Native.XDown or Native.XUp ? 1 : 0;
        }
        catch (Exception error) { _callbackFailure ??= error; }
        return Native.DefWindowProc(window, message, wParam, lParam);
    }
    public void Dispose()
    {
        if (Handle == 0) return;
        OwnWindows.TryRemove(Handle, out _);
        Native.DestroyWindow(Handle); Handle = 0;
        Native.UnregisterClass(_className, _instance); GC.KeepAlive(_procedure);
    }
}

internal sealed record OrdinaryMouseMessage(long Timestamp, int NativeMessageTime, string Kind,
    PixelPoint MessagePoint, PixelPoint ActualCursor, ulong ActualExtra, ulong ExpectedTag,
    bool SourceAvailable, uint SourceDeviceType, uint SourceOriginId, int SourceError, uint WParam)
{
    public string ActualExtraHex => $"0x{ActualExtra:X16}";
    public string ExpectedTagHex => $"0x{ExpectedTag:X16}";
    public bool MatchesExpectedTag => ActualExtra == ExpectedTag;
    public bool Low32MatchesExpectedTag => unchecked((uint)ActualExtra) == unchecked((uint)ExpectedTag);
    public static OrdinaryMouseMessage Capture(nint window, uint message, nuint wParam, nint lParam)
    {
        bool sourceAvailable = Native.GetCurrentInputMessageSource(out var source);
        int sourceError = sourceAvailable ? 0 : Marshal.GetLastWin32Error();
        ulong extra = unchecked((ulong)(nuint)Native.GetMessageExtraInfo());
        // WM_MOUSEWHEEL/HWHEEL already carry screen coordinates; all other exercised WM_* use signed client points.
        var point = new Native.Point(unchecked((short)(long)lParam), unchecked((short)((long)lParam >> 16)));
        if (message is not Native.Wheel and not Native.HWheel && !Native.ClientToScreen(window, ref point))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read own mouse-message screen coordinates.");
        return new(Stopwatch.GetTimestamp(), Native.GetMessageTime(), Native.Name(message, unchecked((uint)wParam)), point.Value,
            Native.Cursor(), extra, Native.TagValue, sourceAvailable, source.DeviceType, source.OriginId, sourceError, unchecked((uint)wParam));
    }
}
