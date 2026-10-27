using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Windows;

internal static class MonitorWindowPlacementTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        if (Application.Current is not null) throw new InvalidOperationException("Run monitor window placement in a fresh process.");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        ApplicationPaths.Configure(["--profile", Path.Combine(Path.GetTempPath(), "FloatCanvas-MonitorWindowPlacement", Guid.NewGuid().ToString("N"))]);
        new UserSettings { ShowFloatingLogo = false, Theme = "light" }.Save();
        var primary = new MonitorInfo("placement-primary", new(-15000, -15000, 900, 800), new(-14980, -14980, 860, 760), 96, 96, true);
        var secondary = new MonitorInfo("placement-secondary", new(-13900, -15400, 1000, 900), new(-13880, -15380, 960, 860), 144, 144, false);
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), BindingFlags.Instance | BindingFlags.NonPublic, null,
            [OverlayMonitorScope.PrimaryScreen, (Func<IReadOnlyList<MonitorInfo>>)(() => [secondary, primary]), false], null)!;
        host.SetMonitorScope(OverlayMonitorScope.SpecificScreen, secondary.DeviceName);
        using var desktop = new DesktopSession(app, host);
        Field<System.Windows.Forms.NotifyIcon>(desktop, "_tray").Visible = false;
        var editor = Field<EditorController>(desktop, "_editor");
        var layers = new LayersWindow(() => editor.Session) { Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        var windows = new List<Window> { layers };
        nint foreground = GetForegroundWindow();
        try
        {
            Invoke(desktop, "ConfigureAppWindow", layers, false, false);
            RECT? firstVisible = null;
            layers.IsVisibleChanged += (_, _) => { if (layers.IsVisible && firstVisible is null) firstVisible = Bounds(layers); };
            Show(layers);
            check("a newly opened editing Layers window starts on the specific active screen", Inside(Bounds(layers), secondary));
            check("the first visible Layers frame is already on its active screen", firstVisible is { } initial && Inside(initial, secondary));

            var remembered = new OverlayPoint(secondary.WorkArea.Left + 140, secondary.WorkArea.Top + 20);
            OverlayCoordinates.PositionToolWindow(new WindowInteropHelper(layers).Handle, remembered); Pump();
            layers.Hide(); Show(layers);
            check("reopening an editing panel keeps its user-chosen physical position on the active display", SameOrigin(Bounds(layers), remembered));

            layers.Hide(); host.SetMonitorScope(OverlayMonitorScope.PrimaryScreen); Show(layers);
            check("a reopened panel whose remembered screen is no longer active moves to the reference screen", Inside(Bounds(layers), primary));

            layers.Hide(); host.SetMonitorScope(OverlayMonitorScope.SpecificScreen, secondary.DeviceName);
            SessionWindowCoordinator.Show(layers); Pump();
            check("coordinator resume also recovers reused panels whose remembered display became inactive", Inside(Bounds(layers), secondary));

            layers.Hide(); host.SetMonitorScope(OverlayMonitorScope.AllScreens);
            var another = new Window { Title = "Unpositioned editing window", Width = 280, Height = 210, Opacity = 0,
                ShowActivated = false, ShowInTaskbar = false };
            windows.Add(another); Invoke(desktop, "ConfigureAppWindow", another, false, false); Show(another);
            check("new all-screen editing windows use the actual primary rather than enumeration order", Inside(Bounds(another), primary));

            var savedLayers = Bounds(layers); layers.Hide();
            var recreated = new LayersWindow(() => editor.Session) { Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
            windows.Add(recreated); Invoke(desktop, "ConfigureAppWindow", recreated, false, false); Show(recreated);
            check("recreated panels restore session memory in physical coordinates while that display remains active", SameOrigin(Bounds(recreated),
                new(savedLayers.Left, savedLayers.Top)));

            var regular = new Window { Title = "Regular positioning control", Width = 250, Height = 180, Left = 123, Top = 97,
                Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
            windows.Add(regular); Invoke(desktop, "ConfigureAppWindow", regular, false, true); Show(regular);
            check("regular windows retain their normal positioning instead of being moved to an editing reference", Bounds(regular).Left > -1000 && Bounds(regular).Top > -1000);

            var anchored = new Window { Title = "Explicit anchored editor", Width = 250, Height = 180, Opacity = 0,
                ShowActivated = false, ShowInTaskbar = false };
            windows.Add(anchored); Invoke(desktop, "ConfigureAppWindow", anchored, false, false);
            nint anchorHandle = new WindowInteropHelper(anchored).EnsureHandle();
            var anchor = new OverlayPoint(primary.WorkArea.Left + 100, primary.WorkArea.Top + 90);
            OverlayCoordinates.PositionToolWindow(anchorHandle, anchor); Show(anchored);
            check("an explicit pre-show tool or inspector anchor overrides the default placement", SameOrigin(Bounds(anchored), anchor));
            check("reference-screen positioning never activates a window or enters editing", GetForegroundWindow() == foreground && !host.IsEditing);
            var highDpi = SessionWindowLayout.EditingOrigin(secondary, 320, 240);
            check("editing default origin scales only window size while retaining the physical desktop origin", highDpi ==
                new OverlayPoint(secondary.WorkArea.Left + (secondary.WorkArea.Width - 480) / 2d,
                    secondary.WorkArea.Top + (secondary.WorkArea.Height - 360) / 2d));
        }
        finally
        {
            foreach (var window in windows)
            {
                window.Close();
                nint handle = new WindowInteropHelper(window).Handle;
                if (handle != 0) HwndSource.FromHwnd(handle)?.Dispose();
            }
            Pump();
        }

        void Show(Window window) { Invoke(desktop, "ShowAppWindow", window, false); Pump(); }
    }

    private static bool Inside(RECT rect, MonitorInfo monitor) => rect.Left >= monitor.WorkArea.Left && rect.Top >= monitor.WorkArea.Top &&
        rect.Left < monitor.WorkArea.Right && rect.Top < monitor.WorkArea.Bottom;
    private static bool SameOrigin(RECT rect, OverlayPoint point) => Math.Abs(rect.Left - point.X) <= 1 && Math.Abs(rect.Top - point.Y) <= 1;
    private static RECT Bounds(Window window)
    {
        if (!GetWindowRect(new WindowInteropHelper(window).Handle, out var bounds)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return bounds;
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object?[] arguments) => target.GetType().GetMethod(name, Private)!.Invoke(target, arguments);
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out RECT bounds);
}
