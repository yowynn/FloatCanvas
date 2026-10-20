using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

/// <summary>Actual private bridge/native/WPF routes in the existing isolated-profile fixture; no global keys.</summary>
internal static class ShortcutRoutingMatrixRuntimeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check, DesktopSession session, OverlayHost host, Window toolbar,
        Action<KeyboardInputPacket, Action> dispatch)
    {
        var editor = Field<EditorController>(session, "_editor");
        var options = Field<Window>(session, "_toolOptionsWindow");
        var buttons = Field<Dictionary<EditorTool, Button>>(session, "_toolbarToolButtons");
        var state = editor.Session;
        using var nativeEvents = new NativeKeyObservation(host);
        state.Execute("shortcut matrix fixture", document => document.Elements.Clear());
        var element = new CanvasElement { Kind = ElementKind.Rectangle, Width = 80, Height = 40 };
        state.Add(element); state.SetSelection([element.Id]);
        foreach (string surface in new[] { "canvas", "toolbar" })
        {
            foreach (var (vk, modifiers, label) in new[]
            {
                (0x42u, OverlayModifiers.Alt, "Alt+B"),
                (0x53u, OverlayModifiers.Control | OverlayModifiers.Alt, "Ctrl+Alt+S"),
                (0x42u, OverlayModifiers.Windows, "Win+B")
            })
            {
                editor.Tool = EditorTool.Rectangle; CloseOptions(); Focus(surface);
                int undo = state.UndoCount; var selection = state.Selection.ToArray();
                bool modal = false;
                var existing = Application.Current.Windows.Cast<Window>().ToHashSet();
                var cancelUnexpected = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Send, (_, _) =>
                {
                    foreach (var window in Application.Current.Windows.Cast<Window>().Where(window => !existing.Contains(window) && window.IsVisible).ToArray())
                    { modal = true; window.Close(); }
                    EnumThreadWindows(GetCurrentThreadId(), (hwnd, _) =>
                    {
                        var name = new StringBuilder(32);
                        if (GetClassNameW(hwnd, name, name.Capacity) != 0 && name.ToString() == "#32770")
                        { modal = true; PostMessageW(hwnd, 0x111, 2, 0); } // IDCANCEL, our fixture's own native dialog only.
                        return true;
                    }, 0);
                }, Dispatcher.CurrentDispatcher);
                try { Send(vk, modifiers); Send(vk, modifiers, up: true); ClearModifiers(); Pump(); }
                finally { cancelUnexpected.Stop(); }
                check($"{surface}: {label} remains outside application commands through the actual private bridge",
                    editor.Tool == EditorTool.Rectangle && !options.IsVisible && state.UndoCount == undo &&
                    state.Selection.SetEquals(selection) && !modal && host.IsEditing);
            }

            editor.Tool = EditorTool.Rectangle; CloseOptions(); Focus(surface);
            Send(0x42, OverlayModifiers.None);
            check($"{surface}: a fresh B selects Freehand through the actual route", editor.Tool == EditorTool.Freehand);
            bool open = options.IsVisible;
            Send(0x42, OverlayModifiers.None, repeat: true);
            Send(0x42, OverlayModifiers.None, repeat: true);
            Send(0x42, OverlayModifiers.None, repeat: true);
            Send(0x42, OverlayModifiers.None, up: true);
            check($"{surface}: holding a tool key does not repeatedly toggle its options window",
                editor.Tool == EditorTool.Freehand && options.IsVisible == open);

            CloseOptions(); Focus(surface); state.SetSelection([element.Id]);
            var transform = state.Document.Elements.Single(item => item.Id == element.Id).Transform;
            Send(0x27, OverlayModifiers.None);
            Send(0x27, OverlayModifiers.None, repeat: true);
            Send(0x27, OverlayModifiers.None, up: true);
            var current = state.Document.Elements.Single(item => item.Id == element.Id).Transform;
            check($"{surface}: arrow repeats remain repeatable application commands",
                current == Affine2D.Translation(2, 0).Compose(transform));
        }
        ClearModifiers();

        void CloseOptions() => typeof(DesktopSession).GetMethod("CloseToolOptions", Private)!.Invoke(session, null);
        void Focus(string surface)
        {
            toolbar.Activate(); Pump();
            if (surface == "canvas") check("matrix focuses its own native canvas", host.TryFocusEditingCanvas());
            else
            {
                var handle = new WindowInteropHelper(toolbar).Handle;
                check("matrix focuses its own ordinary toolbar control", SetForegroundWindow(handle) && buttons[EditorTool.Select].Focus());
            }
            Pump();
        }
        void Send(uint vk, OverlayModifiers modifiers, bool up = false, bool repeat = false)
        {
            bool nativeTarget = host.Diagnostics.Any(pair => pair.InputHandle == GetFocus());
            int eventCount = nativeEvents.Events.Count;
            var keys = new byte[256];
            if (modifiers.HasFlag(OverlayModifiers.Control)) keys[0x11] = keys[0xA2] = 128;
            if (modifiers.HasFlag(OverlayModifiers.Shift)) keys[0x10] = keys[0xA0] = 128;
            if (modifiers.HasFlag(OverlayModifiers.Alt)) keys[0x12] = keys[0xA4] = 128;
            if (modifiers.HasFlag(OverlayModifiers.Windows)) keys[0x5B] = 128;
            if (!up) keys[vk] = 128;
            bool system = modifiers.HasFlag(OverlayModifiers.Alt);
            var packet = (KeyboardInputPacket)Activator.CreateInstance(typeof(KeyboardInputPacket), Private, null,
                [1L, 1L, KeyboardInputPacketKind.Key, vk, MapVirtualKeyW(vk, 0), system ? 0x20u : 0u,
                    unchecked((uint)Environment.TickCount), up, repeat, GetForegroundWindow(), keys, false, null,
                    up ? system ? 0x105u : 0x101u : system ? 0x104u : 0x100u], null)!;
            dispatch(packet, () => { }); Pump();
            if (nativeTarget && !up)
                check($"native key {vk:X}: typed modifiers/repeat precede one unchanged legacy observation",
                    nativeEvents.Events.Count == eventCount + 2 &&
                    nativeEvents.Events[eventCount] == (true, (int)vk, modifiers, repeat) &&
                    nativeEvents.Events[eventCount + 1] == (false, (int)vk, OverlayModifiers.None, false));
        }
        void ClearModifiers() { foreach (uint key in new uint[] { 0x11, 0x10, 0x12, 0x5B }) Send(key, OverlayModifiers.None, up: true); }
    }

    private sealed class NativeKeyObservation : IDisposable
    {
        private readonly OverlayHost host;
        internal List<(bool Typed, int Key, OverlayModifiers Modifiers, bool Repeat)> Events { get; } = [];
        internal NativeKeyObservation(OverlayHost host)
        { this.host = host; host.KeyInput += Typed; host.KeyPressed += Legacy; }
        private void Typed(OverlayKeyEvent key) => Events.Add((true, key.VirtualKey, key.Modifiers, key.IsRepeat));
        private void Legacy(int key) => Events.Add((false, key, OverlayModifiers.None, false));
        public void Dispose() { host.KeyInput -= Typed; host.KeyPressed -= Legacy; }
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Pump()
    { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
    private delegate bool EnumWindowCallback(nint hwnd, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, EnumWindowCallback callback, nint parameter);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd, StringBuilder text, int capacity);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint key, uint map);
}
