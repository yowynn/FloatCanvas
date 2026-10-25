using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

/// <summary>Opt-in observation of an actual system screen-clip transition over our owned receiver.
/// Does not inject input, activate windows or capture another application's pixels.</summary>
internal static class Phase3ScreenClipProbe
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static string Root => Environment.GetEnvironmentVariable("FLOATCANVAS_PHASE3_ARTIFACTS") ?? Path.Combine(Path.GetTempPath(), "FloatCanvas-phase3-screenclip");
    internal static void Inventory()
    {
        var processes = Process.GetProcesses().Where(p => p.ProcessName is "SnippingTool" or "ScreenClippingHost" or "ShareX" or "FloatCanvas").ToDictionary(p => (uint)p.Id, p => p.ProcessName);
        var rows = new List<object>();
        EnumWindow callback = (window, _) =>
        {
            GetWindowThreadProcessId(window, out uint process);
            if (processes.TryGetValue(process, out string? name)) rows.Add(new { Name = name, Window = Describe(window) });
            return true;
        };
        EnumWindows(callback, 0); GC.KeepAlive(callback);
        Directory.CreateDirectory(Root);
        string json = JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(Root, "existing-window-bands.json"), json); Console.WriteLine(json);
    }
    internal static void Observe()
    {
        // Passive, bounded observation only. It never enters editing or operates external HWNDs.
        Directory.CreateDirectory(Root);
        string path = Path.Combine(Root, "passive-window-events-" + Environment.ProcessId + ".jsonl");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var time = Stopwatch.StartNew();
        bool Relevant(nint window)
        {
            GetWindowThreadProcessId(window, out uint process);
            try { return Process.GetProcessById((int)process).ProcessName is "FloatCanvas" or "SnippingTool" or "ScreenClippingHost"; }
            catch (ArgumentException) { return false; }
        }
        void Write(string stage, object data) => File.AppendAllText(path, JsonSerializer.Serialize(new { Ms = time.Elapsed.TotalMilliseconds, Stage = stage, Data = data }) + Environment.NewLine);
        WinEvent callback = (_, eventId, window, objectId, childId, _, eventTime) =>
        {
            if (window == 0 || objectId != 0 || childId != 0 || GetAncestor(window, 2) != window || !Relevant(window)) return;
            Write("Event", new { Event = eventId, EventTime = eventTime, Window = Describe(window), Foreground = Describe(GetForegroundWindow()) });
        };
        nint events = SetWinEventHook(0x8002, 0x8004, 0, callback, 0, 0, 0);
        nint focus = SetWinEventHook(3, 3, 0, callback, 0, 0, 0);
        if (events == 0 || focus == 0) throw new System.ComponentModel.Win32Exception();
        string? previous = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            var visible = new List<object>();
            EnumWindow enumerate = (window, _) => { if (IsWindowVisible(window) && Relevant(window)) visible.Add(Describe(window)); return true; };
            EnumWindows(enumerate, 0); GC.KeepAlive(enumerate);
            string state = JsonSerializer.Serialize(visible);
            if (state != previous) { Write("Visible", JsonDocument.Parse(state).RootElement); previous = state; }
            if (time.Elapsed.TotalSeconds >= 60) app.Shutdown();
        };
        Write("Ready", new { TimeoutSeconds = 60 }); timer.Start();
        try { app.Run(); }
        finally { timer.Stop(); UnhookWinEvent(events); UnhookWinEvent(focus); GC.KeepAlive(callback); Write("Stopped", new { }); }
    }
    internal static void BandTransfer()
    {
        // Only newly created, hidden own HWNDs. Never changes a live user's window.
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null, [OverlayMonitorScope.PrimaryScreen, false], null)!;
        host.EnableSystemInput();
        nint window = host.Diagnostics[0].DisplayHandle;
        GetWindowBand(window, out uint before);
        bool moved = SetWindowBand(window, 0, 1); int error = Marshal.GetLastWin32Error();
        GetWindowBand(window, out uint after);
        bool restored = !moved || SetWindowBand(window, 0, before);
        GetWindowBand(window, out uint final);
        Console.WriteLine(JsonSerializer.Serialize(new { Before = before, Moved = moved, Error = error, After = after, Restored = restored, Final = final, Visible = IsWindowVisible(window) }));
        var tool = new Window { ShowActivated = false };
        var interop = new WindowInteropHelper(tool) { Owner = host.SystemWindowOwner };
        nint handle = interop.EnsureHandle(); GetWindowBand(handle, out uint toolBefore);
        interop.Owner = host.ControlHandle; GetWindowBand(handle, out uint toolAfter);
        interop.Owner = host.SystemWindowOwner; GetWindowBand(handle, out uint toolFinal);
        Console.WriteLine(JsonSerializer.Serialize(new { ToolBefore = toolBefore, ToolAfter = toolAfter, ToolFinal = toolFinal, Visible = IsWindowVisible(handle) }));
        tool.Close();
    }
    internal static void Receiver()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var primary = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        var window = new Window { Title = "FloatCanvas Phase3 Screenshot Receiver", Background = Brushes.WhiteSmoke,
            Content = new System.Windows.Controls.TextBlock { Text = "FloatCanvas isolated screen-clip test\nOwned background — no user content", Margin = new Thickness(80), FontSize = 28 },
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowActivated = false,
            Left = 0, Top = 0, Width = 1000, Height = 700 };
        window.SourceInitialized += (_, _) => SetWindowPos(new WindowInteropHelper(window).Handle, 0, primary.Left, primary.Top, primary.Width, primary.Height, 0x14);
        var deadline = new DispatcherTimer { Interval = TimeSpan.FromSeconds(180) }; deadline.Tick += (_, _) => window.Close();
        window.Show(); deadline.Start(); app.Run(); deadline.Stop();
    }
    internal static int Run()
    {
        if (!uint.TryParse(Environment.GetEnvironmentVariable("FLOATCANVAS_SCREENCLIP_RECEIVER_PID"), out uint expected)) throw new InvalidOperationException("Explicit own receiver PID required.");
        using var receiver = Process.GetProcessById((int)expected);
        if (expected == Environment.ProcessId || receiver.MainModule?.FileName != Environment.ProcessPath) throw new InvalidOperationException("Own receiver required.");
        Directory.CreateDirectory(Root); string path = Path.Combine(Root, "screenclip-" + Environment.ProcessId + ".jsonl");
        var time = Stopwatch.StartNew();
        void Record(string stage, object? state = null) => File.AppendAllText(path, JsonSerializer.Serialize(new { Ms = time.Elapsed.TotalMilliseconds, Stage = stage, State = state }) + Environment.NewLine);
        ApplicationPaths.Configure(["--profile", Path.Combine(Root, "profile-" + Environment.ProcessId)]);
        new UserSettings { Language = "en", Theme = "light", ShowFloatingLogo = false, Toggle = new(7, 0x77), Hide = new(7, 0x76) }.Save();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null, [OverlayMonitorScope.PrimaryScreen, false], null)!;
        host.EnableSystemInput();
        using var session = new DesktopSession(app, host);
        ((System.Windows.Forms.NotifyIcon)typeof(DesktopSession).GetField("_tray", Private)!.GetValue(session)!).Visible = false;
        var toolbar = (Window)typeof(DesktopSession).GetField("_toolbar", Private)!.GetValue(session)!;
        var editor = (EditorController)typeof(DesktopSession).GetField("_editor", Private)!.GetValue(session)!;
        var captureTools = host.CaptureToolVisuals;
        host.CaptureToolVisuals = () =>
        {
            Record("CaptureToolsBegin"); var visuals = captureTools!(); Record("CaptureToolsEnd", new { Count = visuals.Count }); return visuals;
        };
        Subclass control = (nint window, uint message, nuint wparam, nint lparam, nuint id, nuint data) =>
        {
            bool traced = message is 0x8005 or 0x8002 or 0x8462 || message == 0x113 && wparam is 0x4644 or 0x464D;
            if (traced) Record("ControlBegin", new { Message = message, Wparam = (ulong)wparam });
            nint result = DefSubclassProc(window, message, wparam, lparam);
            if (traced) Record("ControlEnd", new { Message = message, Wparam = (ulong)wparam });
            return result;
        };
        if (!SetWindowSubclass(host.ControlHandle, control, 0x503, 0)) throw new System.ComponentModel.Win32Exception();
        var primary = host.PrimaryMonitor;
        editor.Session.Execute("screen clip sample", doc => doc.Elements = [new() { Kind = ElementKind.Rectangle, Width = 700, Height = 600,
            Transform = Affine2D.Translation(primary.Bounds.Left + 250, primary.Bounds.Top + 250), Style = new() { Fill = 0xFF2383E2, StrokeWidth = 0 } }]);
        host.EditingFocusLost += () => Record("FocusLost", new { toolbar.IsVisible, host.IsEditing });
        toolbar.IsVisibleChanged += (_, _) => Record("ToolbarVisibility", new { toolbar.IsVisible });
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(25) };
        bool entered = false, positioned = false; double? inputReady = null; string? previous = null; double? end = null;
        timer.Tick += (_, _) =>
        {
            nint foreground = GetForegroundWindow(); GetWindowThreadProcessId(foreground, out uint process);
            if (!entered && process == expected)
            {
                host.SetVisible(true);
                bool accepted = (bool)typeof(DesktopSession).GetMethod("SetEditing", Private)!.Invoke(session, [true, false])!;
                if (!accepted) { Record("EntryRejected"); app.Shutdown(2); return; }
                entered = true;
                Record("Entered", new { Receiver = expected, Host = Environment.ProcessId });
            }
            if (entered && host.HasEditingInput && toolbar.IsVisible)
            {
                inputReady ??= time.Elapsed.TotalMilliseconds;
                if (!positioned && time.Elapsed.TotalMilliseconds - inputReady >= 250)
                {
                    toolbar.Left = (primary.Bounds.Left + 420) * 96d / primary.DpiX;
                    toolbar.Top = (primary.Bounds.Top + 420) * 96d / primary.DpiY;
                    positioned = true; Record("CandidateReady", new { toolbar.Left, toolbar.Top });
                }
            }
            string state = JsonSerializer.Serialize(new { Foreground = Describe(foreground), host.IsEditing, host.HasEditingInput,
                ScreenshotPresentation = typeof(OverlayHost).GetField("_systemScreenshot", Private)!.GetValue(host) is not null,
                Toolbar = Describe(new WindowInteropHelper(toolbar).Handle), Displays = host.Diagnostics.Select(d => Describe(d.DisplayHandle)).ToArray() });
            if (state != previous) { previous = state; Record("State", JsonDocument.Parse(state).RootElement); }
            if (entered && process != expected && process != Environment.ProcessId && end is null)
            { Record("ExternalTransition"); end = time.Elapsed.TotalSeconds + 45; }
            if (time.Elapsed.TotalSeconds > 120 || end is { } deadline && time.Elapsed.TotalSeconds > deadline) app.Shutdown();
        };
        WinEvent events = (_, eventId, window, objectId, childId, _, _) =>
        {
            if (window == 0 || objectId != 0 || childId != 0) return;
            if (GetAncestor(window, 2) != window) return;
            GetWindowThreadProcessId(window, out uint owner);
            if (owner != Environment.ProcessId && owner != expected)
            {
                try { using var candidate = Process.GetProcessById((int)owner); if (candidate.ProcessName is not ("SnippingTool" or "ScreenClippingHost")) return; }
                catch (ArgumentException) { return; }
            }
            Record("WindowEvent", new { Event = eventId, Window = Describe(window) });
        };
        nint hook = SetWinEventHook(0x8002, 0x8004, 0, events, 0, 0, 0);
        timer.Start(); Record("Ready", new { Receiver = expected, Host = Environment.ProcessId });
        try { return app.Run(); }
        finally { timer.Stop(); UnhookWinEvent(hook); RemoveWindowSubclass(host.ControlHandle, control, 0x503); GC.KeepAlive(control); GC.KeepAlive(events); Record("Stopped"); }
    }
    private static object Describe(nint window)
    {
        GetWindowThreadProcessId(window, out uint process); var name = new StringBuilder(128); GetClassNameW(window, name, name.Capacity);
        GetWindowRect(window, out var rect);
        return new { Handle = (long)window, Process = process, Class = name.ToString(), Band = GetWindowBand(window, out uint band) ? band : uint.MaxValue,
            Visible = IsWindowVisible(window), Topmost = ((long)GetWindowLongPtrW(window, -20) & 8) != 0, Owner = (long)GetWindow(window, 4),
            Rect = new[] { rect.Left, rect.Top, rect.Right, rect.Bottom } };
    }
    private delegate void WinEvent(nint hook, uint eventId, nint window, int objectId, int childId, uint thread, uint time);
    private delegate bool EnumWindow(nint window, nint parameter);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern bool GetWindowBand(nint window, out uint band);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint first, uint last, nint module, WinEvent callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, nint parameter);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowBand(nint window, nint after, uint band);
    private delegate nint Subclass(nint window, uint message, nuint wparam, nint lparam, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint window, Subclass callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint window, Subclass callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nuint wparam, nint lparam);
}
