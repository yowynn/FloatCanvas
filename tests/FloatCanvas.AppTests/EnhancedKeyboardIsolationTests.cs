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

/// <summary>Opt-in real enhanced input gate. All synthetic key input is guarded by the
/// exact foreground HWND/PID of its own child; global hooks observe only Space/Alt/Tab.
/// --physical-space replaces the enhanced canvas pulse with one actual user Space.
/// The optional real Alt+Tab phase is driven by the user, never injected.</summary>
internal static class EnhancedKeyboardIsolationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string ChildArgument = "--enhanced-keyboard-observer";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static bool TryRunChild(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args is not [ChildArgument, var directory]) return false;
        Observer(directory); return true;
    }

    internal static void Run(Action<string, bool> check)
    {
        if (Application.Current is not null) throw new InvalidOperationException("Run the enhanced keyboard gate in a fresh process.");
        var desktop = new StringBuilder(256);
        GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, desktop, desktop.Capacity * 2, out _);
        if (desktop.ToString().StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal))
            throw new InvalidOperationException("Enhanced input needs the real input desktop, not --isolated-ui-desktop.");
        string root = Environment.GetEnvironmentVariable("FLOATCANVAS_ENHANCED_KEYBOARD_EVIDENCE") ??
            Path.Combine(Path.GetTempPath(), "FloatCanvas-enhanced-keyboard");
        string run = Path.Combine(Path.GetFullPath(root), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        Console.WriteLine("ENHANCED_KEYBOARD_RUN " + run); Console.Out.Flush();
        bool physicalSpace = Environment.GetCommandLineArgs().Contains("--physical-space");
        Write(run, "launch.json", new { AtUtc = DateTimeOffset.UtcNow, Process = Environment.ProcessId,
            Executable = Environment.ProcessPath, PhysicalSpaceRequested = physicalSpace,
            Assemblies = new[] { typeof(DesktopSession).Assembly, typeof(OverlayHost).Assembly }.Select(assembly =>
                new { assembly.Location, Mvid = assembly.ManifestModule.ModuleVersionId }) });
        var observations = new List<object>(); var outcomes = new List<(string Name, bool Passed)>();
        nint originalForeground = GetForegroundWindow();
        Process? child = null; OverlayHost? host = null; DesktopSession? session = null;
        Task<string>? childOutput = null, childError = null;
        Application? app = null; int commandId = 0; string? failure = null;
        var previousContext = SynchronizationContext.Current;
        int localSpace = 0;
        try
        {
            // This child is an interactive, deliberately visible receiver. Keep its GUI
            // startup state normal; CreateNoWindow suppresses only the console.
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Normal,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(ChildArgument); start.ArgumentList.Add(run);
            child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the owned keyboard observer.");
            childOutput = child.StandardOutput.ReadToEndAsync(); childError = child.StandardError.ReadToEndAsync();
            Wait(() => Read(run)?.Ready == true, "owned observer readiness");
            var identity = Read(run)!;
            RequireWindow((nint)identity.Primary, child.Id); RequireWindow((nint)identity.Peer, child.Id);
            Activate((nint)identity.Primary, child.Id, child);
            RequireForeground((nint)identity.Primary, child.Id);
            Command("reset", preview: false);
            PulseSpace((nint)identity.Primary, child.Id);
            Wait(() => Read(run) is { LlDown: >= 1, LlUp: >= 1, WmDown: >= 1, WmUp: >= 1 }, "normal Space positive control");
            Finding("pass-through delivers one balanced Space pair to the independent WM and LL observer",
                Read(run) is { LlDown: 1, LlUp: 1, WmDown: 1, WmUp: 1 });

            ApplicationPaths.Configure(["--profile", Path.Combine(run, "profile"), "--primary-screen"]);
            new UserSettings { Language = "en", ShowFloatingLogo = false, Theme = "light" }.Save();
            app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
            var primary = ((IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, null)!).First(m => m.IsPrimary);
            var bounds = new PixelBounds(primary.WorkArea.Left + 30, primary.WorkArea.Top + 160, 640, 360);
            Func<IReadOnlyList<MonitorInfo>> provider = () => [primary with { Bounds = bounds }];
            host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null, [OverlayMonitorScope.PrimaryScreen, provider, false], null)!;
            host.EnableSystemInput(Path.Combine(run, "native-input-safety.log"));
            session = new DesktopSession(app, host);
            Field<System.Windows.Forms.NotifyIcon>(session, "_tray").Visible = false;
            Await((Task)Invoke("InitializeDocumentsAsync", false)!);
            host.KeyInput += key => { if (key.VirtualKey == 0x20) localSpace++; };
            host.EditingFocusLost += () => Snapshot("editing-focus-lost");
            Command("reset", preview: true);
            Enter();
            int beforeSpace = localSpace;
            if (physicalSpace)
            {
                RequireForeground((nint)identity.Primary, child.Id);
                Console.WriteLine("ENHANCED_KEYBOARD_PHYSICAL_SPACE_READY press and release Space once; no key is injected during this phase"); Console.Out.Flush();
            }
            else PulseSpace((nint)identity.Primary, child.Id);
            Wait(() => localSpace > beforeSpace || Read(run)?.LlDown > 0 || !host.IsEditing, "editing Space delivery or leakage", physicalSpace ? 120000 : 6000);
            Wait(ReleasedKeys, "Space released after the enhanced canvas phase");
            PumpFor(250); Snapshot("canvas-space");
            var leaked = Read(run)!;
            Finding("enhanced canvas receives exactly one Space without replaying it globally", localSpace == beforeSpace + 1);
            Finding("enhanced Space never reaches the pre-existing external low-level keyboard hook", leaked.LlDown == 0 && leaked.LlUp == 0);
            Finding("enhanced Space does not activate the receiver's Peek-like preview", !leaked.Preview && host.IsEditing && host.HasEditingInput);
            Finding("enhanced Space leaves the original external foreground window unchanged", GetForegroundWindow() == (nint)identity.Primary);

            if (host.IsEditing && host.HasEditingInput)
            {
                var toolbar = Field<Window>(session, "_toolbar");
                var originalContent = (UIElement)toolbar.Content;
                toolbar.Content = null;
                var text = new TextBox { Text = "normal", Width = 180, Height = 26 };
                // Disable composition only on this fixture-owned field; do not change
                // the user's keyboard layout or global IME state.
                InputMethod.SetIsInputMethodEnabled(text, false);
                var tabSource = new Button { Content = "Toolbar Tab check", Margin = new Thickness(8, 0, 0, 0) };
                var editors = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 110, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                editors.Children.Add(text); editors.Children.Add(tabSource);
                var body = new Grid(); body.Children.Add(originalContent); body.Children.Add(editors); toolbar.Content = body;
                PumpFor(50);
                try
                {
                    if (!host.TryFocusEditingWindow(new WindowInteropHelper(toolbar).Handle)) throw new InvalidOperationException("Cannot focus the fixture's own toolbar text field.");
                    Keyboard.Focus(text); text.CaretIndex = text.Text.Length; PumpFor(50);
                    if (!text.IsKeyboardFocused || GetFocus() != new WindowInteropHelper(toolbar).Handle)
                        throw new InvalidOperationException("The owned text field does not have actual local keyboard focus.");
                    PulseSpace((nint)identity.Primary, child.Id);
                    Wait(() => text.Text != "normal" || Read(run)?.LlDown > 0 || !host.IsEditing, "isolated WPF text input");
                    PumpFor(150); Snapshot("toolbar-text-space");
                    Finding("enhanced WPF text input receives Space once through the production bridge", text.Text == "normal ");
                    Finding("typing Space in an owned text field remains isolated from the external hook", Read(run) is { LlDown: 0, LlUp: 0, Preview: false } && host.HasEditingInput);

                    bool capsLock = (GetKeyState(0x14) & 1) != 0;
                    string shiftedText = "normal " + (capsLock ? "a" : "A");
                    PulseChord((nint)identity.Primary, child.Id, 0x41, 0xA0); // left Shift+A
                    Wait(() => text.Text != "normal " || !host.IsEditing, "Shift+A text input");
                    PumpFor(150); Snapshot("toolbar-shift-a");
                    observations.Add(new { Phase = "toolbar-shift-a-text", Actual = text.Text, Expected = shiftedText, CapsLock = capsLock });
                    Finding("enhanced Shift+A applies the passed modifier exactly once in WPF text", text.Text == shiftedText);
                    Finding("Shift+A leaves physical, local and WPF modifier states released", ModifiersReleased());

                    string beforeUndo = text.Text;
                    PulseChord((nint)identity.Primary, child.Id, 0x5A, 0xA2); // left Ctrl+Z
                    Wait(() => text.Text != beforeUndo || !host.IsEditing, "Ctrl+Z WPF text undo");
                    PumpFor(150); Snapshot("toolbar-control-z");
                    Finding("enhanced Ctrl+Z changes the focused WPF text through its normal undo command", text.Text != beforeUndo && text.IsKeyboardFocused);
                    Finding("Ctrl+Z leaves physical, local and WPF modifier states released", ModifiersReleased());
                    PulseChord((nint)identity.Primary, child.Id, 0x59, 0xA2); // left Ctrl+Y
                    Wait(() => text.Text == beforeUndo || !host.IsEditing, "Ctrl+Y WPF text redo");
                    PumpFor(150); Snapshot("toolbar-control-y");
                    Finding("enhanced Ctrl+Y restores the focused WPF text through its normal redo command", text.Text == beforeUndo && text.IsKeyboardFocused);
                    Finding("Ctrl+Y leaves physical, local and WPF modifier states released", ModifiersReleased());

                    // TextBox editing keys are local; this non-editor button deliberately
                    // exercises the production toolbar's existing Tab expansion policy.
                    Keyboard.Focus(tabSource); PumpFor(50);
                    if (!tabSource.IsKeyboardFocused) throw new InvalidOperationException("Cannot focus the fixture's toolbar Tab source.");
                    var settings = Field<UserSettings>(session, "_settings");
                    bool expanded = settings.ToolbarExpanded;
                    PulseChord((nint)identity.Primary, child.Id, 0x09);
                    Wait(() => settings.ToolbarExpanded != expanded || !host.IsEditing, "toolbar Tab expansion");
                    PumpFor(150); Snapshot("toolbar-tab");
                    Finding("enhanced Tab retains the production toolbar expansion command", host.HasEditingInput && settings.ToolbarExpanded != expanded);
                    Finding("the enhanced toolbar Tab command never reaches the external keyboard hook", Read(run)?.TabDown == 0);
                }
                finally { toolbar.Content = null; body.Children.Clear(); toolbar.Content = originalContent; }
            }
            else observations.Add(new { Phase = "toolbar-text-space", Skipped = "Earlier Space escaped and ended editing; no further key was injected." });

            Invoke("SetEditing", false, false); PumpFor(100);
            Command("reset", preview: false);
            Command("activate-primary", preview: false);
            Wait(() => GetForegroundWindow() == (nint)identity.Primary, "owned receiver foreground after editing");
            PulseSpace((nint)identity.Primary, child.Id);
            Wait(() => Read(run) is { LlDown: >= 1, LlUp: >= 1, WmDown: >= 1, WmUp: >= 1 }, "Space restores after editing ends");
            Finding("leaving editing restores one balanced Space pair to normal external input", Read(run) is { LlDown: 1, LlUp: 1, WmDown: 1, WmUp: 1 });

            Command("reset", preview: false); Enter();
            if (Environment.GetCommandLineArgs().Contains("--manual-alt-tab"))
            {
                Console.WriteLine("ENHANCED_KEYBOARD_MANUAL_ALT_TAB_READY press and release Alt+Tab to switch away within 30 seconds"); Console.Out.Flush();
                Wait(() => !host.IsEditing && Read(run) is { AltDown: >= 1, TabDown: >= 1 }, "manual Alt+Tab foreground handoff", 30000);
                Finding("real manual Alt+Tab remains visible to the external hook and releases editing", !host.IsEditing && Read(run) is { AltDown: >= 1, TabDown: >= 1 });
            }
            else
            {
                // Do not inject an OS switch chord into the user's MRU list. The automated
                // control transfers only to this receiver's second verified HWND.
                RequireForeground((nint)identity.Primary, child.Id);
                Command("activate-peer", preview: false);
                Wait(() => GetForegroundWindow() == (nint)identity.Peer, "owned peer foreground transfer");
                Wait(() => !host.IsEditing, "owned external foreground switch releases editing");
                Finding("an explicit external application-window switch still releases enhanced editing", !host.IsEditing && GetForegroundWindow() == (nint)identity.Peer);
                observations.Add(new { Phase = "alt-tab", AutomatedChord = false, Limit = "The automatic gate verifies controlled external focus transfer. --manual-alt-tab verifies the real Alt+Tab chord." });
            }
            Snapshot("finished");
            foreach (var outcome in outcomes) check(outcome.Name, outcome.Passed);

            void Enter()
            {
                RequireForeground((nint)identity.Primary, child.Id); Wait(ReleasedKeys, "released physical keys before editing");
                if (!(bool)Invoke("SetEditing", true, false)!) throw new InvalidOperationException("Production enhanced editing entry was rejected.");
                Wait(() => host.HasEditingInput, "real enhanced input acknowledgement");
                PumpFor(150);
                if (!host.TryFocusEditingWindow(host.Diagnostics.Single().InputHandle)) throw new InvalidOperationException("Cannot focus the fixture's own canvas input HWND.");
                Snapshot("entered");
            }
            object? Invoke(string name, params object[] values) => typeof(DesktopSession).GetMethod(name, Private)!.Invoke(session, values);
            void Command(string action, bool preview)
            {
                int id = ++commandId;
                Write(run, "command.json", new CommandState(id, action, preview));
                Wait(() => Read(run)?.Command == id, "observer command " + action);
            }
            void Snapshot(string phase)
            {
                observations.Add(new { Phase = phase, AtUtc = DateTimeOffset.UtcNow, Foreground = (long)GetForegroundWindow(), Focus = (long)GetFocus(),
                    Enhanced = host?.UsesSystemInput, Editing = host?.IsEditing, Input = host?.HasEditingInput, Isolation = host?.IsKeyboardIsolationActive,
                    HookEnabled = host?.IsKeyboardIsolationEnabled,
                    PrivateLease = host is null ? false : Field<KeyboardInputIsolation?>(host, "_keyboardIsolation")?.IsLeaseActive == true,
                    Suppressed = host is null ? 0 : Field<KeyboardInputIsolation?>(host, "_keyboardIsolation")?.SuppressedCount ?? 0,
                    LocalSpace = localSpace, Observer = Read(run) });
            }
            void Finding(string name, bool passed)
            { outcomes.Add((name, passed)); Console.WriteLine($"ENHANCED_KEYBOARD_FINDING {passed}: {name}"); Console.Out.Flush(); }
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            try { if (host?.IsEditing == true) host.ExitEditing(false); } finally { session?.Dispose(); if (session is null) host?.Dispose(); }
            if (app is not null) { foreach (var window in app.Windows.Cast<Window>().ToArray()) window.Close(); app.Shutdown(); }
            SynchronizationContext.SetSynchronizationContext(previousContext);
            if (child is not null)
            {
                File.WriteAllText(Path.Combine(run, "stop"), "stop");
                if (!child.WaitForExit(2500)) { child.Kill(); child.WaitForExit(2500); }
                Write(run, "observer-exit.json", new { child.Id, child.ExitCode });
                File.WriteAllText(Path.Combine(run, "observer.stdout.log"), childOutput?.GetAwaiter().GetResult());
                File.WriteAllText(Path.Combine(run, "observer.stderr.log"), childError?.GetAwaiter().GetResult());
                child.Dispose();
            }
            var remainingForeground = GetForegroundWindow();
            GetWindowThreadProcessId(remainingForeground, out uint remainingProcess);
            // Preserve a user's deliberate external switch, including manual Alt+Tab.
            if ((remainingForeground == 0 || remainingProcess == Environment.ProcessId) && originalForeground != 0 && IsWindow(originalForeground))
                SetForegroundWindow(originalForeground);
            Write(run, "result.json", new { Passed = failure is null && outcomes.All(x => x.Passed), Failure = failure, PhysicalSpaceRequested = physicalSpace,
                Findings = outcomes.Select(x => new { x.Name, x.Passed }), Observations = observations,
                Limitations = "Guarded SendInput proves input-stream routing, not physical HID equivalence. The automatic gate does not inject Alt+Tab; use --manual-alt-tab for that real chord." });
            Console.WriteLine("ENHANCED_KEYBOARD_RESULT " + Path.Combine(run, "result.json"));
        }
    }

    private sealed record CommandState(int Id, string Action, bool Preview);
    private sealed record ObserverState(bool Ready, int Process, long Primary, long Peer, int Command,
        int LlDown, int LlUp, int WmDown, int WmUp, int AltDown, int TabDown, int LlInjectedDown, int LlPhysicalDown, bool Preview);

    private static void Observer(string directory)
    {
        Directory.CreateDirectory(directory);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var startButton = new Button { Content = "Start owned keyboard check", Margin = new Thickness(22), Padding = new Thickness(12) };
        var primary = new Window { Title = "FloatCanvas owned shortcut receiver", Left = 200, Top = 200, Width = 640, Height = 300,
            Content = startButton, ShowActivated = false };
        var peer = new Window { Title = "FloatCanvas owned switch target", Left = 900, Top = 200, Width = 420, Height = 180,
            Content = new TextBlock { Margin = new Thickness(22), Text = "Second owned window for controlled focus-transfer validation." }, ShowActivated = false };
        var preview = new Window { Title = "FloatCanvas owned Peek-like preview", Width = 360, Height = 180,
            Content = new TextBlock { Margin = new Thickness(22), Text = "The external low-level hook received Space while its receiver was foreground." } };
        primary.Show(); peer.Show();
        nint primaryHandle = new WindowInteropHelper(primary).Handle, peerHandle = new WindowInteropHelper(peer).Handle;
        int llDown = 0, llUp = 0, wmDown = 0, wmUp = 0, altDown = 0, tabDown = 0, injectedDown = 0, physicalDown = 0, command = 0, previewEnabled = 0;
        HwndSource.FromHwnd(primaryHandle).AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
        {
            if (wParam == 0x20 && message is 0x100 or 0x104) wmDown++;
            if (wParam == 0x20 && message is 0x101 or 0x105) wmUp++;
            return 0;
        });
        KeyboardHook callback = (code, message, pointer) =>
        {
            if (code >= 0 && message is 0x100 or 0x101 or 0x104 or 0x105)
            {
                var key = Marshal.PtrToStructure<HookKey>(pointer);
                bool up = message is 0x101 or 0x105;
                if (key.Key == 0x20)
                {
                    if (up) llUp++;
                    else { llDown++; if ((key.Flags & 0x10) != 0) injectedDown++; else physicalDown++; }
                    if (!up && previewEnabled != 0 && GetForegroundWindow() == primaryHandle)
                        app.Dispatcher.BeginInvoke(() => { preview.Show(); preview.Activate(); });
                }
                if (!up && key.Key is 0x12 or 0xA4 or 0xA5) altDown++;
                if (!up && key.Key == 0x09) tabDown++;
            }
            return CallNextHookEx(0, code, message, pointer);
        };
        nint hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
        if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            if (File.Exists(Path.Combine(directory, "stop"))) { app.Shutdown(); return; }
            var next = ReadJson<CommandState>(Path.Combine(directory, "command.json"));
            if (next is not null && next.Id != command)
            {
                if (next.Action == "reset") { llDown = llUp = wmDown = wmUp = altDown = tabDown = injectedDown = physicalDown = 0; preview.Hide(); }
                if (next.Action == "activate-primary") primary.Activate();
                if (next.Action == "activate-peer" && GetForegroundWindow() == primaryHandle) peer.Activate();
                previewEnabled = next.Preview ? 1 : 0; command = next.Id;
            }
            Save();
        };
        void Save()
        {
            try
            {
                Write(directory, "observer.json", new ObserverState(true, Environment.ProcessId, (long)primaryHandle,
                    (long)peerHandle, command, llDown, llUp, wmDown, wmUp, altDown, tabDown, injectedDown, physicalDown, preview.IsVisible));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Retry a transient snapshot replacement failure on the next timer
                // turn. Persistent failures still time out the driver's assertion.
                Console.Error.WriteLine("OBSERVER_STATUS_RETRY " + error.GetType().Name);
            }
        }
        Save(); timer.Start();
        try { app.Run(); }
        finally { timer.Stop(); UnhookWindowsHookEx(hook); GC.KeepAlive(callback); }
    }

    private static ObserverState? Read(string run) => ReadJson<ObserverState>(Path.Combine(run, "observer.json"));
    private static T? ReadJson<T>(string path)
    {
        try { using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return JsonSerializer.Deserialize<T>(file); }
        catch (Exception error) when (error is IOException or JsonException) { return default; }
    }
    private static void Write(string directory, string name, object value)
    { string path = Path.Combine(directory, name); File.WriteAllText(path + ".pending", JsonSerializer.Serialize(value, Json)); File.Move(path + ".pending", path, true); }
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static void RequireWindow(nint window, int process)
    { if (window == 0 || !IsWindow(window) || GetWindowThreadProcessId(window, out uint actual) == 0 || actual != process) throw new InvalidOperationException("Test window identity changed; no input sent."); }
    private static void RequireForeground(nint window, int process)
    { RequireWindow(window, process); if (GetForegroundWindow() != window) throw new InvalidOperationException("The exact owned receiver is not foreground; no input sent."); }
    private static bool ReleasedKeys()
    { for (int key = 1; key < 256; key++) if (GetAsyncKeyState(key) < 0) return false; return true; }
    private static void Activate(nint window, int process, Process observer)
    {
        RequireWindow(window, process);
        uint targetThread = GetWindowThreadProcessId(window, out _);
        uint foreground = GetWindowThreadProcessId(GetForegroundWindow(), out _), own = GetCurrentThreadId();
        bool attached = foreground != 0 && foreground != own && AttachThreadInput(own, foreground, true);
        bool attachedTarget = targetThread != own && targetThread != foreground && AttachThreadInput(own, targetThread, true);
        try { SetForegroundWindow(window); }
        finally { if (attachedTarget) AttachThreadInput(own, targetThread, false); if (attached) AttachThreadInput(own, foreground, false); }
        if (GetForegroundWindow() != window && Environment.GetCommandLineArgs().Contains("--wait-for-activation"))
        {
            Console.WriteLine($"ENHANCED_KEYBOARD_ACTIVATION_READY hwnd={(long)window} pid={process} select 'FloatCanvas owned shortcut receiver' and click Start");
            Console.Out.Flush();
            Wait(() => { if (observer.HasExited) throw new InvalidOperationException($"Owned observer exited before activation: {observer.ExitCode}.");
                return GetForegroundWindow() == window && ReleasedKeys(); }, "manual owned receiver activation", 300000);
            PumpFor(250);
        }
        else Wait(() => GetForegroundWindow() == window, "own receiver foreground permission", 2000);
    }
    private static void PulseSpace(nint foreground, int process)
        => PulseChord(foreground, process, 0x20);
    private static void PulseChord(nint foreground, int process, ushort key, params ushort[] modifiers)
    {
        RequireForeground(foreground, process);
        if (!ReleasedKeys()) throw new InvalidOperationException("A physical key/button is held; no input sent.");
        bool allowed = modifiers.Length == 0 && key is 0x20 or 0x09 ||
            modifiers is [0xA0] && key == 0x41 || modifiers is [0xA2] && key is 0x5A or 0x59;
        if (!allowed)
            throw new InvalidOperationException("The fixture permits only Space, Shift+A, Ctrl+Z/Y and toolbar Tab; no OS switch chord is allowed.");
        var inputs = modifiers.Select(value => Make(value, false)).Append(Make(key, false)).Append(Make(key, true))
            .Concat(modifiers.Reverse().Select(value => Make(value, true))).ToArray();
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
        if (sent != inputs.Length)
        {
            int error = Marshal.GetLastWin32Error();
            var active = new List<ushort>();
            foreach (var input in inputs.Take((int)sent))
                if ((input.Data.Keyboard.Flags & 2) != 0) active.Remove(input.Data.Keyboard.Key); else active.Add(input.Data.Keyboard.Key);
            var balance = active.AsEnumerable().Reverse().Select(value => Make(value, true)).ToArray();
            if (balance.Length > 0) SendInput((uint)balance.Length, balance, Marshal.SizeOf<NativeInput>());
            throw new Win32Exception(error);
        }
        static NativeInput Make(ushort value, bool up) => new() { Type = 1, Data = new() { Keyboard = new() { Key = value, Flags = up ? 2u : 0 } } };
    }
    private static bool ModifiersReleased() => Keyboard.Modifiers == ModifierKeys.None &&
        new[] { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C }.All(key => GetKeyState(key) >= 0 && GetAsyncKeyState(key) >= 0);
    private static void Await(Task operation) { Wait(() => operation.IsCompleted, "production document initialization"); operation.GetAwaiter().GetResult(); }
    private static void PumpFor(int milliseconds)
    { long until = Environment.TickCount64 + milliseconds; Wait(() => Environment.TickCount64 >= until, "dispatcher settle", milliseconds + 1000); }
    private static void Wait(Func<bool> predicate, string operation, int milliseconds = 6000)
    {
        if (predicate()) return;
        var watch = Stopwatch.StartNew(); var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (predicate() || watch.ElapsedMilliseconds >= milliseconds) frame.Continue = false; };
        try { timer.Start(); Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        if (!predicate()) throw new TimeoutException("Enhanced keyboard gate: " + operation);
    }

    private delegate nint KeyboardHook(int code, nuint message, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct HookKey { internal uint Key, Scan, Flags, Time; internal nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { internal uint Type; internal InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData { [FieldOffset(0)] internal KeyInput Keyboard; [FieldOffset(0)] internal MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyInput { internal ushort Key, Scan; internal uint Flags, Time; internal nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { internal int X, Y; internal uint Data, Flags, Time; internal nuint Extra; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder name, int length, out int needed);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, KeyboardHook callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
