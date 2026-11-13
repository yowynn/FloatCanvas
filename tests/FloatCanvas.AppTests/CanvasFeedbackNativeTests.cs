using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class CanvasFeedbackNativeTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, bool interactive = false, bool reviewIme = false, bool probeIme = false, bool reviewCandidate = false, bool probeDisplayMessages = false, bool enhanced = false, bool probeToolbarSpace = false)
    {
        // Match the production executable's PerMonitorV2 manifest. The test runner
        // has no DPI manifest, so otherwise WPF exposes virtualized TSF extents.
        nint priorDpi = SetThreadDpiAwarenessContext(-4);
        try { RunCore(check, interactive, reviewIme, probeIme, reviewCandidate, probeDisplayMessages, enhanced, probeToolbarSpace); }
        finally { if (priorDpi != 0) SetThreadDpiAwarenessContext(priorDpi); }
    }
    private static void RunCore(Action<string, bool> check, bool interactive, bool reviewIme, bool probeIme, bool reviewCandidate, bool probeDisplayMessages, bool enhanced, bool probeToolbarSpace)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var priorContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Hidden, null,
            [OverlayMonitorScope.AllScreens, false], null)!;
        if (enhanced) host.EnableSystemInput();
        using var desktop = new DesktopSession(app, host);
        Field<System.Windows.Forms.NotifyIcon>("_tray").Visible = false;
        var toolbar = Field<Window>("_toolbar");
        if (interactive) toolbar.ShowActivated = false;
        toolbar.Show(); Pump();
        if (interactive)
        {
            toolbar.Title = "FloatCanvas canvas feedback diagnostic";
            Wait(() => toolbar.IsActive, 90);
        }
        SetForegroundWindow(new WindowInteropHelper(toolbar).Handle);
        SetFocus(new WindowInteropHelper(toolbar).Handle);
        Call("SetEditing", true, false);
        if (enhanced) Wait(() => host.HasEditingInput);
        Console.WriteLine($"NATIVE_ENTRY editing={host.IsEditing} owned={host.HasEditingInput} foreground={GetForegroundWindow():X}");
        if (!host.IsEditing || !host.HasEditingInput) throw new InvalidOperationException("Native fixture could not acquire its own foreground.");
        var editor = Field<EditorController>("_editor");
        var failed = new List<string>();
        void Expect(string name, bool passed) { Console.WriteLine((passed ? "PASS " : "FAIL ") + name); if (!passed) failed.Add(name); }
        Wait(() => !Field<bool>("_presentationUncertain"));
        if (probeToolbarSpace)
        {
            Call("ActivateTool", EditorTool.Select); Call("CloseToolOptions");
            var button = Field<Dictionary<EditorTool, System.Windows.Controls.Button>>("_toolbarToolButtons")[EditorTool.Select];
            toolbar.Activate(); button.Focus(); Pump();
            var dispatch = (Action<KeyboardInputPacket, Action>)typeof(OverlayHost).GetField("_dispatchPrivateKey", Hidden)!.GetValue(host)!;
            foreach (bool up in new[] { false, true })
            {
                var state = new byte[256]; state[0x20] = up ? (byte)0 : (byte)0x80;
                var packet = (KeyboardInputPacket)Activator.CreateInstance(typeof(KeyboardInputPacket), Hidden, null,
                    [1L, 1L, KeyboardInputPacketKind.Key, 0x20u, MapVirtualKey(0x20, 0), 0u, (uint)Environment.TickCount, up, false,
                        GetForegroundWindow(), state, false, null, up ? 0x101u : 0x100u], null)!;
                dispatch(packet, () => { }); Pump();
                Expect("toolbar Space " + (up ? "release" : "press") + " updates temporary canvas navigation", Field<bool>("_canvasNavigationSpaceHeld") == !up);
            }
            bool options = (bool)typeof(DesktopSession).GetProperty("IsToolOptionsOpen", Hidden)!.GetValue(desktop)!;
            Expect("Space after focusing Select never activates the tool options button", !options);
            host.ExitEditing();
            check("toolbar Space: " + string.Join("; ", failed), failed.Count == 0);
            return;
        }
        if (probeDisplayMessages)
        {
            int changed = 0; host.MonitorsChanged += () => changed++;
            foreach (uint message in new uint[] { 0x7e, 0x2e0 })
            {
                long version = host.EditingSessionVersion, loss = host.ForegroundLossVersion;
                int before = changed;
                var previous = host.Diagnostics.Select(output => output.InputHandle).ToArray();
                var monitor = host.Monitors[0]; var bounds = monitor.Bounds;
                nint rectangle = Marshal.AllocHGlobal(16);
                try
                {
                    Marshal.Copy(new[] { bounds.Left, bounds.Top, bounds.Right, bounds.Bottom }, 0, rectangle, 4);
                    SendMessage(message == 0x2e0 ? previous[0] : host.ControlHandle, message,
                        message == 0x2e0 ? (nuint)(monitor.DpiX | monitor.DpiY << 16) : 32,
                        message == 0x2e0 ? rectangle : (nint)(bounds.Width | bounds.Height << 16));
                }
                finally { Marshal.FreeHGlobal(rectangle); }
                Expect($"display message {message:X} does not release the {(enhanced ? "enhanced" : "desktop")} input lease before rebuilding", host.IsEditing && host.HasEditingInput && host.EditingSessionVersion == version && host.ForegroundLossVersion == loss);
                Wait(() => changed > before);
                Expect($"display message {message:X} rebuilds outputs and preserves its editing lease", host.IsEditing && host.HasEditingInput && host.EditingSessionVersion == version && host.ForegroundLossVersion == loss && !previous.SequenceEqual(host.Diagnostics.Select(output => output.InputHandle)));
                if (!host.IsEditing) break;
            }
            host.ExitEditing();
            SynchronizationContext.SetSynchronizationContext(priorContext);
            check("native monitor messages: " + string.Join("; ", failed), failed.Count == 0);
            return;
        }
        foreach (var output in host.Diagnostics)
        {
            Call("ActivateTool", EditorTool.Text); Call("CloseToolOptions");
            Pump();
            SendMessage(output.InputHandle, 0x201, 1, Pack(180, 160));
            SendMessage(output.InputHandle, 0x202, 0, Pack(180, 160));
            Wait(() => editor.PendingPicking?.IsCompleted != false); Pump();
            var text = Field<InlineTextEdit?>("_inlineText");
            var input = Field<CanvasTextInput?>("_canvasTextInput");
            Console.WriteLine($"TEXT_NATIVE {output.Bounds} active={input?.IsActive} focus={GetFocus():X} foreground={GetForegroundWindow():X} editing={host.IsEditing}");
            Expect("native canvas click retains text keyboard focus on " + output.Bounds, text is not null && input?.IsActive == true);
            if (text is not null)
            {
                var dispatch = (Action<KeyboardInputPacket, Action>)typeof(OverlayHost).GetField("_dispatchPrivateKey", Hidden)!.GetValue(host)!;
                foreach (bool up in new[] { false, true })
                {
                    var state = new byte[256]; state[0x33] = up ? (byte)0 : (byte)0x80;
                    var packet = (KeyboardInputPacket)Activator.CreateInstance(typeof(KeyboardInputPacket), Hidden, null,
                        [1L, 1L, KeyboardInputPacketKind.Key, 0x33u, MapVirtualKey(0x33, 0), 0u, (uint)Environment.TickCount, up, false,
                            GetForegroundWindow(), state, false, null, up ? 0x101u : 0x100u], null)!;
                    dispatch(packet, () => { }); Pump();
                }
                Expect("production keyboard bridge enters the inline text on " + output.Bounds, text.Input.Text == "3");
                if (probeIme && output.Bounds == host.Diagnostics.Last().Bounds)
                {
                    int starts = 0, updates = 0, commits = 0;
                    System.Windows.Input.TextCompositionManager.AddPreviewTextInputStartHandler(text.Input, (_, _) => starts++);
                    System.Windows.Input.TextCompositionManager.AddPreviewTextInputUpdateHandler(text.Input, (_, _) => updates++);
                    System.Windows.Input.TextCompositionManager.AddPreviewTextInputHandler(text.Input, (_, _) => commits++);
                    var method = System.Windows.Input.InputMethod.Current;
                    var stateBefore = method.ImeState;
                    Console.WriteLine("IME_CONTEXT " + System.Text.Json.JsonSerializer.Serialize(new { Layout = GetKeyboardLayout(0).ToInt64(),
                        Culture = System.Windows.Input.InputLanguageManager.Current.CurrentInputLanguage.Name, State = stateBefore.ToString() }));
                    try
                    {
                        text.Input.Clear();
                        method.ImeState = System.Windows.Input.InputMethodState.On;
                        foreach (uint key in new uint[] { 0x4e, 0x49 }) Key(key);
                        Console.WriteLine("IME_COMPOSITION " + System.Text.Json.JsonSerializer.Serialize(new { starts, updates, commits, text.Input.Text, Active = input?.IsActive }));
                        var candidateWait = Stopwatch.StartNew();
                        Wait(() => candidateWait.ElapsedMilliseconds >= 300);
                        // Read the same WPF geometry endpoint queried by TSF; this is not
                        // evidence that the separate system candidate popup was clicked.
                        var textEditor = typeof(System.Windows.Controls.Primitives.TextBoxBase)
                            .GetProperty("TextEditor", Hidden)!.GetValue(text.Input)!;
                        var textStore = textEditor.GetType().GetProperty("TextStore", Hidden)!.GetValue(textEditor)!;
                        var getTextExt = textStore.GetType().GetMethods(Hidden)
                            .Single(method => method.Name.EndsWith(".GetTextExt", StringComparison.Ordinal));
                        object?[] extentArguments = [0, text.Input.CaretIndex, text.Input.CaretIndex, null, null];
                        getTextExt.Invoke(textStore, extentArguments);
                        var extent = extentArguments[3]!;
                        int Edge(string name) => (int)extent.GetType().GetField(name)!.GetValue(extent)!;
                        var expectedCaret = text.LocalToScreen.TransformPoint(new(text.Caret.X, text.Caret.Y)) + editor.CanvasOrigin;
                        var actualCaret = new PointD(Edge("left"), Edge("top"));
                        var carrier = (Window)typeof(CanvasTextInput).GetField("window", Hidden)!.GetValue(input)!;
                        var nativeCaret = text.Input.GetRectFromCharacterIndex(text.Input.CaretIndex);
                        Console.WriteLine("IME_WPF_GEOMETRY " + System.Text.Json.JsonSerializer.Serialize(new { text.Input.CaretIndex, text.Input.Width, text.Input.FontSize, nativeCaret, NativeScreen = text.Input.PointToScreen(nativeCaret.TopLeft), carrier.Left, carrier.Top, Dpi = System.Windows.Media.VisualTreeHelper.GetDpi(carrier).DpiScaleX }));
                        Console.WriteLine("IME_TSF_EXTENT " + System.Text.Json.JsonSerializer.Serialize(new { Expected = expectedCaret, Actual = actualCaret, Right = Edge("right"), Bottom = Edge("bottom") }));
                        Expect("TSF caret geometry follows the secondary canvas caret", Math.Abs(actualCaret.X - expectedCaret.X) <= 3 && Math.Abs(actualCaret.Y - expectedCaret.Y) <= 3);
                        var candidateWindows = new List<object>();
                        nint candidateDpi = SetThreadDpiAwarenessContext(-4);
                        try
                        {
                        EnumWindows((hwnd, _) =>
                        {
                            if (!IsWindowVisible(hwnd)) return true;
                            var name = new System.Text.StringBuilder(256); GetClassName(hwnd, name, name.Capacity);
                            GetWindowThreadProcessId(hwnd, out uint processId);
                            string processName;
                            try { using var process = Process.GetProcessById((int)processId); processName = process.ProcessName; }
                            catch (ArgumentException) { return true; }
                            if ((name.ToString().Contains("IME", StringComparison.OrdinalIgnoreCase) || processName is "TextInputHost" or "ChsIME") && GetWindowRect(hwnd, out var bounds))
                                candidateWindows.Add(new { Class = name.ToString(), Process = processName, bounds.Left, bounds.Top, Width = bounds.Right - bounds.Left, Height = bounds.Bottom - bounds.Top });
                            return true;
                        }, 0);
                        }
                        finally { if (candidateDpi != 0) SetThreadDpiAwarenessContext(candidateDpi); }
                        Console.WriteLine("IME_CANDIDATE_WINDOWS " + System.Text.Json.JsonSerializer.Serialize(candidateWindows));
                        if (reviewCandidate)
                        {
                            Console.WriteLine("IME_CANDIDATE_READY " + System.Text.Json.JsonSerializer.Serialize(new { ExpectedCaret = text.LocalToScreen.TransformPoint(new(text.Caret.X, text.Caret.Y)) + editor.CanvasOrigin }));
                            var reviewWait = Stopwatch.StartNew();
                            Wait(() => !text.IsActive || text.Input.Text.Any(character => character is >= '\u4e00' and <= '\u9fff') || reviewWait.ElapsedMilliseconds >= 90000, 95);
                        }
                        if (!text.Input.Text.Any(character => character is >= '\u4e00' and <= '\u9fff')) Key(0x20);
                        var commitWait = Stopwatch.StartNew();
                        Wait(() => text.Input.Text.Any(character => character is >= '\u4e00' and <= '\u9fff') || commitWait.ElapsedMilliseconds >= 3000);
                        Console.WriteLine("IME_COMMIT " + System.Text.Json.JsonSerializer.Serialize(new { starts, updates, commits, text.Input.Text, Active = input?.IsActive }));
                        Expect("real TSF composition starts through production keyboard packets on the secondary monitor", starts > 0 && updates > 0);
                        Expect("the system IME commits Chinese through production keyboard packets", text.Input.Text.Any(character => character is >= '\u4e00' and <= '\u9fff') && input?.IsActive == true);
                    }
                    finally { method.ImeState = stateBefore; }

                    void Key(uint key)
                    {
                        foreach (bool up in new[] { false, true })
                        {
                            var state = new byte[256]; state[key] = up ? (byte)0 : (byte)0x80;
                            var packet = (KeyboardInputPacket)Activator.CreateInstance(typeof(KeyboardInputPacket), Hidden, null,
                                [1L, 1L, KeyboardInputPacketKind.Key, key, MapVirtualKey(key, 0), 0u, (uint)Environment.TickCount, up, false,
                                    GetForegroundWindow(), state, false, null, up ? 0x101u : 0x100u], null)!;
                            dispatch(packet, () => { }); Pump();
                        }
                    }
                }
                if (reviewIme && output.Bounds == host.Diagnostics.Last().Bounds)
                {
                    text.Input.Clear();
                    toolbar.Title = "FloatCanvas IME review on secondary screen";
                    Console.WriteLine("IME_READY: compose Chinese and press Enter to finish; diagnostic owns only its empty draft.");
                    Wait(() => !text.IsActive, 120);
                    Console.WriteLine("IME_RESULT " + System.Text.Json.JsonSerializer.Serialize(text.Target.Text));
                    Expect("real IME composition commits Chinese on the secondary monitor", text.Target.Text.Any(character => character is >= '\u4e00' and <= '\u9fff'));
                }
                Call("FinishInlineText", false, false); Pump();
            }
        }
        Call("ActivateTool", EditorTool.Canvas); Call("OpenCanvasSettings"); Pump();
        var settings = Field<CanvasToolOptions?>("_canvasToolOptions");
        if (settings is not null)
        {
            Call("ToggleCanvasVisibility"); Pump();
            Expect("visibility shortcut preserves the unpinned canvas settings window", settings.IsVisible);
            Call("ToggleCanvasVisibility"); Pump();
        }
        Call("EndCanvasCalibration", true, true);
        var lastInput = host.Diagnostics.Last().InputHandle;
        SetForegroundWindow(lastInput); SetFocus(lastInput);
        long editingVersion = host.EditingSessionVersion, focusLoss = host.ForegroundLossVersion;
        host.SetMonitorScope(OverlayMonitorScope.PrimaryScreen); Pump();
        Expect("native monitor window replacement preserves editing and its input lease", host.IsEditing && host.HasEditingInput &&
            host.EditingSessionVersion == editingVersion && host.ForegroundLossVersion == focusLoss);
        host.SetMonitorScope(OverlayMonitorScope.AllScreens); Pump();
        Expect("restoring all outputs keeps editing without reentering pass-through", host.IsEditing && host.HasEditingInput && host.Monitors.Count >= 2 &&
            host.EditingSessionVersion == editingVersion && host.ForegroundLossVersion == focusLoss);
        typeof(OverlayHost).GetMethod("RepairWindowOrder", Hidden)!.Invoke(host, null);
        Pump();
        var taskbars = new List<nint>();
        EnumWindows((hwnd, _) =>
        {
            var name = new System.Text.StringBuilder(128); GetClassName(hwnd, name, name.Capacity);
            if (name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" && IsWindowVisible(hwnd)) taskbars.Add(hwnd);
            return true;
        }, 0);
        Expect("actual primary and secondary taskbars stay below the canvas outputs", taskbars.Count >= 2 &&
            host.Diagnostics.All(output => taskbars.All(taskbar => Rank(output.DisplayHandle) < Rank(taskbar))));
        host.ExitEditing();
        SynchronizationContext.SetSynchronizationContext(priorContext);
        check("native canvas feedback: " + string.Join("; ", failed), failed.Count == 0);

        T Field<T>(string name) => (T)typeof(DesktopSession).GetField(name, Hidden)!.GetValue(desktop)!;
        object? Call(string name, params object?[] args) => typeof(DesktopSession).GetMethod(name, Hidden)!.Invoke(desktop, args);
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame);
    }
    private static void Wait(Func<bool> complete, int seconds = 15)
    {
        var watch = Stopwatch.StartNew();
        do { Pump(); if (complete()) return; Thread.Sleep(1); } while (watch.Elapsed < TimeSpan.FromSeconds(seconds));
        throw new TimeoutException("Native feedback did not settle.");
    }
    private static nint Pack(int x, int y) => (nint)((y << 16) | (x & 65535));
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint key, uint type);
    [DllImport("user32.dll")] private static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, nint parameter);
    private delegate bool EnumWindow(nint hwnd, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")] private static extern int GetClassName(nint hwnd, System.Text.StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetTopWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    private static int Rank(nint target)
    {
        int rank = 0;
        for (nint hwnd = GetTopWindow(0); hwnd != 0 && rank < 8192; hwnd = GetWindow(hwnd, 2), rank++)
            if (hwnd == target) return rank;
        return int.MaxValue;
    }
}
