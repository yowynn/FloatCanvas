using System.Text.Json;
using System.Windows.Interop;
using FloatCanvas.PlatformTests;
using FloatCanvas.Windows;
using Wpf = System.Windows;

namespace FloatCanvas.SessionTests;

internal static partial class Program
{
    private static readonly List<object> PopupPixelDiagnostics = [];

    // Read-only evidence collected after the original single-pixel sample. No window,
    // focus, input, additional delay, retry, redraw or composition flush is performed.
    private static void RecordPopupPixel(string phase, object session, OverlayHost host, nint lowerHandle,
        Wpf.Window toolbar, System.Windows.Controls.Primitives.Popup popup, nint popupHandle, Native.Point center, uint pixel)
    {
        Native.GetWindowRect(popupHandle, out var popupBounds);
        Native.GetWindowRect(lowerHandle, out var lowerBounds);
        nint toolbarHandle = new WindowInteropHelper(toolbar).Handle;
        nint hit = Native.WindowFromPoint(center), foreground = Native.GetForegroundWindow();
        GetWindowThreadProcessId(hit, out uint hitPid);
        var windows = host.Diagnostics;
        var sample = new
        {
            Phase = phase, TimestampUtc = DateTimeOffset.UtcNow, ElapsedMilliseconds = DiagnosticClock.Elapsed.TotalMilliseconds,
            RawColorref = pixel, ColorrefHex = $"0x{pixel:X8}", InvalidPixel = pixel == uint.MaxValue,
            Red = pixel & 255, Green = (pixel >> 8) & 255, Blue = (pixel >> 16) & 255, OriginalRedPredicate = IsRed(pixel),
            SampleX = center.X, SampleY = center.Y,
            PopupHandle = popupHandle.ToInt64(), PopupVisible = Native.IsWindowVisible(popupHandle), PopupOpen = popup.IsOpen,
            PopupBounds = new { popupBounds.Left, popupBounds.Top, popupBounds.Right, popupBounds.Bottom },
            PopupExtendedStyle = Native.ExtendedStyle(popupHandle).ToInt64(), PopupZRank = CurrentToolbarZRank(popupHandle),
            PopupChildActualWidth = ((Wpf.FrameworkElement)popup.Child).ActualWidth,
            PopupChildActualHeight = ((Wpf.FrameworkElement)popup.Child).ActualHeight,
            PopupChildVisible = popup.Child.IsVisible, PopupChildOpacity = popup.Child.Opacity,
            LowerHandle = lowerHandle.ToInt64(), LowerBounds = new { lowerBounds.Left, lowerBounds.Top, lowerBounds.Right, lowerBounds.Bottom },
            LowerZRank = CurrentToolbarZRank(lowerHandle),
            ToolbarHandle = toolbarHandle.ToInt64(), ToolbarVisible = toolbar.IsVisible, ToolbarZRank = CurrentToolbarZRank(toolbarHandle),
            HitHandle = hit.ToInt64(), HitProcessId = hitPid, HitIsPopup = hit == popupHandle, ForegroundHandle = foreground.ToInt64(),
            host.IsEditing, host.IsVisible, host.IsKeyboardIsolationActive, CaptureHandle = host.CaptureHandle.ToInt64(),
            RenderPending = Property<bool>(session, "IsRenderPending"),
            OverlayWindows = windows.Select(window => new
            {
                DisplayHandle = window.DisplayHandle.ToInt64(), DisplayZRank = CurrentToolbarZRank(window.DisplayHandle),
                InputHandle = window.InputHandle.ToInt64(), InputZRank = CurrentToolbarZRank(window.InputHandle),
                window.DisplayVisible, window.InputVisible, window.Bounds
            }).ToArray()
        };
        PopupPixelDiagnostics.Add(sample);
        RecordSessionState("PopupPixel" + phase, session, host, lowerHandle);
        Console.WriteLine("POPUP_PIXEL " + JsonSerializer.Serialize(sample));
    }
}
