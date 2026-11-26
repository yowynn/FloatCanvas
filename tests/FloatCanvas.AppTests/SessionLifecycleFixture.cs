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
            // The first section exercises a resizable 450-DIP panel. Give it a real
            // work area large enough to hold that panel; later input-surface checks use
            // the separate narrow bounded host below.
            NewSession(1400);
            var toolbar = Get<Window>(session!, "_toolbar");
            Invoke("ShowWelcome"); Pump();
            var welcome = Get<Window>(session!, "_welcomeWindow");
            handles.Add(new WindowInteropHelper(welcome).Handle);
            check("one real startup window opens before editing", welcome.IsVisible && !host!.IsEditing && welcome.Content is WelcomeView);
            welcome.Close(); Pump(); Invoke("ShowWelcome"); Pump();
            check("closing the welcome window never recreates it during the session", NullableField(session!, "_welcomeWindow") is null &&
                !app.Windows.Cast<Window>().Any(window => window.Title == WindowTitles.Format(L.Get("Welcome.Title"), true)));

            // Production positions the hidden native toolbar before revealing it. Showing
            // first lets WPF publish a temporary default origin that would be mistaken for
            // a user-saved position by LocationChanged.
            var beforePlacement = Get<UserSettings>(session!, "_settings");
            Invoke("PlaceToolbar"); toolbar.Show(); Pump();
            var point = toolbar.PointToScreen(new Point()); var monitor = host!.PrimaryMonitor;
            double scale = monitor.DpiX / 96d;
            var toolbarSurface = (FrameworkElement)toolbar.Content;
            var toolbarSettings = Get<UserSettings>(session!, "_settings");
            check($"real toolbar starts at screen top and centers its complete surface " +
                $"(point={point.X:0.#},{point.Y:0.#} size={toolbar.ActualWidth * scale:0.#}x{toolbar.ActualHeight * scale:0.#} " +
                $"desired={toolbarSurface.DesiredSize.Width * scale:0.#}x{toolbarSurface.DesiredSize.Height * scale:0.#} " +
                $"before={beforePlacement.ToolbarX:0.#},{beforePlacement.ToolbarY:0.#} " +
                $"saved={toolbarSettings.ToolbarX:0.#},{toolbarSettings.ToolbarY:0.#} " +
                $"monitor={monitor.Bounds.Left:0.#},{monitor.Bounds.Top:0.#},{monitor.Bounds.Width:0.#}x{monitor.Bounds.Height:0.#})",
                Math.Abs(point.Y - monitor.Bounds.Top) <= 1 &&
                Math.Abs(point.X + toolbar.ActualWidth * scale / 2 - (monitor.Bounds.Left + monitor.Bounds.Width / 2d)) <= 1);
            toolbar.Left += 25; toolbar.Top += 8; Pump(); Invoke("SaveToolbarPosition");
            var saved = Get<UserSettings>(session!, "_settings");
            check("toolbar movement updates only this session's position", saved.ToolbarX is not null && saved.ToolbarY is not null && !File.Exists(Path.Combine(profile, "settings.json")));
            toolbar.Hide();

            var editor = Get<EditorController>(session!, "_editor");
            var first = new InspectorWindow(() => editor.Session) { ShowActivated = false };
            Invoke("ConfigureAppWindow", first, false, false); first.Show(); Pump(); handles.Add(new WindowInteropHelper(first).Handle);
            first.Left += 27; first.Top = 40; first.Width += 44; first.Height += 30; Pump();
            var bounds = new Rect(first.Left, first.Top, first.ActualWidth, first.ActualHeight);
            SessionWindowLayout.TryOrigin(first, out var firstOrigin);
            first.Close(); Pump();
            var geometry = Get<Dictionary<string, Rect>>(Get<SessionWindowLayout>(session!, "_windowLayout"), "bounds");
            geometry.TryGetValue(first.GetType().FullName + ":" + first.Title, out var retained);
            var reopened = new InspectorWindow(() => editor.Session) { ShowActivated = false };
            Invoke("ConfigureAppWindow", reopened, false, false); reopened.Show(); Pump(); handles.Add(new WindowInteropHelper(reopened).Handle);
            SessionWindowLayout.TryOrigin(reopened, out var reopenedOrigin);
            check($"closing and recreating a real panel keeps its position and size within the session " +
                $"(before={bounds.Left:0.#},{bounds.Top:0.#},{bounds.Width:0.#}x{bounds.Height:0.#} " +
                $"retained={retained.Left:0.#},{retained.Top:0.#},{retained.Width:0.#}x{retained.Height:0.#} " +
                $"after={reopened.Left:0.#},{reopened.Top:0.#},{reopened.ActualWidth:0.#}x{reopened.ActualHeight:0.#} " +
                $"property={reopened.Width:0.#}x{reopened.Height:0.#} native={firstOrigin.X:0.#},{firstOrigin.Y:0.#}->{reopenedOrigin.X:0.#},{reopenedOrigin.Y:0.#})",
                Math.Abs(reopened.Left - bounds.Left) < 1 &&
                Math.Abs(reopened.Top - bounds.Top) < 1 && Math.Abs(reopened.ActualWidth - bounds.Width) < 1 && Math.Abs(reopened.ActualHeight - bounds.Height) < 1);
            reopened.Close(); Pump();
            var freshLayout = new SessionWindowLayout(); var freshPanel = new InspectorWindow(() => editor.Session);
            freshLayout.Track(freshPanel);
            check($"a new session layout restores the panel's product defaults " +
                $"(size={freshPanel.Width:0.#}x{freshPanel.Height:0.#} left={freshPanel.Left:0.#})",
                freshPanel.Width == PropertyPanelLayout.WindowWidth && freshPanel.Height == 700 && double.IsNaN(freshPanel.Left));
            freshPanel.Close();
            session!.Dispose(); session = null; host = null; Pump();

            NewSession(); Invoke("ShowWelcome"); Pump();
            welcome = Get<Window>(session!, "_welcomeWindow"); handles.Add(new WindowInteropHelper(welcome).Handle);
            check("a new DesktopSession offers the welcome window again with fresh geometry", welcome.IsVisible &&
                Get<UserSettings>(session!, "_settings").ToolbarX is null && Get<UserSettings>(session!, "_settings").ToolbarY is null);
            WithCursorAt(host!.PrimaryMonitor.Bounds, () =>
            {
                WithForeground(welcome, () => ((WelcomeView)welcome.Content).StartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
                Pump(300);
            });
            check("the real start button closes the welcome window through SetEditing", NullableField(session!, "_welcomeWindow") is null && !welcome.IsVisible);
            check($"start button enters editing through the fixture's bounded input surface " +
                $"(editing={host!.IsEditing} heights={string.Join(',', host.Diagnostics.Select(pair => pair.Bounds.Height))} " +
                $"status={Get<TextBlock>(session!, "_status").Text})",
                host.IsEditing && host.Diagnostics.All(pair => pair.Bounds.Height <= 180));
            Invoke("SetEditing", false, false); Pump();
            check("start-button edit session releases every input window", !host.IsEditing && host.Diagnostics.All(pair => !pair.InputVisible));
            Invoke("ShowWelcome"); check("returning to normal operation does not reopen the welcome window", NullableField(session!, "_welcomeWindow") is null);
            session!.Dispose(); session = null; host = null; Pump();

            NewSession(); Invoke("ShowWelcome"); Pump(); welcome = Get<Window>(session!, "_welcomeWindow");
            handles.Add(new WindowInteropHelper(welcome).Handle);
            var toggle = (Action)typeof(OverlayHost).GetField("ToggleRequested", PrivateInstance)!.GetValue(host!)!;
            WithCursorAt(host!.PrimaryMonitor.Bounds, () => { WithForeground(welcome, toggle); Pump(300); });
            check("the host shortcut route closes welcome and enters the same editing path", NullableField(session!, "_welcomeWindow") is null && !welcome.IsVisible && host!.IsEditing);
            Invoke("SetEditing", false, false); Pump();
            check("shortcut edit session releases every input window", !host!.IsEditing && host.Diagnostics.All(pair => !pair.InputVisible));
            Invoke("OpenWelcome",true);Pump();welcome=Get<Window>(session!,"_welcomeWindow");
            handles.Add(new WindowInteropHelper(welcome).Handle);
            WithCursorAt(host!.PrimaryMonitor.Bounds, () => { WithForeground(welcome, toggle); Pump(300); });
            check("entering drawing mode closes the manually reopened welcome",
                !welcome.IsVisible && NullableField(session!, "_welcomeWindow") is null && host.IsEditing);
            Invoke("SetEditing",false,false);Pump();
            session!.Dispose();session=null;host=null;Pump();
            NewSession();Invoke("ShowWelcome");Pump();welcome=Get<Window>(session!,"_welcomeWindow");
            handles.Add(new WindowInteropHelper(welcome).Handle);
            Invoke("OpenWelcome",true);Pump();
            check("manual open promotes the existing startup welcome without duplicating it",ReferenceEquals(welcome,Get<Window>(session!,"_welcomeWindow"))&&Get<bool>(session!,"_welcomeManual"));
            WithCursorAt(host!.PrimaryMonitor.Bounds, () => { WithForeground(welcome, () => Invoke("SetEditing", true, false)); Pump(300); });
            check("entering drawing mode closes the promoted startup welcome",
                !welcome.IsVisible && NullableField(session!, "_welcomeWindow") is null && host.IsEditing);
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

        void NewSession(int tileHeight = 160)
        {
            var primary = ((IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!).First(m => m.IsPrimary);
            var tile = new PixelBounds(primary.WorkArea.Left + 16, primary.WorkArea.Top + 16,
                Math.Min(2200, primary.WorkArea.Width - 32), Math.Min(tileHeight, primary.WorkArea.Height - 32));
            var bounded = primary with { Bounds = tile, WorkArea = tile };
            Func<IReadOnlyList<MonitorInfo>> provider = () => [bounded];
            host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), PrivateInstance, null, [OverlayMonitorScope.PrimaryScreen, provider, false], null)!;
            handles.AddRange(host.Diagnostics.SelectMany(pair => new[] { pair.DisplayHandle, pair.InputHandle }).Append(host.ControlHandle));
            session = new DesktopSession(app, host);
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
    private static void Pump(int milliseconds) { System.Threading.Thread.Sleep(milliseconds); Pump(); }
    private static void WithForeground(Window window,Action action)
    {
        uint current=GetCurrentThreadId(),foreground=GetWindowThreadProcessId(GetForegroundWindow(),out _);
        bool attached=current!=foreground&&AttachThreadInput(current,foreground,true);
        try {SetForegroundWindow(new WindowInteropHelper(window).Handle);action();}
        finally {if(attached)AttachThreadInput(current,foreground,false);}
    }
    private static void WithCursorAt(PixelBounds bounds, Action action)
    {
        if (!GetCursorPos(out var original)) { action(); return; }
        try
        {
            SetCursorPos(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            action();
        }
        finally { SetCursorPos(original.X, original.Y); }
    }
    [StructLayout(LayoutKind.Sequential)]private struct CursorPoint { public int X, Y; }
    [DllImport("user32.dll")]private static extern bool AttachThreadInput(uint from,uint to,bool attach);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint handle,out uint process);
    [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
}
