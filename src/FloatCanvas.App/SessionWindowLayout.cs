using System.Windows;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

/// <summary>Window geometry retained only for the lifetime of a DesktopSession.</summary>
internal sealed class SessionWindowLayout
{
    private const string OwnerResource = "FloatCanvas.SessionWindowLayout";
    private readonly Dictionary<string, Rect> bounds = [];

    internal void BindRoot(Window window) => window.Resources[OwnerResource] = this;

    // Native ownership is not a WPF logical-parent relationship, so nested dialogs
    // explicitly inherit this process-only store through their owner chain.
    internal static void TrackOwned(Window window)
    {
        if (window.Resources.Contains(OwnerResource)) return;
        for (Window? owner = window.Owner; owner is not null; owner = owner.Owner)
            if (owner.Resources[OwnerResource] is SessionWindowLayout layout) { layout.Track(window); return; }
    }

    internal void Track(Window window)
    {
        if (window.Resources.Contains(OwnerResource)) return;
        BindRoot(window);
        string key = window.GetType().FullName + ":" + window.Title;
        if (bounds.TryGetValue(key, out var previous))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = previous.Left; window.Top = previous.Top;
            window.Width = previous.Width;
            // Fixed-size prompts still fit their current text. Only resizable tool
            // panels have a user-selected height to restore.
            if (window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
            { window.Height = previous.Height; window.SizeToContent = SizeToContent.Manual; }
        }
        void Remember()
        {
            if (window.WindowState != WindowState.Normal || !window.IsLoaded ||
                !double.IsFinite(window.Left) || !double.IsFinite(window.Top) || window.ActualWidth <= 1 || window.ActualHeight <= 1) return;
            bounds[key] = new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
        }
        window.LocationChanged += (_, _) => Remember();
        window.SizeChanged += (_, _) => Remember();
        window.Loaded += (_, _) => Remember();
        window.IsVisibleChanged += (_, _) => Remember();
        window.Closed += (_, _) => Remember();
    }

    internal static OverlayPoint ToolbarOrigin(MonitorInfo monitor, double widthDip, double heightDip, double? savedX, double? savedY)
    {
        double width = widthDip * monitor.DpiX / 96, height = heightDip * monitor.DpiY / 96;
        double x = savedX ?? monitor.Bounds.Left + (monitor.Bounds.Width - width) / 2;
        double y = savedY ?? monitor.Bounds.Top;
        return new(Math.Clamp(x, monitor.Bounds.Left, Math.Max(monitor.Bounds.Left, monitor.Bounds.Right - width)),
            Math.Clamp(y, monitor.Bounds.Top, Math.Max(monitor.Bounds.Top, monitor.WorkArea.Bottom - height)));
    }
}
