using System.Windows;
using System.Windows.Media;
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
        // An editing request can still be waiting for entry keys or input acknowledgement.
        // Keep the launcher until ShowEditingWindows has actually shown its replacement.
        if (!_settings.ShowPassThroughLauncher || (_host.IsEditing && _toolbar.IsVisible))
        { _passThroughLauncher?.Hide(); return; }
        if (_passThroughLauncher is null)
        {
            _passThroughLauncher = new();
            _passThroughLauncher.EnterEditingRequested += () => RunUiAction(() => SetEditing(true));
            _passThroughLauncher.ToggleVisibilityRequested += () => RunUiAction(ToggleCanvasVisibility);
        }
        OverlayPoint origin;
        DpiScale toolbarDpi;
        if (_toolbarPlaced && PresentationSource.FromVisual(_toolbarLauncher) is not null)
        {
            var point = _toolbarLauncher.PointToScreen(new Point()); origin = new(point.X, point.Y);
            toolbarDpi = VisualTreeHelper.GetDpi(_toolbarLauncher);
        }
        else
        {
            var surface = (FrameworkElement)_toolbar.Content;
            surface.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var primary = _host.PrimaryMonitor;
            var target = _settings.ToolbarX is { } x && _settings.ToolbarY is { } y
                ? _host.Monitors.FirstOrDefault(m => m.WorkArea.Contains(new OverlayPoint(x, y))) ?? primary : primary;
            origin = SessionWindowLayout.ToolbarOrigin(target, surface.DesiredSize.Width, surface.DesiredSize.Height, _settings.ToolbarX, _settings.ToolbarY);
            toolbarDpi = new DpiScale(target.DpiX / 96d, target.DpiY / 96d);
        }
        _passThroughLauncher.ShowAt(origin, _host.IsVisible, toolbarDpi);
    }
}
