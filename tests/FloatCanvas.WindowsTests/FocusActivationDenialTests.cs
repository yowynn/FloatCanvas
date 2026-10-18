using FloatCanvas.Windows;
using static FloatCanvas.Windows.NativeMethods;

namespace FloatCanvas.WindowsTests;

/// <summary>Hidden real HWNDs only; no activation, global keyboard hooks, or input injection.</summary>
internal static class FocusActivationDenialTests
{
    public static void Run(Action<bool, string> check)
    {
        nint foreground = GetForegroundWindow(), focus = GetFocus(), capture = GetCapture();
        var bounds = new PixelBounds(40, 120, 420, 220);
        var monitor = new MonitorInfo("focus-denial-fixture", bounds, bounds, 96, 96, true);
        using var host = new OverlayHost(OverlayMonitorScope.PrimaryScreen, () => [monitor], false);
        bool gateCalled = false;
        host.CanBeginCanvasGesture = () => { gateCalled = true; return false; };
        check(!host.TryFocusEditingCanvas(), "A pass-through session cannot use canvas fallback to enter editing");
        check(!gateCalled, "Invalid editing entry is denied before consulting application callbacks");
        check(!host.IsEditing && !host.IsKeyboardIsolationEnabled && !host.IsKeyboardIsolationActive,
            "Denied fallback does not start an editing session or create an input lease");
        check(GetForegroundWindow() == foreground && GetFocus() == focus && GetCapture() == capture,
            "Denied fallback preserves native foreground, keyboard focus and capture");
        check(host.Diagnostics.All(pair => !pair.InputVisible && !pair.DisplayVisible),
            "Denied fallback leaves all hidden native fixture surfaces hidden");
        int presses = 0;
        host.Pointer += _ => presses++;
        foreach (var button in new[] { OverlayMouseButton.Left, OverlayMouseButton.Right, OverlayMouseButton.Middle })
            check(!host.TryForwardCapturedCanvasPress(new(80, 160), button, 120),
                "Pass-through denies a captured " + button + " press without entering editing");
        foreach (var point in new[] { new OverlayPoint(double.NaN, 160), new OverlayPoint(80, double.PositiveInfinity) })
            check(!host.TryForwardCapturedCanvasPress(point, OverlayMouseButton.Left, 120),
                "Invalid physical coordinates cannot be forwarded");
        check(!host.TryForwardCapturedCanvasPress(new(80, 160), OverlayMouseButton.None, 120) &&
            !host.TryForwardCapturedCanvasPress(new(80, 160), (OverlayMouseButton)99, 120) &&
            !host.TryForwardCapturedCanvasPress(new(80, 160), OverlayMouseButton.Left, 120, 0),
            "Invalid button and click identities are rejected");
        check(presses == 0 && GetForegroundWindow() == foreground && GetFocus() == focus && GetCapture() == capture,
            "Rejected press forwarding emits no canvas event and preserves native focus/capture");
    }
}
