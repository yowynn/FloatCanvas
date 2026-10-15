using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FloatCanvas.PlatformTests;

/// <summary>A real, disposable lower application. Input injection is restricted to its rectangle.</summary>
internal sealed class NativeTestWindow : IDisposable
{
    private readonly Native.WndProc _wndProc;
    private readonly string _className = "FloatCanvas.PlatformTests." + Guid.NewGuid().ToString("N");
    private bool _dragging;
    private Native.Point _down;
    public nint Handle { get; private set; }
    public InputCounts Counts { get; } = new();

    public NativeTestWindow()
    {
        _wndProc = WindowProc;
        var wc = new Native.WindowClass
        {
            Size = (uint)Marshal.SizeOf<Native.WindowClass>(),
            Style = 0x0008, // CS_DBLCLKS
            WindowProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            Instance = Native.GetModuleHandle(null),
            Background = (nint)6,
            ClassName = _className
        };
        if (Native.RegisterClassEx(ref wc) == 0) throw new Win32Exception();
        var width = Math.Min(800, Native.GetSystemMetrics(0) - 80);
        var height = Math.Min(560, Native.GetSystemMetrics(1) - 80);
        Handle = Native.CreateWindowEx(0, _className, "浮绘 · 自建输入验证窗口", 0x10CF0000,
            40, 40, width, height, 0, 0, wc.Instance, 0);
        if (Handle == 0) throw new Win32Exception();
        Native.ShowWindow(Handle, 5);
        Native.UpdateWindow(Handle);
    }

    public Native.Point ScreenPoint(int x, int y)
    {
        var p = new Native.Point(x, y);
        Native.ClientToScreen(Handle, ref p);
        return p;
    }

    public bool OwnsPoint(Native.Point point)
    {
        Native.GetClientRect(Handle, out var r);
        var p = point;
        Native.ScreenToClient(Handle, ref p);
        return p.X >= 0 && p.Y >= 0 && p.X < r.Right && p.Y < r.Bottom;
    }

    public void Activate()
    {
        Native.SetForegroundWindow(Handle);
        Native.SetFocus(Handle);
    }

    private nint WindowProc(nint window, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case 0x000F: // WM_PAINT
                var dc = Native.BeginPaint(window, out var ps);
                Native.GetClientRect(window, out var rect);
                Native.FillRect(dc, ref rect, (nint)6);
                var textRect = new Native.Rect(20, 12, 700, 90);
                Native.DrawText(dc,
                    "FloatCanvas M1 native input target\n测试仅向此窗口注入输入；完成后自动关闭。\n蓝色不透明标注覆盖区和空白区均应可穿透。", -1, ref textRect, 0x10);
                Native.EndPaint(window, ref ps);
                return 0;
            case 0x0201: // WM_LBUTTONDOWN
            case 0x0203: // WM_LBUTTONDBLCLK
                if (message == 0x0201) Counts.Clicks++; else Counts.DoubleClicks++;
                _dragging = true;
                _down = PointFromLParam(lParam);
                Native.SetCapture(window);
                Native.SetFocus(window);
                return 0;
            case 0x0200:
                if (_dragging)
                {
                    var p = PointFromLParam(lParam);
                    if (Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) > 10) Counts.DragMoves++;
                }
                return 0;
            case 0x0202:
                Counts.Ups++;
                _dragging = false;
                Native.ReleaseCapture();
                return 0;
            case 0x020A:
                Counts.Wheels++;
                return 0;
            case 0x0100:
                if (wParam == 0x41) Counts.KeysA++;
                return 0;
            case 0x0002:
                return 0;
        }
        return Native.DefWindowProc(window, message, wParam, lParam);
    }

    private static Native.Point PointFromLParam(nint value) =>
        new(unchecked((short)((long)value & 0xffff)), unchecked((short)(((long)value >> 16) & 0xffff)));

    public void Dispose()
    {
        if (Handle != 0) Native.DestroyWindow(Handle);
        Handle = 0;
        Native.UnregisterClass(_className, Native.GetModuleHandle(null));
        GC.KeepAlive(_wndProc);
    }
}

internal sealed class InputCounts
{
    public int Clicks { get; set; }
    public int DoubleClicks { get; set; }
    public int Ups { get; set; }
    public int DragMoves { get; set; }
    public int Wheels { get; set; }
    public int KeysA { get; set; }
    public InputCounts Snapshot() => (InputCounts)MemberwiseClone();
    public bool SameAs(InputCounts other) => Clicks == other.Clicks && DoubleClicks == other.DoubleClicks &&
        Ups == other.Ups && DragMoves == other.DragMoves && Wheels == other.Wheels && KeysA == other.KeysA;
}
