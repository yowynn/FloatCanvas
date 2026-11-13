using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed class CanvasModeSwitch() : CaptionModeSwitch((first, foreground) => CanvasModeVisuals.Icon(first, foreground: foreground))
{
    internal GroupCanvasMode Mode { get; private set; }
    internal void Synchronize(GroupCanvasMode mode, bool enabled, string? disabledReason = null)
    {
        Mode = mode;
        bool independent = mode == GroupCanvasMode.Temporary;
        Synchronize(independent, enabled, L.Get("GroupCanvas.Display"),
            L.Get(independent ? "GroupCanvas.Temporary" : "GroupCanvas.RootProject"),
            L.Get(independent ? "GroupCanvas.SwitchIndependent" : "GroupCanvas.SwitchProject"), disabledReason);
    }
}
