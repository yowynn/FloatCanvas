using System.ComponentModel;
using System.Runtime.InteropServices;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Windows;

/// <summary>Separates the application baseline from optional Windows capabilities.</summary>
public sealed class WindowsCapabilities
{
    private static readonly Version MinimumVersion = new(10, 0, 14393);
    private static readonly Version PerMonitorV2Version = new(10, 0, 15063);
    private static readonly Version CaptureExclusionVersion = new(10, 0, 19041);
    public static WindowsCapabilities Current { get; } = FromVersion(Environment.OSVersion.Version);
    public Version Version { get; }
    public bool IsSupported => Version >= MinimumVersion;
    public bool SupportsPerMonitorV2 => Version >= PerMonitorV2Version;
    public bool SupportsCaptureExclusion => Version >= CaptureExclusionVersion;
    internal nint DpiAwarenessContext => SupportsPerMonitorV2 ? -4 : -3;

    private WindowsCapabilities(Version version) => Version = version;

    public static WindowsCapabilities FromVersion(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new(version);
    }
}

/// <summary>Uses physical desktop pixels on both PMv1 and PMv2 systems and restores the caller's context.</summary>
internal readonly struct ThreadDpiAwarenessScope : IDisposable
{
    private readonly nint previous;
    private readonly Func<nint, nint> setContext;

    internal ThreadDpiAwarenessScope(WindowsCapabilities capabilities, Func<nint, nint>? setter = null)
    {
        setContext = setter ?? NativeMethods.SetThreadDpiAwarenessContext;
        previous = setContext(capabilities.DpiAwarenessContext);
        if (previous == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), L.Get("Overlay.DpiContextFailed"));
    }

    public void Dispose()
    {
        if (previous != 0 && setContext(previous) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), L.Get("Overlay.DpiContextRestoreFailed"));
    }
}
