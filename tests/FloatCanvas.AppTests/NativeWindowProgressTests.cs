using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Windows;

/// <summary>Hidden HWND message replay and the real pixel-render queue. Never activates a window or installs an active input lease.</summary>
internal static class NativeWindowProgressTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        nint foreground = GetForegroundWindow();
        var bounds = new PixelBounds(-20000, -20000, 64, 64);
        Func<IReadOnlyList<MonitorInfo>> monitors = () => [new("hidden-progress", bounds, bounds, 96, 96, true)];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null,
            [OverlayMonitorScope.PrimaryScreen, monitors, false], null)!;
        var results = new List<(string Name, bool Passed)>();
        using (var session = new DesktopSession(app, host))
        {
            // Disable the inactive production hook before exercising hidden messages. The
            // replacement below constructs the real service/state without starting its thread.
            var isolationField = typeof(OverlayHost).GetField("_keyboardIsolation", Private)!;
            ((IDisposable)isolationField.GetValue(host)!).Dispose(); isolationField.SetValue(host, null);
            Field<System.Windows.Forms.NotifyIcon>(session, "_tray").Visible = false;
            Pump(app.Dispatcher);
            // Render into the existing hidden output HWNDs without showing them.
            typeof(OverlayHost).GetProperty("IsVisible")!.SetValue(host, true);
            long frames = host.PresentedFrameCount;
            Invoke(session, "RequestPixelRender");
            long framesAtInput = -1;
            app.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => framesAtInput = host.PresentedFrameCount);
            Pump(app.Dispatcher);
            results.Add(("pending auxiliary input runs before an animation/live-background pixel refresh", framesAtInput == frames));
            results.Add(("deferred pixel refresh still presents its frame", host.PresentedFrameCount > frames));
            Console.WriteLine($"PIXEL_RENDER_INPUT_ORDER framesBefore={frames} framesAtInput={framesAtInput} framesAfter={host.PresentedFrameCount}");
            frames = host.PresentedFrameCount;
            Invoke(session, "RequestPixelRender");
            Invoke(session, "RequestRender");
            app.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => framesAtInput = host.PresentedFrameCount);
            Pump(app.Dispatcher);
            results.Add(("a real edit promotes an already queued pixel refresh for immediate feedback", framesAtInput > frames));

            var toolbar = Field<Window>(session, "_toolbar");
            new WindowInteropHelper(toolbar).EnsureHandle();
            var coordinator = Field<SessionWindowCoordinator>(session, "_windowCoordinator");
            var editor = Field<EditorController>(session, "_editor");
            var isolation = (KeyboardInputIsolation)Activator.CreateInstance(typeof(KeyboardInputIsolation), Private, null,
                [host.ControlHandle, (uint)0x8003, 256, TimeSpan.FromMilliseconds(1200)], null)!;
            isolationField.SetValue(host, isolation);
            var state = Field<object>(isolation, "state");
            long lease = (long)state.GetType().GetMethod("Begin")!.Invoke(state,
                [new byte[256], HotkeyBinding.DefaultToggle, HotkeyBinding.DefaultHide, Environment.TickCount64])!;
            typeof(OverlayHost).GetField("_keyboardLease", Private)!.SetValue(host, lease);
            typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(host, true);
            var heartbeat = typeof(KeyboardInputIsolation).GetField("lastHeartbeat", Private)!;
            var panels = new Window[] { new InspectorWindow(() => editor.Session), new LayersWindow(() => editor.Session), new CanvasSettingsWindow() };
            try
            {
                foreach (var panel in panels)
                {
                    panel.Owner = toolbar; coordinator.Register(panel); AppWindowChrome.Apply(panel);
                    nint handle = new WindowInteropHelper(panel).EnsureHandle();
                    foreach (int message in new[] { 0x0231, 0x0216, 0x0214, 0x0232 })
                    {
                        // Timers are intentionally absent: replay the native move/size
                        // messages on the production chrome, not WPF MouseMove staging.
                        heartbeat.SetValue(isolation, Environment.TickCount64 - 1300);
                        var rectangle = Marshal.AllocHGlobal(16);
                        try
                        {
                            Marshal.Copy(new[] { 10, 10, 350, 400 }, 0, rectangle, 4);
                            SendMessageW(handle, (uint)message, 0, rectangle);
                        }
                        finally { Marshal.FreeHGlobal(rectangle); }
                        bool healthy = Environment.TickCount64 - (long)heartbeat.GetValue(isolation)! < 1200;
                        results.Add(($"{panel.GetType().Name} native {message:X4} reports real UI progress before the lease deadline", healthy && host.IsEditing && isolation.IsLeaseActive));
                    }
                    results.Add(($"{panel.GetType().Name} heartbeat replay remains hidden", !panel.IsVisible));
                }
                // Lack of genuine progress must still end the real suppression state,
                // and its production LeaseEnded handler must still leave editing.
                heartbeat.SetValue(isolation, Environment.TickCount64 - 1300);
                bool healthyNow = Environment.TickCount64 - (long)heartbeat.GetValue(isolation)! <= 1200;
                state.GetType().GetMethod("Process")!.Invoke(state,
                    [(uint)0x41, (uint)30, (uint)0, (uint)0, false, (uint)0x100, host.ControlHandle, true, healthyNow, Environment.TickCount64, null, null]);
                Invoke(host, "HandleKeyboardControlMessage", (uint)0x8003, (nuint)0);
                results.Add(("a genuinely stalled UI still releases input and exits editing", !host.IsEditing && isolation.LastEndReason == KeyboardLeaseEndReason.HeartbeatExpired));
            }
            finally
            {
                typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(host, false);
                foreach (var panel in panels) panel.Close();
                isolation.Dispose(); isolationField.SetValue(host, null);
            }
        }
        Pump(app.Dispatcher);
        results.Add(("hidden progress tests preserve the actual foreground", GetForegroundWindow() == foreground));
        foreach (var result in results) Console.WriteLine($"NATIVE_PROGRESS {result.Name}: {result.Passed}");
        app.Shutdown();
        foreach (var result in results) check(result.Name, result.Passed);
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static object? Invoke(object owner, string name, params object?[] args) => owner.GetType().GetMethod(name, Private)!.Invoke(owner, args);
    private static void Pump(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint handle, uint message, nuint wParam, nint lParam);
}
