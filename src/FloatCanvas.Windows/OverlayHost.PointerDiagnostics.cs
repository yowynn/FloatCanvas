using System.Diagnostics;
using System.Runtime.InteropServices;
using static FloatCanvas.Windows.NativeMethods;

namespace FloatCanvas.Windows;

/// <summary>Opt-in native message-entry evidence, captured before application Pointer subscribers.</summary>
public readonly record struct OverlayPointerMessageDiagnostic(long Timestamp, long WindowHandle, uint Message,
    long WParam, long LParam, int NativeMessageTime, ulong ExtraInfo, OverlayPoint MessagePosition,
    OverlayPoint CursorPosition, long ForegroundHandle, long CaptureHandle, bool SourceAvailable,
    uint SourceDeviceType, uint SourceOriginId, int SourceError)
{
    public string ExtraInfoHex => $"0x{ExtraInfo:X16}";
}

public sealed partial class OverlayHost
{
    public event Action<OverlayPointerMessageDiagnostic>? PointerMessageObserved;

    private void ObservePointerMessage(WindowState state, uint message, nuint wParam, nint lParam)
    {
        var observer = PointerMessageObserved;
        if (observer is null) return;
        long timestamp = Stopwatch.GetTimestamp();
        // Read the message-associated value first, before coordinate conversion or managed callbacks.
        ulong extra = unchecked((ulong)(nuint)GetMessageExtraInfo());
        int messageTime = GetMessageTime();
        bool sourceAvailable = GetCurrentInputMessageSource(out var source);
        int sourceError = sourceAvailable ? 0 : Marshal.GetLastWin32Error();
        var position = MessagePosition(state, message, lParam);
        using var dpi = new ThreadDpiAwarenessScope(Capabilities);
        GetCursorPos(out var cursor);
        observer(new(timestamp, (long)state.Handle, message, unchecked((long)wParam), (long)lParam,
            messageTime, extra, position, new(cursor.X, cursor.Y), (long)GetForegroundWindow(), (long)GetCapture(),
            sourceAvailable, source.DeviceType, source.OriginId, sourceError));
    }

    [StructLayout(LayoutKind.Sequential)] private struct InputMessageSource { internal uint DeviceType, OriginId; }
    [DllImport("user32.dll")] private static extern nint GetMessageExtraInfo();
    [DllImport("user32.dll")] private static extern int GetMessageTime();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCurrentInputMessageSource(out InputMessageSource source);
}
