using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using FloatCanvas.Windows;
using static FloatCanvas.Windows.NativeMethods;

namespace FloatCanvas.WindowsTests;

/// <summary>Opt-in real foreign-process z-order events on off-screen own fixtures. Never activates or injects input.</summary>
internal static class NativeWindowOrderTests
{
    internal static void Run(Action<bool, string> check)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { RunSta(check); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal static int RunPeer(string[] args)
    {
        using var dpi = new ThreadDpiAwarenessScope(WindowsCapabilities.Current);
        int left = int.Parse(args[1]), top = int.Parse(args[2]);
        nint hwnd = CreateWindowExW(ExToolWindow | ExNoActivate | ExLayered, "STATIC", "FloatCanvas offscreen z-order peer",
            WsPopup, left, top, 180, 80, 0, 0, GetModuleHandleW(null), 0);
        if (hwnd == 0 || !SetLayeredWindowAttributes(hwnd, 0, 1, 2)) return 2;
        try
        {
            ShowWindow(hwnd, 8);
            Console.WriteLine($"{Environment.ProcessId} {(long)hwnd}"); Console.Out.Flush();
            var timeout = Stopwatch.StartNew();
            while (IsWindow(hwnd) && timeout.Elapsed < TimeSpan.FromSeconds(30)) Pump(10);
            return 0;
        }
        finally { if (IsWindow(hwnd)) DestroyWindow(hwnd); }
    }

    private static void RunSta(Action<bool, string> check)
    {
        using var dpi = new ThreadDpiAwarenessScope(WindowsCapabilities.Current);
        nint initialForeground = GetForegroundWindow(), initialFocus = GetFocus(), initialCapture = GetCapture();
        nint initialDpi = GetThreadDpiAwarenessContext();
        int left = GetSystemMetrics(76) + GetSystemMetrics(78) + 4096, top = GetSystemMetrics(77) + 128;
        var first = new MonitorInfo("zorder-offscreen-a", new(left, top, 80, 80), new(left, top, 80, 80), 96, 96, true);
        var second = new MonitorInfo("zorder-offscreen-b", new(left + 80, top, 80, 80), new(left + 80, top, 80, 80), 96, 96, false);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        start.ArgumentList.Add("--window-order-peer"); start.ArgumentList.Add(left.ToString()); start.ArgumentList.Add(top.ToString());
        using var peer = Process.Start(start) ?? throw new InvalidOperationException("Could not start own peer fixture.");
        nint foreign = 0, tool = 0, popup = 0, modal = 0, eventHook = 0;
        var nativeEvents = new List<string>();
        WinEventProcedure observe = (_, eventId, hwnd, obj, child, _, _) =>
        { if (nativeEvents.Count < 60) nativeEvents.Add($"event={eventId:X} hwnd={(long)hwnd} object={obj} child={child}"); };
        OverlayHost? host = null;
        var observers = new List<PositionCounter>();
        try
        {
            string ready = peer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult() ?? "";
            var identity = ready.Split(' ');
            check(identity.Length == 2 && int.Parse(identity[0]) == peer.Id && long.TryParse(identity[1], out _), "The fixture receives its own child process HWND");
            foreign = (nint)long.Parse(identity[1]);
            eventHook = SetWinEventHook(0x8002, 0x8004, 0, observe, 0, 0, 0);
            check(GetWindowThreadProcessId(foreign, out uint owner) != 0 && owner == peer.Id && owner != Environment.ProcessId,
                "The blocker belongs to a real foreign PID, not a same-process stand-in");
            host = new OverlayHost(OverlayMonitorScope.AllScreens, () => [first, second], false);
            var errors = new List<Exception>(); host.Error += errors.Add;
            host.SetVisible(true);
            host.EnableImageDrops(_ => false, (_, _) => false);
            check(host.TryStartDropReception(out _), "Passive OLE reception supplies visible input without entering editing");
            var overlay = host.Diagnostics.ToArray();
            var displays = overlay.Select(row => row.DisplayHandle).ToArray();
            var inputs = overlay.Select(row => row.InputHandle).ToArray();
            tool = CreateOwn(left, top); popup = CreateOwn(left, top, tool); modal = CreateOwn(left, top, tool);
            EnableWindow(tool, false); // A real disabled-owner modal barrier, without making it foreground.
            Pump(250);
            foreach (nint display in displays) observers.Add(new PositionCounter(display));
            nint[] protectedHandles = inputs.Concat(new[] { tool, popup, modal }).ToArray();
            var protectedOrder = Ordered(protectedHandles);
            var geometry = displays.Concat(protectedHandles).ToDictionary(handle => handle, Bounds);
            var styles = displays.Concat(protectedHandles).ToDictionary(handle => handle, handle => (long)GetWindowLongPtr(handle, GwlExStyle));
            long editingVersion = host.EditingSessionVersion, lossVersion = host.ForegroundLossVersion;
            Check(SetWindowPos(foreign, HwndTopmost, 0, 0, 0, 0, 0x213));
            Console.WriteLine($"WINDOW_ORDER_GEOMETRY peer={Bounds(foreign)} displays={string.Join(';', displays.Select(Bounds))}");
            check(IsTopmost(foreign) && displays.All(display => Rank(foreign) < Rank(display)),
                "A real foreign topmost overlapping window starts ahead of both visible displays");
            var latency = Stopwatch.StartNew();
            bool restored = Until(() => displays.All(display => Rank(display) < Rank(foreign)), 1800);
            Console.WriteLine($"WINDOW_ORDER_REPAIR elapsedMs={latency.Elapsed.TotalMilliseconds:F1} restored={restored}");
            if (!restored)
            {
                Console.WriteLine("WINDOW_ORDER_EVENTS " + string.Join(';', nativeEvents));
                if (typeof(OverlayHost).GetMethod("ReadWindowOrder", BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, null) is System.Collections.IEnumerable sampled)
                    foreach (object row in sampled)
                        if (row.ToString()!.Contains($"Process = {peer.Id}") || row.ToString()!.Contains($"Process = {Environment.ProcessId}")) Console.WriteLine("WINDOW_ORDER_ROW " + row);
            }
            check(restored, "Real external topmost reorder events automatically recover both displays");
            check(displays.All(IsTopmost), "Recovered display HWNDs retain their actual WS_EX_TOPMOST bit");
            check(Ordered(protectedHandles).SequenceEqual(protectedOrder) && protectedHandles.All(handle => displays.All(display => Rank(handle) < Rank(display))),
                "Native batch keeps visible input, tool, popup and owned modal in their original relative order above every display");
            check(geometry.All(pair => Bounds(pair.Key) == pair.Value) && styles.All(pair => (long)GetWindowLongPtr(pair.Key, GwlExStyle) == pair.Value),
                "Recovery changes no fixture geometry or extended styles");
            check(!host.IsEditing && host.IsReceivingDrop && !host.IsKeyboardIsolationActive &&
                host.EditingSessionVersion == editingVersion && host.ForegroundLossVersion == lossVersion && GetCapture() == initialCapture,
                "Recovery preserves passive OLE mode, editing epoch, focus-loss version and mouse capture");

            Pump(350); int healthyPositions = observers.Sum(item => item.Count);
            for (int i = 0; i < 60; i++) { NotifyWinEvent(0x8004, foreign, 0, 0); Pump(2); }
            Pump(400);
            check(observers.Sum(item => item.Count) == healthyPositions,
                "Healthy reorder signal bursts produce no display WINDOWPOS writes or feedback loop");
            Check(SetWindowPos(foreign, HwndTopmost, 0, 0, 0, 0, 0x213));
            bool recoveredDuringBurst = false;
            for (int i = 0; i < 80; i++)
            {
                NotifyWinEvent(0x8004, foreign, 0, 0); Pump(3);
                recoveredDuringBurst |= displays.All(display => Rank(display) < Rank(foreign));
            }
            check(recoveredDuringBurst, "A continuing event burst does not postpone repair until the burst ends");
            Pump(400); int settledPositions = observers.Sum(item => item.Count); Pump(350);
            check(observers.Sum(item => item.Count) == settledPositions, "The bounded follow-up settles instead of periodically raising healthy displays");

            // Test-only fault injection: alter only our own HWNDs. This does not reproduce the original corrupting caller.
            foreach (nint display in displays)
            {
                Check(SetWindowPos(display, -2, 0, 0, 0, 0, 0x213)); // HWND_NOTOPMOST
                SetWindowLongPtr(display, GwlExStyle, (nint)(styles[display] | ExTopmost));
            }
            Check(SetWindowPos(foreign, -2, 0, 0, 0, 0, 0x213));
            Check(SetWindowPos(foreign, 0, 0, 0, 0, 0, 0x213)); // Highest ordinary window, still no activation.
            bool flaggedInversion = !IsTopmost(foreign) && displays.All(display => IsTopmost(display) && Rank(foreign) < Rank(display));
            Console.WriteLine($"WINDOW_ORDER_FAULT_INJECTION flaggedInversionSupported={flaggedInversion}; artificial own-HWND state, not the original trigger");
            if (flaggedInversion)
            {
                check(Until(() => displays.All(display => IsTopmost(display) && Rank(display) < Rank(foreign)), 1200),
                    "Fault-injected ordinary-above-flagged-topmost inversion is repaired through real desktop reorder events");
            }
            else
            {
                foreach (nint display in displays) Check(SetWindowPos(display, -2, 0, 0, 0, 0, 0x213));
                Check(SetWindowPos(foreign, 0, 0, 0, 0, 0, 0x213));
                check(displays.All(display => !IsTopmost(display) && Rank(foreign) < Rank(display)),
                    "This OS instead supplies an explicit lost-topmost-flag fault injection");
                check(Until(() => displays.All(display => IsTopmost(display) && Rank(display) < Rank(foreign)), 1200),
                    "An ordinary overlapping blocker above lost-topmost displays is repaired");
            }
            check(Ordered(protectedHandles).SequenceEqual(protectedOrder), "Fault injection recovery also preserves the protected input/tool/modal order");

            host.EndDropReception(); host.SetVisible(false); Pump(250);
            int hiddenPositions = observers.Sum(item => item.Count);
            Check(SetWindowPos(foreign, HwndTopmost, 0, 0, 0, 0, 0x213)); Pump(350);
            check(displays.All(handle => !IsWindowVisible(handle)) && observers.Sum(item => item.Count) == hiddenPositions,
                "Foreign reorder events never show or reposition an explicitly hidden display");
            host.SetVisible(true); Until(() => displays.All(display => Rank(display) < Rank(foreign)), 1000); Pump(250);
            host.ForceLegacyCleanCapture = true;
            using (var capture = host.BeginCleanCapture())
            {
                check(capture.Strategy == CaptureVisibilityStrategy.LegacyInputOffscreen && displays.All(handle => !IsWindowVisible(handle)),
                    "A real clean-capture fallback temporarily hides the own display HWNDs");
                int capturingPositions = observers.Sum(item => item.Count);
                Check(SetWindowPos(foreign, HwndTopmost, 0, 0, 0, 0, 0x213)); Pump(400);
                check(host.IsCapturing && displays.All(handle => !IsWindowVisible(handle)) && observers.Sum(item => item.Count) == capturingPositions,
                    "Capture suspension prevents recovery from exposing or repositioning capture-hidden visuals");
            }
            host.ForceLegacyCleanCapture = false;
            check(Until(() => displays.All(display => IsWindowVisible(display) && Rank(display) < Rank(foreign)), 1000),
                "Capture completion queues recovery after the scope restores its own visuals");
            check(errors.Count == 0 && GetFocus() == initialFocus && GetCapture() == initialCapture && GetThreadDpiAwarenessContext() == initialDpi,
                "Recovery reports no host errors and preserves thread focus, capture and DPI context");
            nint finalForeground = GetForegroundWindow();
            check(!displays.Concat(protectedHandles).Append(host.ControlHandle).Append(foreign).Contains(finalForeground),
                "No fixture window takes foreground");
            Console.WriteLine($"WINDOW_ORDER_FOREGROUND before={(long)initialForeground} after={(long)finalForeground} unchanged={initialForeground == finalForeground}; external changes are not attributable from snapshots alone");
            foreach (var observer in observers) observer.Dispose(); observers.Clear();
            host.Dispose(); host = null;
            Check(SetWindowPos(foreign, HwndTopmost, 0, 0, 0, 0, 0x213)); Pump(250);
            check(displays.Concat(inputs).All(handle => !IsWindow(handle)), "Disposal removes all monitor windows and later peer events are harmless");
        }
        finally
        {
            if (eventHook != 0) UnhookWinEvent(eventHook);
            GC.KeepAlive(observe);
            foreach (var observer in observers) observer.Dispose();
            host?.Dispose();
            if (modal != 0) DestroyWindow(modal); if (popup != 0) DestroyWindow(popup); if (tool != 0) DestroyWindow(tool);
            if (foreign != 0 && IsWindow(foreign)) PostMessageW(foreign, 0x10, 0, 0);
            if (!peer.WaitForExit(2000)) { peer.Kill(); peer.WaitForExit(); } // Only the child created above.
        }
    }

    private static nint CreateOwn(int left, int top, nint owner = 0)
    {
        nint handle = CreateWindowExW(ExTopmost | ExToolWindow | ExNoActivate | ExLayered, "STATIC", "FloatCanvas own z-order fixture",
            WsPopup, left, top, 160, 80, owner, 0, GetModuleHandleW(null), 0);
        Check(handle != 0 && SetLayeredWindowAttributes(handle, 0, 1, 2)); ShowWindow(handle, 8); return handle;
    }
    private static nint[] Ordered(IEnumerable<nint> handles) => handles.OrderBy(Rank).ToArray();
    private static int Rank(nint target)
    {
        int rank = 0; var visited = new HashSet<nint>();
        for (nint handle = GetTopWindow(0); handle != 0 && rank < 8192 && visited.Add(handle); handle = GetWindow(handle, 2), rank++)
            if (handle == target) return rank;
        return int.MaxValue;
    }
    private static bool IsTopmost(nint handle) => ((long)GetWindowLongPtr(handle, GwlExStyle) & ExTopmost) != 0;
    private static PixelBounds Bounds(nint handle) { Check(GetWindowRect(handle, out var bounds)); return bounds.Bounds; }
    private static bool Until(Func<bool> condition, int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < milliseconds) Pump(5);
        return condition();
    }
    private static void Pump(int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        do { while (PeekMessageW(out var message, 0, 0, 0, 1)) { TranslateMessage(ref message); DispatchMessageW(ref message); } Thread.Sleep(1); }
        while (clock.ElapsedMilliseconds < milliseconds);
    }
    private static void Check(bool result) { if (!result) throw new Win32Exception(Marshal.GetLastWin32Error()); }

    private sealed class PositionCounter : IDisposable
    {
        private readonly nint handle, previous;
        private readonly WindowProcedure procedure;
        internal int Count;
        internal PositionCounter(nint window)
        {
            handle = window; procedure = Observe;
            previous = SetWindowLongPtr(window, -4, Marshal.GetFunctionPointerForDelegate(procedure));
            if (previous == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        private nint Observe(nint hwnd, uint message, nuint wParam, nint lParam)
        { if (message == 0x46) Count++; return CallWindowProcW(previous, hwnd, message, wParam, lParam); }
        public void Dispose() { if (IsWindow(handle)) SetWindowLongPtr(handle, -4, previous); GC.KeepAlive(procedure); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Message
    { public nint Hwnd; public uint Id; public nuint WParam; public nint LParam; public uint Time; public Point Point; public uint Private; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessageW(out Message message, nint hwnd, uint first, uint last, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(ref Message message);
    [DllImport("user32.dll")] private static extern nint CallWindowProcW(nint previous, nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern bool EnableWindow(nint hwnd, bool enabled);
    [DllImport("user32.dll")] private static extern void NotifyWinEvent(uint eventId, nint hwnd, int objectId, int childId);
}
