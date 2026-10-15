using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FloatCanvas.MouseTests;

internal static class Native
{
    internal const uint MouseMove = 0x200, LeftDown = 0x201, LeftUp = 0x202,
        RightDown = 0x204, RightUp = 0x205, MiddleDown = 0x207, MiddleUp = 0x208,
        Wheel = 0x20A, XDown = 0x20B, XUp = 0x20C, HWheel = 0x20E;
    internal static readonly int[] ButtonKeys = [1, 2, 4, 5, 6];
    internal delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);
    internal delegate nint HookProcedure(int code, nuint message, nint data);
    [StructLayout(LayoutKind.Sequential)] internal struct Point(int x, int y)
    {
        public int X = x, Y = y;
        public readonly PixelPoint Value => new(X, Y);
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly bool Contains(PixelPoint p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct WindowClass
    {
        public uint Size, Style; public nint Procedure; public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background; public string? MenuName, ClassName; public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Paint
    {
        public nint DC; public int Erase; public Rect Rectangle; public int Restore, Incremental;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Message
    {
        public nint Window; public uint Id; public nuint WParam; public nint LParam;
        public uint Time; public Point Position; public uint Private;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseHookData
    {
        public Point Position; public uint MouseData, Flags, Time; public nuint Extra;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct RawDevice(ushort page, ushort usage, uint flags, nint target)
    {
        public ushort UsagePage = page, Usage = usage; public uint Flags = flags; public nint Target = target;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct RawHeader
    {
        public uint Type, Size; public nint Device, WParam;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct RawMouse
    {
        public ushort Flags, Padding; public uint Buttons, RawButtons;
        public int LastX, LastY; public uint Extra;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct InputMessageSource
    {
        public uint DeviceType, OriginId;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Input
    {
        public uint Type; public MouseInput Data;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    {
        public int X, Y; public uint Data, Flags, Time; public nuint Extra;
    }

    internal static string Name(uint message, uint data = 0) => message switch
    {
        MouseMove => "Move", LeftDown => "LeftDown", LeftUp => "LeftUp",
        RightDown => "RightDown", RightUp => "RightUp", MiddleDown => "MiddleDown", MiddleUp => "MiddleUp",
        XDown => $"X{data >> 16}Down", XUp => $"X{data >> 16}Up", Wheel => "Wheel", HWheel => "HWheel", _ => $"0x{message:X}"
    };
    internal static bool IsMouse(uint message) => message is MouseMove or LeftDown or LeftUp or RightDown or RightUp
        or MiddleDown or MiddleUp or XDown or XUp or Wheel or HWheel;
    internal static string? Button(uint message, uint data) => message switch
    {
        LeftDown or LeftUp => "Left", RightDown or RightUp => "Right", MiddleDown or MiddleUp => "Middle",
        XDown or XUp => $"X{data >> 16}", _ => null
    };
    internal static bool IsDown(uint message) => message is LeftDown or RightDown or MiddleDown or XDown;
    internal static bool IsUp(uint message) => message is LeftUp or RightUp or MiddleUp or XUp;
    internal static PixelPoint Cursor()
    {
        if (!GetCursorPos(out var p)) throw new Win32Exception(Marshal.GetLastWin32Error(), "GetCursorPos failed.");
        return p.Value;
    }
    internal static uint Pid(nint window) { GetWindowThreadProcessId(window, out uint pid); return pid; }
    internal static Rect ClientBounds(nint window)
    {
        if (!GetClientRect(window, out var rect)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var a = new Point(rect.Left, rect.Top); var b = new Point(rect.Right, rect.Bottom);
        if (!ClientToScreen(window, ref a) || !ClientToScreen(window, ref b)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new Rect { Left = a.X, Top = a.Y, Right = b.X, Bottom = b.Y };
    }
    internal static void SendMove(PixelPoint p)
    {
        int x = GetSystemMetrics(76), y = GetSystemMetrics(77), w = GetSystemMetrics(78), h = GetSystemMetrics(79);
        if (w <= 1 || h <= 1) throw new InvalidOperationException("Invalid virtual desktop size.");
        Send(new MouseInput { X = (int)Math.Round((p.X - x) * 65535d / (w - 1)),
            Y = (int)Math.Round((p.Y - y) * 65535d / (h - 1)), Flags = 0xC001, Extra = StimulusTag });
    }
    // Use a distinctive value that fits both ULONG_PTR and RAWMOUSE's ULONG extra field.
    // The observed chain retained only the low 32 bits of our original 64-bit marker.
    // Comparison still requires the complete expected value; no masking is used for IsStimulus.
    internal const ulong TagValue = 0x46434D32; // FCM2: stimulus identification, never a relay marker.
    internal static nuint StimulusTag => unchecked((nuint)TagValue);
    internal static object InteropLayoutEvidence() => new
    {
        PointerBytes = IntPtr.Size, InputBytes = Marshal.SizeOf<Input>(), MouseInputBytes = Marshal.SizeOf<MouseInput>(),
        InputMouseOffset = Marshal.OffsetOf<Input>(nameof(Input.Data)).ToInt32(),
        MouseInputExtraOffset = Marshal.OffsetOf<MouseInput>(nameof(MouseInput.Extra)).ToInt32(),
        MouseHookBytes = Marshal.SizeOf<MouseHookData>(), MouseHookExtraOffset = Marshal.OffsetOf<MouseHookData>(nameof(MouseHookData.Extra)).ToInt32(),
        ExpectedTag = TagValue, ExpectedTagHex = $"0x{TagValue:X16}", ExpectedTagLow32Hex = $"0x{unchecked((uint)TagValue):X8}"
    };
    internal static void SendMouse(uint flags, uint data = 0) => Send(new MouseInput { Flags = flags, Data = data, Extra = StimulusTag });
    private static void Send(MouseInput input)
    {
        if (SendInput(1, [new Input { Type = 0, Data = input }], Marshal.SizeOf<Input>()) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Guarded own-window SendInput stimulus failed.");
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] internal static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern nint GetMessageExtraInfo();
    [DllImport("user32.dll")] internal static extern int GetMessageTime();
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetCurrentInputMessageSource(out InputMessageSource source);
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ushort RegisterClassEx(ref WindowClass value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", CharSet = CharSet.Unicode)] internal static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool UnregisterClass(string name, nint instance);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool UpdateWindow(nint window);
    [DllImport("user32.dll")] internal static extern bool InvalidateRect(nint window, nint rect, bool erase);
    [DllImport("user32.dll")] internal static extern nint BeginPaint(nint window, out Paint paint);
    [DllImport("user32.dll")] internal static extern bool EndPaint(nint window, ref Paint paint);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int DrawText(nint dc, string text, int count, ref Rect rect, uint format);
    [DllImport("gdi32.dll")] internal static extern bool Ellipse(nint dc, int left, int top, int right, int bottom);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint GetRawInputData(nint input, uint command, nint data, ref uint size, uint headerSize);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWindowsHookEx(int kind, HookProcedure callback, nint module, uint thread);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] internal static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] internal static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] internal static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}

internal sealed record PixelPoint(int X, int Y);
