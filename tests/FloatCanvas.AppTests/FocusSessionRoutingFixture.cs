using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

/// <summary>Opt-in production window lifecycle regression. Retains real keyboard isolation,
/// uses only a bounded native canvas and a unique explicit profile. Most scenarios invoke own
/// controls; explicit Escape/pointer cases guard injected input against their own HWND/PID.</summary>
internal static class FocusSessionRoutingFixture
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        if (Application.Current is not null) throw new InvalidOperationException("Run --focus-session-only in a fresh process.");
        string root = Environment.GetEnvironmentVariable("FLOATCANVAS_FOCUS_ARTIFACTS") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "artifacts", "FloatCanvas-focus-session", "tests");
        var args = Environment.GetCommandLineArgs(); int caseIndex = Array.IndexOf(args, "--focus-case");
        string scenario = caseIndex >= 0 && caseIndex + 1 < args.Length ? args[caseIndex + 1] :
            Environment.GetEnvironmentVariable("FLOATCANVAS_FOCUS_CASE") ?? "rename-confirm";
        string run = Path.Combine(root, scenario + "-" + Guid.NewGuid().ToString("N"));
        string profile = Path.Combine(run, "profile"); Directory.CreateDirectory(profile);
        ApplicationPaths.Configure(["--profile", profile, "--primary-screen"]);
        new UserSettings { Language = scenario == "history-preview-ui" ? "zh-CN" : "en", Theme = "light" }.Save();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        nint originalForeground = GetForegroundWindow();
        DesktopSession? session = null; OverlayHost? host = null; Window? toolbar = null;
        EditorController? observedEditor = null;
        Process? external = null; nint externalHandle = 0;
        var observations = new List<object>(); var pointers = new List<OverlayPointerEvent>(); var attached = new HashSet<Window>();
        var checks = new List<object>(); var originalCheck = check;
        check = (name, condition) => { checks.Add(new { Name = name, Passed = condition }); originalCheck(name, condition); };
        PreProcessInputEventHandler pointerTrace = (_, e) =>
        {
            if (e.StagingItem.Input is not MouseButtonEventArgs mouse) return;
            var source = mouse.OriginalSource as DependencyObject ?? mouse.MouseDevice.Target as DependencyObject;
            var sourceWindow = source is null ? null : PresentationSource.FromDependencyObject(source) as HwndSource;
            var captured = Mouse.Captured as DependencyObject;
            var captureWindow = captured is null ? null : PresentationSource.FromDependencyObject(captured) as HwndSource;
            Point? point = sourceWindow?.RootVisual is UIElement rootVisual ? rootVisual.PointToScreen(mouse.GetPosition(rootVisual)) : null;
            Console.WriteLine($"FOCUS_WPF {mouse.RoutedEvent?.Name} {mouse.ChangedButton}/{mouse.ButtonState} original={mouse.OriginalSource?.GetType().Name} target={mouse.MouseDevice.Target?.GetType().Name} source={sourceWindow?.Handle:X} root={sourceWindow?.RootVisual?.GetType().Name} captured={captured?.GetType().Name} captureSource={captureWindow?.Handle:X}/{captureWindow?.RootVisual?.GetType().Name} nativeCapture={GetCapture():X} point={point} handled={mouse.Handled}");
            observations.Add(new { Stage = "WpfPointer", Event = mouse.RoutedEvent?.Name, Original = mouse.OriginalSource?.GetType().Name, Target = mouse.MouseDevice.Target?.GetType().Name,
                SourceHandle = (long)(sourceWindow?.Handle ?? 0), SourceRoot = sourceWindow?.RootVisual?.GetType().Name, Captured = captured?.GetType().Name,
                CaptureHandle = (long)(captureWindow?.Handle ?? 0), CaptureRoot = captureWindow?.RootVisual?.GetType().Name, NativeCapture = (long)GetCapture(), X = point?.X, Y = point?.Y });
        };
        InputManager.Current.PreProcessInput += pointerTrace;
        Exception? failure = null;
        try
        {
            var primary = ((IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!).First(m => m.IsPrimary);
            // Real DXGI capture validates the physical monitor rectangle. Only snapshot
            // cases need that complete rectangle; pointer injection stays in a small own ROI.
            var tile = scenario.StartsWith("snapshot-", StringComparison.Ordinal) || scenario.StartsWith("session-capture-", StringComparison.Ordinal) || scenario == "animation-liveblur" ? primary.Bounds :
                new PixelBounds(primary.WorkArea.Left + 30, primary.WorkArea.Top + 160, 640, 360);
            Func<IReadOnlyList<MonitorInfo>> provider = () => [primary with { Bounds = tile }];
            host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null, [OverlayMonitorScope.PrimaryScreen, provider, false], null)!;
            host.Pointer += pointer => { pointers.Add(pointer); Console.WriteLine($"FOCUS_POINTER {pointer.Kind} {pointer.Button} {pointer.Position} {pointer.Modifiers}"); };
            host.EditingFocusLost += () => Snapshot("EditingFocusLost");
            host.KeyboardIsolationReleased += reason => Snapshot("KeyboardIsolationReleased:" + reason);
            session = new DesktopSession(app, host);
            Field<System.Windows.Forms.NotifyIcon>(session, "_tray").Visible = false;
            toolbar = Field<Window>(session, "_toolbar"); AttachWindows();
            var library = Field<StampLibrary>(session, "_stampLibrary");
            var editor = Field<EditorController>(session, "_editor");
            observedEditor = editor;
            bool lastTransaction = editor.Session.IsTransactionActive;
            editor.Changed += () =>
            {
                bool transaction = editor.Session.IsTransactionActive;
                if (transaction != lastTransaction)
                {
                    string? endingStack = lastTransaction && !transaction ? Environment.StackTrace : null;
                    Console.WriteLine($"FOCUS_EDITOR transaction={transaction} objects={editor.Session.Document.Elements.Count} undo={editor.Session.UndoCount} tool={editor.Tool} stack={endingStack}");
                    observations.Add(new { Stage = "EditorTransaction", Active = transaction, Elements = editor.Session.Document.Elements.Count, Undo = editor.Session.UndoCount, Stack = endingStack });
                    lastTransaction = transaction;
                }
            };
            Await(library.LoadAsync());
            var source = new EditorSession(new SceneDocument { Elements = [new CanvasElement { Kind = ElementKind.Rectangle, Width = 40, Height = 30 }] });
            source.SelectAll(); Await(library.SaveSelectionAsync(source, "Focus regression stamp"));
            toolbar.Show(); Pump(50); host.SetVisible(true);
            if (args.Contains("--wait-for-start") || Environment.GetEnvironmentVariable("FLOATCANVAS_FOCUS_WAIT_FOR_START") == "1")
            {
                bool started = false;
                var start = new Button { Content = "Start isolated focus regression", Padding = new Thickness(12), Margin = new Thickness(18) };
                var gate = new Window { Title = "FloatCanvas Focus Regression Start", Width = 360, Height = 140,
                    Content = start, ShowInTaskbar = true, Topmost = true, ShowActivated = true, Style = null,
                    Left = (primary.WorkArea.Right - 650) * 96d / primary.DpiX, Top = (primary.WorkArea.Top + 250) * 96d / primary.DpiY,
                    WindowStyle = WindowStyle.SingleBorderWindow, ResizeMode = ResizeMode.NoResize };
                app.MainWindow = gate;
                start.Click += (_, _) => { started = true; Console.WriteLine("FOCUS_START_CLICKED"); Console.Out.Flush(); };
                gate.Show(); AttachWindows();
                Console.WriteLine($"FOCUS_START_WAIT hwnd={new WindowInteropHelper(gate).Handle:X} timeoutSeconds=90"); Console.Out.Flush();
                Wait(() => started || !gate.IsVisible, "foreground permission click", 90);
                if (!started) throw new OperationCanceledException("The isolated focus regression start window was closed before Start.");
                app.MainWindow = toolbar; ActivateOwnWindow(new WindowInteropHelper(toolbar).Handle); gate.Close();
            }
            ActivateOwnWindow(new WindowInteropHelper(toolbar).Handle);
            check("production focus fixture enters its own bounded canvas", (bool)Invoke("SetEditing", true, false)!);
            Pump(100);
            toolbar.Left = (primary.WorkArea.Right - 1500) * 96d / primary.DpiX;
            toolbar.Top = (primary.WorkArea.Top + 20) * 96d / primary.DpiY;
            Pump(50); Snapshot("editing-entered");
            check("production focus fixture retains the actual keyboard isolation lease", host.IsEditing && host.IsKeyboardIsolationActive);
            if (scenario.StartsWith("session-capture-", StringComparison.Ordinal))
            {
                SessionCaptureRoutingFixture.Run(check, session, host, editor, toolbar, run, scenario, Snapshot);
                return;
            }
            if (scenario.StartsWith("gpu-", StringComparison.Ordinal))
            {
                GpuRoutingFixture.Run(check, session, host, editor, toolbar, run, scenario, Snapshot);
                return;
            }
            if (scenario.StartsWith("animation-", StringComparison.Ordinal))
            {
                AnimationRoutingFixture.Run(check, session, host, editor, toolbar, run, scenario, Snapshot);
                return;
            }
            if (scenario.StartsWith("snapshot-", StringComparison.Ordinal))
            {
                SnapshotRoutingFixture.Run(check, session, host, editor, toolbar, run, scenario, Snapshot);
                return;
            }
            if (scenario.StartsWith("drag-sized", StringComparison.Ordinal))
            {
                DragSizedObjectsFixture.Run(check, session, host, editor, toolbar, run, scenario, Snapshot);
                return;
            }
            if (scenario.StartsWith("history-", StringComparison.Ordinal))
            {
                string[] historyCases = scenario == "history-boundaries"
                    ? ["history-cancel", "history-pass-through", "history-mode-exit", "history-external"] : [scenario];
                foreach (string historyCase in historyCases)
                {
                    Console.WriteLine("FOCUS_CASE " + historyCase);
                    if (!host.IsEditing)
                    {
                        toolbar.Show(); ActivateOwnWindow(new WindowInteropHelper(toolbar).Handle);
                        check("an independent history case acquires its own explicit edit entry", (bool)Invoke("SetEditing", true, false)!);
                        Pump(75);
                    }
                    editor.Session.Execute("isolated history fixture setup", document => document.Elements.Clear()); editor.Session.SetSelection([]);
                    HistoryRestoreRoutingFixture.Run(check, app, session, host, editor, toolbar, profile, run, historyCase, Snapshot, AttachWindows);
                }
                return;
            }
            if (scenario == "colors-matrix") { foreach (string colorCase in new[] { "color-secondary-only", "color-canvas", "color-pinned", "color-toolbar" }) RunColorCase(colorCase); return; }
            if (scenario.StartsWith("color-", StringComparison.Ordinal)) { RunColorCase(scenario); return; }
            string[] completions = scenario is "rename-matrix" or "full-matrix"
                ? ["direct-close", "close-cancel", "owner-unavailable", "rename-confirm", "rename-cancel", "rename-escape", "rename-close", .. Enumerable.Repeat("rename-confirm", 20)]
                : scenario == "final-boundaries" ? ["rename-confirm", "owner-unavailable", "modal-external"]
                : scenario == "rename-repeat" ? Enumerable.Repeat("rename-confirm", 20).ToArray() : [scenario];
            foreach (string completion in completions)
            {
            Console.WriteLine("FOCUS_CASE " + completion);
            Invoke("ShowStampLibrary");
            Wait(() => NullableField(session, "_stampWindow") is Window { IsVisible: true }, "stamp manager opening");
            var manager = Field<Window>(session, "_stampWindow"); AttachWindows(); Pump(100);
            var list = Field<ListBox>(manager, "grid"); list.SelectedIndex = 0;
            Snapshot("manager-opened");
            check("the real manager is enabled and editing remains active", manager.IsVisible && manager.IsEnabled && host.IsEditing && host.IsKeyboardIsolationActive);
            if (completion == "close-cancel")
            {
                System.ComponentModel.CancelEventHandler cancelClose = (_, e) => e.Cancel = true;
                manager.Closing += cancelClose;
                try { SystemCommands.CloseWindowCommand.Execute(null, manager); Pump(120); }
                finally { manager.Closing -= cancelClose; }
                check("cancelled production close preserves the manager as the active input surface", manager.IsVisible && manager.IsEnabled &&
                    GetForegroundWindow() == new WindowInteropHelper(manager).Handle && host.IsEditing && host.IsKeyboardIsolationActive);
            }
            if (completion == "owner-unavailable")
            {
                var child = new Window { Title = "Unavailable owner focus regression", Owner = manager, Width = 240, Height = 120, Content = new TextBlock { Text = "Own child surface" } };
                Invoke("ConfigureAppWindow", child, false); child.Show(); child.Activate(); Pump(100); AttachWindows();
                manager.IsEnabled = false;
                try
                {
                    SystemCommands.CloseWindowCommand.Execute(null, child); Pump(100);
                    check("closing an owned surface skips its disabled owner and returns to the toolbar", !child.IsVisible && GetForegroundWindow() == new WindowInteropHelper(toolbar).Handle &&
                        toolbar.IsVisible && host.IsEditing && host.IsKeyboardIsolationActive);
                }
                finally { manager.IsEnabled = true; if (child.IsVisible) child.Close(); }
            }
            if (completion is not ("direct-close" or "close-cancel" or "owner-unavailable"))
            {
                if (completion == "modal-external")
                {
                    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                    start.ArgumentList.Add("--focus-external-helper"); start.ArgumentList.Add("--focus-helper-directory"); start.ArgumentList.Add(run);
                    external = Process.Start(start) ?? throw new InvalidOperationException("Could not start independent test receiver.");
                    Wait(() => File.Exists(Path.Combine(run, "external-ready.json")), "external receiver readiness");
                    using var ready = JsonDocument.Parse(File.ReadAllText(Path.Combine(run, "external-ready.json")));
                    check("the external foreground receiver is a separate owned test process", ready.RootElement.GetProperty("Process").GetInt32() == external.Id && external.Id != Environment.ProcessId);
                    externalHandle = (nint)ready.RootElement.GetProperty("Handle").GetInt64();
                }
                var buttons = Field<Dictionary<string, Button>>(manager, "commandButtons");
                bool completed = false; Exception? dialogError = null;
                var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
                var deadline = DateTime.UtcNow.AddSeconds(5);
                timer.Tick += (_, _) =>
                {
                    try
                    {
                        AttachWindows();
                        var modal = app.Windows.Cast<Window>().FirstOrDefault(w => !ReferenceEquals(w, manager) && ReferenceEquals(w.Owner, manager) && w.IsVisible);
                        if (modal is null) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Rename did not open its production modal dialog."); return; }
                        timer.Stop(); Snapshot("rename-modal-opened");
                        check("the production modal disables its own manager", !IsWindowEnabled(new WindowInteropHelper(manager).Handle) && host.IsEditing);
                        if (completion == "modal-external")
                        {
                            if (!AllowSetForegroundWindow((uint)external!.Id)) throw new InvalidOperationException("Could not grant foreground permission to the owned external receiver.");
                            File.WriteAllText(Path.Combine(run, "external-activate"), "activate");
                            Wait(() => GetForegroundWindow() == externalHandle, "external receiver foreground");
                            Wait(() => !host.IsEditing, "editing release after external activation");
                            Snapshot("external-foreground-during-modal");
                            InvokeButton(Descendants(modal).OfType<Button>().Single(b => b.IsCancel));
                            completed = true; return;
                        }
                        if (completion == "rename-confirm")
                        {
                            var name = Descendants(modal).OfType<TextBox>().Single(); name.Text = "Renamed focus regression stamp";
                            InvokeButton(Descendants(modal).OfType<Button>().Single(b => b.IsDefault));
                        }
                        else if (completion == "rename-cancel") InvokeButton(Descendants(modal).OfType<Button>().Single(b => b.IsCancel));
                        else if (completion == "rename-escape") PressOwnEscape(modal);
                        else if (completion == "rename-close") modal.Close();
                        else throw new ArgumentException("Unknown focus scenario: " + scenario);
                        completed = true;
                    }
                    catch (Exception error)
                    {
                        dialogError = error; timer.Stop();
                        foreach (var window in app.Windows.Cast<Window>().Where(w => ReferenceEquals(w.Owner, manager)).ToArray()) window.Close();
                    }
                };
                timer.Start();
                try { buttons["Stamp.Rename"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                finally { timer.Stop(); }
                if (dialogError is not null) throw dialogError;
                check("the actual nested rename dialog completed", completed);
                Wait(() => !Field<bool>(manager, "busy"), "rename completion");
                Pump(150); Snapshot("rename-returned");
                if (completion == "modal-external")
                {
                    check("an external activation releases editing and hides the toolbar", !host.IsEditing && !host.IsKeyboardIsolationActive && !toolbar.IsVisible);
                    check("a late modal return never steals the independent external foreground", GetForegroundWindow() == externalHandle);
                    check("external dismissal does not change the document or history", editor.Session.Document.Elements.Count == 0 && editor.Session.UndoCount == 0);
                    continue;
                }
                check("returning from rename preserves the manager and edit lease", manager.IsVisible && manager.IsEnabled && host.IsEditing && host.IsKeyboardIsolationActive);
                if (completion == "rename-confirm") check("rename writes only the fixture stamp", library.Items.Single().Name == "Renamed focus regression stamp");
            }
            // Deliberately do not Activate/Focus anything after close: that would hide the regression.
            SystemCommands.CloseWindowCommand.Execute(null, manager); Pump(350); Snapshot("manager-closed");
            check("closing the real manager after nested editing keeps the toolbar visible and enabled", host.IsEditing && toolbar.IsVisible && toolbar.IsEnabled);
            check("closing the real manager preserves keyboard isolation and releases pointer capture", host.IsKeyboardIsolationActive && GetCapture() == 0);
            check("internal window closure leaves an own enabled foreground", IsOwnForeground() && IsWindowEnabled(GetForegroundWindow()));
            check("window management leaves document selection and history unchanged", editor.Session.Document.Elements.Count == 0 && editor.Session.Selection.Count == 0 && editor.Session.UndoCount == 0);
            }
            if (scenario == "full-matrix") foreach (string colorCase in new[] { "color-secondary-only", "color-canvas", "color-pinned" }) RunColorCase(colorCase);

            void RunColorCase(string colorCase)
            {
                Console.WriteLine("FOCUS_CASE " + colorCase);
                if (Field<bool>(session, "_toolOptionsPinned")) Field<Button>(session, "_toolOptionsPin").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Invoke("SwitchToolState", EditorTool.Rectangle, false);
                Invoke("OpenToolOptions"); Pump(75); AttachWindows();
                var options = Field<Window>(session, "_toolOptionsWindow");
                // Keep this fixture's panels away from its bounded drawing target. This is
                // placement setup only; all subsequent input uses the actual native target.
                options.Left = (tile.Right + 70) * 96d / primary.DpiX;
                options.Top = primary.WorkArea.Top * 96d / primary.DpiY + 80;
                Pump(75);
                var panel = Field<ToolOptionsPanel>(session, "_toolOptionsPanel");
                var color = Field<ColorField>(panel, "foreground");
                var popup = Field<Popup>(color, "popup");
                Field<Button>(color, "button").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(100);
                Snapshot("color-child-opened");
                check("production parameter and color child are simultaneously open", options.IsVisible && popup.IsOpen && host.IsEditing && host.IsKeyboardIsolationActive);
                if (colorCase == "color-secondary-only")
                {
                    check("closing only the color child is a real production operation", color.TryClosePicker()); Pump(100);
                    check("closing only the color child preserves its parent and toolbar", !popup.IsOpen && options.IsVisible && toolbar.IsVisible && host.IsEditing && host.IsKeyboardIsolationActive);
                    return;
                }
                if (colorCase == "color-pinned") Field<Button>(session, "_toolOptionsPin").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Field<TextBox>(Field<ColorPickerPanel>(color, "picker"), "hex").Text = "#336699";
                int beforeUndo = editor.Session.UndoCount, beforeCount = editor.Session.Document.Elements.Count;
                if (colorCase is "color-canvas" or "color-pinned")
                {
                    var target = host.Diagnostics.Single().InputHandle;
                    DrawOwnCanvas(target, tile, beforeCount * 200);
                    Pump(200); Snapshot("canvas-press-closed-color-stack");
                    Console.WriteLine($"FOCUS_CANVAS before={beforeCount}/{beforeUndo} after={editor.Session.Document.Elements.Count}/{editor.Session.UndoCount} transaction={editor.Session.IsTransactionActive} pointers={string.Join(';', pointers.Select(p => p.Kind))}");
                    check("one physical canvas drag dismisses its color child", !popup.IsOpen);
                    check("a physical canvas drag respects the parameter panel pin", options.IsVisible == (colorCase == "color-pinned"));
                    check("the original physical drag reaches the canvas exactly once", editor.Session.Document.Elements.Count == beforeCount + 1 && editor.Session.UndoCount == beforeUndo + 1 && !editor.Session.IsTransactionActive);
                    check("the first canvas object uses the color draft committed by the same dismissal", editor.Session.Document.Elements.Last().Style.Stroke == 0xFF336699);
                    check("canvas collapse preserves the toolbar, edit lease and released capture", toolbar.IsVisible && toolbar.IsEnabled && host.IsEditing && host.IsKeyboardIsolationActive && GetCapture() == 0);
                }
                else if (colorCase == "color-toolbar")
                {
                    var button = Field<Dictionary<EditorTool, Button>>(session, "_toolbarToolButtons")[EditorTool.Line];
                    var point = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
                    var target = new PixelPoint((int)Math.Round(point.X), (int)Math.Round(point.Y));
                    DriveOwnPointer(new WindowInteropHelper(toolbar).Handle, target, target);
                    Pump(150); Snapshot("toolbar-press-closed-color-stack");
                    check("one physical toolbar press closes its previous color child and selects the requested tool", !popup.IsOpen && editor.Tool == EditorTool.Line);
                    check("a closing color draft stays with its original tool rather than changing a newly selected tool", editor.Style.Stroke == ColorLibrary.ForegroundRed);
                    check("a toolbar handoff preserves editing and creates no canvas history", toolbar.IsVisible && toolbar.IsEnabled && host.IsEditing && host.IsKeyboardIsolationActive &&
                        editor.Session.Document.Elements.Count == beforeCount && editor.Session.UndoCount == beforeUndo && GetCapture() == 0);
                }
                else throw new ArgumentException("Unknown color focus scenario: " + colorCase);
            }
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            Snapshot("before-cleanup");
            File.WriteAllText(Path.Combine(run, "result.json"), JsonSerializer.Serialize(new { Scenario = scenario, Profile = profile, Process = Environment.ProcessId, Passed = failure is null, Error = failure?.ToString(), Checks = checks, Pointers = pointers, Observations = observations }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("FOCUS_REPORT " + Path.Combine(run, "result.json"));
            host?.ExitEditing(); session?.Dispose();
            foreach (var window in app.Windows.Cast<Window>().ToArray()) window.Close();
            if (external is not null)
            {
                File.WriteAllText(Path.Combine(run, "external-exit"), "exit");
                Wait(() => external.HasExited, "owned external receiver shutdown"); external.Dispose();
            }
            Pump(50); SynchronizationContext.SetSynchronizationContext(previousContext);
            InputManager.Current.PreProcessInput -= pointerTrace;
            if (originalForeground != 0 && IsWindow(originalForeground)) SetForegroundWindow(originalForeground);
        }

        object? Invoke(string name, params object[] args) => typeof(DesktopSession).GetMethod(name, Private)!.Invoke(session, args);
        void AttachWindows()
        {
            foreach (var window in app.Windows.Cast<Window>()) if (attached.Add(window))
            {
                string title = window.Title;
                window.Activated += (_, _) => Snapshot("Activated:" + title);
                window.Deactivated += (_, _) => Snapshot("Deactivated:" + title);
                window.Closed += (_, _) => Snapshot("Closed:" + title);
                window.IsVisibleChanged += (_, _) => Snapshot("VisibleChanged:" + title);
            }
        }
        void Snapshot(string stage)
        {
            var windows = app.Windows.Cast<Window>().Select(w => new { w.Title, Handle = (long)new WindowInteropHelper(w).Handle, w.IsVisible, w.IsEnabled,
                NativeEnabled = new WindowInteropHelper(w).Handle is var handle && handle != 0 && IsWindowEnabled(handle), w.IsActive, Owner = w.Owner?.Title }).ToArray();
            GetWindowThreadProcessId(GetForegroundWindow(), out uint foregroundProcess);
            string? leaseReason = host is null ? null : NullableField(host, "_keyboardIsolation") is KeyboardInputIsolation isolation ? isolation.LastEndReason?.ToString() : null;
            observations.Add(new { Stage = stage, At = DateTimeOffset.Now, Foreground = (long)GetForegroundWindow(), ForegroundProcess = foregroundProcess, Capture = (long)GetCapture(), Editing = host?.IsEditing, Lease = host?.IsKeyboardIsolationActive, LeaseReason = leaseReason, ToolbarVisible = toolbar?.IsVisible, ToolbarEnabled = toolbar?.IsEnabled,
                ElementCount = observedEditor?.Session.Document.Elements.Count, UndoCount = observedEditor?.Session.UndoCount, Transaction = observedEditor?.Session.IsTransactionActive, Windows = windows });
            Console.WriteLine($"FOCUS stage={stage} editing={host?.IsEditing} lease={host?.IsKeyboardIsolationActive} toolbar={toolbar?.IsVisible}/{toolbar?.IsEnabled} fg={GetForegroundWindow():X} capture={GetCapture():X}");
        }
    }
    // Dedicated child used by the external-foreground scenario. It has no DesktopSession,
    // never opens application data, and accepts requests only through this run's artifact files.
    internal static void RunExternalHelper(string[] args)
    {
        int index = Array.IndexOf(args, "--focus-helper-directory");
        if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Missing helper directory.");
        string directory = Path.GetFullPath(args[index + 1]);
        bool watchdogGate = args.Contains("--focus-helper-watchdog");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var receiverInput = new TextBox { Text = watchdogGate ? "Click here, release all keys/buttons, then wait about 8 seconds." : "Independent test receiver", IsReadOnly = true, Margin = new Thickness(20) };
        var window = new Window { Title = watchdogGate ? "FloatCanvas Watchdog Receiver - click to start" : "FloatCanvas owned external foreground fixture", Width = watchdogGate ? 500 : 280, Height = 110,
            Left = 650, Top = 500, Content = receiverInput,
            ShowActivated = false, ShowInTaskbar = watchdogGate, Topmost = true };
        int f6Down = 0, f6Up = 0;
        void InputResult()
        {
            string temporary = Path.Combine(directory, "external-input.pending");
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { Process = Environment.ProcessId, F6Down = f6Down, F6Up = f6Up }));
            File.Move(temporary, Path.Combine(directory, "external-input.json"), true);
        }
        window.Activated += (_, _) => receiverInput.Focus();
        window.PreviewKeyDown += (_, e) => { if (e.Key == Key.F6) { f6Down++; InputResult(); } };
        window.PreviewKeyUp += (_, e) => { if (e.Key == Key.F6) { f6Up++; InputResult(); } };
        window.Show();
        File.WriteAllText(Path.Combine(directory, "external-ready.json"), JsonSerializer.Serialize(new { Process = Environment.ProcessId, Handle = (long)new WindowInteropHelper(window).Handle }));
        var deadline = DateTime.UtcNow.AddSeconds(watchdogGate ? 75 : 45);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        bool activated = false;
        timer.Tick += (_, _) =>
        {
            if (!activated && File.Exists(Path.Combine(directory, "external-activate")))
            {
                activated = true; SetForegroundWindow(new WindowInteropHelper(window).Handle);
                File.WriteAllText(Path.Combine(directory, "external-activated.json"), JsonSerializer.Serialize(new { Foreground = (long)GetForegroundWindow() }));
            }
            if (File.Exists(Path.Combine(directory, "external-exit")) || DateTime.UtcNow >= deadline) { timer.Stop(); window.Close(); app.Shutdown(); }
        };
        timer.Start(); app.Run();
    }
    private static void DrawOwnCanvas(nint target, PixelBounds bounds, int offsetX)
        => DriveOwnPointer(target, new(bounds.Left + 80 + offsetX, bounds.Top + 100), new(bounds.Left + 220 + offsetX, bounds.Top + 200));
    private static void DriveOwnPointer(nint target, PixelPoint start, PixelPoint end)
    {
        nint previousDpi = SetThreadDpiAwarenessContext(-4);
        GetCursorPos(out var original); bool held = false;
        try
        {
            Move(start.X, start.Y);
            Send(2); held = true; Pump(75);
            Move(end.X, end.Y);
            Send(4); held = false; Pump(75);
        }
        finally
        {
            if (IsOwnForeground())
            {
                if (held) Send(4);
                SetCursorPos(original.X, original.Y);
            }
            if (previousDpi != 0) SetThreadDpiAwarenessContext(previousDpi);
        }
        void Move(int x, int y)
        {
            nint actual = WindowFromPhysicalPoint(new(x, y));
            if (!IsOwnForeground() || actual != target)
            {
                GetWindowRect(target, out var rectangle);
                throw new InvalidOperationException($"Physical regression point is not its own surface; no input injected. point={x},{y} actual={actual:X} expected={target:X} native={rectangle.Left},{rectangle.Top},{rectangle.Right},{rectangle.Bottom}");
            }
            int left = GetSystemMetrics(76), top = GetSystemMetrics(77), width = GetSystemMetrics(78), height = GetSystemMetrics(79);
            Send(0xC001, (int)Math.Round((x - left) * 65535d / (width - 1)), (int)Math.Round((y - top) * 65535d / (height - 1)));
            Pump(75);
        }
        void Send(uint flags, int x = 0, int y = 0)
        {
            if (!IsOwnForeground()) throw new InvalidOperationException("Physical regression lost its foreground; no input injected.");
            nint capture = GetCapture();
            if (capture != 0 && (GetWindowThreadProcessId(capture, out uint process) == 0 || process != Environment.ProcessId))
                throw new InvalidOperationException("Physical regression capture belongs to another process; no input injected.");
            if (SendInput(1, [new() { Type = 0, Data = new() { Mouse = new() { X = x, Y = y, Flags = flags } } }], Marshal.SizeOf<NativeInput>()) != 1)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
    }
    private static void InvokeButton(Button button) => typeof(Button).GetMethod("OnClick", Private)!.Invoke(button, null);
    private static void PressOwnEscape(Window window)
    {
        if (!IsOwnForeground() || GetForegroundWindow() != new WindowInteropHelper(window).Handle)
            throw new InvalidOperationException("Escape regression lost its exact own modal; no input injected.");
        NativeInput[] input = [new() { Type = 1, Data = new() { Keyboard = new() { Key = 0x1B } } },
            new() { Type = 1, Data = new() { Keyboard = new() { Key = 0x1B, Flags = 2 } } }];
        if (SendInput(2, input, Marshal.SizeOf<NativeInput>()) != 2) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    private static object? NullableField(object target, string name) => target.GetType().GetField(name, Private)!.GetValue(target);
    private static T Field<T>(object target, string name) => (T)NullableField(target, name)!;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        if (root is Window window && window.Content is DependencyObject content) { yield return content; foreach (var child in Descendants(content)) yield return child; }
        else foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
    private static void Await(Task task) { Wait(() => task.IsCompleted, "asynchronous fixture setup"); task.GetAwaiter().GetResult(); }
    private static void Wait(Func<bool> done, string operation, int timeoutSeconds = 8)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!done()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException(operation); Pump(25); }
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => frame.Continue = false;
        try { timer.Start(); Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
    }
    private static bool IsOwnForeground() => GetWindowThreadProcessId(GetForegroundWindow(), out uint process) != 0 && process == Environment.ProcessId;
    private static void ActivateOwnWindow(nint window)
    {
        if (!SetForegroundWindow(window) && GetForegroundWindow() != window) throw new InvalidOperationException("Fixture could not acquire its own foreground.");
        SetFocus(window);
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint process);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nuint wparam, nint lparam);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct PixelPoint(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct PixelRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [DllImport("user32.dll")] private static extern nint WindowFromPhysicalPoint(PixelPoint point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PixelPoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out PixelRect rectangle);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] input, int size);
}
