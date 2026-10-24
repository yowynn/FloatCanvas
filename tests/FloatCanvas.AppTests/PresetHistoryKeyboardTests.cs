using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FloatCanvas.App;

internal static class PresetHistoryKeyboardTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        var library = new ColorLibrary(Path.Combine(ApplicationPaths.ProfileDirectory, "preset-history-key-colors.json"));
        library.Save("fixture", 0xFF112233);
        var field = new ColorField(ColorRole.Foreground, library);
        var owner = new Window { Content = field };
        // This no-HWND fixture needs only the coordinator's owner resource and history callback.
        using var coordinator = new SessionWindowCoordinator(null!, owner);
        var commands = new List<int>(); bool handledBeforeCommand = false;
        KeyEventArgs? current = null;
        coordinator.HistoryCommand = key => { handledBeforeCommand = current?.Handled == true; commands.Add(key); };
        var root = (FrameworkElement)field.PickerRoot;
        root.Measure(new Size(420, 700)); root.Arrange(new Rect(0, 0, 420, 700)); root.UpdateLayout();
        var picker = Field<ColorPickerPanel>(field, "picker");
        var tile = Field<PixelTilePanel>(picker, "pinned").Children.OfType<Button>().Single();
        var text = Field<TextBox>(picker, "hex");
        // Initialize WPF's keyboard provider before installing synthetic thread-local state.
        _ = Keyboard.PrimaryDevice; _ = Keyboard.Modifiers;
        var original = ReadKeyboard();
        try
        {
            foreach (var (key, modifiers) in new[] { (Key.Z, ModifierKeys.Control), (Key.Z, ModifierKeys.Control | ModifierKeys.Shift), (Key.Y, ModifierKeys.Control) })
            {
                SetModifiers(original, modifiers);
                check("color history fixture installs exact " + modifiers, Keyboard.Modifiers == modifiers);
                int before = commands.Count; current = Args(key); tile.RaiseEvent(current);
                check("color popup routes " + modifiers + "+" + key + " exactly once", current.Handled && commands.Count == before + 1 && commands[^1] == KeyInterop.VirtualKeyFromKey(key));
                check("color popup consumes history key before invoking application command", handledBeforeCommand);
                current.RoutedEvent = Keyboard.PreviewKeyDownEvent; tile.RaiseEvent(current);
                check("already handled color history key cannot invoke application command twice", commands.Count == before + 1);
            }
            SetModifiers(original, ModifierKeys.Control);
            int count = commands.Count; current = Args(Key.Z); text.RaiseEvent(current);
            check("color hex editor retains its local Ctrl+Z", commands.Count == count);
            foreach (var source in new DependencyObject[] { new TextBox(), new PasswordBox(), new ComboBox(), new Slider(), new System.Windows.Controls.Primitives.Thumb(), new MenuItem() })
                check("color history preserves local " + source.GetType().Name + " editing", !PresetHistoryKeyboardRoute.ShouldRoute(Key.Z, ModifierKeys.Control, source));
            foreach (var modifiers in new[] { ModifierKeys.None, ModifierKeys.Shift, ModifierKeys.Alt | ModifierKeys.Control, ModifierKeys.Control | ModifierKeys.Windows })
                check("color history excludes unrelated modifiers " + modifiers, !PresetHistoryKeyboardRoute.ShouldRoute(Key.Z, modifiers, tile));
            check("color history route excludes tool switching, save and toolbar commands", !PresetHistoryKeyboardRoute.ShouldRoute(Key.B, ModifierKeys.None, tile) && !PresetHistoryKeyboardRoute.ShouldRoute(Key.S, ModifierKeys.Control, tile) && !PresetHistoryKeyboardRoute.ShouldRoute(Key.Tab, ModifierKeys.None, tile));
            coordinator.HistoryCommand = null; count = commands.Count; current = Args(Key.Z); tile.RaiseEvent(current);
            check("detached color popup without application callback does not consume history", !current.Handled && commands.Count == count);
            check("color history fixture never creates a native owner window", !owner.IsVisible && new System.Windows.Interop.WindowInteropHelper(owner).Handle == 0);
        }
        finally
        {
            if (!SetKeyboardState(original)) throw new InvalidOperationException("Cannot restore fixture keyboard state.");
            owner.Close();
        }
        check("color history fixture restores its exact thread keyboard state", original.SequenceEqual(ReadKeyboard()));
    }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static KeyEventArgs Args(Key key) => new(Keyboard.PrimaryDevice, new DetachedSource(), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
    private sealed class DetachedSource : PresentationSource
    {
        public override System.Windows.Media.Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override System.Windows.Media.CompositionTarget? GetCompositionTargetCore() => null;
    }
    private static byte[] ReadKeyboard()
    {
        var state = new byte[256];
        if (!GetKeyboardState(state)) throw new InvalidOperationException("Cannot read fixture keyboard state.");
        return state;
    }
    private static void SetModifiers(byte[] original, ModifierKeys modifiers)
    {
        PeekMessage(out _, 0, 0, 0, 0);
        var next = (byte[])original.Clone();
        foreach (int key in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 }) next[key] &= 0x7F;
        if ((modifiers & ModifierKeys.Control) != 0) next[0x11] = next[0xA2] = 0x80;
        if ((modifiers & ModifierKeys.Shift) != 0) next[0x10] = next[0xA0] = 0x80;
        if (!SetKeyboardState(next)) throw new InvalidOperationException("Cannot set fixture keyboard state.");
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetKeyboardState(byte[] state);
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Message message, nint window, uint first, uint last, uint remove);
}
