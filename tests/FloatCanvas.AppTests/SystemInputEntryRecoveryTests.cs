using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

/// <summary>Real pending-entry timer and registered application-event recovery, on hidden own windows.</summary>
internal static class SystemInputEntryRecoveryTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        if (Application.Current is not null) throw new InvalidOperationException("Run --system-input-entry-recovery-only in a fresh process.");
        string previousProfile = ApplicationPaths.ProfileDirectory, previousLanguage = L.Preference;
        string profile = Path.Combine(Path.GetTempPath(), "FloatCanvas-EntryRecovery", Guid.NewGuid().ToString("N"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            Names();
            foreach (var mode in new[] { OverlayBackendMode.Auto, OverlayBackendMode.Enhanced })
            {
                string ownProfile = Path.Combine(profile, mode.ToString());
                ApplicationPaths.Configure(["--profile", ownProfile]);
                new UserSettings { Language = "zh-CN", PreferGpuAcceleration = false, ShowFloatingLogo = true }.Save();
                using var receiver = ForegroundReceiver.Start(Path.Combine(profile, "receiver-" + mode));
                using var host = HiddenHost();
                var protection = new OverlayBackendProtection(ownProfile, mode, () => new(true, "entry recovery fixture"));
                check(mode + " entry fixture initializes its hidden enhanced backend", protection.TryEnable(host.EnableSystemInput));
                using var session = new DesktopSession(app, host, protection);
                Field<System.Windows.Forms.NotifyIcon>(session, "_tray").Visible = false;
                var editor = Field<EditorController>(session, "_editor");
                var original = editor.Session;
                original.Add(new CanvasElement { Kind = ElementKind.Rectangle, Width = 53, Height = 29 });
                string content = JsonSerializer.Serialize(original.Document);
                int undo = original.UndoCount, unavailable = 0;
                IReadOnlyList<uint>? observed = null;
                host.SystemInputUnavailable += () => unavailable++;
                host.SystemInputEntryBlocked += keys => observed = keys.ToArray();
                string marker = Path.Combine(ownProfile, "overlay-backend-state.json");
                string? backendState = File.Exists(marker) ? File.ReadAllText(marker) : null;
                using var pending = new PendingEntryFixture(host);
                pending.Begin();
                check(mode + " native entry begins pending with its queued blocker", host.IsEditing && !host.HasEditingInput);
                Invoke(session, "RefreshPassThroughLauncher");
                check(mode + " pending entry does not expose the passive launcher before the toolbar",
                    Field<PassThroughLauncherWindow?>(session, "_passThroughLauncher") is not { IsVisible: true });
                // Exercise the exact fixture hazard deterministically: a queue
                // refresh between Begin and the first production timer callback.
                if (!SetKeyboardState(new byte[256])) throw new InvalidOperationException("Could not refresh fixture queue state.");
                pending.PumpUntilBlocked(() => observed is not null, check);
                check(mode + " actual native entry timer retains the fixture blocker across a queue refresh", pending.NativeTimerTicks > 0 && pending.QueueRefreshes > 0);
                Console.WriteLine($"INFO blocker snapshot: count={observed?.Count}, fixtureDigit2={observed?.Contains(0x32u)}");
                check(mode + " real native timer reports the fixed blocking digit", observed is not null && observed.Contains(0x32u));
                check(mode + " blocked entry fully cancels pending input", !host.IsEditing && !host.HasEditingInput && host.Diagnostics.All(pair => !pair.InputVisible));
                check(mode + " blocked entry preserves scene and undo history", ReferenceEquals(original, editor.Session) &&
                    JsonSerializer.Serialize(editor.Session.Document) == content && original.UndoCount == undo);
                check(mode + " blocked entry does not raise a generic backend failure", unavailable == 0 && !protection.IsDisabled &&
                    !Field<bool>(session, "_overlaySwitchPending") && backendState == (File.Exists(marker) ? File.ReadAllText(marker) : null));
                var launcher = Field<PassThroughLauncherWindow>(session, "_passThroughLauncher");
                // The thread-local fixture controls digit 2, while the production
                // reader must also retain any real async keys held by the human.
                string message = L.Format("Input.EntryRecovery.Blocked", DesktopSession.FormatSystemInputEntryBlockers(observed!));
                check(mode + " blocked entry exposes its actionable launcher and status hint", launcher.IsVisible &&
                    Equals(launcher.Launcher.ToolTip, message) && AutomationProperties.GetHelpText(launcher.Launcher) == message &&
                    AutomationProperties.GetHelpText(launcher) == message && Field<TextBlock>(session, "_status").Text == message);
                Invoke(session, "RefreshPassThroughLauncher");
                launcher = Field<PassThroughLauncherWindow>(session, "_passThroughLauncher");
                check(mode + " refreshing the launcher retains its recovery hint", Equals(launcher.Launcher.ToolTip, message));

                // The registered app event also preserves the older capture session,
                // rather than returning to the live canvas when merely cancelling entry.
                var capture = new EditorSession(new SceneDocument { Kind = DocumentKind.FrozenCapture,
                    Elements = [new() { Kind = ElementKind.Ellipse, Width = 23, Height = 17 }] }, original.Assets);
                typeof(DesktopSession).GetField("_capture", Private)!.SetValue(session, capture);
                editor.ReplaceSession(capture, new(3, 4));
                string captured = JsonSerializer.Serialize(capture.Document);
                RaiseBlocked(host, [0x32]);
                check(mode + " application recovery retains the active legacy capture", ReferenceEquals(capture, editor.Session) &&
                    JsonSerializer.Serialize(capture.Document) == captured && editor.ScreenOrigin == new PointD(3, 4));
                RaiseBlocked(host, []);
                launcher = Field<PassThroughLauncherWindow>(session, "_passThroughLauncher");
                check(mode + " empty blocker state still explains timeout and retry", Equals(launcher.Launcher.ToolTip, L.Get("Input.EntryRecovery.Unknown")));
                RaiseBlocked(host, [0x32]);
                launcher = Field<PassThroughLauncherWindow>(session, "_passThroughLauncher");
                var activated = (Action)typeof(OverlayHost).GetField("InputActivated", Private)!.GetValue(host)!;
                activated();
                check(mode + " actual input activation clears the persistent recovery hint", Equals(launcher.Launcher.ToolTip, L.Get("Settings.PassThroughLauncherHint")) &&
                    AutomationProperties.GetHelpText(launcher.Launcher) == L.Get("Settings.PassThroughLauncherHint"));
                if (mode == OverlayBackendMode.Enhanced)
                {
                    ((Action)typeof(OverlayHost).GetField("SystemInputUnavailable", Private)!.GetValue(host)!)();
                    check("forced enhancement reports generic input failure without silently swallowing it", Field<TextBlock>(session, "_status").Text == L.Get("Desktop.InputInterrupted") &&
                        !protection.IsDisabled && !Field<bool>(session, "_overlaySwitchPending"));
                }
                check(mode + " entry recovery leaves the external foreground in place and opens no modal",
                    !IsOwnForeground() && !System.Windows.Interop.ComponentDispatcher.IsThreadModal);
            }
        }
        finally
        {
            foreach (var window in app.Windows.Cast<Window>().ToArray()) window.Close();
            ApplicationPaths.Configure(["--profile", previousProfile]); L.Initialize(previousLanguage);
        }

        void Names()
        {
            foreach (string language in new[] { "zh-CN", "en" })
            {
                L.Initialize(language);
                check(language + " digit labels describe the literal key cap", DesktopSession.FormatSystemInputEntryBlockers([0x32]) == "2");
                string modifiers = string.Join(L.Get("Input.EntryRecovery.Separator"), new[] { L.Get("Input.EntryRecovery.Control"),
                    L.Get("Input.EntryRecovery.Shift"), L.Get("Input.EntryRecovery.Alt"), L.Get("Input.EntryRecovery.Windows") });
                check(language + " generic and side-specific modifier aliases are deduplicated", DesktopSession.FormatSystemInputEntryBlockers(
                    [0x11, 0xA2, 0xA3, 0x10, 0xA0, 0xA1, 0x12, 0xA4, 0xA5, 0x5B, 0x5C]) == modifiers);
                string mice = string.Join(L.Get("Input.EntryRecovery.Separator"), new[] { L.Get("Input.EntryRecovery.MouseLeft"), L.Get("Input.EntryRecovery.MouseRight"),
                    L.Get("Input.EntryRecovery.MouseMiddle"), L.Get("Input.EntryRecovery.MouseX1"), L.Get("Input.EntryRecovery.MouseX2") });
                check(language + " mouse blockers have localized names", DesktopSession.FormatSystemInputEntryBlockers([1, 2, 4, 5, 6]) == mice);
                check(language + " a long blocker list is bounded with a remainder count", DesktopSession.FormatSystemInputEntryBlockers(
                    Enumerable.Range(0x30, 10).Select(key => (uint)key).ToArray()).EndsWith(L.Format("Input.EntryRecovery.More", 2), StringComparison.Ordinal));
            }
        }
    }

    private static OverlayHost HiddenHost()
    {
        var primary = ((IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!).First(m => m.IsPrimary);
        var offscreen = new PixelBounds(-32000, -32000, 640, 360);
        Func<IReadOnlyList<MonitorInfo>> provider = () => [primary with { Bounds = offscreen, WorkArea = offscreen }];
        return (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null, [OverlayMonitorScope.PrimaryScreen, provider, false], null)!;
    }
    private static void RaiseBlocked(OverlayHost host, IReadOnlyList<uint> keys) =>
        ((Action<IReadOnlyList<uint>>)typeof(OverlayHost).GetField("SystemInputEntryBlocked", Private)!.GetValue(host)!)(keys);
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static object? Invoke(object value, string name, params object[] args) => value.GetType().GetMethod(name, Private)!.Invoke(value, args);
    // Windows can refresh GetKeyState as this thread removes real input messages.
    // Keep the simulated blocker at the production timer's read boundary, instead
    // of assuming one SetKeyboardState survives an arbitrary dispatcher pump.
    // The hook is limited to this fixture UI thread and its hidden native owner.
    private sealed class PendingEntryFixture : IDisposable
    {
        private readonly OverlayHost host;
        private readonly byte[] originalKeys = new byte[256], queued = new byte[256];
        private readonly SubclassProcedure timerObserver;
        private readonly WinEventProcedure foregroundObserver;
        private readonly List<string> trace = new();
        private readonly nint foregroundHook;
        private Exception? failure;
        private nint target;
        private int focusLosses, foregroundChanges, cancellations, activations;
        private bool targetChanged;
        internal int NativeTimerTicks { get; private set; }
        internal int QueueRefreshes { get; private set; }
        internal int OwnForegroundEvents { get; private set; }

        internal PendingEntryFixture(OverlayHost host)
        {
            this.host = host;
            if (!GetKeyboardState(originalKeys)) throw new InvalidOperationException("Could not capture fixture keyboard state.");
            queued[0x32] = 0x80;
            timerObserver = BeforeWindowMessage;
            foregroundObserver = OnForeground;
            if (!SetWindowSubclass(host.SystemWindowOwner, timerObserver, 1, 0))
                throw new InvalidOperationException("Could not observe the fixture's native timer.");
            foregroundHook = SetWinEventHook(3, 3, 0, foregroundObserver, 0, 0, 0); // EVENT_SYSTEM_FOREGROUND
            if (foregroundHook == 0)
            {
                RemoveWindowSubclass(host.SystemWindowOwner, timerObserver, 1);
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            host.EditingFocusLost += OnFocusLost;
            host.InputActivated += OnActivated;
        }

        internal void Begin()
        {
            target = GetForegroundWindow(); targetChanged = false;
            if (target == 0 || IsOwnForeground()) throw new InvalidOperationException("The hidden entry fixture requires an existing external foreground.");
            var system = typeof(OverlayHost).GetField("_systemInput", Private)!.GetValue(host)!;
            system.GetType().GetMethod("SetEntryKeyProbeForTesting", Private)!.Invoke(system, [true]);
            SetBlocker();
            if (!host.TryEnterEditing(out var error)) throw new InvalidOperationException("Fixture entry failed: " + error + Diagnostics());
            Record("begin");
        }

        private nint BeforeWindowMessage(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
        {
            if (message == 0x113 && wParam == 0x4643)
            {
                try
                {
                    NativeTimerTicks++;
                    bool refreshed = GetKeyState(0x32) >= 0;
                    if (refreshed) { QueueRefreshes++; Record("thread queue refreshed before native timer"); }
                    if (GetForegroundWindow() != target) { targetChanged = true; Record("native timer sees changed external target"); }
                    // This is only SetKeyboardState for this thread. In particular,
                    // no synthetic key reaches the system or the foreground app.
                    SetBlocker();
                }
                catch (Exception error)
                {
                    failure = error;
                    // Cancel before the production timer can acquire input after a
                    // fixture failure. Never unwind an exception through a hook.
                    try { host.ExitEditing(restorePreviousForeground: false); }
                    catch (Exception cleanupError) { failure = new AggregateException(error, cleanupError); }
                }
            }
            return DefSubclassProc(window, message, wParam, lParam);
        }

        private void OnForeground(nint hook, uint eventId, nint window, int objectId, int childId, uint thread, uint time)
        {
            try
            {
                foregroundChanges++;
                if (GetWindowThreadProcessId(window, out uint process) != 0 && process == Environment.ProcessId) OwnForegroundEvents++;
                if (host.IsEditing && window != target) targetChanged = true;
                Record("foreground event");
            }
            catch (Exception error) { failure = error; }
        }
        private void OnFocusLost() { focusLosses++; Record("EditingFocusLost"); }
        private void OnActivated() { activations++; Record("InputActivated"); }
        private void SetBlocker()
        {
            if (!SetKeyboardState(queued)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        private void Record(string reason)
        {
            if (trace.Count < 20) trace.Add(reason + ": " + State());
        }
        private string State()
        {
            var system = typeof(OverlayHost).GetField("_systemInput", Private)!.GetValue(host)!;
            var status = system.GetType().GetProperty("Status", Private)!.GetValue(system)!;
            string native = string.Join(",", status.GetType().GetFields().Select(field => field.Name + "=" + field.GetValue(status)));
            return $"editing={host.IsEditing},queued2={GetKeyState(0x32) < 0},async2={GetAsyncKeyState(0x32) < 0},fg={GetForegroundWindow():X},target={target:X},{native}";
        }
        private string Diagnostics() => $" NativeTimers={NativeTimerTicks}, queueRefreshes={QueueRefreshes}, foregroundChanges={foregroundChanges}, focusLosses={focusLosses}, activations={activations}, retries={cancellations}. " + string.Join(" | ", trace) + " | final: " + State();

        internal void PumpUntilBlocked(Func<bool> ready, Action<string, bool> check)
        {
            var frame = new DispatcherFrame(); var watch = System.Diagnostics.Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) =>
            {
                try
                {
                    if (ready() || failure is not null || watch.Elapsed > TimeSpan.FromSeconds(8)) { frame.Continue = false; return; }
                    if (!host.IsEditing)
                    {
                        // The human can change external foreground during this test.
                        // Verify the production cancellation, then retry its new target.
                        bool normalCancellation = targetChanged && focusLosses > cancellations && !host.HasEditingInput && activations == 0;
                        check("an external foreground change cancels pending entry without acquiring input", normalCancellation);
                        if (!normalCancellation || ++cancellations > 3) { frame.Continue = false; return; }
                        Begin(); watch.Restart();
                    }
                }
                catch (Exception error) { failure = error; frame.Continue = false; }
            };
            timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
            if (failure is not null) throw new InvalidOperationException("Entry fixture failed." + Diagnostics(), failure);
            if (!ready()) throw new TimeoutException("Native pending entry did not reach its recoverable timeout." + Diagnostics());
            if (activations != 0 || GetForegroundWindow() != target)
                throw new InvalidOperationException("The pending fixture must never acquire input or leave the external target foreground." + Diagnostics());
            Console.WriteLine("INFO native entry fixture:" + Diagnostics());
        }

        public void Dispose()
        {
            try { if (host.IsEditing) host.ExitEditing(restorePreviousForeground: false); }
            finally
            {
                var system = typeof(OverlayHost).GetField("_systemInput", Private)!.GetValue(host)!;
                system.GetType().GetMethod("SetEntryKeyProbeForTesting", Private)!.Invoke(system, [false]);
                UnhookWinEvent(foregroundHook); RemoveWindowSubclass(host.SystemWindowOwner, timerObserver, 1);
                host.EditingFocusLost -= OnFocusLost; host.InputActivated -= OnActivated;
                try
                {
                    if (!SetKeyboardState(originalKeys)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
                finally { GC.KeepAlive(timerObserver); GC.KeepAlive(foregroundObserver); }
            }
        }
    }

    private sealed class ForegroundReceiver : IDisposable
    {
        private readonly Process process;
        private readonly string directory;
        private readonly nint originalForeground;

        private ForegroundReceiver(Process process, string directory, nint originalForeground)
        {
            this.process = process;
            this.directory = directory;
            this.originalForeground = originalForeground;
        }

        internal static ForegroundReceiver Start(string directory)
        {
            Directory.CreateDirectory(directory);
            nint original = GetForegroundWindow();
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--focus-external-helper");
            start.ArgumentList.Add("--focus-helper-directory");
            start.ArgumentList.Add(directory);
            var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the foreground receiver.");
            try
            {
                WaitForFile(Path.Combine(directory, "external-ready.json"), "foreground receiver readiness");
                using var ready = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "external-ready.json")));
                if (ready.RootElement.GetProperty("Process").GetInt32() != process.Id)
                    throw new InvalidDataException("The foreground receiver reported another process.");
                nint receiver = (nint)ready.RootElement.GetProperty("Handle").GetInt64();

                var gate = new Window
                {
                    Title = "FloatCanvas Input Entry Test Gate",
                    Width = 220,
                    Height = 80,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    ShowInTaskbar = true,
                    Topmost = true
                };
                gate.Show();
                try
                {
                    ActivateOwnWindow(new WindowInteropHelper(gate).Handle);
                    uint currentThread = GetCurrentThreadId();
                    uint receiverThread = GetWindowThreadProcessId(receiver, out _);
                    bool attached = receiverThread != 0 && receiverThread != currentThread &&
                        AttachThreadInput(currentThread, receiverThread, true);
                    try
                    {
                        SetForegroundWindow(receiver);
                        SetFocus(receiver);
                    }
                    finally
                    {
                        if (attached) AttachThreadInput(currentThread, receiverThread, false);
                    }
                    File.WriteAllText(Path.Combine(directory, "external-activate"), "activate");
                    WaitUntil(() => GetForegroundWindow() == receiver, "foreground receiver activation");
                }
                finally { gate.Close(); }
                return new(process, directory, original);
            }
            catch
            {
                if (!process.HasExited)
                {
                    File.WriteAllText(Path.Combine(directory, "external-exit"), "exit");
                    process.WaitForExit(3000);
                    if (!process.HasExited) process.Kill(true);
                }
                process.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            try
            {
                File.WriteAllText(Path.Combine(directory, "external-exit"), "exit");
                if (!process.WaitForExit(3000)) process.Kill(true);
            }
            finally
            {
                process.Dispose();
                if (originalForeground != 0 && IsWindow(originalForeground))
                    SetForegroundWindow(originalForeground);
            }
        }
    }

    private static void WaitForFile(string path, string operation)
    {
        WaitUntil(() => File.Exists(path), operation);
    }

    private static void WaitUntil(Func<bool> condition, string operation, int timeoutMilliseconds = 8000)
    {
        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline) throw new TimeoutException(operation + " timed out.");
            Pump(20);
        }
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(milliseconds),
            DispatcherPriority.Background,
            (_, _) => frame.Continue = false,
            Dispatcher.CurrentDispatcher);
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
    }

    private static void ActivateOwnWindow(nint window)
    {
        nint foreground = GetForegroundWindow();
        uint thread = GetCurrentThreadId();
        uint foregroundThread = foreground == 0 ? 0 : GetWindowThreadProcessId(foreground, out _);
        bool attached = foregroundThread != 0 && foregroundThread != thread &&
            AttachThreadInput(thread, foregroundThread, true);
        try
        {
            SetForegroundWindow(window);
            SetFocus(window);
        }
        finally
        {
            if (attached) AttachThreadInput(thread, foregroundThread, false);
        }
        if (GetForegroundWindow() != window)
            throw new InvalidOperationException("The test gate could not acquire foreground.");
    }

    private static bool IsOwnForeground() => GetWindowThreadProcessId(GetForegroundWindow(), out uint process) != 0 && process == Environment.ProcessId;
    private delegate nint SubclassProcedure(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    private delegate void WinEventProcedure(nint hook, uint eventId, nint window, int objectId, int childId, uint thread, uint time);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint window, SubclassProcedure callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint window, SubclassProcedure callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWinEventHook(uint first, uint last, nint module, WinEventProcedure callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool value);
    [DllImport("user32.dll")] private static extern bool SetFocus(nint window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
}
