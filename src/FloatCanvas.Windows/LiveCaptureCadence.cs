using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FloatCanvas.Windows;

/// <summary>Bound CPU readback to the display cadence, independently of arrival notification frequency.</summary>
internal static class LiveCaptureCadence
{
    internal const int MaximumFramesPerSecond = 60;
    internal static int ResolveRate(IEnumerable<uint> displayRates) => displayRates
        .Select(rate => rate is > 1 and <= 1000 ? (int)Math.Min(rate, MaximumFramesPerSecond) : MaximumFramesPerSecond)
        .DefaultIfEmpty(MaximumFramesPerSecond).Max();

    internal static long Interval(int rate) => (Stopwatch.Frequency + rate - 1) / rate;
    internal static int QueryRate(IEnumerable<MonitorInfo> monitors)
    {
        return ResolveRate(monitors.Select(monitor =>
        {
            var mode = new DisplayMode { Size = 220 };
            return EnumDisplaySettingsW(monitor.DeviceName, -1, ref mode) ? mode.Frequency : 0u;
        }));
    }

    // DEVMODEW's display-frequency member is outside its printer/display union.
    [StructLayout(LayoutKind.Explicit, Size = 220, CharSet = CharSet.Unicode)]
    private struct DisplayMode
    {
        [FieldOffset(68)] internal ushort Size;
        [FieldOffset(184)] internal uint Frequency;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsW(string device, int mode, ref DisplayMode settings);
}
