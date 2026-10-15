using System.Runtime.InteropServices;

namespace FloatCanvas.Windows;

internal static class NativeMethods
{
    internal const int GwlpUserData = -21, GwlExStyle = -20;
    internal const uint WsPopup = 0x80000000, ExTopmost = 8, ExTransparent = 0x20, ExToolWindow = 0x80,
        ExLayered = 0x80000, ExNoActivate = 0x8000000;
    internal const uint WmNcCreate = 0x81, WmNcDestroy = 0x82, WmPaint = 0xF, WmEraseBackground = 0x14,
        WmClose = 0x10, WmDisplayChange = 0x7E, WmDpiChanged = 0x2E0, WmHotkey = 0x312,
        WmMouseMove = 0x200, WmLeftDown = 0x201, WmLeftUp = 0x202, WmLeftDouble = 0x203,
        WmRightDown = 0x204, WmRightUp = 0x205, WmRightDouble = 0x206, WmMiddleDown = 0x207,
        WmMiddleUp = 0x208, WmMiddleDouble = 0x209, WmMouseWheel = 0x20A,
        WmKeyDown = 0x100, WmSysKeyDown = 0x104, WmKillFocus = 8, WmCancelMode = 0x1F,
        WmCaptureChanged = 0x215, WmRefreshMonitors = 0x8001, WmValidateEditingFocus = 0x8002;
    internal static readonly nint HwndTopmost = -1;
    internal static readonly nint DpiPerMonitorV2 = -4;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint WindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void WinEventProcedure(nint hook, uint eventId, nint hwnd, int objectId, int childId, uint threadId, uint eventTime);
    internal delegate bool MonitorEnumProcedure(nint monitor, nint dc, ref Rect rect, nint data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClass
    {
        internal uint Size, Style;
        internal WindowProcedure Procedure;
        internal int ClassExtra, WindowExtra;
        internal nint Instance, Icon, Cursor, Background;
        internal string? MenuName;
        internal string ClassName;
        internal nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct CreateStructure
    {
        internal nint Parameter, Instance, Menu, Parent;
        internal int Height, Width, Y, X, Style;
        internal nint Name, Class;
        internal uint ExtendedStyle;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int X, Y; internal Point(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] internal struct Size { internal int Width, Height; internal Size(int width, int height) { Width = width; Height = height; } }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { internal int Left, Top, Right, Bottom; internal readonly PixelBounds Bounds => new(Left, Top, Right - Left, Bottom - Top); }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInformation
    {
        internal int Size;
        internal Rect Monitor, Work;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Device;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapHeader
    {
        internal uint Size;
        internal int Width, Height;
        internal ushort Planes, BitCount;
        internal uint Compression, SizeImage;
        internal int XPelsPerMeter, YPelsPerMeter;
        internal uint ClrUsed, ClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct BitmapInformation { internal BitmapHeader Header; internal uint Color; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] internal struct BlendFunction { internal byte Operation, Flags, ConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct LayeredInformation
    {
        internal uint Size;
        internal nint DestinationDc;
        internal Point* Destination;
        internal Size* Dimensions;
        internal nint SourceDc;
        internal Point* Source;
        internal uint ColorKey;
        internal BlendFunction* Blend;
        internal uint Flags;
        internal Rect* Dirty;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct PaintStructure
    {
        internal nint Dc;
        internal int Erase;
        internal Rect Paint;
        internal int Restore, IncUpdate;
        internal fixed byte Reserved[32];
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandleW(string? name);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ushort RegisterClassExW(ref WindowClass value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] internal static extern nint DefWindowProcW(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] internal static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] internal static extern nint GetTopWindow(nint parent);
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern unsafe bool UpdateLayeredWindowIndirect(nint hwnd, ref LayeredInformation value);
    [DllImport("user32.dll")] internal static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] internal static extern nint SetFocus(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetFocus();
    [DllImport("user32.dll")] internal static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProcedure callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] internal static extern nint SetCapture(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetCapture();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ReleaseCapture();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCursorPos(out Point position);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ClientToScreen(nint hwnd, ref Point position);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
    [DllImport("user32.dll")] internal static extern short GetKeyState(int key);
    [DllImport("user32.dll")] internal static extern nint LoadCursorW(nint instance, nint name);
    [DllImport("user32.dll")] internal static extern nint SetCursor(nint cursor);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnumProcedure callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetMonitorInfoW(nint monitor, ref MonitorInformation info);
    [DllImport("shcore.dll")] internal static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] internal static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] internal static extern nint GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] internal static extern int GetAwarenessFromDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessageW(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern nint BeginPaint(nint hwnd, out PaintStructure paint);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EndPaint(nint hwnd, ref PaintStructure paint);
    [DllImport("user32.dll")] internal static extern int FillRect(nint dc, ref Rect rect, nint brush);
    [DllImport("gdi32.dll")] internal static extern nint GetStockObject(int index);
    [DllImport("gdi32.dll")] internal static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern nint CreateDIBSection(nint dc, ref BitmapInformation info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] internal static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteDC(nint dc);
}
