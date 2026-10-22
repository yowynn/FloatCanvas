using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.ComponentModel;
using System.Runtime.InteropServices;
using FloatCanvas.App;
using L = FloatCanvas.Localization.AppText;

internal static class ToolOptionsKeyboardRouteTests
{
    internal static void Run(Action<string, bool> check)
    {
        byte[] originalState = ThreadModifierScope.Read();
        using (new ThreadModifierScope(ModifierKeys.None)) RunIsolated(check);
        check("keyboard route fixture restores its exact thread keyboard snapshot", originalState.SequenceEqual(ThreadModifierScope.Read()));
    }

    private static void RunIsolated(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; AppTheme.Apply("light");
        var panel = new StackPanel(); var button = new Button { Content = "Recipe" }; var text = new TextBox(); var slider = new Slider(); var combo = new ComboBox();
        panel.Children.Add(button); panel.Children.Add(text); panel.Children.Add(slider); panel.Children.Add(combo);
        var window = new Window { Content = panel }; var commands = new List<int>(); bool colorOpen = false; int dismissals = 0; bool pinned = false;
        ToolOptionsKeyboardRoute.Attach(window, () =>
        {
            if (colorOpen) { colorOpen = false; dismissals++; return true; }
            if (!pinned) { dismissals++; return true; }
            return false;
        }, () => colorOpen, commands.Add);
        try
        {
            foreach (var key in new[] { Key.B, Key.L, Key.M, Key.OemMinus, Key.OemPlus })
            {
                int previous = commands.Count; var args = Send(button, key);
                check("independent options window routes " + key + " from an ordinary control", args.Handled && commands.Count == previous + 1 && commands[^1] == KeyInterop.VirtualKeyFromKey(key));
            }
            int before = commands.Count; Send(text, Key.B); Send(text, Key.D3); Send(text, Key.D2); Send(slider, Key.Right); Send(combo, Key.B);
            check("text, numeric, slider and font controls keep their local keys", commands.Count == before);
            var tab = Send(button, Key.Tab);
            check("Tab on an ordinary tool control routes exactly one toolbar command", tab.Handled && commands.Count == before + 1 && commands[^1] == 0x09);
            before = commands.Count;
            foreach(var source in new UIElement[]{text,slider,combo})
            {
                Send(source,Key.Tab);
                check("Tab in local " + source.GetType().Name + " does not fold the toolbar", commands.Count == before);
            }
            foreach(var modifiers in new[] { ModifierKeys.Shift, ModifierKeys.Control, ModifierKeys.Alt, ModifierKeys.Windows })
            {
                check(modifiers + "+Tab does not become a toolbar command", !ToolOptionsKeyboardRoute.ShouldRoute(0x09, modifiers, button));
                // WPF KeyboardDevice.Modifiers exposes only left/right Alt, Ctrl and Shift.
                // The Windows-key boundary remains the explicit shortcut-policy check above.
                if (modifiers == ModifierKeys.Windows) continue;
                using var modified = new ThreadModifierScope(modifiers);
                check("window route observes the fixture's " + modifiers + " state", Keyboard.Modifiers == modifiers);
                Send(button, Key.Tab);
                check(modifiers + "+Tab remains excluded through the real window handler", commands.Count == before);
            }
            check("modifier route cases restore the no-modifier fixture state", Keyboard.Modifiers == ModifierKeys.None);
            check("holding Tab cannot repeatedly fold the toolbar", !EditorShortcutPolicy.AllowsRepeat(0x09, FloatCanvas.Windows.OverlayModifiers.None));
            foreach (var source in new DependencyObject[] { button, text, slider, combo })
                check(source.GetType().Name + " preserves project save and open shortcuts", ToolOptionsKeyboardRoute.ShouldRoute(0x53, ModifierKeys.Control, source) && ToolOptionsKeyboardRoute.ShouldRoute(0x4F, ModifierKeys.Control, source));
            check("textbox undo and clipboard keys remain local", !ToolOptionsKeyboardRoute.ShouldRoute(0x5A, ModifierKeys.Control, text) && !ToolOptionsKeyboardRoute.ShouldRoute(0x56, ModifierKeys.Control, text));
            foreach (int command in new[] { 0x0D, 0x4E, 0x08, 0x2E })
            {
                var modifiers = command == 0x0D ? ModifierKeys.None : ModifierKeys.Control;
                check("ordinary tool controls forward canvas command " + command, ToolOptionsKeyboardRoute.ShouldRoute(command, modifiers, button));
                check("text controls retain local Enter and word deletion, except explicit new canvas " + command,
                    ToolOptionsKeyboardRoute.ShouldRoute(command, modifiers, text) == (command == 0x4E));
            }
            check("menus and system modifier keys are not repurposed as tools", !ToolOptionsKeyboardRoute.ShouldRoute(0x42, ModifierKeys.None, new MenuItem()) && !ToolOptionsKeyboardRoute.ShouldRoute(0x42, ModifierKeys.Alt, button));
            var modalSource = new Button(); var modalWindow = new Window { Content = modalSource };
            var modalKey = new KeyEventArgs(Keyboard.PrimaryDevice, new DetachedSource(), Environment.TickCount, Key.OemPlus) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            bool consumedBeforeCommand = false;
            ToolOptionsKeyboardRoute.Attach(modalWindow, () => false, () => false, _ => consumedBeforeCommand = modalKey.Handled);
            try { modalSource.RaiseEvent(modalKey); check("a modal-capable command consumes its source key before invoking the command", consumedBeforeCommand); }
            finally { modalWindow.Close(); }
            colorOpen = true; Send(button, Key.L); Send(button, Key.Tab); check("a nested color editor prevents tool switching and toolbar folding", commands.Count == before);
            Send(button, Key.Escape); check("first Escape dismisses only the color level", !colorOpen && dismissals == 1 && commands.Count == before);
            Send(button, Key.Escape); check("next Escape dismisses an unpinned tool window", dismissals == 2 && commands.Count == before);
            pinned = true; Send(button, Key.Escape); check("Escape from a pinned window reaches the application mode policy", commands.Last() == 0x1B && dismissals == 2);
            check("visibility guidance no longer promises a mode change", L.Get("Acceptance2.Welcome.ToggleVisibilityDescription", "zh-CN").Contains("不改变当前模式") && L.Get("Acceptance2.Welcome.ToggleVisibilityDescription", "en").Contains("current mode"));
            check("keyboard route fixture creates no native window", !window.IsVisible && new System.Windows.Interop.WindowInteropHelper(window).Handle == 0);
        }
        finally { window.Close(); }
    }
    private static KeyEventArgs Send(UIElement target, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, new DetachedSource(), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(args); return args;
    }
    private sealed class DetachedSource : PresentationSource
    {
        public override System.Windows.Media.Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override System.Windows.Media.CompositionTarget? GetCompositionTargetCore() => null;
    }

    // Raised KeyEventArgs do not change Keyboard.Modifiers. Keep this no-HWND fixture
    // independent of the keyboard snapshot inherited when its process/thread starts.
    // SetKeyboardState affects only this thread; no physical/global input is generated.
    private sealed class ThreadModifierScope : IDisposable
    {
        private readonly byte[] original = Read();
        internal ThreadModifierScope(ModifierKeys modifiers)
        {
            byte[] next = (byte[])original.Clone();
            foreach (int key in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 }) next[key] &= 0x7F;
            if ((modifiers & ModifierKeys.Shift) != 0) { next[0x10] |= 0x80; next[0xA0] |= 0x80; }
            if ((modifiers & ModifierKeys.Control) != 0) { next[0x11] |= 0x80; next[0xA2] |= 0x80; }
            if ((modifiers & ModifierKeys.Alt) != 0) { next[0x12] |= 0x80; next[0xA4] |= 0x80; }
            if (!SetKeyboardState(next)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Dispose()
        { if (!SetKeyboardState(original)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        internal static byte[] Read()
        {
            byte[] state = new byte[256];
            if (!GetKeyboardState(state)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return state;
        }
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetKeyboardState(byte[] state);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetKeyboardState(byte[] state);
    }
}
