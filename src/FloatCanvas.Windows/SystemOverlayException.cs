using System.Runtime.InteropServices;

namespace FloatCanvas.Windows;

/// <summary>An unsuccessful call through the optional system overlay ABI.</summary>
public sealed class SystemOverlayException : Exception
{
    internal SystemOverlayException(int result)
        : base($"System overlay request failed (0x{result:X8}).", Marshal.GetExceptionForHR(result))
    { HResult = result; }
}
