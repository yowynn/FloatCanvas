using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Interop;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

/// <summary>Opt-in physical-input boundary. Every injection is limited to the fixture's
/// own foreground/input HWND; the existing user application and profile are untouched.</summary>
internal static class CtrlCreationRoutingFixture
{
    internal static void Run(Action<string, bool> check, OverlayHost host, EditorController editor, Window? keyboardWindow = null,
        Action<Action, Action>? duringPendingPacket = null, bool bufferMouseBeforeControlUp = false)
    {
        var target = host.Diagnostics.Single();
        string scenario = Environment.GetEnvironmentVariable("FLOATCANVAS_INPUT_CASE") ?? "";
        bool bothControls = scenario.Contains("-both", StringComparison.Ordinal), withShift = scenario.Contains("-shift", StringComparison.Ordinal);
        bool sameTick = scenario.Contains("-same-tick", StringComparison.Ordinal);
        ushort controlKey = scenario.Contains("-right", StringComparison.Ordinal) || bothControls ? (ushort)0xA3 : (ushort)0xA2;
        GetCursorPos(out var originalCursor);
        bool controlHeld = false, mouseHeld = false, shiftHeld = false;
        bool pumpInput = true;
        uint? forcedInputTime = null;
        var samples = new List<OverlayPointerEvent>();
        void Observe(OverlayPointerEvent e) => samples.Add(e);
        host.Pointer += Observe;
        try
        {
            check("the physical Ctrl fixture uses the production rectangle toolbar activation", editor.Tool == EditorTool.Rectangle);
            CheckTarget();
            check("the physical Ctrl fixture retains the production keyboard-isolation lease", host.IsKeyboardIsolationActive);
            Move(80, 80); Mouse(2); Move(260, 220); Mouse(4);
            Console.WriteLine($"CTRL_NATIVE firstDrag elements={editor.Session.Document.Elements.Count} tool={editor.Tool} input={string.Join(';', samples.Select(s => $"{s.Kind}:{s.Position}:{s.Modifiers}"))}");
            var original = editor.Session.Document.Elements.Single().Clone();
            check("the physical Ctrl fixture creates and automatically selects its first rectangle", editor.Session.Selection.SetEquals([original.Id]));
            Move(170, 150);
            if (keyboardWindow is not null)
            {
                SetForegroundWindow(new WindowInteropHelper(keyboardWindow).Handle); keyboardWindow.Activate(); keyboardWindow.Focus();
                PumpFor(TimeSpan.FromMilliseconds(100));
            }
            bool previewVisible = false;
            void BeginCtrlCreation()
            {
                if (sameTick) forcedInputTime = unchecked((uint)Environment.TickCount64);
                if (bothControls) Key(0xA2, false);
                Key(controlKey, false); controlHeld = true;
                if (bothControls) Key(0xA2, true);
                if (withShift) { Key(0xA0, false); shiftHeld = true; }
                Console.WriteLine($"CTRL_NATIVE afterKey packetModifiers={OverlayHost.CurrentModifiers} interactionModifiers={host.InteractionModifiers} showSelection={editor.ShouldShowSelection(host.InteractionModifiers)}");
                check("a real isolated Ctrl press suppresses selection even if its private packet is pending", !editor.ShouldShowSelection(host.InteractionModifiers));
                if (bufferMouseBeforeControlUp)
                {
                    pumpInput = false;
                    Mouse(2); if (!sameTick) Thread.Sleep(15); Key(controlKey, true); controlHeld = false;
                    forcedInputTime = null;
                    pumpInput = true; PumpFor(TimeSpan.FromMilliseconds(100));
                }
                else Mouse(2);
                Move(300, 240);
                Console.WriteLine($"CTRL_NATIVE duringDrag elements={editor.Session.Document.Elements.Count} modifiers={OverlayHost.CurrentModifiers} pointer={string.Join(';', samples.TakeLast(4).Select(s => $"{s.Kind}:{s.Modifiers}"))}");
                previewVisible = editor.Session.Document.Elements.Count == 2;
            }
            if (duringPendingPacket is null) BeginCtrlCreation();
            else { duringPendingPacket(BeginCtrlCreation, () => Key(0x78, false)); Key(0x78, true); }
            PumpFor(TimeSpan.FromMilliseconds(100));
            Console.WriteLine($"CTRL_NATIVE afterQueueRelease elements={editor.Session.Document.Elements.Count} modifiers={OverlayHost.CurrentModifiers} showSelection={editor.ShouldShowSelection(OverlayHost.CurrentModifiers)}");
            if (controlHeld) { Key(controlKey, true); controlHeld = false; }
            Move(330, 260); Mouse(4);
            if (shiftHeld) { Key(0xA0, true); shiftHeld = false; }
            var preserved = editor.Session.Document.Elements.Single(element => element.Id == original.Id);
            Console.WriteLine($"CTRL_NATIVE completed elements={editor.Session.Document.Elements.Count} oldOriginal={original.Transform} oldNow={preserved.Transform}");
            check("a real isolated Ctrl press previews the second rectangle before mouse-up", previewVisible);
            check("releasing real Ctrl mid-drag keeps creation ownership and leaves the old rectangle unchanged", editor.Session.Document.Elements.Count == 2 &&
                preserved.Transform == original.Transform && preserved.Width == original.Width && preserved.Height == original.Height);
            if (withShift)
            {
                var created = editor.Session.Document.Elements.Single(element => element.Id != original.Id);
                check("real Ctrl creation retains the independent held Shift aspect constraint", Math.Abs(created.Width - created.Height) < .1);
            }
            check("real Ctrl overlap creation commits one undo step", editor.Session.UndoCount == 2 && editor.Session.Undo() && editor.Session.Document.Elements.Count == 1);
            editor.Session.SetSelection([original.Id]);
            Move(170, 150); Mouse(2); Move(180, 160); Mouse(4);
            var moved = editor.Session.Document.Elements.Single();
            check("ordinary dragging still moves the selected original rectangle after Ctrl creation and undo", editor.Session.Document.Elements.Count == 1 &&
                Math.Abs(moved.Transform.OffsetX - original.Transform.OffsetX - 10) < 1 && Math.Abs(moved.Transform.OffsetY - original.Transform.OffsetY - 10) < 1);
        }
        finally
        {
            if (OwnsForeground())
            {
                if (mouseHeld) Mouse(4);
                if (controlHeld) Key(controlKey, true);
                if (shiftHeld) Key(0xA0, true);
                SetCursorPos(originalCursor.X, originalCursor.Y);
            }
            host.Pointer -= Observe;
        }

        bool OwnsForeground() => GetWindowThreadProcessId(GetForegroundWindow(), out uint process) != 0 && process == Environment.ProcessId;
        void CheckTarget()
        {
            if (!OwnsForeground() || !host.IsEditing || !host.IsKeyboardIsolationActive ||
                GetForegroundWindow() != target.InputHandle && GetForegroundWindow() != (keyboardWindow is null ? 0 : new WindowInteropHelper(keyboardWindow).Handle) ||
                GetCapture() is var capture && capture != 0 && capture != target.InputHandle)
                throw new InvalidOperationException($"Ctrl fixture lost its exact own input target; injection stopped. foreground={GetForegroundWindow():X} own={OwnsForeground()} expected={target.InputHandle:X} capture={GetCapture():X} editing={host.IsEditing} lease={host.IsKeyboardIsolationActive}");
        }
        void Send(Input input)
        {
            CheckTarget();
            if (forcedInputTime is { } time)
            { if (input.Type == 0) input.Data.Mouse.Time = time; else input.Data.Keyboard.Time = time; }
            if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (pumpInput) PumpFor(TimeSpan.FromMilliseconds(100));
            CheckTarget();
        }
        void Move(int x, int y)
        {
            var screen = new PixelPoint(target.Bounds.Left + x, target.Bounds.Top + y);
            CheckTarget();
            if (GetCapture() == 0 && WindowFromPhysicalPoint(screen) != target.InputHandle)
                throw new InvalidOperationException($"Ctrl fixture target point is not its own bounded input surface; injection stopped. point={screen} actual={WindowFromPhysicalPoint(screen):X} expected={target.InputHandle:X} bounds={target.Bounds}");
            int left = GetSystemMetrics(76), top = GetSystemMetrics(77), width = GetSystemMetrics(78), height = GetSystemMetrics(79);
            Send(new() { Type = 0, Data = new() { Mouse = new() { X = (int)Math.Round((screen.X - left) * 65535d / (width - 1)),
                Y = (int)Math.Round((screen.Y - top) * 65535d / (height - 1)), Flags = 0xC001 } } });
        }
        void Mouse(uint flags)
        {
            GetCursorPos(out var cursor);
            if (GetCapture() == 0 && WindowFromPhysicalPoint(cursor) != target.InputHandle)
                throw new InvalidOperationException("Ctrl fixture mouse press does not target its own input surface; injection stopped.");
            Send(new() { Type = 0, Data = new() { Mouse = new() { Flags = flags } } });
            mouseHeld = flags == 2;
        }
        void Key(ushort key, bool up) => Send(new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Flags = (up ? 2u : 0) | (key == 0xA3 ? 1u : 0) } } });
    }

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => frame.Continue = false;
        try { timer.Start(); Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
    }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct PixelPoint(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] private static extern nint WindowFromPhysicalPoint(PixelPoint point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PixelPoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
