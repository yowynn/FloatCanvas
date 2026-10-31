using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using FloatCanvas.App;
using FloatCanvas.Windows;

internal static class PrivateKeyboardBridgeStateTests
{
    internal static void Run(Action<string, bool> check)
    {
        if (Application.Current is not null) throw new InvalidOperationException("Run private keyboard bridge checks in a fresh process.");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        byte[] original = ThreadModifierScope.Read();
        var text = new TextBox { Text = "untouched" };
        using var source = new HwndSource(new HwndSourceParameters("FloatCanvas.PrivateKeyboardBridge.StateOnly")
        {
            PositionX = -20000, PositionY = -20000, Width = 80, Height = 40,
            WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080
        }) { RootVisual = text };
        int nativeKeys = 0, routedKeys = 0, completed = 0, predicateCalls = 0;
        source.AddHook((nint hwnd, int message, nint key, nint data, ref bool handled) =>
        {
            if (message is 0x100 or 0x101 or 0x104 or 0x105) nativeKeys++;
            return 0;
        });
        text.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler((_, _) => routedKeys++), true);
        text.AddHandler(Keyboard.PreviewKeyUpEvent, new KeyEventHandler((_, _) => routedKeys++), true);
        try
        {
            nint foreign = GetDesktopWindow();
            GetWindowThreadProcessId(foreign, out uint owner);
            check("bridge ownership fixture uses a real foreign desktop HWND", foreign != 0 && owner != Environment.ProcessId);
            check("ordinary bridge rejects external foreground even with an owned target",
                !PrivateKeyboardBridge.CanDispatchTo(foreign, source.Handle, () => false));
            check("enhanced bridge admits external foreground only with explicit editing-input ownership",
                PrivateKeyboardBridge.CanDispatchTo(foreign, source.Handle, () => true));
            check("enhanced editing predicate cannot authorize a foreign key target",
                !PrivateKeyboardBridge.CanDispatchTo(source.Handle, foreign, () => true));
            check("enhanced editing predicate cannot authorize a missing key target",
                !PrivateKeyboardBridge.CanDispatchTo(foreign, 0, () => true));
            check("ordinary own foreground preserves the existing bridge route",
                PrivateKeyboardBridge.CanDispatchTo(source.Handle, source.Handle, () => false));
            var physical = Enumerable.Repeat((byte)1, 256).ToArray(); physical[0x41] = 0x81;
            PrivateKeyboardBridge.ReconcilePhysicalModifiers(physical, key => key is 0xA0 or 0xA3 or 0x5C);
            check("final balance preserves physically held left/right modifiers and normalizes generic state", physical[0xA0] == 0x81 &&
                physical[0xA1] == 1 && physical[0xA2] == 1 && physical[0xA3] == 0x81 && physical[0x10] == 0x81 && physical[0x11] == 0x81 &&
                physical[0x12] == 1 && physical[0x5B] == 1 && physical[0x5C] == 0x81);
            PrivateKeyboardBridge.ReconcilePhysicalModifiers(physical, _ => false);
            check("final balance clears released modifiers without changing ordinary keys, mouse bits or toggle bits",
                new[] { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C }.All(key => physical[key] == 1) &&
                physical[0x41] == 0x81 && physical[1] == 1);
            var bridge = new PrivateKeyboardBridge(hasEditingInput: () => { predicateCalls++; return true; });
            var shifted = new byte[256]; shifted[0x10] = shifted[0xA0] = 0x80;
            check("a state-only modifier snapshot completes normally", bridge.Process(Packet(1, 0xA0, false, shifted), () => completed++));
            check("state-only Shift-down updates the ordered thread-local snapshot", Down(0x10) && Down(0xA0));
            var oldSpace = (byte[])shifted.Clone(); oldSpace[0x20] = 0x80;
            check("the queued-key fixture has no native key target", GetFocus() == 0);
            check("an older queued key with no destination is rejected without replay",
                !bridge.Process(Packet(2, 0x20, false, oldSpace, stateOnly: false), () => completed++));
            check("an older queued key snapshot can restore its packet-local Shift state", Down(0x10));
            check("the later passed modifier release also completes normally", bridge.Process(Packet(3, 0xA0, true, new byte[256]), () => completed++));
            check("a later state-only release prevents stale queued Shift from sticking", !Down(0x10) && !Down(0xA0) && !Down(0x20));
            check("each ordered packet acknowledges exactly once", completed == 3);
            check("state-only packets do not ask permission to dispatch or replay keys", predicateCalls == 0);
            check("state-only packets do not duplicate native or WPF key messages", nativeKeys == 0 && routedKeys == 0);
            check("state-only packets do not translate duplicate text", text.Text == "untouched");
            var staleBalance = new byte[256];
            foreach (int key in new[] { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C }) staleBalance[key] = 0x80;
            check("the final state-only lease balance completes normally",
                bridge.Process(Packet(4, 0, true, staleBalance, localBalance: true), () => completed++));
            byte[] balanced = ThreadModifierScope.Read();
            check("the final balance reconciles stale modifier bits to the current physical snapshot",
                new[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C }.All(key => ((balanced[key] & 0x80) != 0) == (GetAsyncKeyState(key) < 0)));
            check("the final balance does not dispatch, translate or duplicate its acknowledgement",
                completed == 4 && predicateCalls == 0 && nativeKeys == 0 && routedKeys == 0 && text.Text == "untouched");
            check("bridge checks never expose the native test surface", !IsWindowVisible(source.Handle));
        }
        finally
        {
            if (!SetKeyboardState(original)) throw new Win32Exception(Marshal.GetLastWin32Error());
            source.RootVisual = null;
            app.Shutdown();
        }
    }

    private static bool Down(int key) => (ThreadModifierScope.Read()[key] & 0x80) != 0;
    private static KeyboardInputPacket Packet(long sequence, uint key, bool up, byte[] state, bool stateOnly = true, bool localBalance = false)
    {
        var packet = (KeyboardInputPacket)Activator.CreateInstance(typeof(KeyboardInputPacket), BindingFlags.Instance | BindingFlags.NonPublic, null,
            [sequence, 1L, KeyboardInputPacketKind.Key, key, 0u, 0u, (uint)Environment.TickCount, up, false, (nint)0, state, localBalance, null, up ? 0x101u : 0x100u], null)!;
        typeof(KeyboardInputPacket).GetProperty(nameof(KeyboardInputPacket.IsStateOnly))!.SetValue(packet, stateOnly);
        return packet;
    }

    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetKeyboardState(byte[] state);
}
