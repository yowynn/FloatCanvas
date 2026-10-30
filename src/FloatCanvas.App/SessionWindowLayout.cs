using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

/// <summary>Window geometry retained only for the lifetime of a DesktopSession.</summary>
internal sealed class SessionWindowLayout
{
    private const string OwnerResource = "FloatCanvas.SessionWindowLayout";
    private const string PlacementResource = OwnerResource + ".EditingPlacement";
    private readonly Dictionary<string, Rect> bounds = [];
    private readonly Dictionary<string, OverlayPoint> physicalOrigins = [];

    internal void BindRoot(Window window) => window.Resources[OwnerResource] = this;

    // Native ownership is not a WPF logical-parent relationship, so nested dialogs
    // explicitly inherit this process-only store through their owner chain.
    internal static void TrackOwned(Window window)
    {
        if (window.Resources.Contains(OwnerResource)) return;
        for (Window? owner = window.Owner; owner is not null; owner = owner.Owner)
            if (owner.Resources[OwnerResource] is SessionWindowLayout layout)
            {
                layout.Track(window);
                if (owner.Resources[PlacementResource] is EditingPlacement placement) layout.TrackEditing(window, placement.Monitors);
                return;
            }
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
            if (TryOrigin(window, out var origin)) physicalOrigins[key] = origin;
        }
        window.LocationChanged += (_, _) => Remember();
        window.SizeChanged += (_, _) => Remember();
        window.Loaded += (_, _) => Remember();
        window.IsVisibleChanged += (_, _) => Remember();
        window.Closed += (_, _) => Remember();
    }

    internal void TrackEditing(Window window, Func<IReadOnlyList<MonitorInfo>> monitors)
    {
        Track(window);
        if (window.Resources.Contains(PlacementResource)) return;
        var placement = new EditingPlacement(this, window, monitors);
        window.Resources[PlacementResource] = placement;
        window.SourceInitialized += (_, _) => placement.Prepare();
    }

    internal static void PrepareForShow(Window window)
    {
        if (OverlayWindowScope.IsRegularWindow(window)) return;
        if (window.Resources[PlacementResource] is EditingPlacement placement) placement.Prepare();
    }

    internal static PixelBounds? EditingWorkArea(Window window, OverlayPoint position)
    {
        if (OverlayWindowScope.IsRegularWindow(window) || window.Resources[PlacementResource] is not EditingPlacement placement) return null;
        var outputs = placement.Monitors();
        return outputs.Count == 0 ? null : (outputs.FirstOrDefault(output => output.Bounds.Contains(position)) ?? outputs[0]).WorkArea;
    }

    private sealed class EditingPlacement(SessionWindowLayout layout, Window window, Func<IReadOnlyList<MonitorInfo>> monitors)
    {
        internal Func<IReadOnlyList<MonitorInfo>> Monitors => monitors;
        private readonly string key = window.GetType().FullName + ":" + window.Title;
        private readonly bool explicitPosition = double.IsFinite(window.Left) && double.IsFinite(window.Top);
        private OverlayPoint? preparedOrigin;
        private bool preparing;

        internal void Prepare()
        {
            if (preparing || window.IsVisible || OverlayWindowScope.IsRegularWindow(window) || window.WindowState != WindowState.Normal) return;
            var outputs = monitors();
            if (outputs.Count == 0) return;
            preparing = true;
            try
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                nint handle = new WindowInteropHelper(window).EnsureHandle();
                bool hasOrigin = TryOrigin(window, out var current);
                // An explicit tool/inspector anchor may have moved the hidden HWND
                // after its initial default position was prepared.
                bool anchored = hasOrigin && preparedOrigin is { } prepared && current != prepared;
                OverlayPoint? wanted = anchored ? current : layout.physicalOrigins.TryGetValue(key, out var saved) ? saved :
                    explicitPosition && hasOrigin ? current : preparedOrigin;
                var target = wanted is { } point ? outputs.FirstOrDefault(output => output.WorkArea.Contains(point)) : null;
                target ??= outputs[0];
                var size = Measure(window, target);
                var origin = EditingOrigin(target, size.Width, size.Height,
                    wanted is { } position && target.WorkArea.Contains(position) ? position : null);
                OverlayCoordinates.PositionToolWindow(handle, origin);
                preparedOrigin = origin;
            }
            finally { preparing = false; }
        }
    }

    internal static OverlayPoint EditingOrigin(MonitorInfo monitor, double widthDip, double heightDip, OverlayPoint? requested = null)
    {
        double width = widthDip * monitor.DpiX / 96, height = heightDip * monitor.DpiY / 96;
        var work = monitor.WorkArea;
        return new(Math.Clamp(requested?.X ?? work.Left + (work.Width - width) / 2, work.Left, Math.Max(work.Left, work.Right - width)),
            Math.Clamp(requested?.Y ?? work.Top + (work.Height - height) / 2, work.Top, Math.Max(work.Top, work.Bottom - height)));
    }

    private static Size Measure(Window window, MonitorInfo monitor)
    {
        bool contentWidth = window.SizeToContent is SizeToContent.Width or SizeToContent.WidthAndHeight;
        bool contentHeight = window.SizeToContent is SizeToContent.Height or SizeToContent.WidthAndHeight;
        double width = double.IsFinite(window.Width) ? window.Width : window.ActualWidth > 1 ? window.ActualWidth : monitor.WorkArea.Width * 96d / monitor.DpiX;
        window.ApplyTemplate();
        var root = VisualTreeHelper.GetChildrenCount(window) > 0 ? VisualTreeHelper.GetChild(window, 0) as FrameworkElement : null;
        root?.Measure(new Size(contentWidth ? window.MaxWidth : width, window.MaxHeight));
        if (contentWidth && root is not null) width = root.DesiredSize.Width;
        double height = !contentHeight && double.IsFinite(window.Height) ? window.Height : root?.DesiredSize.Height ?? window.ActualHeight;
        return new(Math.Clamp(width, window.MinWidth, window.MaxWidth), Math.Clamp(height, window.MinHeight, window.MaxHeight));
    }

    internal static bool TryOrigin(Window window, out OverlayPoint origin)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle != 0 && GetWindowRect(handle, out var rectangle))
        { origin = new(rectangle.Left, rectangle.Top); return true; }
        origin = default; return false;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRect rectangle);

    internal static OverlayPoint ToolbarOrigin(MonitorInfo monitor, double widthDip, double heightDip, double? savedX, double? savedY)
    {
        double width = widthDip * monitor.DpiX / 96, height = heightDip * monitor.DpiY / 96;
        double x = savedX ?? monitor.Bounds.Left + (monitor.Bounds.Width - width) / 2;
        double y = savedY ?? monitor.Bounds.Top;
        return new(Math.Clamp(x, monitor.Bounds.Left, Math.Max(monitor.Bounds.Left, monitor.Bounds.Right - width)),
            Math.Clamp(y, monitor.Bounds.Top, Math.Max(monitor.Bounds.Top, monitor.WorkArea.Bottom - height)));
    }
}
