using System.Windows;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private PassThroughLauncherWindow? _passThroughLauncher;

    private void ToggleFloatingLogo()
    {
        bool previous=_settings.ShowPassThroughLauncher;
        _settings.ShowPassThroughLauncher=!previous;
        try { _settings.Save(); }
        catch { _settings.ShowPassThroughLauncher=previous; throw; }
        RefreshPassThroughLauncher();
    }

    private void RefreshPassThroughLauncher()
    {
        if (_disposed || _toolbar is null) return;
        RefreshMascotVisibility(_host.IsVisible);
        // An editing request can still be waiting for entry keys or input acknowledgement.
        // Keep the launcher until ShowEditingWindows has actually shown its replacement.
        if (!_settings.ShowPassThroughLauncher || _host.IsSystemScreenshotActive || (_host.IsEditing && _toolbar.IsVisible))
        { _passThroughLauncher?.Hide(); return; }
        if (_passThroughLauncher is null)
        {
            _passThroughLauncher = new();
            _passThroughLauncher.EnterEditingRequested += () => RunUiAction(() => SetEditing(true));
            _passThroughLauncher.ToggleVisibilityRequested += () => RunUiAction(ToggleCanvasVisibility);
            _passThroughLauncher.PositionChanged += origin =>
            {
                _settings.ToolbarX = origin.X; _settings.ToolbarY = origin.Y;
            };
        }
        if (_settings.ToolbarX is null || _settings.ToolbarY is null)
        {
            var surface = (FrameworkElement)_toolbar.Content;
            surface.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var primary = _host.PrimaryMonitor;
            var initial = SessionWindowLayout.ToolbarOrigin(primary, surface.DesiredSize.Width, surface.DesiredSize.Height, null, null);
            _settings.ToolbarX = initial.X; _settings.ToolbarY = initial.Y;
        }
        var target = _host.Monitors.FirstOrDefault(m => m.Bounds.Contains(new OverlayPoint(_settings.ToolbarX.Value, _settings.ToolbarY.Value))) ?? _host.PrimaryMonitor;
        var origin = SessionWindowLayout.ToolbarOrigin(target, PassThroughLauncherWindow.ArtworkSize, PassThroughLauncherWindow.ArtworkSize,
            _settings.ToolbarX, _settings.ToolbarY);
        _settings.ToolbarX = origin.X; _settings.ToolbarY = origin.Y;
        _passThroughLauncher.ShowAt(origin, _host.IsVisible);
    }
}
