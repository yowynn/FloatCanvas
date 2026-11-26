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
            PixelRect nativeRect = default;
            bool hasNativeRect = false;
            if (sourceWindow is not null)
                hasNativeRect = GetWindowRect(sourceWindow.Handle, out nativeRect);
            Console.WriteLine($"FOCUS_WPF {mouse.RoutedEvent?.Name} {mouse.ChangedButton}/{mouse.ButtonState} original={mouse.OriginalSource?.GetType().Name} target={mouse.MouseDevice.Target?.GetType().Name} source={sourceWindow?.Handle:X} root={sourceWindow?.RootVisual?.GetType().Name} captured={captured?.GetType().Name} captureSource={captureWindow?.Handle:X}/{captureWindow?.RootVisual?.GetType().Name} nativeCapture={GetCapture():X} point={point} handled={mouse.Handled}");
            observations.Add(new { Stage = "WpfPointer", Event = mouse.RoutedEvent?.Name, Original = mouse.OriginalSource?.GetType().Name, Target = mouse.MouseDevice.Target?.GetType().Name,
                SourceHandle = (long)(sourceWindow?.Handle ?? 0), SourceRoot = sourceWindow?.RootVisual?.GetType().Name, Captured = captured?.GetType().Name,
                CaptureHandle = (long)(captureWindow?.Handle ?? 0), CaptureRoot = captureWindow?.RootVisual?.GetType().Name, NativeCapture = (long)GetCapture(), X = point?.X, Y = point?.Y,
                NativeLeft = hasNativeRect ? nativeRect.Left : (int?)null, NativeTop = hasNativeRect ? nativeRect.Top : (int?)null,
                NativeRight = hasNativeRect ? nativeRect.Right : (int?)null, NativeBottom = hasNativeRect ? nativeRect.Bottom : (int?)null,
                WpfLeft = sourceWindow?.RootVisual is Window sourceRoot ? sourceRoot.Left : (double?)null,
                WpfTop = sourceWindow?.RootVisual is Window sourceTop ? sourceTop.Top : (double?)null });
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
            if (scenario == "desktop-fallback-selection" || Environment.GetEnvironmentVariable("FLOATCANVAS_FOCUS_BACKEND") == "enhanced")
                host.EnableSystemInput();
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
            if (scenario is not ("desktop-fallback-selection" or "desktop-keyboard-toggle"))
            {
                check("production focus fixture enters its own bounded canvas", (bool)Invoke("SetEditing", true, false)!);
                Wait(() => !host.IsEditing || host.IsKeyboardIsolationActive, "initial keyboard isolation lease");
            }
            double toolbarLeftPixels = Math.Max(primary.WorkArea.Left, primary.WorkArea.Right - 1500);
            toolbar.Left = toolbarLeftPixels * 96d / primary.DpiX;
            toolbar.Top = (primary.WorkArea.Top + 20) * 96d / primary.DpiY;
            Pump(50); Snapshot("editing-entered");
            if (scenario is not ("desktop-fallback-selection" or "desktop-keyboard-toggle"))
                check("production focus fixture retains the actual keyboard isolation lease", host.IsEditing && host.IsKeyboardIsolationActive);
            if (scenario == "desktop-keyboard-toggle")
            {
                const ushort control = 0xA2, fixtureToggle = 0x87, backtick = 0xC0;
                host.RegisterHotkeys(new(2, fixtureToggle), default);
                Invoke("SwitchToolState", EditorTool.Select, false);
                var origin = editor.CanvasOrigin;
                var keyboard = new CanvasElement { Kind = ElementKind.Presentation, Width = 420, Height = 130,
                    Transform = Affine2D.Translation(tile.Left + 30 - origin.X, tile.Top + 60 - origin.Y),
                    Presentation = new() { DisplayStyle = PresentationDisplayStyle.Keyboard } };
                var rectangle = new CanvasElement { Kind = ElementKind.Rectangle, Width = 40, Height = 40,
                    Transform = Affine2D.Translation(tile.Left + 160 - origin.X, tile.Top + 240 - origin.Y),
                    Style = new() { Background = 0xFFFF3300 } };
                editor.Session.Add(keyboard); editor.Session.Add(rectangle); editor.Session.SetSelection([rectangle.Id]);
                Invoke("UpdatePresentationRuntime");
                var runtime = Field<PresentationRuntimeBroker>(session, "_presentationRuntime");
                Wait(() => runtime.ObservingKeyboard, "live keyboard observer");
                var isolation = Field<KeyboardInputIsolation>(host, "_keyboardIsolation");
                var hook = typeof(KeyboardInputIsolation).GetMethod("OnKeyboard", Private)!;
                var chord = new ushort[] { control, fixtureToggle };
                try
                {
                    SendKeys(chord, up: false);
                    Wait(() => host.IsEditing && host.IsKeyboardIsolationActive, "registered desktop keyboard hotkey");
                    check("Ctrl from the held entry chord hides the frame", !editor.ShouldShowSelection(host.InteractionModifiers));
                    var target = host.Diagnostics.Single().InputHandle;
                    var edge = new PixelPoint(tile.Left + 160, tile.Top + 250);
                    ReplayHook(backtick, 0x29, up: false);
                    Pump(90);
                    check("the keyboard layer sees the held hotkey while editing", Keys(runtime.Frame, control, backtick));
                    var whileHeld = ReadScreenEdge(target, edge);
                    // Deliberately remove only this fixture's hook. Windows can deliver
                    // a native edge even if the original low-level callback is missing.
                    var installedHook = typeof(KeyboardInputIsolation).GetField("hook", Private)!;
                    nint handle = (nint)installedHook.GetValue(isolation)!;
                    if (handle == 0 || !UnhookWindowsHookEx(handle)) throw new InvalidOperationException("Could not detach the fixture keyboard hook.");
                    installedHook.SetValue(isolation, (nint)0);
                    SendKeys(chord.Reverse(), up: true);
                    ObserveRaw(backtick, 0x29, up: true);
                    Pump(250);
                    check("the native hotkey release clears the editing modifier and restores selection",
                        host.InteractionModifiers == OverlayModifiers.None && editor.ShouldShowSelection(host.InteractionModifiers) &&
                        editor.SelectionScreenCorners.Count == 4 && CountNewBluePixels(whileHeld, ReadScreenEdge(target, edge)) >= 8);
                    check("the live keyboard clears the complete hotkey chord", runtime.Frame.PressedKeys.Count == 0 && Presented()?.PressedKeys.Count == 0);
                    foreach (var (key, scan) in new[] { (0x51, 0x10), (0x55, 0x16) })
                    {
                        ObserveRaw(key, scan, up: false);
                        Wait(() => Keys(Presented(), key), "raw key is visible in the keyboard demonstration");
                        ObserveRaw(key, scan, up: true);
                        Wait(() => Presented()?.PressedKeys.Count == 0, "raw key is released in the keyboard demonstration");
                    }
                    check("device-backed Q and U enter and leave live keyboard frames without a hook or window message", Presented()?.PressedKeys.Count == 0);
                    SendKeys([0xA1], up: false);
                    Pump(100);
                    check("the native keyboard route observes a new right Shift with the hook missing",
                        Keys(runtime.Frame, 0xA1) && Keys(Presented(), 0xA1));
                    SendKeys([0xA1], up: true);
                    Pump(100);
                    check("the native keyboard route clears that right Shift on release", Presented()?.PressedKeys.Count == 0);

                    int shiftDowns = 0;
                    host.PresentationInputObserved += batch => shiftDowns += batch.Changes.Count(change =>
                        change.Kind == PresentationObservedKind.KeyDown && change.VirtualKey == 0xA1);
                    uint time = unchecked((uint)Environment.TickCount64);
                    ReplayHook(0xA1, 0x36, up: false, flags: 0x10, time: time);
                    SendKeys([0xA1], up: false, time: time);
                    Pump(120);
                    check("one edge seen by both input routes is published only once", shiftDowns == 1 && Keys(Presented(), 0xA1));
                    ReplayHook(0xA1, 0x36, up: true, flags: 0x10);
                    SendKeys([0xA1], up: true);
                    ReplayHook(0x48, 0x23, up: false);
                    Pump(250);
                    check("the keyboard demonstration continues to submit ordinary hook input in editing mode", Keys(runtime.Frame, 0x48) && Keys(Presented(), 0x48));
                    ReplayHook(0x48, 0x23, up: true);
                    Pump(180);
                    check("the submitted keyboard frame clears a regular key on release", Presented()?.PressedKeys.Count == 0);
                }
                finally
                {
                    if (IsOwnForeground()) SendKeys(chord.Reverse().Prepend((ushort)0xA1).Where(key => GetAsyncKeyState(key) < 0), up: true);
                }
                return;

                PresentationFrame? Presented() => Field<PresentationFrame?>(session, "_presentedPresentationFrame");
                void ObserveRaw(int key, int scan, bool up) => host!.ObserveOrdinaryRawPresentationKeys(new(runtime.InputEpoch,
                    [new(up ? PresentationObservedKind.RawKeyUp : PresentationObservedKind.RawKeyDown,
                        Environment.TickCount64, VirtualKey: key, ScanCode: scan, InputOwner: PresentationInputOwner.FloatCanvas)], null, false));
                static bool Keys(PresentationFrame? frame, params int[] keys) => frame is not null &&
                    frame.PressedKeys.Count == keys.Length && frame.PressedKeys.Select(key => key.VirtualKey).ToHashSet().SetEquals(keys);

                void ReplayHook(uint key, uint scan, bool up, uint flags = 0, uint? time = null)
                {
                    if (!IsOwnForeground()) throw new InvalidOperationException("The key replay fixture lost its own foreground.");
                    // Windows intentionally labels SendInput as injected. Exercise ordinary
                    // physical-key decoding by replaying hook metadata only inside this process.
                    var data = new HookKeyboardEvent { VirtualKey = key, Scan = scan, Flags = flags | (up ? 0x80u : 0),
                        Time = time ?? unchecked((uint)Environment.TickCount64) };
                    nint packet = Marshal.AllocHGlobal(Marshal.SizeOf<HookKeyboardEvent>());
                    try { Marshal.StructureToPtr(data, packet, false); hook.Invoke(isolation, [0, up ? (nuint)0x101 : 0x100, packet]); }
                    finally { Marshal.FreeHGlobal(packet); }
                }
                void SendKeys(IEnumerable<ushort> keys, bool up, uint time = 0)
                {
                    if (!IsOwnForeground() || !up && keys.Any(key => GetAsyncKeyState(key) < 0))
                        throw new InvalidOperationException("The native hotkey fixture does not own a clean keyboard target.");
                    NativeInput[] inputs = keys.Select(key => new NativeInput { Type = 1,
                        Data = new() { Keyboard = key == 0xA1
                            ? new() { Scan = 0x36, Flags = (up ? 2u : 0u) | 8u, Time = time }
                            : new() { Key = key, Flags = up ? 2u : 0u, Time = time } } }).ToArray();
                    if (inputs.Length != 0)
                    {
                        var sent = Task.Run(() => (Count: SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()), Error: Marshal.GetLastWin32Error()));
                        Await(sent);
                        if (sent.Result.Count != inputs.Length) throw new System.ComponentModel.Win32Exception(sent.Result.Error);
                    }
                }
            }
            if (scenario is "desktop-selection" or "desktop-fallback-selection")
            {
                Invoke("SwitchToolState", EditorTool.Select, false);
                var local = new PointD(tile.Left + 160, tile.Top + 190) - editor.CanvasOrigin;
                CanvasElement Rectangle(double x) => new() { Kind = ElementKind.Rectangle, Width = 40, Height = 40,
                    Transform = Affine2D.Translation(local.X + x, local.Y), Style = new() { Background = 0xFFFF3300 } };
                var first = Rectangle(0); var second = Rectangle(85);
                editor.Session.Add(first); editor.Session.Add(second); editor.Session.SetSelection([]);
                if (scenario == "desktop-fallback-selection")
                {
                    var fallback = session.PrepareDesktopFallbackAsync();
                    Await(fallback);
                    var saved = fallback.GetAwaiter().GetResult();
                    session.Dispose();
                    host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null,
                        [OverlayMonitorScope.PrimaryScreen, provider, false], null)!;
                    session = new DesktopSession(app, host, fallbackState: saved);
                    Field<System.Windows.Forms.NotifyIcon>(session, "_tray").Visible = false;
                    Invoke("RestoreFallbackWorkspace", saved);
                    Invoke("OnMonitorsChanged");
                    editor = Field<EditorController>(session, "_editor");
                    observedEditor = editor;
                    toolbar = Field<Window>(session, "_toolbar");
                    AttachWindows();
                    toolbar.Show(); Pump(60);
                    ActivateOwnWindow(new WindowInteropHelper(toolbar).Handle);
                    check("ordinary editing enters again after switching backends", (bool)Invoke("SetEditing", true, false)!);
                    toolbar.Top = (primary.WorkArea.Top + 20) * 96d / primary.DpiY;
                }
                check("selection fixture uses the ordinary desktop backend", !host.UsesSystemInput);
                check("ordinary selection has the restored visible canvas", host.IsVisible);
                Pump(650);
                var target = host.Diagnostics.Single().InputHandle;
                var singleEdge = new PixelPoint(tile.Left + 160, tile.Top + 200);
                var groupEdge = new PixelPoint(tile.Left + 285, tile.Top + 200);
                var marqueeEdge = new PixelPoint(tile.Left + 130, tile.Top + 200);
                var singleBefore = ReadScreenEdge(target, singleEdge);
                var groupBefore = ReadScreenEdge(target, groupEdge);
                var marqueeBefore = ReadScreenEdge(target, marqueeEdge);
                host.Pointer += pointer => Console.WriteLine($"DESKTOP_SELECTION_POINTER {pointer.Kind} {pointer.Button} {pointer.Position}");
                Console.WriteLine($"DESKTOP_SELECTION before origin={editor.CanvasOrigin} viewport={editor.CanvasViewport} local={local}" +
                    $" suspended={Field<bool>(editor, "presentedPickingSuspended")} revision={editor.Session.PreviewRevision}/{Field<long>(editor, "presentedContentRevision")}" +
                    $" hasEditable={Field<bool>(session, "_hasEditableCanvasOccurrence")} rendered={Field<SceneDocument?>(editor, "presentedScene")?.Elements.Count}");
                DriveOwnPointer(target, new(tile.Left + 180, tile.Top + 210), new(tile.Left + 180, tile.Top + 210));
                Pump(250);
                Console.WriteLine($"DESKTOP_SELECTION after count={editor.Session.Selection.Count} corners={editor.SelectionScreenCorners.Count}" +
                    $" tool={editor.Tool} suspended={Field<bool>(editor, "presentedPickingSuspended")} revision={editor.Session.PreviewRevision}/{Field<long>(editor, "presentedContentRevision")}");
                check("ordinary canvas click selects one object and exposes its frame",
                    editor.Session.Selection.SetEquals([first.Id]) && editor.SelectionScreenCorners.Count == 4);
                CheckVisibleEdge("single-selection frame", singleEdge, singleBefore);
                SendShift(true);
                try
                {
                    DriveOwnPointer(target, new(tile.Left + 265, tile.Top + 210), new(tile.Left + 265, tile.Top + 210));
                }
                finally { SendShift(false); }
                Pump(250);
                check("ordinary canvas Shift click adds a second selection", editor.Session.Selection.SetEquals([first.Id, second.Id]));
                CheckVisibleEdge("multi-selection frame", groupEdge, groupBefore);
                editor.Session.SetSelection([]);
                bool marqueeSeen = false;
                host.Pointer += args => { if (args.Kind == OverlayPointerKind.Move && editor.MarqueeScreenPolygon is { Count: 4 }) marqueeSeen = true; };
                DriveOwnPointer(target, new(tile.Left + 130, tile.Top + 165), new(tile.Left + 300, tile.Top + 255),
                    beforeRelease: () => CheckVisibleEdge("drag marquee", marqueeEdge, marqueeBefore));
                Pump(350);
                check("ordinary canvas drag displays a marquee while held", marqueeSeen);
                check("marquee selects both ordinary-canvas objects", editor.Session.Selection.SetEquals([first.Id, second.Id]));
                return;

                void CheckVisibleEdge(string label, PixelPoint edge, uint[] before)
                {
                    int changedBlue = CountNewBluePixels(before, ReadScreenEdge(target, edge));
                    Console.WriteLine($"DESKTOP_SELECTION_VISIBLE {label} blue={changedBlue}");
                    check($"ordinary canvas paints the {label} on the desktop", changedBlue >= 8);
                }

                void SendShift(bool held)
                {
                    if (held && GetForegroundWindow() != target)
                        throw new InvalidOperationException("Canvas is not the owned foreground; no modifier pressed.");
                    if (SendInput(1, [new() { Type = 1, Data = new() { Keyboard = new() { Key = 0xA0, Flags = held ? 0u : 2u } } }],
                        Marshal.SizeOf<NativeInput>()) != 1)
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    Pump(40);
                }
            }
            if (scenario is "toolbar-delete-cancel" or "toolbar-delete-confirm")
            {
                var shelf = Field<StampShelfControl>(session, "_stampShelf");
                var stamp = library.Items.Single();
                Await(library.PinAsync(0, stamp.Id));
                Pump(100);
                Invoke("RaiseVisibleToolWindows");
                Pump(50);
                var slot = ((StackPanel)shelf.Content).Children.OfType<Button>().ElementAt(0);
                var slotCenter = slot.PointToScreen(new Point(slot.ActualWidth / 2, slot.ActualHeight / 2));
                DriveOwnPointer(new WindowInteropHelper(toolbar).Handle,
                    new((int)Math.Round(slotCenter.X), (int)Math.Round(slotCenter.Y)),
                    new((int)Math.Round(slotCenter.X), (int)Math.Round(slotCenter.Y)), right: true);
                Pump(150);
                var menu = slot.ContextMenu as OverlayContextMenu ??
                    throw new InvalidOperationException("Toolbar stamp context menu did not open.");
                var delete = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "Stamp.Delete"));
                var deleteSource = PresentationSource.FromVisual(delete) as HwndSource ??
                    throw new InvalidOperationException("Toolbar stamp context menu has no native presentation source.");
                var deleteCenter = delete.PointToScreen(new Point(delete.ActualWidth / 2, delete.ActualHeight / 2));
                string response = scenario.EndsWith("cancel", StringComparison.Ordinal) ? "Stamp.Cancel" : "Stamp.Confirm";
                bool clicked = false;
                var deadline = DateTime.UtcNow.AddSeconds(5);
                var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
                timer.Tick += (_, _) =>
                {
                    var dialog = app.Windows.Cast<Window>().FirstOrDefault(window =>
                        !ReferenceEquals(window, toolbar) && window.IsVisible && ReferenceEquals(window.Owner, toolbar));
                    if (dialog is null)
                    {
                        if (DateTime.UtcNow >= deadline) throw new TimeoutException("Toolbar stamp delete confirmation did not open.");
                        return;
                    }
                    if (WindowRevealGate.IsPending(dialog)) return;
                    var button = Descendants(dialog).OfType<Button>().Single(control => Equals(control.Content, L.Get(response)));
                    if (button.ActualWidth <= 0 || button.ActualHeight <= 0) return;
                    timer.Stop();
                    Snapshot("toolbar-delete-confirmation-opened");
                    var center = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
                    DriveOwnPointer(new WindowInteropHelper(dialog).Handle,
                        new((int)Math.Round(center.X), (int)Math.Round(center.Y)),
                        new((int)Math.Round(center.X), (int)Math.Round(center.Y)));
                    clicked = true;
                };
                timer.Start();
                try
                {
                    DriveOwnPointer(deleteSource.Handle,
                        new((int)Math.Round(deleteCenter.X), (int)Math.Round(deleteCenter.Y)),
                        new((int)Math.Round(deleteCenter.X), (int)Math.Round(deleteCenter.Y)));
                }
                finally { timer.Stop(); }
                Pump(250);
                Snapshot("toolbar-delete-returned");
                check("a physical toolbar stamp delete confirmation returns to the editing session",
                    clicked && host.IsEditing && host.IsKeyboardIsolationActive && toolbar.IsVisible && toolbar.IsEnabled);
                check("toolbar stamp delete confirmation leaves an own enabled foreground",
                    IsOwnForeground() && IsWindowEnabled(GetForegroundWindow()));
                return;
            }
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
                var popup = Field<PopupHandle>(color, "popupDisplay");
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
            ShowActivated = false, ShowInTaskbar = watchdogGate, Topmost = !args.Contains("--focus-helper-ordinary") };
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
    private static uint[] ReadScreenEdge(nint target, PixelPoint top)
    {
        const int width = 3;
        const int height = 20;
        int left = top.X - 1;
        var colors = new uint[width * height];
        for (int i = 0; i < colors.Length; i++)
        {
            if (WindowFromPhysicalPoint(new(left + i % width, top.Y + i / width)) != target)
                throw new InvalidOperationException("Selection frame sample is outside the owned test canvas.");
        }
        using var bitmap = new System.Drawing.Bitmap(width, height);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(left, top.Y, 0, 0, bitmap.Size);
        for (int i = 0; i < colors.Length; i++)
            colors[i] = unchecked((uint)bitmap.GetPixel(i % width, i / width).ToArgb());
        return colors;
    }
    private static int CountNewBluePixels(uint[] before, uint[] after) => before.Zip(after).Count(pair =>
    {
        uint color = pair.Second;
        int red = (int)(color >> 16 & 255), green = (int)(color >> 8 & 255), blue = (int)(color & 255);
        return pair.First != color && blue >= 150 && blue > red + 48 && blue > green + 32;
    });
    internal static void DriveOwnPointer(nint target, PixelPoint start, PixelPoint end, bool right = false, bool allowDelegatedInput = false, Action? beforeRelease = null)
    {
        nint previousDpi = SetThreadDpiAwarenessContext(-4);
        GetCursorPos(out var original); bool held = false;
        try
        {
            Move(start.X, start.Y);
            if (GetCursorPos(out var pressedAt) && !MatchesClickPoint(pressedAt, start))
                throw new InvalidOperationException($"Pointer moved before the test press. Expected {start}, actual {pressedAt}.");
            Send(right ? 8u : 2u); held = true; Pump(75);
            if (end != start) Move(end.X, end.Y);
            else if (GetCursorPos(out var releasedAt) && !MatchesClickPoint(releasedAt, end))
                throw new InvalidOperationException($"Pointer moved before the test release. Expected {end}, actual {releasedAt}.");
            beforeRelease?.Invoke();
            Send(right ? 16u : 4u); held = false; Pump(75);
        }
        finally
        {
            if (allowDelegatedInput || IsOwnForeground())
            {
                if (held) Send(right ? 16u : 4u);
                SetCursorPos(original.X, original.Y);
            }
            if (previousDpi != 0) SetThreadDpiAwarenessContext(previousDpi);
        }
        // Normalized SendInput coordinates can land one pixel off after rounding.
        static bool MatchesClickPoint(PixelPoint actual, PixelPoint expected) =>
            Math.Abs(actual.X - expected.X) <= 1 && Math.Abs(actual.Y - expected.Y) <= 1;
        void Move(int x, int y)
        {
            nint actual = WindowFromPhysicalPoint(new(x, y));
            if ((!allowDelegatedInput && !IsOwnForeground()) || actual != target)
            {
                GetWindowRect(target, out var rectangle);
                var title = new System.Text.StringBuilder(256);
                _ = GetWindowText(actual, title, title.Capacity);
                uint actualProcess = 0;
                _ = GetWindowThreadProcessId(actual, out actualProcess);
                throw new InvalidOperationException($"Physical regression point is not its own surface; no input injected. point={x},{y} actual={actual:X} expected={target:X} native={rectangle.Left},{rectangle.Top},{rectangle.Right},{rectangle.Bottom} actualTitle={title} actualProcess={actualProcess}");
            }
            int left = GetSystemMetrics(76), top = GetSystemMetrics(77), width = GetSystemMetrics(78), height = GetSystemMetrics(79);
            Send(0xC001, (int)Math.Round((x - left) * 65535d / (width - 1)), (int)Math.Round((y - top) * 65535d / (height - 1)));
            Pump(75);
        }
        void Send(uint flags, int x = 0, int y = 0)
        {
            if (!allowDelegatedInput && !IsOwnForeground()) throw new InvalidOperationException("Physical regression lost its foreground; no input injected.");
            nint capture = GetCapture();
            if (capture != 0 && (GetWindowThreadProcessId(capture, out uint process) == 0 || process != Environment.ProcessId))
                throw new InvalidOperationException("Physical regression capture belongs to another process; no input injected.");
            if (SendInput(1, [new() { Type = 0, Data = new() { Mouse = new() { X = x, Y = y, Flags = flags } } }], Marshal.SizeOf<NativeInput>()) != 1)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
    }
    internal static nint WindowAtPhysicalPoint(int x, int y) => WindowFromPhysicalPoint(new(x, y));
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
        nint foreground = GetForegroundWindow();
        if (foreground != window)
        {
            uint currentThread = GetCurrentThreadId();
            uint foregroundThread = foreground == 0 ? 0 : GetWindowThreadProcessId(foreground, out _);
            bool attached = foregroundThread != 0 && foregroundThread != currentThread &&
                AttachThreadInput(currentThread, foregroundThread, true);
            try
            {
                SetForegroundWindow(window);
                SetFocus(window);
            }
            finally
            {
                if (attached) AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
        if (GetForegroundWindow() != window)
            throw new InvalidOperationException("Fixture could not acquire its own foreground.");
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, System.Text.StringBuilder text, int maximum);
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool value);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint process);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nuint wparam, nint lparam);
    [StructLayout(LayoutKind.Sequential)] internal readonly record struct PixelPoint(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct PixelRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct HookKeyboardEvent { public uint VirtualKey, Scan, Flags, Time; public nuint Extra; }
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint WindowFromPhysicalPoint(PixelPoint point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PixelPoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out PixelRect rectangle);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] input, int size);
}
