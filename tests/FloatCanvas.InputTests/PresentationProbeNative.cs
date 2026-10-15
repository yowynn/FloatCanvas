using System.ComponentModel;
using System.Runtime.InteropServices;
using FloatCanvas.PlatformTests;
using FloatCanvas.Windows;

namespace FloatCanvas.InputTests;

internal static class PresentationProbeNative
{
    internal static int InputStructureBytes => Marshal.SizeOf<Input>();
    internal static int InputMouseOffsetBytes => Marshal.OffsetOf<Input>(nameof(Input.Data)).ToInt32();
    internal static int MouseExtraOffsetBytes => Marshal.OffsetOf<MouseInput>(nameof(MouseInput.Extra)).ToInt32();
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public Native.Rect CaretRect;
    }

    internal static uint OwnWindowThread(nint hwnd)
    {
        uint thread = GetWindowThreadProcessId(hwnd, out uint process);
        if (thread == 0 || process != Environment.ProcessId) throw new InvalidOperationException("Probe HWND no longer belongs to this process.");
        return thread;
    }

    internal static bool IsOwnWindow(nint hwnd) => GetWindowThreadProcessId(hwnd, out uint process) != 0 && process == Environment.ProcessId;

    internal static void GuardMove(nint input, uint thread, PixelBounds lowerClient, Native.Point point)
    {
        if (!lowerClient.Contains(new(point.X, point.Y)) || Native.GetForegroundWindow() != input ||
            Native.WindowFromPoint(point) != input || OwnWindowThread(input) != thread)
            throw new InvalidOperationException("Presentation producer stopped: its exact owned input HWND is not foreground and under the guarded point.");
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(thread, ref info) || info.Capture != input)
            throw new InvalidOperationException("Presentation producer stopped: the owned input HWND no longer has mouse capture.");
    }

    internal static void SendTaggedMove(Native.Point point, int sequence)
    {
        int x = Native.GetSystemMetrics(76), y = Native.GetSystemMetrics(77);
        int width = Native.GetSystemMetrics(78), height = Native.GetSystemMetrics(79);
        var input = new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput
        {
            X = (int)Math.Round((point.X - x) * 65535d / (width - 1)),
            Y = (int)Math.Round((point.Y - y) * 65535d / (height - 1)),
            Data = 0, Flags = 0xC001, Time = 0, Extra = PresentationMeasurements.EncodeSequence(sequence)
        } } };
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    // A targeted native button message avoids holding a global synthetic mouse button.
    // In particular, cleanup cannot send a global LeftUp into a newly chosen external foreground.
    internal static void PostButton(nint input, MonitorInfo tile, Native.Point point, bool down)
    {
        OwnWindowThread(input);
        int x = point.X - tile.Bounds.Left, y = point.Y - tile.Bounds.Top;
        nint position = (nint)((y & 0xFFFF) << 16 | (x & 0xFFFF));
        if (!PostMessage(input, down ? 0x0201u : 0x0202u, down ? 1u : 0u, position))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static void CancelOwnedGesture(nint input)
    {
        OwnWindowThread(input);
        if (!PostMessage(input, 0x001F, 0, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static ulong CurrentMessageExtra() => unchecked((ulong)(nuint)GetMessageExtraInfo());
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetMessageExtraInfo();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
}
