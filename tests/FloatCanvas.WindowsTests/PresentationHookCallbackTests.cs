using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using FloatCanvas.Windows;

namespace FloatCanvas.WindowsTests;

/// <summary>Direct calls with owned fixture structs. Never installs a hook, injects input or activates a window.</summary>
internal static class PresentationHookCallbackTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<bool, string> check)
    {
        nint foreground = GetForegroundWindow();
        if (foreground == 0 || GetWindowThreadProcessId(foreground, out uint foregroundProcess) == 0)
            throw new InvalidOperationException("This callback fixture needs existing foreground window metadata; it never changes the foreground.");
        nint window = CreateWindowExW(0, "STATIC", "FloatCanvas.Presentation.CallbackFixture", 0, 0, 0, 0, 0, -3, 0, 0, 0);
        if (window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            using var input = (KeyboardInputIsolation)Activator.CreateInstance(typeof(KeyboardInputIsolation), Hidden, null,
                [window, 0x8003u, 256, TimeSpan.FromSeconds(10)], null)!;
            check(!input.IsThreadAlive && Field<nint>(input, "hook") == 0 && Field<nint>(input, "pointerHook") == 0 && !IsWindowVisible(window),
                "Callback fixture owns one hidden window and never starts either global hook");
            var state = Field<KeyboardSuppressionState>(input, "state");
            state.Begin(new byte[256], HotkeyBinding.DefaultToggle, HotkeyBinding.DefaultHide, Environment.TickCount64, passModifiers: true);
            Set(input, "delegatedForeground", foreground); Set(input, "delegatedProcess", foregroundProcess);
            Set(input, "lastHeartbeat", Environment.TickCount64);
            input.ConfigurePresentationInputTap(true, true, 777, 0x8006);
            var tap = Field<PresentationInputTap>(input, "presentationInput");
            uint generation = tap.Generation;
            var results = new[] { Key(0x11, 29, false), Key(0x43, 46, false), Key(0x43, 46, true), Key(0x11, 29, true) };
            check(results[1] == 1 && results[2] == 1 && state.IsActive && input.Failure is null,
                "Actual keyboard callback still suppresses only the delegated Ctrl+C main-key stream");
            check(PeekMessage(out var notification, window, 0x8006, 0x8006, 1) && notification.WParam == generation &&
                !PeekMessage(out _, window, 0x8006, 0x8006, 1),
                "Actual callbacks coalesce passive metadata into one owned-window WM_APP notification");
            check(input.TryDrainPresentationInput(generation, out var keyboard) && keyboard!.Epoch == 777 && keyboard.Changes.Count == 4 &&
                keyboard.Changes.Select(key => key.VirtualKey).SequenceEqual(new[] { 0xA2, 0x43, 0x43, 0xA2 }),
                "Actual keyboard callback exposes modifier and main-key make/break metadata independently of Raw Input");
            var privatePackets = new List<KeyboardInputPacket>();
            while (input.TryDequeue(out var packet)) { privatePackets.Add(packet!); input.CompletePacket(packet!.Sequence); }
            check(privatePackets.Count == 4 && privatePackets.Count(packet => packet.IsStateOnly) == 2 &&
                privatePackets.Count(packet => !packet.IsStateOnly) == 2,
                "Presentation observation does not alter the existing private keyboard queue or passed modifier policy");
            foreach (uint message in new uint[] { 0x201, 0x202, 0x204, 0x205, 0x207, 0x208 }) Pointer(message);
            check(input.TryDrainPresentationInput(generation, out var pointer) && pointer!.Changes.Count == 6 &&
                pointer.Changes.Select(change => change.Button).SequenceEqual(new[] { 0, 0, 1, 1, 2, 2 }) &&
                pointer.Changes.All(change => change.HasPosition && change.X == -30000 && change.Y == -30000),
                "Actual mouse callback collects left right middle down/up with physical fixture coordinates");
            check(state.IsActive && input.Failure is null && !input.IsThreadAlive,
                "Mouse observation neither ends editing nor starts an input observer");
            uint previous = generation;
            input.ConfigurePresentationInputTap(true, true, 778, 0x8006); generation = tap.Generation;
            Key(0x43, 46, false); Key(0x43, 46, true);
            check(!input.TryDrainPresentationInput(previous, out _) && input.TryDrainPresentationInput(generation, out var newRoute) && newRoute!.Epoch == 778,
                "Queued old notifications cannot drain a newer callback route");
            var labels = typeof(PresentationInputTap).GetField("labels", Hidden)!;
            var originalLabels = labels.GetValue(tap);
            labels.SetValue(tap, null); // Controlled observer-only fault; no actual input source is running.
            try
            {
                nint down = Key(0x43, 46, false), up = Key(0x43, 46, true);
                check(down == 1 && up == 1 && state.IsActive && input.Failure is null,
                    "A failed visual metadata observer cannot change suppression or close the editing lease");
            }
            finally { labels.SetValue(tap, originalLabels); }
            Key(0x5B, 0x5B, false);
            uint beforeLeaseEnd = tap.Generation;
            input.EndLease(); generation = tap.Generation;
            Key(0x5B, 0x5B, true);
            check(generation == beforeLeaseEnd && input.TryDrainPresentationInput(generation, out var spanning) &&
                spanning!.Changes is [{ Kind: PresentationObservedKind.KeyDown, VirtualKey: 0x5B }, { Kind: PresentationObservedKind.KeyUp, VirtualKey: 0x5B }],
                "Ending the editing lease preserves queued physical keyboard edges and their matching release");
            Key(0x43, 46, false); Key(0x43, 46, true); Pointer(0x201);
            check(input.TryDrainPresentationInput(generation, out var passive) && passive!.Changes.Count == 2 &&
                passive.Changes.All(value => value.Kind is PresentationObservedKind.KeyDown or PresentationObservedKind.KeyUp),
                "The same keyboard callback continues in pass-through while editing-only pointer observation stops");
            input.ConfigurePresentationInputTap(false, false, 779, 0x8006); generation = tap.Generation;
            Key(0x43, 46, false); Key(0x43, 46, true);
            check(!input.TryDrainPresentationInput(generation, out _), "Disabling presentation stops observation without changing the hook lease");
            check(!IsWindowVisible(window) && !input.IsThreadAlive,
                "Callback fixture finishes without an exposed window, hook thread or synthetic system input");

            nint Key(uint vk, uint scan, bool up)
            {
                Guard(); return Invoke("OnKeyboard", up ? 0x101u : 0x100u,
                    new KeyboardEvent { VirtualKey = vk, ScanCode = scan, Flags = up ? 0x80u : 0u, Time = unchecked((uint)Environment.TickCount) });
            }
            void Pointer(uint message)
            { Guard(); Invoke("OnPointer", message, new MouseEvent { X = -30000, Y = -30000, Time = unchecked((uint)Environment.TickCount) }); }
            nint Invoke<T>(string name, uint message, T data) where T : struct
            {
                nint memory = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
                try { Marshal.StructureToPtr(data, memory, false); return (nint)typeof(KeyboardInputIsolation).GetMethod(name, Hidden)!.Invoke(input, [0, (nuint)message, memory])!; }
                finally { Marshal.FreeHGlobal(memory); }
            }
            void Guard()
            {
                if (GetForegroundWindow() != foreground) throw new InvalidOperationException("Foreground metadata changed; callback fixture aborted without input injection.");
            }
        }
        finally { DestroyWindow(window); }
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Hidden)!.SetValue(target, value);
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardEvent { public uint VirtualKey, ScanCode, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseEvent { public int X, Y; public uint Data, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowExW(uint extended, string name, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessage(out Message message, nint window, uint minimum, uint maximum, uint remove);
}
