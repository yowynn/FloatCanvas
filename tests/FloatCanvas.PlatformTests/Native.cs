using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace FloatCanvas.PlatformTests;

internal static class Native
{
    internal delegate nint WndProc(nint hWnd, uint msg, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)] internal struct Point(int x, int y) { public int X = x; public int Y = y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect(int l, int t, int r, int b) { public int Left=l, Top=t, Right=r, Bottom=b; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] internal struct WindowClass
    {
        public uint Size, Style; public nint WindowProc; public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background; public string? MenuName, ClassName; public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Paint
    {
        public nint DeviceContext; public int Erase; public Rect Rectangle; public int Restore, Incremental;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst=32)] public byte[] Reserved;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    {
        public int X, Y; public uint Data, Flags, Time; public nuint Extra;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    {
        public ushort Key, Scan; public uint Flags, Time; public nuint Extra;
    }
    internal static void Move(Point p)
    {
        var x = GetSystemMetrics(76); var y = GetSystemMetrics(77);
        var width = GetSystemMetrics(78); var height = GetSystemMetrics(79);
        Send(new Input { Type=0, Data=new InputUnion { Mouse=new MouseInput {
            X=(int)Math.Round((p.X-x)*65535.0/(width-1)), Y=(int)Math.Round((p.Y-y)*65535.0/(height-1)), Flags=0xC001 } } });
    }
    internal static void Mouse(uint flags, uint data=0) => Send(new Input { Type=0,
        Data=new InputUnion { Mouse=new MouseInput { Flags=flags, Data=data } } });
    internal static void Key(ushort key, bool up=false) => Send(new Input { Type=1,
        Data=new InputUnion { Keyboard=new KeyboardInput { Key=key, Flags=up ? 2u : 0u } } });
    private static void Send(Input input)
    {
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SendInput to the self-owned test window failed.");
    }
    internal static nint ExtendedStyle(nint window) => GetWindowLongPtr(window, -20);
    internal static bool IsOwnWindow(nint window) { GetWindowThreadProcessId(window, out uint owner); return window != 0 && owner == Environment.ProcessId; }
    internal static async Task ActivateOwnedFixtureAsync(NativeTestWindow target)
    {
        using var process = Process.GetCurrentProcess(); long started = process.StartTime.ToUniversalTime().Ticks;
        nint window = target.Handle; bool topmost = ((long)ExtendedStyle(window) & 8) != 0;
        GetCursorPos(out var previousCursor); bool held = false;
        var point = target.ScreenPoint(600, 400);
        try
        {
            RequireIdentity();
            if (!SetWindowPos(window, -1, 0, 0, 0, 0, 0x53)) throw new Win32Exception();
            if (DwmFlush() != 0) throw new InvalidOperationException("Owned fixture compositor flush failed.");
            await Task.Delay(70);
            RequirePoint();
            nint dc = GetDC(0); uint color;
            try { color = GetPixel(dc, point.X, point.Y); } finally { ReleaseDC(0, dc); }
            if ((color & 255) < 245 || ((color >> 8) & 255) < 245 || ((color >> 16) & 255) < 245)
                throw new InvalidOperationException("Owned activation point is not the expected white client background.");
            int clicks = target.Counts.Clicks, ups = target.Counts.Ups;
            RequirePoint(); Move(point); RequirePoint(); Mouse(2); held = true; RequirePoint(); Mouse(4); held = false;
            var wait = Stopwatch.StartNew();
            while (GetForegroundWindow() != window || target.Counts.Clicks != clicks + 1 || target.Counts.Ups != ups + 1)
            { if (wait.ElapsedMilliseconds > 1500) throw new InvalidOperationException("Owned click did not activate its exact fixture HWND."); await Task.Delay(10); }
            Console.WriteLine($"OWNED_FIXTURE_ACTIVATED pid={process.Id} startedUtcTicks={started} hwnd={window} point={point.X},{point.Y} clicks={target.Counts.Clicks} ups={target.Counts.Ups}");
        }
        finally
        {
            if (held && IsOwnWindow(window) && (GetCapture() == window || WindowFromPoint(point) == window)) Mouse(4);
            if (IsOwnWindow(window)) SetWindowPos(window, topmost ? -1 : -2, 0, 0, 0, 0, 0x13);
            SetCursorPos(previousCursor.X, previousCursor.Y);
        }
        void RequireIdentity()
        {
            if (process.HasExited || process.Id != Environment.ProcessId || process.StartTime.ToUniversalTime().Ticks != started ||
                target.Handle != window || !IsOwnWindow(window) || !IsWindowVisible(window))
                throw new InvalidOperationException("Owned activation fixture PID/time/HWND identity changed.");
        }
        void RequirePoint()
        { RequireIdentity(); if (!target.OwnsPoint(point) || WindowFromPoint(point) != window) throw new InvalidOperationException("Owned activation point does not hit its PID/time-verified HWND; no input is allowed."); }
    }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint owner);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll", SetLastError=true)] private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint SetFocus(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetCapture();
    [DllImport("user32.dll")] internal static extern nint SetCapture(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool ReleaseCapture();
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern ushort RegisterClassEx(ref WindowClass wc);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern nint CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", EntryPoint="DefWindowProcW", CharSet=CharSet.Unicode)] internal static extern nint DefWindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] internal static extern bool UnregisterClass(string name, nint instance);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool UpdateWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll")] internal static extern bool ScreenToClient(nint hwnd, ref Point point);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] internal static extern uint GetPixel(nint dc, int x, int y);
    [DllImport("user32.dll", SetLastError=true)] internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] internal static extern nint BeginPaint(nint hwnd, out Paint paint);
    [DllImport("user32.dll")] internal static extern bool EndPaint(nint hwnd, ref Paint paint);
    [DllImport("user32.dll")] internal static extern int FillRect(nint hdc, ref Rect rect, nint brush);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] internal static extern int DrawText(nint hdc, string text, int length, ref Rect rect, uint format);
}
