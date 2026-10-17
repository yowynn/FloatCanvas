using FloatCanvas.Windows;

namespace FloatCanvas.WindowsTests;

internal static class WindowsCompatibilityTests
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (var (version, supported, pmv2, exclusion) in new[]
        {
            (new Version(6, 3, 9600), false, false, false),
            (new Version(10, 0, 10240), false, false, false),
            (new Version(10, 0, 14392), false, false, false),
            (new Version(10, 0, 14393), true, false, false),
            (new Version(10, 0, 15062), true, false, false),
            (new Version(10, 0, 15063), true, true, false),
            (new Version(10, 0, 17763), true, true, false),
            (new Version(10, 0, 19040), true, true, false),
            (new Version(10, 0, 19041), true, true, true),
            (new Version(10, 0, 19044), true, true, true),
            (new Version(10, 0, 19045), true, true, true),
            (new Version(10, 0, 22000), true, true, true),
            (new Version(10, 1, 1), true, true, true),
            (new Version(11, 0, 1), true, true, true)
        })
        {
            var capabilities = WindowsCapabilities.FromVersion(version);
            check(capabilities.Version == version && capabilities.IsSupported == supported &&
                capabilities.SupportsPerMonitorV2 == pmv2 && capabilities.SupportsCaptureExclusion == exclusion,
                $"{version} separates the application baseline, PMv2 and capture exclusion");
        }
        check(WindowsCapabilities.Current.Version == Environment.OSVersion.Version, "Current capabilities reflect the actual OS version");
        bool rejected = false;
        try { WindowsCapabilities.FromVersion(null!); } catch (ArgumentNullException) { rejected = true; }
        check(rejected, "A missing OS version is rejected instead of silently assuming modern capabilities");
    }
}
