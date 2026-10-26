using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Windows;
using Wpf = System.Windows;
using Interop = System.Windows.Interop;
using Threading = System.Windows.Threading;
using L = FloatCanvas.Localization.AppText;

/// <summary>
/// Opt-in native floating-panel regression. This fixture shows only its own windows, never enters
/// editing, registers no hotkeys and sends native caption messages only to its own HWNDs.
/// It never injects desktop input. Keep it outside the detached test suite.
/// </summary>
internal static class WindowLayoutFixture
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private delegate bool EnumWindow(nint handle, nint parameter);

    public static void Run(Action<string, bool> assert)
    {
        if (Wpf.Application.Current is not null)
            throw new InvalidOperationException("Run --window-layout-only in its own test process.");

        string profile = Path.Combine(Path.GetTempPath(), "FloatCanvas-WindowLayout", Guid.NewGuid().ToString("N"));
        ApplicationPaths.Configure(["--profile", profile, "--primary-screen"]);
        Directory.CreateDirectory(profile);
        var app = new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
        DesktopSession? session = null;
        var shown = new List<Wpf.Window>();
        var handles = new List<nint>();
        var results = new List<LayoutSnapshot>();
        nint[] hostHandles = [];
        try
        {
            session = new DesktopSession(app);
            var host = Field<OverlayHost>(session, "_host");
            var editor = Field<EditorController>(session, "_editor");
            var toolbar = Field<Wpf.Window>(session, "_toolbar");
            hostHandles = host.Diagnostics.SelectMany(pair => new[] { pair.DisplayHandle, pair.InputHandle })
                .Append(host.ControlHandle).ToArray();

            // The production creation/configuration/Show paths remain intact. Only activation is
            // disabled in this isolated process so the fixture does not steal the user's foreground.
            var inherited = app.TryFindResource(typeof(Wpf.Window)) as Wpf.Style;
            var nonActivating = inherited is null ? new Wpf.Style(typeof(Wpf.Window)) : new Wpf.Style(typeof(Wpf.Window), inherited);
            nonActivating.Setters.Add(new Wpf.Setter(Wpf.Window.ShowActivatedProperty, false));
            app.Resources[typeof(Wpf.Window)] = nonActivating;

            Invoke(session, "ShowWelcome");
            var welcome = app.Windows.Cast<Wpf.Window>().Single(window => window.Title == WindowTitles.Format(L.Get("Welcome.Title"), true));
            shown.Add(welcome);
            CheckFirstShow("welcome", welcome, 430);
            assert("welcome remains owned by the initialized but hidden toolbar", ReferenceEquals(welcome.Owner, toolbar) &&
                new Interop.WindowInteropHelper(toolbar).Handle != 0 && !toolbar.IsVisible);
            welcome.Close();

            var inspector = new InspectorWindow(() => editor.Session);
            // Match ToggleInspector's initial sizing within the current monitor's work area.
            var monitor = host.PrimaryMonitor;
            inspector.Height = Math.Max(inspector.MinHeight, Math.Min(inspector.Height, monitor.WorkArea.Height * 96 / monitor.DpiY - 24));
            FieldInfo inspectorField = typeof(DesktopSession).GetField("_inspector", PrivateInstance)!;
            inspectorField.SetValue(session, inspector);
            Invoke(session, "ConfigureAppWindow", inspector, false);
            shown.Add(inspector);
            inspector.Show();
            CheckFirstShow("properties", inspector, 340);
            CheckFloatingPanel("properties", inspector);
            inspector.Hide();

            var layers = new LayersWindow(() => editor.Session);
            typeof(DesktopSession).GetField("_layers", PrivateInstance)!.SetValue(session, layers);
            Invoke(session, "ConfigureAppWindow", layers, false);
            shown.Add(layers);
            layers.Show();
            CheckFirstShow("layers", layers, 320);
            CheckHeaderHelp("layers", layers, L.Get("Layers.HelpWithRename"));
            CheckFloatingPanel("layers", layers);
            layers.Hide();

            var resizable = new Wpf.Window { Title = "Shared floating panel regression", Width = 420, Height = 300,
                ShowActivated = false, Content = new System.Windows.Controls.TextBlock { Text = "Shared chrome content" } };
            Invoke(session, "ConfigureAppWindow", resizable, false);
            shown.Add(resizable);
            resizable.Show();
            PumpToIdle(app.Dispatcher);
            handles.Add(new Interop.WindowInteropHelper(resizable).Handle);
            AppWindowChrome.SetHelp(resizable, "Layer shortcuts", "Ctrl+D · Duplicate\nDelete · Delete selection");
            CheckHeaderHelp("shared", resizable, "Ctrl+D · Duplicate\nDelete · Delete selection");
            CheckFloatingPanel("shared-resizable", resizable);
            resizable.Close();

            assert("native layout fixture never enters editing or exposes an input window", !host.IsEditing &&
                host.Diagnostics.All(pair => !pair.InputVisible));
            assert("native layout fixture leaves the toolbar hidden", !toolbar.IsVisible);

            void CheckFirstShow(string name, Wpf.Window window, double expectedWidth)
            {
                PumpToIdle(app.Dispatcher);
                nint handle = new Interop.WindowInteropHelper(window).Handle;
                handles.Add(handle);
                if (!GetWindowRect(handle, out var rectangle))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                uint dpi = GetDpiForWindow(handle);
                int cloak = 0;
                int cloakResult = DwmGetWindowAttribute(handle, 14, out cloak, sizeof(int));
                var order = OwnWindowOrder();
                int position = order.IndexOf(handle);
                bool aboveDisplay = host.Diagnostics.All(pair => position >= 0 && position < order.IndexOf(pair.DisplayHandle));
                double expectedPixels = expectedWidth * dpi / 96d;
                var snapshot = new LayoutSnapshot(name, window.Width, window.ActualWidth, window.ActualHeight,
                    rectangle.Left, rectangle.Top, rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top,
                    dpi, handle.ToString("X"), GetWindow(handle, 4).ToString("X"), position, cloakResult, cloak);
                results.Add(snapshot);
                Console.WriteLine(JsonSerializer.Serialize(snapshot));

                assert(name + " first Show preserves its declared width instead of the provisional one-pixel HWND",
                    Math.Abs(window.Width - expectedWidth) < .01 && Math.Abs(window.ActualWidth - expectedWidth) <= 1 &&
                    Math.Abs(snapshot.PixelWidth - expectedPixels) <= 3 && snapshot.PixelHeight > 100);
                assert(name + " first Show has visible uncloaked native content", window.IsVisible && IsWindowVisible(handle) &&
                    cloakResult == 0 && cloak == 0 && window.ActualHeight > AppWindowChrome.CaptionHeight);
                assert(name + " first Show remains topmost above the passive canvas", window.Topmost &&
                    (GetWindowLongPtr(handle, -20).ToInt64() & 8) != 0 && aboveDisplay);
                assert(name + " first Show has a real owner and the shared application title bar", window.Owner is not null &&
                    GetWindow(handle, 4) == new Interop.WindowInteropHelper(window.Owner).Handle &&
                    Wpf.Shell.WindowChrome.GetWindowChrome(window)?.CaptionHeight == AppWindowChrome.CaptionHeight);
            }

            void CheckFloatingPanel(string name, Wpf.Window window)
            {
                double originalWidth = window.ActualWidth, originalHeight = window.ActualHeight;
                nint handle = new Interop.WindowInteropHelper(window).Handle;
                int bindings = window.CommandBindings.Count;
                AppWindowChrome.Apply(window);
                assert(name + " reapplying chrome to a native window does not duplicate its commands", window.CommandBindings.Count == bindings);
                long style = GetWindowLongPtr(handle, -16).ToInt64();
                assert(name + " native panel preserves resize without minimize or maximize styles",
                    (style & 0x00040000) != 0 && (style & 0x00030000) == 0);
                CheckNativeRounding(name + " normal", handle);
                foreach (string trigger in new[] { "title-double-click", "system-maximize", "system-minimize" })
                {
                    var normalTitle = (Wpf.FrameworkElement)window.Template.FindName("TitleBar", window);
                    var captionPoint = normalTitle.PointToScreen(new Wpf.Point(50, 18));
                    if (trigger == "title-double-click") SendMessage(handle, 0x00A3, 2, PackPoint(captionPoint));
                    else SendMessage(handle, 0x0112, trigger == "system-maximize" ? 0xF030 : 0xF020, 0);
                    PumpToIdle(app.Dispatcher);
                    assert(name + " " + trigger + " leaves the floating panel visible at its original size",
                        window.WindowState == Wpf.WindowState.Normal && window.IsVisible && IsWindowVisible(handle) &&
                        Math.Abs(window.ActualWidth - originalWidth) <= 1 && Math.Abs(window.ActualHeight - originalHeight) <= 1);
                }
                foreach (var state in new[] { Wpf.WindowState.Minimized, Wpf.WindowState.Maximized })
                {
                    window.WindowState = state;
                    PumpToIdle(app.Dispatcher);
                    assert(name + " repairs an unsupported programmatic " + state + " state without a cropped icon window",
                        window.WindowState == Wpf.WindowState.Normal && window.IsVisible &&
                        Math.Abs(window.ActualWidth - originalWidth) <= 1 && Math.Abs(window.ActualHeight - originalHeight) <= 1);
                }

                var title = (Wpf.FrameworkElement)window.Template.FindName("TitleBar", window);
                var caption = title.PointToScreen(new Wpf.Point(50, 18));
                assert(name + " retains native caption dragging", SendMessage(handle, 0x0084, 0, PackPoint(caption)) == 2);
                window.Width = originalWidth + 24;
                window.Height = originalHeight + 20;
                PumpToIdle(app.Dispatcher);
                GetWindowRect(handle, out var resized);
                var corner = new Wpf.Point(resized.Right - 2, resized.Bottom - 2);
                assert(name + " retains free sizing and the native bottom-right resize target",
                    Math.Abs(window.ActualWidth - (originalWidth + 24)) <= 1 && Math.Abs(window.ActualHeight - (originalHeight + 20)) <= 1 &&
                    SendMessage(handle, 0x0084, 0, PackPoint(corner)) == 17);
                window.Width = originalWidth;
                window.Height = originalHeight;

                // Simulate a saved position made unreachable by a display-layout change. This
                // changes only this fixture's HWND, without moving or clicking the desktop cursor.
                window.Hide();
                window.Left = -10000;
                window.Top = -10000;
                window.Show();
                PumpToIdle(app.Dispatcher);
                CheckReachableCaption(name, window, handle);
                CheckNativeRounding(name + " restored floating panel", handle);
                var closeCommand = (System.Windows.Controls.Button)window.Template.FindName("Close", window);
                ((System.Windows.Input.RoutedCommand)closeCommand.Command).Execute(null, window);
                PumpToIdle(app.Dispatcher);
                assert(name + " close caption dismisses the window instead of trapping the user", !window.IsVisible);
            }

            void CheckHeaderHelp(string name, Wpf.Window window, string helpText)
            {
                var help = (System.Windows.Controls.Button)window.Template.FindName("Help", window);
                var popup = (System.Windows.Controls.Primitives.Popup)window.Template.FindName("HelpPopup", window);
                help.RaiseEvent(new Wpf.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, help));
                PumpToIdle(app.Dispatcher);
                Console.WriteLine(JsonSerializer.Serialize(new { Name = name + "-header-help", popup.IsOpen,
                    Text = LogicalDescendants(popup.Child).OfType<System.Windows.Controls.TextBlock>().Select(text => text.Text).ToArray() }));
                assert(name + " header help click opens the supplied shortcut explanation", popup.IsOpen &&
                    LogicalDescendants(popup.Child).OfType<System.Windows.Controls.TextBlock>().Any(text => text.Text == helpText));
                var child = (Wpf.FrameworkElement)popup.Child;
                var top = child.PointToScreen(new Wpf.Point(0, 0));
                var bottom = child.PointToScreen(new Wpf.Point(child.ActualWidth, child.ActualHeight));
                nint handle = new Interop.WindowInteropHelper(window).Handle;
                var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                assert(name + " header help stays fully within the current monitor work area", top.X >= monitor.Work.Left &&
                    top.Y >= monitor.Work.Top && bottom.X <= monitor.Work.Right && bottom.Y <= monitor.Work.Bottom);
                var escape = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                    Wpf.PresentationSource.FromVisual(window), Environment.TickCount, System.Windows.Input.Key.Escape)
                    { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                window.RaiseEvent(escape);
                assert(name + " Escape closes header help without dismissing the panel", escape.Handled && !popup.IsOpen && window.IsVisible);
                help.RaiseEvent(new Wpf.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, help));
                window.Hide();
                assert(name + " hiding a panel also dismisses its detached help popup", !popup.IsOpen);
                window.Show();
                PumpToIdle(app.Dispatcher);
            }

            void CheckReachableCaption(string name, Wpf.Window window, nint handle)
            {
                var title = (Wpf.FrameworkElement)window.Template.FindName("TitleBar", window);
                var close = (Wpf.FrameworkElement)window.Template.FindName("Close", window);
                var titleTop = title.PointToScreen(new Wpf.Point(0, 0));
                var closeTop = close.PointToScreen(new Wpf.Point(0, 0));
                var closeBottom = close.PointToScreen(new Wpf.Point(close.ActualWidth, close.ActualHeight));
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref info))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                GetWindowRect(handle, out var rectangle);
                Console.WriteLine(JsonSerializer.Serialize(new { Name = name + "-reachable", State = window.WindowState.ToString(),
                    Window = new { rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom },
                    TitleTop = titleTop, CloseTop = closeTop, CloseBottom = closeBottom,
                    Work = new { info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom } }));
                assert(name + " reopening an off-screen panel recovers its close button inside the monitor work area",
                    closeTop.X >= info.Work.Left && closeTop.Y >= info.Work.Top && closeBottom.X <= info.Work.Right &&
                    closeBottom.Y <= info.Work.Bottom && close.ActualHeight > 0);
                assert(name + " reopening preserves the compact visible title above the content", titleTop.Y >= info.Work.Top &&
                    title.ActualHeight >= AppWindowChrome.CaptionHeight - 1);
                var closeCenter = new Wpf.Point((closeTop.X + closeBottom.X) / 2, (closeTop.Y + closeBottom.Y) / 2);
                var hit = window.InputHitTest(window.PointFromScreen(closeCenter)) as Wpf.DependencyObject;
                while (hit is not null && !ReferenceEquals(hit, close)) hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
                assert(name + " close button remains logically hittable after position recovery", ReferenceEquals(hit, close) && close.IsEnabled);
            }

            void CheckNativeRounding(string name, nint handle)
            {
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
                nint region = CreateRectRgn(0, 0, 0, 0);
                int regionKind;
                try { regionKind = GetWindowRgn(handle, region); }
                finally { DeleteObject(region); }
                int result = DwmGetWindowAttribute(handle, 33, out int preference, sizeof(int));
                Console.WriteLine(JsonSerializer.Serialize(new { Name = name, NativeRoundingPreference = preference,
                    DwmHresult = result, WindowRegion = regionKind }));
                assert(name + " permits DWM rounding without a WPF window region", result == 0 && preference == 2 && regionKind == 0);
            }

        }
        finally
        {
            foreach (var window in shown) window.Close();
            session?.Dispose();
            app.Shutdown();
            if (!app.Dispatcher.HasShutdownFinished) PumpToIdle(app.Dispatcher);
            File.WriteAllText(Path.Combine(profile, "window-layout-result.json"), JsonSerializer.Serialize(new
            {
                Profile = profile, Windows = results,
                RemainingFixtureWindows = handles.Concat(hostHandles).Where(IsWindow).Select(handle => handle.ToString("X")).ToArray()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        assert("native layout fixture disposes every window it created", handles.Concat(hostHandles).All(handle => !IsWindow(handle)));
        Console.WriteLine("Native layout evidence: " + Path.Combine(profile, "window-layout-result.json"));
    }

    private static T Field<T>(DesktopSession session, string name) =>
        (T)typeof(DesktopSession).GetField(name, PrivateInstance)!.GetValue(session)!;

    private static IEnumerable<Wpf.DependencyObject> LogicalDescendants(Wpf.DependencyObject root)
    {
        yield return root;
        foreach (var child in Wpf.LogicalTreeHelper.GetChildren(root).OfType<Wpf.DependencyObject>())
            foreach (var nested in LogicalDescendants(child)) yield return nested;
    }

    private static void Invoke(DesktopSession session, string name, params object[] arguments)
    {
        try { typeof(DesktopSession).GetMethod(name, PrivateInstance)!.Invoke(session, arguments); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); }
    }

    private static void PumpToIdle(Threading.Dispatcher dispatcher)
    {
        if (dispatcher.HasShutdownFinished) return;
        var frame = new Threading.DispatcherFrame();
        dispatcher.BeginInvoke(Threading.DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Threading.Dispatcher.PushFrame(frame);
    }

    private static List<nint> OwnWindowOrder()
    {
        var result = new List<nint>();
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out uint process);
            if (process == Environment.ProcessId) result.Add(handle);
            return true;
        }, 0);
        return result;
    }

    private static nint PackPoint(Wpf.Point point) => (nint)((uint)(ushort)(short)point.X | ((uint)(ushort)(short)point.Y << 16));

    private sealed record LayoutSnapshot(string Name, double Width, double ActualWidth, double ActualHeight,
        int Left, int Top, int PixelWidth, int PixelHeight, uint Dpi, string Hwnd, string Owner, int ZOrder,
        int CloakHresult, int Cloaked);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRectangle Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint process);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint handle, out NativeRectangle rectangle);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint handle);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint handle, uint attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint handle, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)] private static extern bool GetMonitorInfo(nint handle, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint handle, nint region);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint handle, uint message, nint wParam, nint lParam);
}
