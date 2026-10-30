using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

/// <summary>Own-window reproduction of the two-click new-canvas focus lifecycle.
/// Uses the ordinary backend, a bounded canvas, a unique profile and the real keyboard lease.
/// Requires explicit live-desktop opt-in because an inactive desktop cannot retain that lease.</summary>
internal static class NewCanvasEscapeRuntimeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        bool live = Environment.GetCommandLineArgs().Contains("--new-canvas-escape-live", StringComparer.Ordinal);
        var desktop = new StringBuilder(256);
        if (!live || !GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, desktop, desktop.Capacity * 2, out _) ||
            desktop.ToString().StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal))
            throw new InvalidOperationException("Run --new-canvas-escape-only --new-canvas-escape-live on the input desktop.");
        if (Application.Current is not null) throw new InvalidOperationException("Run the new-canvas Escape fixture in a fresh process.");
        string profile = Path.Combine(Environment.GetEnvironmentVariable("FLOATCANVAS_NEW_CANVAS_ESCAPE_ARTIFACTS") ??
            Path.Combine(Path.GetTempPath(), "FloatCanvas-new-canvas-escape"), Guid.NewGuid().ToString("N"), "profile");
        Directory.CreateDirectory(profile);
        Console.WriteLine("NEW_CANVAS_ESCAPE_PROFILE " + profile);
        ApplicationPaths.Configure(["--profile", profile, "--primary-screen"]);
        new UserSettings { Language = "en", Theme = "light", ShowFloatingLogo = false }.Save();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var previousContext = SynchronizationContext.Current;
        var keyboardState = ThreadModifierScope.Read();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        OverlayHost? host = null;
        DesktopSession? session = null;
        var outcomes = new List<(string Name, bool Passed)>();
        try
        {
            var primary = ((IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!).First(m => m.IsPrimary);
            var bounds = new PixelBounds(primary.WorkArea.Left + 30, primary.WorkArea.Top + 160, 640, 360);
            Func<IReadOnlyList<MonitorInfo>> provider = () => [primary with { Bounds = bounds }];
            host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null,
                [OverlayMonitorScope.PrimaryScreen, provider, false], null)!;
            session = new DesktopSession(app, host);
            Field<System.Windows.Forms.NotifyIcon>(session, "_tray").Visible = false;
            var toolbar = Field<Window>(session, "_toolbar");
            var button = Field<NewCanvasButton>(session, "_newCanvasButton");
            var editor = Field<EditorController>(session, "_editor");
            host.SetVisible(true);
            Enter();
            Key();
            Observe("one Escape exits the untouched production editing session", !host.IsEditing);
            Enter();
            editor.Tool = EditorTool.Freehand;
            editor.Session.Add(new() { Kind = ElementKind.Rectangle, Width = 40, Height = 30 });
            var before = editor.Session;
            Click();
            WaitFor(() => button.IsConfirming, "first-click confirmation");
            Observe("the first actual new-canvas click arms inline confirmation", button.IsConfirming && ReferenceEquals(editor.Session, before));
            Snapshot("confirmed-first-click");
            Click();
            WaitFor(() => !ReferenceEquals(editor.Session, before) && button.IsEnabled && toolbar.IsEnabled && !Busy(), "new canvas completion");
            Observe("the confirming click creates a blank canvas and preserves editing", editor.Session.Document.Elements.Count == 0 && host.IsEditing && !button.IsConfirming);
            Snapshot("after-create-without-refocus");
            Key();
            Snapshot("after-escape-without-refocus");
            Observe("one Escape after the confirming click exits editing without a blank-area click", !host.IsEditing);

            void Enter()
            {
                toolbar.Left = (primary.WorkArea.Left + 50) * 96d / primary.DpiX;
                toolbar.Top = (primary.WorkArea.Top + 50) * 96d / primary.DpiY;
                toolbar.ShowActivated = false; toolbar.Show(); Pump();
                nint handle = new WindowInteropHelper(toolbar).Handle;
                bool entered = false;
                typeof(NativeInputRoutingFixture).GetMethod("ActivateFixtureWindow", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [handle, (Action)(() => entered = (bool)Invoke("SetEditing", true, false)!)]);
                for (int settle = 0; settle < 3; settle++) { Thread.Sleep(20); Pump(); }
                if (!entered || !host.IsEditing)
                {
                    Snapshot("editing-entry-failed");
                    throw new InvalidOperationException("The fixture cannot enter production editing; native activation is unavailable on this desktop.");
                }
                Activate(handle); Keyboard.Focus(toolbar); Pump();
                Snapshot("entered");
            }
            void Click()
            {
                int clicks = 0;
                RoutedEventHandler observed = (_, _) => clicks++;
                button.Click += observed;
                var device = (MouseDevice)typeof(ToolOptionsInputFixture).GetMethod("ControlledMouse", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [button])!;
                try
                {
                    InputManager.Current.ProcessInput(new MouseButtonEventArgs(device, Environment.TickCount, MouseButton.Left)
                        { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = button });
                    Pump();
                    Observe("the production pointer-down focuses the button in its own native toolbar HWND",
                        button.IsKeyboardFocusWithin && GetFocus() == new WindowInteropHelper(toolbar).Handle);
                    Snapshot("new-canvas-pointer-down");
                }
                finally { ToolOptionsInputFixture.ReleaseControlledMouse(device); }
                button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                Pump();
                if (clicks == 0) typeof(Button).GetMethod("OnClick", Private)!.Invoke(button, null);
                button.Click -= observed;
                Observe("controlled pointer click invokes the production new-canvas handler exactly once", clicks == 1);
            }
            void Key()
            {
                foreach (bool up in new[] { false, true })
                {
                    var state = new byte[256]; if (!up) state[0x1B] = 128;
                    if (!SetKeyboardState(state)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    nint focus = GetFocus();
                    // Exiting on key-down hides the window before key-up. Never refocus it.
                    if (focus == 0 && up && !host.IsEditing) return;
                    if (focus == 0) throw new InvalidOperationException("No fixture HWND has focus; no key sent.");
                    if (GetWindowThreadProcessId(focus, out uint process) != GetCurrentThreadId() || process != Environment.ProcessId)
                    {
                        throw new InvalidOperationException("Key transport must target an exact fixture-owned HWND.");
                    }
                    uint data = 1 | ((MapVirtualKey(0x1B, 0) & 0xff) << 16) | (up ? 0xC0000000u : 0u);
                    SendMessage(focus, up ? 0x101u : 0x100u, 0x1B, unchecked((nint)(int)data));
                    Pump();
                }
            }
            bool Busy() => (bool)typeof(DesktopSession).GetProperty("DocumentOperationBusy", Private)!.GetValue(session)!;
            object? Invoke(string name, params object[] args) => typeof(DesktopSession).GetMethod(name, Private)!.Invoke(session, args);
            void WaitFor(Func<bool> complete, string stage)
            {
                try { Wait(complete, stage); }
                catch { Snapshot("timeout-" + stage); throw; }
            }
            void Snapshot(string phase)
            {
                var focused = Keyboard.FocusedElement as DependencyObject;
                Console.WriteLine("NEW_CANVAS_ESCAPE " + JsonSerializer.Serialize(new
                {
                    Phase = phase, Desktop = desktop.ToString(), host.IsEditing,
                    host.HasEditingInput, host.IsKeyboardIsolationActive, ToolbarEnabled = toolbar.IsEnabled, ButtonEnabled = button.IsEnabled,
                    ButtonHasFocus = button.IsKeyboardFocusWithin, NativeFocus = (long)GetFocus(),
                    NativeForeground = (long)GetForegroundWindow(), ToolbarHandle = (long)new WindowInteropHelper(toolbar).Handle,
                    WpfFocus = focused?.GetType().Name,
                    FocusSource = (long)((focused is null ? null : PresentationSource.FromDependencyObject(focused) as HwndSource)?.Handle ?? 0),
                    Busy = Busy(), button.IsConfirming
                }));
            }
        }
        finally
        {
            if (host?.IsEditing == true) host.ExitEditing(false);
            session?.Dispose(); if (session is null) host?.Dispose();
            foreach (var window in app.Windows.Cast<Window>().ToArray()) window.Close();
            SetKeyboardState(keyboardState); Pump();
            SynchronizationContext.SetSynchronizationContext(previousContext); app.Shutdown();
        }
        foreach (var outcome in outcomes) check(outcome.Name, outcome.Passed);
        void Observe(string name, bool passed)
        {
            outcomes.Add((name, passed));
            Console.WriteLine($"NEW_CANVAS_ESCAPE_FINDING {passed}: {name}");
        }
    }

    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static void Activate(nint handle) { SetForegroundWindow(handle); SetActiveWindow(handle); SetFocus(handle); Pump(); }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
    private static void Wait(Func<bool> complete, string stage)
    {
        if (complete()) { Pump(); return; }
        var watch = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            if (complete() || watch.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false;
        };
        try { timer.Start(); Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        if (!complete()) throw new TimeoutException("New-canvas Escape fixture: " + stage);
        Pump();
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder name, int length, out int needed);
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] private static extern nint SetActiveWindow(nint handle);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint handle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint process);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint key, uint mode);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetKeyboardState(byte[] state);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint handle, uint message, nint key, nint data);
}
