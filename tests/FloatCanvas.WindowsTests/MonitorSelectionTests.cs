using FloatCanvas.Windows;

namespace FloatCanvas.WindowsTests;

internal static class MonitorSelectionTests
{
    internal static void Run(Action<bool, string> check)
    {
        var primary = new MonitorInfo("primary", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), 96, 96, true);
        var left = new MonitorInfo("left", new(-2560, -720, 2560, 1440), new(-2560, -720, 2560, 1400), 144, 144, false);
        MonitorInfo[] layout = [left, primary];
        var selected = OverlayMonitorSelection.Select(layout, OverlayMonitorScope.PrimaryScreen);
        check(selected.Count == 1 && ReferenceEquals(selected[0], primary),
            "Primary scope selects the actual primary even when a negative-origin secondary is enumerated first");
        var all = OverlayMonitorSelection.Select(layout, OverlayMonitorScope.AllScreens);
        check(all.Count == 2 && ReferenceEquals(all[0], primary) && ReferenceEquals(all[1], left),
            "All-screens scope preserves every screen and places the primary first");
        check(ReferenceEquals(layout[0], left) && ReferenceEquals(layout[1], primary),
            "Screen selection does not reorder the provider's collection");
        selected = OverlayMonitorSelection.Select(layout, OverlayMonitorScope.SpecificScreen, "LEFT");
        check(selected.Count == 1 && ReferenceEquals(selected[0], left), "Specific scope matches device identity case-insensitively and preserves negative coordinates and DPI");
        selected = OverlayMonitorSelection.Select(layout, OverlayMonitorScope.SpecificScreen, "disconnected");
        check(selected.Count == 1 && ReferenceEquals(selected[0], primary), "A missing saved monitor falls back to the actual primary");

        var abovePortrait = new MonitorInfo("above-portrait", new(100, -2560, 1440, 2560),
            new(100, -2560, 1440, 2520), 192, 192, false);
        foreach (uint dpi in new uint[] { 96, 144, 192 })
        {
            var changedPrimary = primary with { DpiX = dpi, DpiY = dpi };
            selected = OverlayMonitorSelection.Select([abovePortrait, left, changedPrimary], OverlayMonitorScope.PrimaryScreen);
            check(selected.Count == 1 && selected[0] == changedPrimary && selected[0].Bounds == primary.Bounds &&
                selected[0].WorkArea == primary.WorkArea && selected[0].DpiX == dpi && selected[0].DpiY == dpi,
                $"Primary scope preserves physical bounds, work area and {dpi * 100 / 96}% DPI despite mixed-DPI side/above screens");
            var physical = OverlayCoordinates.DipToPixel(new(120, 80), selected[0]);
            check(physical == new OverlayPoint(120 * dpi / 96d, 80 * dpi / 96d),
                $"Selected primary retains its own {dpi * 100 / 96}% coordinate mapping");
        }
        selected = OverlayMonitorSelection.Select([left, abovePortrait], OverlayMonitorScope.PrimaryScreen);
        check(selected.Count == 1 && ReferenceEquals(selected[0], left) && selected[0].Bounds.Left == -2560 &&
            selected[0].DpiX == 144, "Missing primary flags fall back to the first valid screen without changing its negative coordinates or DPI");
        all = OverlayMonitorSelection.Select([left, abovePortrait], OverlayMonitorScope.AllScreens);
        check(all.Count == 2 && ReferenceEquals(all[0], left) && ReferenceEquals(all[1], abovePortrait),
            "All-screens fallback is stable and preserves an above-screen portrait layout");
        var invalid = primary with { DeviceName = "invalid", Bounds = new(0, 0, 0, 1080) };
        selected = OverlayMonitorSelection.Select([invalid, abovePortrait, left], OverlayMonitorScope.PrimaryScreen);
        check(selected.Count == 1 && ReferenceEquals(selected[0], abovePortrait),
            "An unusable primary entry cannot prevent fallback to the first valid portrait screen");
        all = OverlayMonitorSelection.Select([invalid, abovePortrait, left], OverlayMonitorScope.AllScreens);
        check(all.Count == 2 && ReferenceEquals(all[0], abovePortrait) && ReferenceEquals(all[1], left),
            "Invalid display dimensions are excluded before window or DIB creation");
        foreach (var scope in new[] { OverlayMonitorScope.AllScreens, OverlayMonitorScope.PrimaryScreen })
        {
            check(Throws<InvalidOperationException>(() => OverlayMonitorSelection.Select([], scope)),
                $"{scope} rejects an empty enumeration explicitly");
            check(Throws<InvalidOperationException>(() => OverlayMonitorSelection.Select([invalid], scope)),
                $"{scope} rejects an enumeration with no valid screens explicitly");
            selected = OverlayMonitorSelection.Select([primary], scope);
            check(selected.Count == 1 && ReferenceEquals(selected[0], primary),
                $"{scope} keeps the single-screen case unchanged");
        }
        check(Throws<ArgumentOutOfRangeException>(() => OverlayMonitorSelection.Select(layout, (OverlayMonitorScope)99)),
            "Unknown screen scope is rejected rather than silently choosing a different mode");
        check(Throws<ArgumentNullException>(() => OverlayMonitorSelection.Select(null!, OverlayMonitorScope.AllScreens)),
            "A null enumeration is rejected explicitly");
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
}
