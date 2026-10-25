using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;

/// <summary>Visible production canvas for manual/native input acceptance. Only this process,
/// its bounded native windows, its white backdrop and its independent profile are owned here.</summary>
internal static class UnifiedPreviewVisualFixture
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        if (Application.Current is not null) throw new InvalidOperationException("Run the visual fixture in a fresh process.");
        string artifacts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "artifacts");
        string output = Path.GetFullPath(Environment.GetEnvironmentVariable("FLOATCANVAS_PREVIEW_VISUAL_OUTPUT") ??
            Path.Combine(artifacts, "FloatCanvas-preview-visual-20261025", $"{Environment.ProcessId}-{Guid.NewGuid():N}"));
        if (!output.StartsWith(Path.GetFullPath(artifacts) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetPathRoot(output), @"C:\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The isolated visual fixture output must stay inside the C: Codex artifacts directory.");
        Directory.CreateDirectory(output);
        string profile = Path.Combine(output, "profile");
        ApplicationPaths.Configure(["--profile", profile, "--primary-screen"]);
        var toggle = new HotkeyBinding(7, 0x7A); var hide = new HotkeyBinding(7, 0x7B);
        new UserSettings { Language = "en", Theme = "light", PreferGpuAcceleration = true, OverlayMode = "desktop",
            ShowFloatingLogo = false, Toggle = toggle, Hide = hide }.Save();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var oldContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        nint originalForeground = GetForegroundWindow();
        DesktopSession? session = null; OverlayHost? host = null; Window? backdrop = null;
        var timer = new DispatcherTimer(DispatcherPriority.Input, app.Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        DateTimeOffset started = DateTimeOffset.UtcNow, deadline = started.AddSeconds(180);
        Exception? fatal = null;
        long down = 0, moves = 0, up = 0, canceled = 0, submissions = 0, submittedContent = 0, submittedInput = 0, submittedControls = 0;
        bool dirty = true, ready = false, timedOut = false;
        int enterClicks = 0;
        string exitReason = "running";
        nint inputHandle = 0, displayHandle = 0, backdropHandle = 0, toolbarHandle = 0;
        PixelBounds tile = default;
        EditorController? editor = null; CanvasPreviewPump? preview = null; Guid rectangleId = default;
        Button? enterButton = null;
        app.DispatcherUnhandledException += (_, args) =>
        {
            fatal = args.Exception; args.Handled = true; exitReason = "dispatcher-error";
            app.Shutdown(1);
        };
        try
        {
            var primary = ((IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, null)!).First(monitor => monitor.IsPrimary);
            int width = Math.Min(800, Math.Max(320, primary.WorkArea.Width - 80));
            int height = Math.Min(500, Math.Max(240, primary.WorkArea.Height - 200));
            tile = new(primary.WorkArea.Left + Math.Min(160, Math.Max(20, (primary.WorkArea.Width - width) / 3)),
                primary.WorkArea.Top + Math.Min(220, Math.Max(50, (primary.WorkArea.Height - height) / 3)), width, height);
            backdrop = new Window
            {
                Title = "FloatCanvas Preview Backdrop", Style = null, Background = Brushes.White,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = true,
                ShowActivated = true, Topmost = true,
                Left = tile.Left * 96d / primary.DpiX, Top = tile.Top * 96d / primary.DpiY,
                Width = tile.Width * 96d / primary.DpiX, Height = tile.Height * 96d / primary.DpiY
            };
            enterButton = new Button { Content = "Enter editing", Style = null, Width = 140, Height = 32,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(12), Background = Brushes.WhiteSmoke, Foreground = Brushes.Black };
            System.Windows.Automation.AutomationProperties.SetName(enterButton, "Enter editing");
            backdrop.Content = enterButton;
            app.MainWindow = backdrop; backdrop.Show(); backdrop.UpdateLayout();
            backdropHandle = new WindowInteropHelper(backdrop).Handle;
            if (!SetWindowPos(backdropHandle, new nint(-1), tile.Left, tile.Top, tile.Width, tile.Height, 0x40))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var monitor = primary with { Bounds = tile, WorkArea = tile };
            host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null,
                [OverlayMonitorScope.PrimaryScreen, (Func<IReadOnlyList<MonitorInfo>>)(() => [monitor]), false], null)!;
            session = new DesktopSession(app, host);
            Field<System.Windows.Forms.NotifyIcon>(session, "_tray").Visible = false;
            // No global registration is invoked. These non-default bindings identify only
            // this fixture's private input lease and its own UI hints.
            typeof(OverlayHost).GetField("_toggleHotkey", Private)!.SetValue(host, (HotkeyBinding?)toggle);
            typeof(OverlayHost).GetField("_hideHotkey", Private)!.SetValue(host, (HotkeyBinding?)hide);
            var toolbar = Field<Window>(session, "_toolbar"); toolbar.Title = "FloatCanvas Preview Toolbar";
            toolbar.ShowActivated = false;
            // Register the real, discoverable fixture window as an application window.
            // Its root/taskbar identity stays visible while the editing tools are hidden.
            Field<SessionWindowCoordinator>(session, "_windowCoordinator").Register(backdrop);
            backdrop.PreviewKeyDown += (_, args) =>
            {
                if (!host.IsEditing || args.Handled || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
                int key = KeyInterop.VirtualKeyFromKey(args.Key == Key.System ? args.SystemKey : args.Key);
                if (key is not (0x5A or 0x59) || !EditorShortcutPolicy.ShouldRoute(key, Keyboard.Modifiers, args.OriginalSource as DependencyObject)) return;
                args.Handled = true;
                if (!args.IsRepeat) typeof(DesktopSession).GetMethod("OnKey", Private)!.Invoke(session, [key]);
            };
            enterButton.Click += (_, _) =>
            {
                enterClicks++;
                bool entered = (bool)typeof(DesktopSession).GetMethod("SetEditing", Private)!.Invoke(session, [true, false])!;
                Console.WriteLine($"UNIFIED_PREVIEW_VISUAL_ENTER accepted={entered} clicks={enterClicks}");
                KeepBackdropBelowCanvas(); dirty = true;
            };
            editor = Field<EditorController>(session, "_editor"); preview = Field<CanvasPreviewPump>(session, "_canvasPreview");
            preview.Failed += error => { fatal = error; exitReason = "preview-error"; app.Shutdown(1); };
            editor.Tool = EditorTool.Select;
            var rectangle = new CanvasElement { Kind = ElementKind.Rectangle, Width = 120, Height = 80,
                Transform = Affine2D.Translation(tile.Left + 100, tile.Top + 100),
                Style = new() { Fill = 0xFFFF0000, StrokeWidth = 0 } };
            rectangleId = rectangle.Id;
            editor.Session.Execute("visual preview rectangle", document => document.Elements = [rectangle]);
            editor.Session.SelectAll();
            editor.Changed += () => dirty = true;
            preview.FrameReady += () => dirty = true;
            host.Pointer += pointer =>
            {
                if (pointer.Kind == OverlayPointerKind.Down) down++;
                else if (pointer.Kind == OverlayPointerKind.Move) moves++;
                else if (pointer.Kind == OverlayPointerKind.Up) up++;
                else if (pointer.Kind == OverlayPointerKind.Cancel) canceled++;
                dirty = true;
            };
            host.FrameSubmitted += frame =>
            {
                if (!frame.Succeeded || frame.Outputs.Count == 0) return;
                submissions++; submittedContent = frame.ContentSequence; submittedInput = frame.InputSequence;
                submittedControls = frame.ControlsSequence; dirty = true;
            };
            host.SetVisible(true);
            bool entered = false;
            ActivateOwned(backdropHandle, () => entered = (bool)typeof(DesktopSession).GetMethod("SetEditing", Private)!
                .Invoke(session, [true, false])!);
            check("the visible preview fixture owns a real bounded native editing lease", entered && host.IsEditing && host.IsKeyboardIsolationActive);
            var windows = host.Diagnostics.Single(); inputHandle = windows.InputHandle; displayHandle = windows.DisplayHandle;
            if (!SetWindowText(inputHandle, "FloatCanvas Preview Canvas") || !SetWindowText(displayHandle, "FloatCanvas Preview Display"))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            toolbar.UpdateLayout(); toolbarHandle = new WindowInteropHelper(toolbar).Handle;
            int toolbarY = Math.Min(tile.Bottom + 20, primary.WorkArea.Bottom - 100);
            SetWindowPos(toolbarHandle, new nint(-1), tile.Left, toolbarY, 0, 0, 0x11);
            // The opaque white test window is a genuine backdrop, below both production canvas HWNDs.
            SetWindowPos(backdropHandle, displayHandle, tile.Left, tile.Top, tile.Width, tile.Height, 0x10);
            host.InputActivated += KeepBackdropBelowCanvas;
            timer.Tick += (_, _) =>
            {
                if (File.Exists(Path.Combine(output, "stop"))) { exitReason = "stop-file"; app.Shutdown(); return; }
                if (DateTimeOffset.UtcNow >= deadline) { timedOut = true; exitReason = "timeout"; app.Shutdown(); return; }
                // DesktopSession raises its application's WPF windows during entry. This
                // fixture-only WPF window remains a background, rather than a tool panel.
                KeepBackdropBelowCanvas();
                bool canEnter = !host.IsEditing;
                if (enterButton.IsEnabled != canEnter) { enterButton.IsEnabled = canEnter; dirty = true; }
                bool pending = Field<DispatcherOperation?>(session, "_renderOperation")?.Status == DispatcherOperationStatus.Pending ||
                    Field<DispatcherOperation?>(session, "_presentOperation")?.Status == DispatcherOperationStatus.Pending;
                if (!ready && !preview.Busy && !pending && submittedContent > 0)
                {
                    ready = true; WriteState("ready.json");
                    Console.WriteLine($"UNIFIED_PREVIEW_VISUAL_READY directory={output} input={inputHandle:X} display={displayHandle:X} toolbar={toolbarHandle:X}");
                    Console.Out.Flush();
                }
                if (dirty) { dirty = false; WriteState("state.json"); }
            };
            timer.Start(); app.Run();
            if (fatal is not null) throw new InvalidOperationException("The visual preview fixture failed on its UI dispatcher.", fatal);
        }
        finally
        {
            timer.Stop();
            bool wasOwnForeground = GetWindowThreadProcessId(GetForegroundWindow(), out uint foregroundProcess) != 0 && foregroundProcess == Environment.ProcessId;
            try { WriteState("final-state.json"); }
            finally
            {
                try { session?.Dispose(); }
                finally
                {
                    host?.Dispose(); backdrop?.Close();
                    SynchronizationContext.SetSynchronizationContext(oldContext);
                    if (wasOwnForeground && originalForeground != 0 && IsWindow(originalForeground)) SetForegroundWindow(originalForeground);
                }
            }
        }

        void KeepBackdropBelowCanvas()
        {
            if (host?.IsEditing == true && backdropHandle != 0 && displayHandle != 0)
                SetWindowPos(backdropHandle, displayHandle, 0, 0, 0, 0, 0x13);
        }

        void WriteState(string file)
        {
            var document = editor?.Session.Document;
            var rectangle = document?.Elements.FirstOrDefault(element => element.Id == rectangleId);
            var state = new
            {
                SchemaVersion = 1, ProcessId = Environment.ProcessId, Directory = output, ProfileDirectory = profile,
                StartedAt = started, Deadline = deadline, ObservedAt = DateTimeOffset.UtcNow, Ready = ready, ExitReason = exitReason, TimedOut = timedOut,
                InputTitle = "FloatCanvas Preview Canvas", DisplayTitle = "FloatCanvas Preview Display",
                BackdropTitle = "FloatCanvas Preview Backdrop", ToolbarTitle = "FloatCanvas Preview Toolbar",
                InputHandle = inputHandle.ToInt64(), DisplayHandle = displayHandle.ToInt64(), BackdropHandle = backdropHandle.ToInt64(), ToolbarHandle = toolbarHandle.ToInt64(),
                CanvasBounds = tile, RectangleId = rectangleId, Rectangle = rectangle is null ? null : new
                {
                    rectangle.Width, rectangle.Height, rectangle.Transform, rectangle.Style.Fill,
                    ScreenBounds = SceneRenderer.ProjectedBounds(document!, rectangle, includeStroke: true)
                },
                Selection = editor?.Session.Selection.ToArray(), SelectionCorners = editor?.SelectionScreenCorners,
                RotationAnchor = editor?.SelectionScreenRotationAnchor, RotationHandle = editor?.SelectionScreenRotationHandle,
                EndpointHandles = editor?.SelectionScreenEndpoints, IsTransactionActive = editor?.Session.IsTransactionActive,
                UndoCount = editor?.Session.UndoCount, RedoCount = editor?.Session.RedoCount,
                IsEditing = host?.IsEditing, IsVisible = host?.IsVisible, KeyboardLeaseActive = host?.IsKeyboardIsolationActive,
                InputWindowVisible = inputHandle != 0 && IsWindowVisible(inputHandle), DisplayWindowVisible = displayHandle != 0 && IsWindowVisible(displayHandle),
                PointerCounts = new { Down = down, Move = moves, Up = up, Cancel = canceled }, NativeSubmissionCount = submissions,
                EnterEditingClicks = enterClicks, CanEnterEditing = enterButton?.IsEnabled,
                ContentSequence = submittedContent, InputSequence = submittedInput, ControlsSequence = submittedControls,
                CompletedWorkerFrames = preview?.CompletedFrames, PreviewBusy = preview?.Busy, LastRenderedPixelCount = host?.LastRenderedPixelCount,
                Error = fatal?.ToString(), GlobalHotkeysRegistered = false
            };
            string path = Path.Combine(output, file), temporary = path + ".new";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            try { File.Move(temporary, path, overwrite: true); }
            catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33) { dirty = true; }
        }
    }

    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static void ActivateOwned(nint handle, Action enter)
    {
        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _), ownThread = GetCurrentThreadId();
        bool attached = foregroundThread != ownThread && AttachThreadInput(ownThread, foregroundThread, true);
        try { SetForegroundWindow(handle); SetFocus(handle); enter(); }
        finally { if (attached) AttachThreadInput(ownThread, foregroundThread, false); }
    }
    [DllImport("user32.dll", EntryPoint = "SetWindowTextW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetWindowText(nint window, string text);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
}
