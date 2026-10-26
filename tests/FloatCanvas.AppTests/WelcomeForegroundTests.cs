using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

internal static class WelcomeForegroundTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, bool enhanced)
    {
        string run = Path.GetFullPath(Path.Combine(".artifacts", "welcome-focus", (enhanced ? "enhanced-" : "desktop-") + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(run); ApplicationPaths.Configure(["--profile", Path.Combine(run, "profile")]);
        new UserSettings { Language = "zh-CN", ShowFloatingLogo = false, Toggle = new(7, 133), Hide = new(7, 132) }.Save();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        DesktopSession? session = null; OverlayHost? host = null; Process? peer = null;
        var observations = new List<object>();
        try
        {
            var primary = ((IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!).First(m => m.IsPrimary);
            Func<IReadOnlyList<MonitorInfo>> provider = () => [primary with { Bounds = new PixelBounds(primary.WorkArea.Left + 30, primary.WorkArea.Top + 30, 640, 160) }];
            host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null, [OverlayMonitorScope.PrimaryScreen, provider, false], null)!;
            if (enhanced) host.EnableSystemInput();
            session = new DesktopSession(app, host);
            Field<System.Windows.Forms.NotifyIcon>(session, "_tray").Visible = false;
            var isolation = typeof(OverlayHost).GetField("_keyboardIsolation", Private)!;
            (isolation.GetValue(host) as IDisposable)?.Dispose(); isolation.SetValue(host, null);
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--focus-external-helper"); start.ArgumentList.Add("--focus-helper-directory"); start.ArgumentList.Add(run); start.ArgumentList.Add("--focus-helper-ordinary");
            peer = Process.Start(start)!;
            Wait(() => File.Exists(Path.Combine(run, "external-ready.json")), 5000);
            using var ready = JsonDocument.Parse(File.ReadAllText(Path.Combine(run, "external-ready.json")));
            nint softwareA = (nint)ready.RootElement.GetProperty("Handle").GetInt64();
            var toolbar = Field<Window>(session, "_toolbar"); toolbar.Show(); Pump();
            ActivateSoftwareA();
            check("software A owns foreground before editing", GetForegroundWindow() == softwareA); EnterEditing();
            Wait(() => host.IsEditing && host.HasEditingInput, 3000);
            Invoke("OpenWelcome", false);
            check("automatic welcome cannot interrupt an active editing session", host.IsEditing && Field<Window?>(session, "_welcomeWindow") is null);
            var menu = Field<OverlayContextMenu>(session, "_toolbarMenu"); menu.IsOpen = true; Pump();
            var welcomeAction = Preview4MenuTests.Items(menu).Single(item => Equals(item.Header, L.Get("Welcome.Open")));
            observations.Add(new { Stage = "BeforeWelcome", Foreground = (long)GetForegroundWindow(), SoftwareA = (long)softwareA, host.IsEditing, host.UsesSystemInput });
            welcomeAction.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var welcome = Field<Window>(session, "_welcomeWindow"); nint handle = new WindowInteropHelper(welcome).Handle;
            observations.Add(new { Stage = "AfterWelcomeCall", Foreground = (long)GetForegroundWindow(), Welcome = (long)handle, welcome.IsActive, host.IsEditing });
            Pump(250);
            observations.Add(new { Stage = "AfterQueuedFocusWork", Foreground = (long)GetForegroundWindow(), Welcome = (long)handle, welcome.IsActive, host.IsEditing });
            check("opening welcome exits editing and keeps an ordinary independent window", !host.IsEditing && welcome.IsVisible && !welcome.Topmost && welcome.Owner is null);
            check("welcome opened from the editing menu owns foreground after focus handoff", GetForegroundWindow() == handle && welcome.IsActive);
            var userDestination = new Window { Title = "Own fixture: later user destination", Width = 240, Height = 100, Content = new TextBlock { Text = "User chose another window" } };
            userDestination.Show(); userDestination.Activate(); Pump(50);
            nint destination = new WindowInteropHelper(userDestination).Handle;
            check("a stale welcome activation cannot take focus from a later user destination", GetForegroundWindow() == destination &&
                !SessionWindowCoordinator.ActivateRegularWindowForUserAction(welcome, softwareA) && GetForegroundWindow() == destination);
            userDestination.Close();
            check("normal edit exit still returns to software A", RestoreNormally());

            bool RestoreNormally()
            {
                welcome.Close(); toolbar.Show(); Pump();
                ActivateSoftwareA(); EnterEditing();
                Wait(() => host.IsEditing && host.HasEditingInput, 3000);
                Invoke("SetEditing", false, false); Pump(100);
                return !host.IsEditing && GetForegroundWindow() == softwareA;
            }
            object? Invoke(string method, params object[] arguments) => typeof(DesktopSession).GetMethod(method, Private)!.Invoke(session, arguments);
            void EnterEditing()
            {
                uint current = GetCurrentThreadId(), foreground = GetWindowThreadProcessId(GetForegroundWindow(), out _);
                bool attached = !enhanced && foreground != current && AttachThreadInput(current, foreground, true);
                try { Invoke("SetEditing", true, false); }
                finally { if (attached) AttachThreadInput(current, foreground, false); }
            }
            void ActivateSoftwareA()
            {
                uint current = GetCurrentThreadId(), foreground = GetWindowThreadProcessId(GetForegroundWindow(), out _), target = GetWindowThreadProcessId(softwareA, out _);
                bool attachedForeground = foreground != current && AttachThreadInput(current, foreground, true);
                bool attachedTarget = target != current && target != foreground && AttachThreadInput(current, target, true);
                try { if (!SetForegroundWindow(softwareA)) throw new InvalidOperationException("Could not activate the owned software-A fixture."); SetFocus(softwareA); }
                finally { if (attachedTarget) AttachThreadInput(current, target, false); if (attachedForeground) AttachThreadInput(current, foreground, false); }
                Pump(50);
            }
        }
        finally
        {
            File.WriteAllText(Path.Combine(run, "observations.json"), JsonSerializer.Serialize(observations, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("WELCOME_FOCUS_REPORT " + Path.Combine(run, "observations.json"));
            session?.Dispose(); if (session is null) host?.Dispose();
            File.WriteAllText(Path.Combine(run, "external-exit"), "exit");
            if (peer is not null) { peer.WaitForExit(3000); peer.Dispose(); }
            app.Shutdown();
        }
    }

    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static void Wait(Func<bool> complete, int timeout)
    {
        var clock = Stopwatch.StartNew();
        while (!complete() && clock.ElapsedMilliseconds < timeout) Pump(15);
        if (!complete()) throw new TimeoutException("Welcome foreground fixture timed out.");
    }
    private static void Pump(int milliseconds = 0)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(Math.Max(1, milliseconds)) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint first, uint second, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
