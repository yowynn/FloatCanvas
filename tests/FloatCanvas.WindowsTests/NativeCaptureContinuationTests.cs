using System.Runtime.InteropServices;
using FloatCanvas.Windows;
using static FloatCanvas.Windows.NativeMethods;

namespace FloatCanvas.WindowsTests;

/// <summary>Opt-in transparent bounded own HWNDs only; no activation, hooks, injected input, pixels or profile.</summary>
internal static class NativeCaptureContinuationTests
{
    internal static void Run(Action<bool, string> check)
    {
        nint foreground = GetForegroundWindow(), focus = GetFocus(), capture = GetCapture();
        var bounds = new PixelBounds(40, 120, 24, 18);
        var monitor = new MonitorInfo("capture-continuation-fixture", bounds, bounds, 96, 96, true);
        using var host = new OverlayHost(OverlayMonitorScope.PrimaryScreen, () => [monitor], false);
        var diagnostics = new List<CaptureDiagnosticEvent>(); host.CaptureDiagnostic += diagnostics.Add;
        host.CaptureDiagnostic += _ => throw new IOException("A diagnostic listener is unavailable.");
        nint chrome = CreateWindowExW(ExToolWindow | ExNoActivate | ExLayered, "STATIC", "FloatCanvas capture rollback fixture",
            WsPopup, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0, 0, GetModuleHandleW(null), 0);
        check(chrome != 0 && SetLayeredWindowAttributes(chrome, 0, 0, 2), "Only a fully transparent bounded own editor HWND is created");
        nint child = 0;
        try
        {
            ShowWindow(chrome, 8);
            long origin = host.EditingSessionVersion;
            var scope = host.BeginCleanCapture([chrome]);
            check(IsWindowVisible(chrome), "Modern capture keeps visible own HWNDs visible without mechanical foreground transfer");
            check(scope.Strategy == CaptureVisibilityStrategy.ExcludeVisible && GetWindowDisplayAffinity(chrome, out var affinity) && affinity == 0x11,
                "Modern scope establishes real WDA_EXCLUDEFROMCAPTURE on the own visible HWND");
            check(host.IsCapturing && !host.IsEditing && host.EditingSessionVersion == origin && scope.Continuation is null,
                "No-action capture retains its original mode/epoch and publishes no premature receipt");
            check(scope.FrameBoundary.Timestamp > 0 && scope.FrameBoundary.Requirement == CaptureFrameRequirement.CurrentDesktopFromNewDuplication,
                "Scope supplies a typed new-duplication boundary instead of silently setting timestamp zero");
            scope.Dispose();
            check(scope.Status == CaptureRestoreStatus.PassThrough && !host.IsCapturing && !host.IsEditing && scope.Continuation is not null,
                "A passive capture remains passive without activation");
            check(IsWindowVisible(chrome) && GetWindowDisplayAffinity(chrome, out affinity) && affinity == 0,
                "Disposal restores affinity without touching unchanged visibility");
            check(host.TryConsumeCaptureContinuation(scope.Continuation!, origin, out long current) && current == origin &&
                !host.TryConsumeCaptureContinuation(scope.Continuation!, origin, out _), "Exact same-epoch native receipt is consumed once");
            check(diagnostics.Any(x => x.Phase == "Outcome" && x.Reason == "PassThrough" && !x.UserIntentChanged && !x.ExternalInputObserved),
                "Throwing diagnostic subscribers do not affect the outcome");
            check(diagnostics.All(x => x.AllowedForegrounds.Count == 0), "No diagnostic or continuation contains an external foreground whitelist");
            scope.Dispose(); check(scope.Status == CaptureRestoreStatus.PassThrough, "Disposal is idempotent");
            int outcomes = diagnostics.Count(x => x.Phase == "Outcome"); bool rejected = false;
            try { host.BeginCleanCapture([new nint(-1)]); } catch (System.ComponentModel.Win32Exception) { rejected = true; }
            check(rejected && diagnostics.Count(x => x.Phase == "Outcome") == outcomes + 1 && diagnostics.Last(x => x.Phase == "Outcome").Status == CaptureRestoreStatus.Failed,
                "Partial construction failure produces one failed outcome without a focus handoff");
            check(IsWindowVisible(chrome) && host.EditingSessionVersion == origin, "Construction failure preserves unrelated visible chrome and epoch");
            scope = host.BeginCleanCapture([chrome], () => false); scope.Dispose();
            check(scope.Status == CaptureRestoreStatus.GuardRejected && scope.Continuation is null && IsWindowVisible(chrome),
                "A document guard rejects insertion without hiding an untouched own panel");
            scope = host.BeginCleanCapture([chrome]);
            var dpiFailure = new InvalidOperationException("Injected restore DPI context failure.");
            scope.RestoreDpiScopeFactory = () => throw dpiFailure;
            AggregateException? dpiAggregate = null;
            try { scope.Dispose(); } catch (AggregateException error) { dpiAggregate = error; }
            check(dpiAggregate?.Flatten().InnerExceptions.Contains(dpiFailure) == true,
                "A restore DPI context failure is retained in the cleanup aggregate");
            check(!host.IsCapturing && scope.Status == CaptureRestoreStatus.Failed && scope.Continuation is null,
                "DPI setup failure cannot strand the host in capture or issue a receipt");
            check(IsWindowVisible(chrome) && GetWindowDisplayAffinity(chrome, out affinity) && affinity == 0 && host.EditingSessionVersion == origin,
                "DPI setup failure still attempts owned affinity cleanup without changing the epoch or foreground");
            scope.Dispose();
            check(scope.Status == CaptureRestoreStatus.Failed, "The failed cleanup remains idempotent after all finally blocks executed");
            scope = host.BeginCleanCapture([chrome]); ShowWindow(chrome, 0); host.CancelPendingCapture(); scope.Dispose();
            check(!IsWindowVisible(chrome) && scope.Status == CaptureRestoreStatus.Cancelled, "Disposal never resurrects a window hidden after an all-visible capture started");
            ShowWindow(chrome, 8);
            // Display affinity is top-level-only. A real child gives the visual fallback a deterministic API denial.
            child = CreateWindowExW(0, "STATIC", "capture child", 0x40000000, 0, 0, 4, 4, chrome, 0, GetModuleHandleW(null), 0);
            check(child != 0, "A bounded child supplies a genuine visual-affinity failure"); ShowWindow(child, 8);
            scope = host.BeginCleanCapture([child]);
            check(scope.Strategy == CaptureVisibilityStrategy.HideVisuals && !IsWindowVisible(child) && IsWindowVisible(chrome),
                "A visual-affinity failure hides only a non-active visual, never the input carrier");
            scope.Dispose(); check(IsWindowVisible(child), "A successful visual fallback restores only the visual it hid");
            scope = host.BeginCleanCapture([child], () => false); scope.Dispose();
            check(scope.Status == CaptureRestoreStatus.GuardRejected && !IsWindowVisible(child), "A stale fallback cannot resurrect its hidden visual");
            host.ForceLegacyCleanCapture = true;
            scope = host.BeginCleanCapture([chrome]);
            check(scope.Strategy == CaptureVisibilityStrategy.LegacyInputOffscreen && !IsWindowVisible(chrome), "Forced legacy capability uses the visual-only fallback without a Shell whitelist");
            scope.Dispose(); check(IsWindowVisible(chrome) && !host.IsEditing && host.EditingSessionVersion == origin,
                "A passive legacy capture never activates or invents an input lease");
            host.ForceLegacyCleanCapture = false;
            scope = host.BeginCleanCapture(); host.ExitEditing(); scope.Dispose();
            check(scope.Status == CaptureRestoreStatus.Cancelled && scope.Continuation is null && host.EditingSessionVersion == origin,
                "An explicit exit cancels even when mode and epoch were already unchanged");
            scope = host.BeginCleanCapture(); host.SetVisible(false); scope.Dispose();
            check(scope.Status == CaptureRestoreStatus.Cancelled && !host.IsVisible && scope.Continuation is null, "An idempotent explicit hide remains final");
            scope = host.BeginCleanCapture();
            check(!host.TryEnterEditing(out var failure) && failure is not null && !host.IsEditing, "Entry during capture cannot borrow an old receipt");
            scope.Dispose(); check(scope.Status == CaptureRestoreStatus.Cancelled, "Rejected entry cancels the old operation");
            scope = host.BeginCleanCapture(); SendMessageW(host.ControlHandle, WmHotkey, 0x4641, 0); scope.Dispose();
            check(scope.Status == CaptureRestoreStatus.Cancelled && !host.IsEditing, "Capture-time toggle cancels without waking editing");
            scope = host.BeginCleanCapture(); SendMessageW(host.ControlHandle, WmHotkey, 0x4642, 0); scope.Dispose();
            check(scope.Status == CaptureRestoreStatus.Cancelled && !host.IsVisible, "Capture-time hide stays hidden");
            check(GetForegroundWindow() == foreground && GetFocus() == focus && GetCapture() == capture,
                "Hidden native regressions preserve actual foreground, keyboard focus and mouse capture");
        }
        finally { if (child != 0) DestroyWindow(child); if (chrome != 0) DestroyWindow(chrome); }
    }
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint hwnd, uint message, nuint wParam, nint lParam);
}
