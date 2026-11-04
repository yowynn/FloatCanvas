namespace FloatCanvas.App;

internal enum CanvasViewingCommand { OriginalSize, CenterContent, FitContent, FullCanvas, Reset }

internal sealed partial class CanvasSettingsWindow
{
    internal void SetAuthoringAllowed(bool allowed)
    { authoringControls.IsEnabled = allowed; presets.IsEnabled = allowed; }
}
