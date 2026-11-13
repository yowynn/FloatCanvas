namespace FloatCanvas.App;

internal enum CanvasViewingCommand { OriginalSize, CenterContent, FitContent, FullCanvas, Reset }

internal sealed partial class CanvasToolOptions
{
    internal void SetAuthoringAllowed(bool allowed)
    { if (!allowed) CancelEditing(); authoringControls.IsEnabled = allowed; presets.IsEnabled = allowed; }
}
