namespace FloatCanvas.Windows;

internal static class CaptureInputIntent
{
    internal static bool CountsKeyboard(bool ownsForeground, bool suppressed) => !ownsForeground && !suppressed;
    internal static bool CountsPointer(bool externalHitTarget, bool ownsForeground, bool ownsCapture) =>
        externalHitTarget && !(ownsForeground && ownsCapture);
}
