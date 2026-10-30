using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Windows;

internal static class ToolbarWindowBoundsTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const uint NoSize = 0x0001, NoMove = 0x0002, NoZOrder = 0x0004, NoActivate = 0x0010, NoOwnerZOrder = 0x0200;

    internal static void Run(Action<string, bool> check, string? output = null)
    {
        if (Application.Current is not null) throw new InvalidOperationException("Run toolbar bounds in a fresh isolated process.");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        output ??= Path.Combine(Path.GetTempPath(), "FloatCanvas-toolbar-bounds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        ApplicationPaths.Configure(["--profile", Path.Combine(output, "profile")]);
        new UserSettings { ShowFloatingLogo = false, ShowPassThroughLauncher = false, PreferGpuAcceleration = false, Theme = "light" }.Save();
        var primary = new MonitorInfo("toolbar-primary", new(-18000, -18000, 4500, 2400), new(-18000, -18000, 4500, 2400), 96, 96, true);
        var secondary = new MonitorInfo("toolbar-secondary", new(-13500, -18000, 4500, 2400), new(-13500, -18000, 4500, 2400), 144, 144, false);
        var separated = new MonitorInfo("toolbar-separated", new(-8500, -18000, 4500, 2400), new(-8500, -18000, 4500, 2400), 96, 96, false);
        IReadOnlyList<MonitorInfo> monitors = [secondary, separated, primary];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null,
            [OverlayMonitorScope.PrimaryScreen, (Func<IReadOnlyList<MonitorInfo>>)(() => monitors), false], null)!;
        using var session = new DesktopSession(app, host);
        Field<System.Windows.Forms.NotifyIcon>(session, "_tray").Visible = false;
        var toolbar = Field<Window>(session, "_toolbar");
        toolbar.Opacity = 0; toolbar.ShowActivated = false; toolbar.ShowInTaskbar = false;
        Invoke(session, "PlaceToolbar"); toolbar.Show(); Pump(); toolbar.UpdateLayout();
        nint handle = new WindowInteropHelper(toolbar).Handle;
        var expanded = Bounds(handle);
        check("toolbar bounds fixture uses the actual expanded production toolbar", expanded.Width > 500 && expanded.Height > 0);
        Move(handle, primary.Bounds.Left + 80, primary.Bounds.Top + 120);
        int outside = 0;
        void Located(object? sender, EventArgs args) { if (!Covered(Bounds(handle), host.Monitors)) outside++; }
        toolbar.LocationChanged += Located;
        RectPixels constrained;
        try { Move(handle, primary.Bounds.Left - 300, primary.Bounds.Top + 120); constrained = Bounds(handle); }
        finally { toolbar.LocationChanged -= Located; }
        File.WriteAllText(Path.Combine(output, "first-native-move.txt"), $"before={expanded}; actual={constrained}; outsideLocationEvents={outside}");
        check("native toolbar movement is constrained before any outside LocationChanged frame (" + constrained + "; outside=" + outside + ")",
            Covered(constrained, host.Monitors) && outside == 0 && constrained.Left == primary.Bounds.Left);
        var canonical = Field<UserSettings>(session, "_settings");
        check("live constrained toolbar movement saves its actual native origin as the canonical position", canonical.ToolbarX == constrained.Left && canonical.ToolbarY == constrained.Top);
        ProductionScopes(session, toolbar, handle, host, primary, secondary, separated, check);
        ProductionExpansion(session, toolbar, host, primary, expanded, check);
        NativeHook(toolbar, primary, secondary, check);
    }

    private static void ProductionScopes(DesktopSession session, Window toolbar, nint handle, OverlayHost host, MonitorInfo primary, MonitorInfo secondary, MonitorInfo separated, Action<string, bool> check)
    {
        host.SetMonitorScope(OverlayMonitorScope.SpecificScreen, secondary.DeviceName);
        Move(handle, primary.Bounds.Left + 200, primary.Bounds.Top + 150);
        var rect = Bounds(handle);
        check("live specific-screen toolbar bounds exclude every other physically available screen", Covered(rect, host.Monitors) && rect.Left >= secondary.Bounds.Left);
        host.SetMonitorScope(OverlayMonitorScope.AllScreens);
        int seam = secondary.Bounds.Left - rect.Width / 2;
        Move(handle, seam, primary.Bounds.Top + 150); rect = Bounds(handle);
        check("an all-screen toolbar can straddle the adjacent monitor seam without being pushed to one screen", Covered(rect, host.Monitors) && rect.Left == seam && rect.Left < secondary.Bounds.Left && rect.Right > secondary.Bounds.Left);
        var settings = Field<UserSettings>(session, "_settings");
        check("an all-screen seam position is stored in physical canonical coordinates", settings.ToolbarX == rect.Left && settings.ToolbarY == rect.Top);
        var savedSeam = rect;
        Invoke(session, "PlaceToolbar"); Pump(); rect = Bounds(handle);
        check("placing the toolbar again preserves a valid saved seam-straddling position", rect.Equals(savedSeam) && settings.ToolbarX == rect.Left && settings.ToolbarY == rect.Top);
        int nearSeam = secondary.Bounds.Left - 30;
        int seamTop = primary.Bounds.Top + 150;
        Move(handle, nearSeam, seamTop); toolbar.Hide();
        settings.ShowPassThroughLauncher = true;
        Invoke(session, "RefreshPassThroughLauncher"); Pump();
        var passive = Field<PassThroughLauncherWindow>(session, "_passThroughLauncher");
        passive.Opacity = 0;
        var passiveBounds = Bounds(new WindowInteropHelper(passive).Handle);
        check("a passive icon preserves a valid origin beside an all-screen seam (" + passiveBounds + "; saved=" + settings.ToolbarX + ")",
            Covered(passiveBounds, host.Monitors) && passiveBounds.Left == nearSeam && passiveBounds.Top == seamTop && settings.ToolbarX == nearSeam && settings.ToolbarY == seamTop);
        var savedPassive = passiveBounds;
        Move(handle, primary.Bounds.Left + 300, primary.Bounds.Top + 260);
        passiveBounds = Bounds(new WindowInteropHelper(passive).Handle);
        check("moving the hidden editing HWND cannot overwrite the visible passive origin (" + passiveBounds + "; saved=" + settings.ToolbarX + ")",
            passiveBounds.Equals(savedPassive) && settings.ToolbarX == nearSeam && settings.ToolbarY == seamTop);
        foreach (bool specific in new[] { false, true })
        {
            host.SetMonitorScope(specific ? OverlayMonitorScope.SpecificScreen : OverlayMonitorScope.PrimaryScreen, specific ? secondary.DeviceName : null);
            var active = specific ? secondary : primary;
            nint passiveHandle = new WindowInteropHelper(passive).Handle;
            int outside = 0;
            void Located(object? sender, EventArgs args) { if (!Covered(Bounds(passiveHandle), host.Monitors)) outside++; }
            passive.LocationChanged += Located;
            try { Move(passiveHandle, active.Bounds.Left - 200, active.Bounds.Top + 180); }
            finally { passive.LocationChanged -= Located; }
            passiveBounds = Bounds(passiveHandle);
            check((specific ? "specific-screen" : "primary-only") + " passive native movement is constrained before an outside frame",
                Covered(passiveBounds, host.Monitors) && passiveBounds.Left == active.Bounds.Left && outside == 0);
            Invoke(passive, "CompleteDrag", new Point(passive.Left - 1, passive.Top));
            check((specific ? "specific-screen" : "primary-only") + " passive drag completion saves the constrained shared origin",
                settings.ToolbarX == passiveBounds.Left && settings.ToolbarY == passiveBounds.Top);
        }
        host.SetMonitorScope(OverlayMonitorScope.AllScreens);
        Move(new WindowInteropHelper(passive).Handle, nearSeam, seamTop);
        Invoke(passive, "CompleteDrag", new Point(passive.Left - 1, passive.Top));
        settings.ShowPassThroughLauncher = false; passive.Hide();
        Invoke(session, "PlaceToolbar"); toolbar.Show(); Pump(); rect = Bounds(handle);
        check("the expanded-passive-expanded round trip preserves its exact near-seam physical origin",
            rect.Left == nearSeam && rect.Top == seamTop && Covered(rect, host.Monitors) && settings.ToolbarX == nearSeam && settings.ToolbarY == seamTop);
        int gap = secondary.Bounds.Right - rect.Width / 2;
        Move(handle, gap, primary.Bounds.Top + 150); rect = Bounds(handle);
        check("an all-screen toolbar never occupies the dead gap inside the monitor bounding rectangle", Covered(rect, host.Monitors) && rect.Left != gap);
        Move(handle, separated.Bounds.Left + 120, separated.Bounds.Top + 150); rect = Bounds(handle);
        check("an all-screen toolbar can move onto a separated active display", Covered(rect, host.Monitors) && rect.Left == separated.Bounds.Left + 120);
        host.SetMonitorScope(OverlayMonitorScope.PrimaryScreen);
        Move(handle, seam, primary.Bounds.Top + 150); rect = Bounds(handle);
        check("switching back to primary-only constrains the existing toolbar using the current scope", Covered(rect, host.Monitors) && rect.Right <= primary.Bounds.Right);
    }

    private static void ProductionExpansion(DesktopSession session, Window toolbar, OverlayHost host, MonitorInfo primary, RectPixels expanded, Action<string, bool> check)
    {
        var settings = Field<UserSettings>(session, "_settings");
        var body = Field<FrameworkElement>(session, "_toolbarExpandedContent");
        nint handle = new WindowInteropHelper(toolbar).Handle;
        settings.ToolbarExpanded = false; body.Visibility = Visibility.Collapsed;
        toolbar.UpdateLayout(); Pump();
        var collapsed = Bounds(handle);
        check("collapsed editing bounds use the actual icon-sized HWND rather than the expanded toolbar width", collapsed.Width < expanded.Width && collapsed.Height > 0);
        Move(handle, primary.Bounds.Right - collapsed.Width, primary.Bounds.Top + 120);
        var atEdge = Bounds(handle);
        check("the collapsed icon can reach the selected display edge", atEdge.Right == primary.Bounds.Right && Covered(atEdge, host.Monitors));
        int outside = 0;
        void Located(object? sender, EventArgs args) { if (!Covered(Bounds(handle), host.Monitors)) outside++; }
        toolbar.LocationChanged += Located;
        try { settings.ToolbarExpanded = true; body.Visibility = Visibility.Visible; toolbar.UpdateLayout(); Pump(); }
        finally { toolbar.LocationChanged -= Located; }
        var reopened = Bounds(handle);
        check("expanding at the edge constrains the whole grown native frame before an outside location is applied",
            reopened.Width == expanded.Width && Covered(reopened, host.Monitors) && outside == 0);
    }

    private static void NativeHook(Window owner, MonitorInfo primary, MonitorInfo secondary, Action<string, bool> check)
    {
        IReadOnlyList<MonitorInfo> regions = [primary];
        var window = new Window { Owner = owner, Width = 94, Height = 94, WindowStyle = WindowStyle.None, AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent, ResizeMode = ResizeMode.NoResize, Opacity = 0, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -19000, Top = -19000 };
        ToolbarWindowBounds.Attach(window, () => regions);
        try
        {
            window.Show(); window.UpdateLayout(); Pump();
            nint handle = new WindowInteropHelper(window).Handle;
            var icon = Bounds(handle);
            var requested = new OverlayPoint(primary.Bounds.Left + 123, primary.Bounds.Top + 234);
            OverlayCoordinates.PositionToolWindow(handle, requested);
            var atOrigin = Bounds(handle);
            check("toolbar HWND constraints preserve negative physical origins without applying DIP scaling", atOrigin.Left == requested.X && atOrigin.Top == requested.Y && atOrigin.Width == icon.Width && atOrigin.Height == icon.Height);
            Move(handle, primary.Bounds.Right - icon.Width, primary.Bounds.Top + 120);
            int outside = 0;
            void Located(object? sender, EventArgs args) { if (!Covered(Bounds(handle), regions)) outside++; }
            window.LocationChanged += Located;
            try
            {
                if (!SetWindowPos(handle, 0, 0, 0, icon.Width + 240, icon.Height + 30, NoMove | NoZOrder | NoActivate | NoOwnerZOrder))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { window.LocationChanged -= Located; }
            var grown = Bounds(handle);
            check("a SWP_NOMOVE size growth can shift origin to keep the full new native size visible", grown.Width == icon.Width + 240 && grown.Height == icon.Height + 30 && Covered(grown, regions) && outside == 0);
            var proposal = new RectPixels { Left = primary.Bounds.Left - 500, Top = primary.Bounds.Top - 300,
                Right = primary.Bounds.Left - 500 + grown.Width, Bottom = primary.Bounds.Top - 300 + grown.Height };
            nint memory = Marshal.AllocHGlobal(Marshal.SizeOf<RectPixels>());
            try
            {
                Marshal.StructureToPtr(proposal, memory, false);
                SendMessage(handle, 0x0216, 2, memory);
                proposal = Marshal.PtrToStructure<RectPixels>(memory);
            }
            finally { Marshal.FreeHGlobal(memory); }
            check("WM_MOVING constrains the proposed physical rectangle without applying an outside frame", Covered(proposal, regions) && Bounds(handle).Equals(grown));
            regions = [secondary];
            Move(handle, primary.Bounds.Left + 150, primary.Bounds.Top + 150);
            check("an already attached HWND reads live monitor regions for every move", Covered(Bounds(handle), regions));
            regions = [secondary with { Bounds = new(secondary.Bounds.Left, secondary.Bounds.Top, 1000, secondary.Bounds.Height),
                WorkArea = new(secondary.Bounds.Left, secondary.Bounds.Top, 1000, secondary.Bounds.Height) }];
            int oversizedWidth = 1300; // Exceeds the allowed region while remaining below the OS maximum native window width.
            if (!SetWindowPos(handle, 0, 0, 0, oversizedWidth, grown.Height, NoMove | NoZOrder | NoActivate | NoOwnerZOrder))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var oversized = Bounds(handle);
            var reachable = new RectPixels { Left = oversized.Left, Top = oversized.Top, Right = oversized.Left + icon.Width, Bottom = oversized.Top + icon.Height };
            check("an unfittable native frame retains its size and keeps its icon-sized origin reachable (actual=" + oversized + "; requestedWidth=" + oversizedWidth + "; requestedHeight=" + grown.Height + "; icon=" + icon.Width + "x" + icon.Height + ")",
                oversized.Width == oversizedWidth && oversized.Height == grown.Height && Covered(reachable, regions));
        }
        finally { window.Close(); }

        var unrelated = new Window { Owner = owner, Width = 150, Height = 100, WindowStyle = WindowStyle.None, Opacity = 0, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = primary.Bounds.Left, Top = primary.Bounds.Top };
        try
        {
            unrelated.Show(); nint handle = new WindowInteropHelper(unrelated).Handle;
            Move(handle, primary.Bounds.Left - 500, primary.Bounds.Top - 200);
            var rect = Bounds(handle);
            check("the toolbar bounds hook does not constrain unrelated application windows", rect.Left == primary.Bounds.Left - 500 && rect.Top == primary.Bounds.Top - 200);
        }
        finally { unrelated.Close(); }
    }

    private static bool Covered(RectPixels rectangle, IReadOnlyList<MonitorInfo> monitors)
    {
        var cuts = monitors.SelectMany(monitor => new[] { monitor.Bounds.Left, monitor.Bounds.Right })
            .Where(x => x > rectangle.Left && x < rectangle.Right).Append(rectangle.Left).Append(rectangle.Right).Distinct().Order().ToArray();
        for (int index = 0; index < cuts.Length - 1; index++)
        {
            var ranges = monitors.Where(monitor => monitor.Bounds.Left <= cuts[index] && monitor.Bounds.Right >= cuts[index + 1])
                .Select(monitor => (Top: Math.Max(rectangle.Top, monitor.Bounds.Top), Bottom: Math.Min(rectangle.Bottom, monitor.Bounds.Bottom)))
                .Where(range => range.Bottom > range.Top).OrderBy(range => range.Top);
            int top = rectangle.Top;
            foreach (var range in ranges)
            {
                if (range.Top > top) return false;
                top = Math.Max(top, range.Bottom);
            }
            if (top < rectangle.Bottom) return false;
        }
        return true;
    }

    private static void Move(nint handle, int x, int y)
    {
        if (!SetWindowPos(handle, 0, x, y, 0, 0, NoSize | NoZOrder | NoActivate | NoOwnerZOrder))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    private static RectPixels Bounds(nint handle)
    {
        if (!GetWindowRect(handle, out var rectangle)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return rectangle;
    }
    private static T Field<T>(object source, string name) => (T)source.GetType().GetField(name, Private)!.GetValue(source)!;
    private static void Invoke(object source, string name, params object[] arguments) => source.GetType().GetMethod(name, Private)!.Invoke(source, arguments);
    private static void Pump()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame);
    }
    [StructLayout(LayoutKind.Sequential)] private struct RectPixels
    {
        internal int Left, Top, Right, Bottom;
        internal int Width => Right - Left;
        internal int Height => Bottom - Top;
        public override readonly string ToString() => $"[{Left},{Top}..{Right},{Bottom}]";
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint window, out RectPixels rectangle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
