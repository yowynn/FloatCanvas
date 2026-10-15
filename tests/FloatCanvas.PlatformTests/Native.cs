using System.ComponentModel;
using System.Runtime.InteropServices;

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
