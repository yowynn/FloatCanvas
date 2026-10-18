using FloatCanvas.Windows;

namespace FloatCanvas.App;

/// <summary>Normal user departures stay quiet; unexpected capture/restoration failures remain discoverable.</summary>
internal static class SnapshotFailureFeedback
{
    internal static bool ShouldNotify(CaptureRestoreStatus status, CaptureDiagnosticEvent? outcome,
        CaptureDiagnosticEvent? latest, bool userCancellation, string? cancellationReason)
    {
        if (userCancellation || status == CaptureRestoreStatus.GuardRejected) return false;
        // Failure outcomes freeze these flags before their own Exit/Cancel cleanup.
        // A later Receipt diagnostic can include cleanup's intent increments.
        var intent = outcome is { Status: not (CaptureRestoreStatus.Restored or CaptureRestoreStatus.PassThrough or CaptureRestoreStatus.Pending) }
            ? outcome : latest ?? outcome;
        if (intent is { UserIntentChanged: true } or { ExternalInputObserved: true }) return false;
        if (outcome?.Reason is "ExplicitCancel" or "SessionChanged" or "AppGuard" or "ExternalInput" or
            "UnexpectedForeground" or "ForegroundEventMismatch") return false;
        // A departure after a successful restore (or during CanvasOnly rendering) is an
        // ordinary cancellation. An actual restore failure still needs its own explanation.
        if (cancellationReason == "ExternalFocus" && outcome?.Status is null or CaptureRestoreStatus.Restored)
            return false;
        return true;
    }
}
