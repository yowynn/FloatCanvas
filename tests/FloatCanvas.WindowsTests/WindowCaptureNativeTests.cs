using System.Diagnostics;
using System.Runtime.InteropServices;
using FloatCanvas.Windows;
using SkiaSharp;

namespace FloatCanvas.WindowsTests;

/// <summary>Opt-in WGC fixture. Only its own unactivated solid-color window is captured.</summary>
internal static class WindowCaptureNativeTests
{
    internal static void Run(Action<bool, string> check)
    {
        using var dpi = new ThreadDpiAwarenessScope(WindowsCapabilities.Current);
        using var source = new SourceWindow();
        nint foreground = GetForegroundWindow();
        check(WindowCaptureCatalog.TryRead(source.Handle, out var target) && target.IsCurrent, "Explicit own-window metadata yields a current runtime target");
        using var capture = new LiveWindowCapture(target);
        var first = WaitFrame(capture, frame => IsColor(frame.Bitmap, SKColors.Red));
        using (first)
        {
            Console.WriteLine($"WINDOW_CAPTURE_FIRST content={first.ContentSize} bitmap={first.Bitmap.Width}x{first.Bitmap.Height} center={first.Bitmap.GetPixel(first.Bitmap.Width / 2, first.Bitmap.Height / 2)}");
            check(first.Bitmap.IsImmutable && first.Bitmap.Width > 0 && first.ContentSize.Width > 0, "WGC returns an owned immutable frame of the selected window");
            check(IsColor(first.Bitmap, SKColors.Red), "Captured WGC pixels track the fixture's red window");
            check(capture.Status == WindowCaptureStatus.Live && first.ConnectionVersion == capture.ConnectionVersion, "A delivered frame identifies the live connection version");
            long oldStream = first.StreamId, oldConnection = first.ConnectionVersion, oldSequence = first.Sequence;
            source.Color(0x00FF0000); // COLORREF blue.
            using var blue = WaitFrame(capture, frame => frame.Sequence > oldSequence && IsColor(frame.Bitmap, SKColors.Blue));
            check(blue.StreamId == oldStream && blue.Sequence > oldSequence && IsColor(first.Bitmap, SKColors.Red), "New source pixels do not mutate a retained earlier frame");
            source.Resize(640, 360);
            using var resized = WaitFrame(capture, frame => frame.ContentSize.Width == 640 && frame.ContentSize.Height == 360);
            check(resized.Bitmap.Width == 640 && resized.Bitmap.Height == 360 && IsColor(resized.Bitmap, SKColors.Blue), "Window resize recreates the full capture pool and preserves source color");
            source.Minimize(); Wait(() => capture.Status == WindowCaptureStatus.Minimized, "minimized status");
            check(capture.Status == WindowCaptureStatus.Minimized && !capture.Completion.IsCompleted, "Minimizing pauses the pool without ending its capture item identity");
            source.Restore();
            using var restored = WaitFrame(capture, frame => frame.Sequence > resized.Sequence);
            check(restored.StreamId == oldStream && IsColor(restored.Bitmap, SKColors.Blue), "Restoring resumes the same selected item without rebinding by title");
            source.Hide(); Wait(() => capture.Status == WindowCaptureStatus.Hidden, "hidden status");
            check(capture.Status == WindowCaptureStatus.Hidden && !capture.Completion.IsCompleted, "Hiding suspends the pool while retaining the selected window item");
            source.Restore();
            using var shown = WaitFrame(capture, frame => frame.Sequence > restored.Sequence);
            check(shown.StreamId == oldStream && IsColor(shown.Bitmap, SKColors.Blue), "Showing the same window resumes its original item after hidden suspension");
            capture.SetActive(false);
            check(!capture.TryGetLatest(out _), "Pause immediately prevents pending frame adoption");
            Wait(() => capture.Status == WindowCaptureStatus.Paused, "paused status");
            long pausedConnection = capture.ConnectionVersion;
            capture.Resume();
            using var resumed = WaitFrame(capture, frame => frame.ConnectionVersion > pausedConnection);
            check(resumed.ConnectionVersion > oldConnection && resumed.StreamId == oldStream, "Explicit resume starts a new generation of the original capture item");
            capture.SetActive(false); Wait(() => capture.Status == WindowCaptureStatus.Paused, "second pause");
            source.Close();
            Wait(() => capture.Completion.IsCompleted, "closed item completion");
            check(capture.Status == WindowCaptureStatus.Closed && !target.IsCurrent, "A window closed while paused ends the retained item and cannot reuse its HWND");
            capture.Resume();
            check(capture.Status == WindowCaptureStatus.Closed && !capture.TryGetLatest(out _), "Resume cannot resurrect a closed capture item");
            check(IsColor(first.Bitmap, SKColors.Red), "A caller-owned old frame remains valid after the source and worker close");
        }
        check(GetForegroundWindow() == foreground, "Own-window capture and lifecycle changes leave the foreground window unchanged");
        Console.WriteLine($"WINDOW_CAPTURE_NATIVE stream={capture.StreamId} arrivals={capture.ArrivalCount} readbacks={capture.ReadbackCount} status={capture.Status}; only own fixture pixels.");
    }
    private static bool IsColor(SKBitmap bitmap, SKColor color)
    {
        var actual = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
        return Math.Abs(actual.Red - color.Red) <= 2 && Math.Abs(actual.Green - color.Green) <= 2 && Math.Abs(actual.Blue - color.Blue) <= 2 && actual.Alpha >= 250;
    }
    private static LiveWindowFrame WaitFrame(LiveWindowCapture capture, Func<LiveWindowFrame, bool> accept)
    {
        LiveWindowFrame? result = null; int rejected = 0;
        try { Wait(() =>
        {
            if (capture.TryGetLatest(out var candidate))
            {
                if (accept(candidate)) { result = candidate; return true; }
                if (++rejected <= 8) Console.WriteLine($"WINDOW_CAPTURE_REJECT sequence={candidate.Sequence} content={candidate.ContentSize} pixels={candidate.Bitmap.Width}x{candidate.Bitmap.Height} center={candidate.Bitmap.GetPixel(candidate.Bitmap.Width / 2, candidate.Bitmap.Height / 2)}");
                candidate.Dispose();
            }
            if (capture.Status is WindowCaptureStatus.Unavailable or WindowCaptureStatus.Closed)
                throw new InvalidOperationException("Own-window capture stopped: " + capture.Status, capture.LastError);
            return false;
        }, "own window frame"); }
        catch { Console.WriteLine($"WINDOW_CAPTURE_WAIT_FAILED status={capture.Status} arrivals={capture.ArrivalCount} readbacks={capture.ReadbackCount} rejected={rejected} error={capture.LastError}"); throw; }
        return result!;
    }
    private static void Wait(Func<bool> condition, string operation)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Timed out waiting for " + operation);
            while (PeekMessage(out var message, 0, 0, 0, 1)) { TranslateMessage(ref message); DispatchMessage(ref message); }
            Thread.Sleep(5);
        }
    }
    private sealed class SourceWindow : IDisposable
    {
        private readonly string name = "FloatCanvas.OwnWindowCapture." + Guid.NewGuid().ToString("N");
        private readonly WindowProcedure procedure = DefWindowProc;
        private readonly List<nint> brushes = [];
        private readonly nint module = GetModuleHandle(null);
        private readonly int location = Environment.GetEnvironmentVariable("FLOATCANVAS_WINDOW_CAPTURE_VISIBLE_FIXTURE") == "1" ? 20 : -22000;
        internal nint Handle { get; private set; }
        internal SourceWindow()
        {
            var brush = CreateSolidBrush(0x000000FF); brushes.Add(brush);
            var klass = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = procedure, Instance = module, Name = name, Background = brush };
            if (RegisterClassEx(ref klass) == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            Handle = CreateWindowEx(0x08000080, name, "FloatCanvas owned capture fixture", 0x90000000, location, location, 320, 240, 0, 0, module, 0);
            if (Handle == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            ShowWindow(Handle, 4); InvalidateRect(Handle, 0, true); UpdateWindow(Handle);
        }
        internal void Color(uint color)
        {
            var brush = CreateSolidBrush(color); brushes.Add(brush); SetClassLongPtr(Handle, -10, brush);
            InvalidateRect(Handle, 0, true); UpdateWindow(Handle); DwmFlush();
        }
        internal void Resize(int width, int height) { SetWindowPos(Handle, 0, location, location, width, height, 0x14); InvalidateRect(Handle, 0, true); UpdateWindow(Handle); }
        internal void Minimize() => ShowWindow(Handle, 7);
        internal void Hide() => ShowWindow(Handle, 0);
        internal void Restore() { ShowWindow(Handle, 4); InvalidateRect(Handle, 0, true); UpdateWindow(Handle); }
        internal void Close() { if (Handle != 0) { DestroyWindow(Handle); Handle = 0; } }
        public void Dispose() { Close(); UnregisterClass(name, module); foreach (var brush in brushes) DeleteObject(brush); GC.KeepAlive(procedure); }
    }
    private delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        internal uint Size, Style; internal WindowProcedure Procedure; internal int ClassExtra, WindowExtra;
        internal nint Instance, Icon, Cursor, Background; internal string? Menu; internal string Name; internal nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Message { internal nint Window; internal uint Id; internal nuint WParam; internal nint LParam; internal uint Time; internal int X, Y; internal uint Private; }
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extended, string klass, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, nint module);
    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")] private static extern nint SetClassLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(nint window, nint rectangle, bool erase);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(nint window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] private static extern bool PeekMessage(out Message message, nint window, uint first, uint last, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
