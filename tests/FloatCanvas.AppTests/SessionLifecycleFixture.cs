using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

/// <summary>
/// Opt-in startup/session geometry checks. Uses a new temporary profile, its own bounded host,
/// no registered hotkeys and no injected desktop input. Editing briefly activates only that tile;
/// every path releases it and restores the original foreground in finally.
/// </summary>
internal static class SessionLifecycleFixture
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        if (Application.Current is not null) throw new InvalidOperationException("Run --session-lifecycle-only in its own process.");
        string profile = Path.Combine(Path.GetTempPath(), "FloatCanvasSessionLifecycle", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile); ApplicationPaths.Configure(["--profile", profile, "--primary-screen"]);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        nint originalForeground = GetForegroundWindow();
        var handles = new List<nint>();
        DesktopSession? session = null; OverlayHost? host = null;
        try
        {
            NewSession();
            var toolbar = Get<Window>(session!, "_toolbar");
            Invoke("ShowWelcome"); Pump();
            var welcome = Get<Window>(session!, "_welcomeWindow");
            handles.Add(new WindowInteropHelper(welcome).Handle);
            check("one real startup window opens before editing", welcome.IsVisible && !host!.IsEditing && welcome.Content is WelcomeView);
            welcome.Close(); Pump(); Invoke("ShowWelcome"); Pump();
            check("closing the welcome window never recreates it during the session", NullableField(session!, "_welcomeWindow") is null &&
                !app.Windows.Cast<Window>().Any(window => window.Title == L.Get("Welcome.Title")));

            toolbar.Show(); Pump(); Invoke("PlaceToolbar"); Pump();
            var point = toolbar.PointToScreen(new Point()); var monitor = host!.PrimaryMonitor;
            double scale = monitor.DpiX / 96d;
            check("real toolbar starts at screen top and centers its complete surface", Math.Abs(point.Y - monitor.Bounds.Top) <= 1 &&
                Math.Abs(point.X + toolbar.ActualWidth * scale / 2 - (monitor.Bounds.Left + monitor.Bounds.Width / 2d)) <= 1);
            toolbar.Left += 25; toolbar.Top += 8; Pump(); Invoke("SaveToolbarPosition");
            var saved = Get<UserSettings>(session!, "_settings");
            check("toolbar movement updates only this session's position", saved.ToolbarX is not null && saved.ToolbarY is not null && !File.Exists(Path.Combine(profile, "settings.json")));
            toolbar.Hide();

            var editor = Get<EditorController>(session!, "_editor");
            var first = new InspectorWindow(() => editor.Session) { ShowActivated = false };
            Invoke("ConfigureAppWindow", first, false); first.Show(); Pump(); handles.Add(new WindowInteropHelper(first).Handle);
            first.Left += 27; first.Top += 19; first.Width += 44; first.Height += 30; Pump();
            var bounds = new Rect(first.Left, first.Top, first.ActualWidth, first.ActualHeight);
            first.Close(); Pump();
            var reopened = new InspectorWindow(() => editor.Session) { ShowActivated = false };
            Invoke("ConfigureAppWindow", reopened, false); reopened.Show(); Pump(); handles.Add(new WindowInteropHelper(reopened).Handle);
            check("closing and recreating a real panel keeps its position and size within the session", Math.Abs(reopened.Left - bounds.Left) < 1 &&
                Math.Abs(reopened.Top - bounds.Top) < 1 && Math.Abs(reopened.ActualWidth - bounds.Width) < 1 && Math.Abs(reopened.ActualHeight - bounds.Height) < 1);
            reopened.Close(); Pump();
            var freshLayout = new SessionWindowLayout(); var freshPanel = new InspectorWindow(() => editor.Session);
            freshLayout.Track(freshPanel);
            check("a new session layout restores the panel's product defaults", freshPanel.Width == 340 && freshPanel.Height == 700 && double.IsNaN(freshPanel.Left));
            freshPanel.Close();
            session!.Dispose(); session = null; host = null; Pump();

            NewSession(); Invoke("ShowWelcome"); Pump();
            welcome = Get<Window>(session!, "_welcomeWindow"); handles.Add(new WindowInteropHelper(welcome).Handle);
            check("a new DesktopSession offers the welcome window again with fresh geometry", welcome.IsVisible &&
                Get<UserSettings>(session!, "_settings").ToolbarX is null && Get<UserSettings>(session!, "_settings").ToolbarY is null);
            ((WelcomeView)welcome.Content).StartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            check("the real start button closes the welcome window through SetEditing", NullableField(session!, "_welcomeWindow") is null && !welcome.IsVisible);
            check("start button enters editing through the fixture's bounded input surface", host!.IsEditing && host.Diagnostics.All(pair => pair.Bounds.Height <= 180));
            Invoke("SetEditing", false, false); Pump();
            check("start-button edit session releases every input window", !host.IsEditing && host.Diagnostics.All(pair => !pair.InputVisible));
            Invoke("ShowWelcome"); check("returning to normal operation does not reopen the welcome window", NullableField(session!, "_welcomeWindow") is null);
            session!.Dispose(); session = null; host = null; Pump();

            NewSession(); Invoke("ShowWelcome"); Pump(); welcome = Get<Window>(session!, "_welcomeWindow");
            handles.Add(new WindowInteropHelper(welcome).Handle);
            var toggle = (Action)typeof(OverlayHost).GetField("ToggleRequested", PrivateInstance)!.GetValue(host!)!;
            toggle(); Pump();
            check("the host shortcut route closes welcome and enters the same editing path", NullableField(session!, "_welcomeWindow") is null && !welcome.IsVisible && host!.IsEditing);
            Invoke("SetEditing", false, false); Pump();
            check("shortcut edit session releases every input window", !host!.IsEditing && host.Diagnostics.All(pair => !pair.InputVisible));
        }
        finally
        {
            if (host is not null) host.ExitEditing();
            session?.Dispose();
            foreach (var window in app.Windows.Cast<Window>().ToArray()) window.Close();
            Pump();
            if (originalForeground != 0 && IsWindow(originalForeground)) SetForegroundWindow(originalForeground);
            Directory.Delete(profile, recursive: true);
        }
        check("lifecycle fixture disposes every own native window", handles.All(handle => !IsWindow(handle)));
        check("lifecycle fixture restores the original foreground window", originalForeground == 0 || !IsWindow(originalForeground) || GetForegroundWindow() == originalForeground);

        void NewSession()
        {
            var primary = ((IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!).First(m => m.IsPrimary);
            var tile = new PixelBounds(primary.WorkArea.Left + 16, primary.WorkArea.Top + 16, Math.Min(2200, primary.WorkArea.Width - 32), 160);
            var bounded = primary with { Bounds = tile, WorkArea = tile };
            Func<IReadOnlyList<MonitorInfo>> provider = () => [bounded];
            host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), PrivateInstance, null, [OverlayMonitorScope.PrimaryScreen, provider, false], null)!;
            handles.AddRange(host.Diagnostics.SelectMany(pair => new[] { pair.DisplayHandle, pair.InputHandle }).Append(host.ControlHandle));
            session = new DesktopSession(app, host);
            // Window lifecycle does not exercise keyboard interception. Dispose only this fixture's
            // idle keyboard helper so no global keyboard hook remains during the bounded activation.
            var isolation = typeof(OverlayHost).GetField("_keyboardIsolation", PrivateInstance)!;
            (isolation.GetValue(host) as IDisposable)?.Dispose(); isolation.SetValue(host, null);
            var inherited = app.TryFindResource(typeof(Window)) as Style;
            var noActivate = inherited is null ? new Style(typeof(Window)) : new Style(typeof(Window), inherited);
            noActivate.Setters.Add(new Setter(Window.ShowActivatedProperty, false)); app.Resources[typeof(Window)] = noActivate;
            handles.Add(new WindowInteropHelper(Get<Window>(session, "_toolbar")).EnsureHandle());
        }
        object? Invoke(string name, params object[] args) => typeof(DesktopSession).GetMethod(name, PrivateInstance)!.Invoke(session, args);
    }
    private static object? NullableField(object target, string name) => target.GetType().GetField(name, PrivateInstance)!.GetValue(target);
    private static T Get<T>(object target, string name) => (T)NullableField(target, name)!;
    private static void Pump()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame);
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint handle);
}
