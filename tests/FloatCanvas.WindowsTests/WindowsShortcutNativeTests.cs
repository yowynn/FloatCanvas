using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using FloatCanvas.Windows;
using static FloatCanvas.Windows.NativeMethods;

namespace FloatCanvas.WindowsTests;

/// <summary>Own bounded focus windows; feed the production pure hook state, never global key injection.</summary>
internal static class WindowsShortcutNativeTests
{
    internal static void Run(Action<bool, string> check)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { RunSta(check); } catch (Exception value) { error = value; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    internal static int Peer(string[] args)
    {
        uint parent = uint.Parse(args[1]);
        nint window = CreateWindowExW(ExNoActivate | ExToolWindow | ExLayered, "STATIC", "Own IME focus peer",
            WsPopup, 410, 100, 180, 100, 0, 0, GetModuleHandleW(null), 0);
        if (window == 0 || !SetLayeredWindowAttributes(window, 0, 1, 2)) return 2;
        nint previous = 0;
        WindowProcedure procedure = (hwnd, message, wParam, lParam) =>
        {
            if (message == WmActivate && (wParam & 0xffff) != 0) AllowSetForegroundWindow(parent);
            return CallWindowProcW(previous, hwnd, message, wParam, lParam);
        };
        previous = SetWindowLongPtr(window, -4, Marshal.GetFunctionPointerForDelegate(procedure));
        try
        {
            ShowWindow(window, 8);
            Console.WriteLine((long)window); Console.Out.Flush();
            var limit = Stopwatch.StartNew();
            while (IsWindow(window) && limit.Elapsed < TimeSpan.FromSeconds(25)) Pump(10);
            return 0;
        }
        finally
        {
            if (IsWindow(window)) { SetWindowLongPtr(window, -4, previous); DestroyWindow(window); }
            GC.KeepAlive(procedure);
        }
    }

    private static void RunSta(Action<bool, string> check)
    {
        nint original = GetForegroundWindow();
        nint window = CreateWindowExW(ExToolWindow | ExLayered, "STATIC", "Own Win+Space focus fixture",
            WsPopup, 100, 100, 300, 140, 0, 0, GetModuleHandleW(null), 0);
        Require(window != 0 && SetLayeredWindowAttributes(window, 0, 1, 2));
        nint edit = CreateWindowExW(0, "EDIT", "unchanged save-dialog text", 0x50000000, 0, 0, 240, 40,
            window, 0, GetModuleHandleW(null), 0);
        Require(edit != 0);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        start.ArgumentList.Add("--windows-shortcuts-peer"); start.ArgumentList.Add(Environment.ProcessId.ToString());
        using var peer = Process.Start(start) ?? throw new InvalidOperationException("Could not start own foreground peer");
        nint external = 0;
        var bounds = new PixelBounds(GetSystemMetrics(76) + GetSystemMetrics(78) + 2048, 0, 40, 40);
        var monitor = new MonitorInfo("ime-offscreen-input", bounds, bounds, 96, 96, true);
        using var host = new OverlayHost(OverlayMonitorScope.PrimaryScreen, () => [monitor], false);
        var failures = new List<Exception>(); host.Error += failures.Add;
        int finished = 0; host.EditingFocusLost += () => finished++;
        try
        {
            external = (nint)long.Parse(peer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult()!);
            check(GetWindowThreadProcessId(external, out uint process) != 0 && process == peer.Id,
                "The simulated transient foreground belongs to an explicitly spawned foreign process");
            ShowWindow(window, 8); ActivateOwnFixture(window, edit);
            host.EnableKeyboardIsolation((_, complete) => complete());
            Enter();
            SendMessageW(edit, 0xb1, 2, 10); // EM_SETSEL on our own text control only.
            long epoch = host.EditingSessionVersion;
            long firstLease = Isolation().CurrentLeaseId;
            SwitchIntent();
            check(host.IsInputMethodSwitchPending && host.IsEditing && finished == 0,
                "Own Win+Space keeps its context guard while foreground has not yet left the text window");
            Require(SetForegroundWindow(external)); Pump(180);
            check(GetForegroundWindow() == external && !Isolation().IsLeaseActive && host.IsEditing &&
                host.EditingSessionVersion == epoch && finished == 0 && host.IsInputMethodSwitchPending,
                "Real external foreground suspends input isolation but never exits/finishes the editing context during the bounded IME candidate");
            Require(SetForegroundWindow(window)); SetFocus(edit); Pump(120);
            check(GetForegroundWindow() == window && GetFocus() == edit && host.IsEditing && host.IsKeyboardIsolationActive &&
                host.EditingSessionVersion == epoch && Isolation().CurrentLeaseId > firstLease && finished == 0,
                "Returning to the exact original HWND resumes a real lease without re-entering editing or changing epoch");
            var text = new System.Text.StringBuilder(128); GetWindowTextW(edit, text, text.Capacity);
            check(text.ToString() == "unchanged save-dialog text" && (long)SendMessageW(edit, 0xb0, 0, 0) == ((10L << 16) | 2),
                "The real own edit text and selection remain intact through the foreground roundtrip");
            check(host.IsInputMethodSwitchPending, "An own-return guard covers WPF activation/deactivation queue settling");
            Pump(280);
            check(!host.IsInputMethodSwitchPending && host.IsEditing, "The own-return guard expires without ending a healthy context");

            SwitchIntent(); Require(SetForegroundWindow(external)); Pump(140);
            Feed(0x58, own: false);
            Pump(100);
            check(!host.IsEditing && finished == 1,
                "New external keyboard intent cancels the candidate and finishes editing once; no global key was injected");
            Require(SetForegroundWindow(window)); Enter();
            Feed(0x5B); Require(SetForegroundWindow(external)); Pump(180);
            check(!host.IsEditing && finished == 2,
                "Plain Win has no IME exemption: real external/Start-like foreground still ends editing");
            Require(SetForegroundWindow(window)); Enter();
            SwitchIntent(); Require(SetForegroundWindow(external)); Pump(2250);
            check(!host.IsEditing && finished == 3 && !host.IsInputMethodSwitchPending,
                "A foreground that never returns expires the two-second candidate and ends editing once");
            check(failures.Count == 0, "The actual host/keyboard service reports no focus or hook errors");
            Console.WriteLine("IME fixture: real own/foreign foreground transitions, production host and hook-state input; Shell IME and global key injection were not invoked.");

            void Enter()
            {
                Require(host.TryEnterEditing(out _));
                Require(SetForegroundWindow(window)); SetFocus(edit); Pump(60);
            }
            KeyboardInputIsolation Isolation() => (KeyboardInputIsolation)typeof(OverlayHost)
                .GetField("_keyboardIsolation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            void Feed(uint key, bool up = false, bool own = true)
            {
                var service = Isolation();
                object gate = typeof(KeyboardInputIsolation).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
                var state = (KeyboardSuppressionState)typeof(KeyboardInputIsolation).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
                lock (gate)
                {
                    long now = Environment.TickCount64;
                    check(!state.Process(key, 0, up ? 0x80u : 0, (uint)now, up, up ? 0x101u : 0x100u,
                        own ? window : external, own, true, now), "Fixture system-key stream passes the production hook policy");
                }
            }
            void SwitchIntent() { Feed(0x5B); Feed(0x20); Feed(0x20, true); Feed(0x5B, true); }
        }
        finally
        {
            host.ExitEditing(restorePreviousForeground: false);
            if (external != 0 && IsWindow(external)) PostMessageW(external, WmClose, 0, 0);
            if (!peer.WaitForExit(1500)) { peer.Kill(); peer.WaitForExit(); }
            if (GetForegroundWindow() == window && original != 0 && IsWindow(original)) SetForegroundWindow(original);
            DestroyWindow(window);
        }
    }
    private static void ActivateOwnFixture(nint window, nint edit)
    {
        Require(GetWindowThreadProcessId(window, out uint owner) != 0 && owner == Environment.ProcessId);
        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _), thread = GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != thread && AttachThreadInput(thread, foregroundThread, true);
        try
        {
            bool accepted = SetForegroundWindow(window); SetFocus(edit);
            bool own = GetForegroundWindow() == window && GetFocus() == edit;
            Console.WriteLine($"IME_FIXTURE_ACTIVATION attached={attached} accepted={accepted} actualOwn={own}");
            Require(accepted && own);
        }
        finally { if (attached) AttachThreadInput(thread, foregroundThread, false); }
    }
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Own native fixture transition was denied"); }
    private static void Pump(int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        do { while (PeekMessageW(out var message, 0, 0, 0, 1)) { TranslateMessage(ref message); DispatchMessageW(ref message); } Thread.Sleep(1); }
        while (clock.ElapsedMilliseconds < milliseconds);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Message
    { public nint Hwnd; public uint Id; public nuint WParam; public nint LParam; public uint Time; public Point Point; public uint Private; }
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out Message message, nint hwnd, uint first, uint last, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(ref Message message);
    [DllImport("user32.dll")] private static extern nint CallWindowProcW(nint previous, nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint process);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint hwnd, System.Text.StringBuilder text, int capacity);
}
