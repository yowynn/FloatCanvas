using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using FloatCanvas.App;
using FloatCanvas.Core;

/// <summary>Exercises the real detached window handlers; no HWND, activation or global input.</summary>
internal static class ShortcutSurfaceRoutingTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        var report = check;
        var failures = new List<string>();
        check = (name, passed) =>
        {
            if (passed) report(name, true);
            else { failures.Add(name); Console.WriteLine("SHORTCUT MATRIX FAIL: " + name); }
        };
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        var state = new EditorSession();
        var first = new CanvasElement { Kind = ElementKind.Rectangle, Width = 40, Height = 30 };
        var second = new CanvasElement { Kind = ElementKind.Ellipse, Width = 30, Height = 20 };
        state.Add(first); state.Add(second); state.SetSelection([first.Id, second.Id]);
        var inspector = new InspectorWindow(() => state);
        var layers = new LayersWindow(() => state);
        var options = new Window { Content = new Grid() };
        var surfaces = new[] { inspector, layers, options };
        var names = new[] { "Inspector", "Layers", "ToolOptions" };
        var commands = new[] { new List<int>(), new List<int>(), new List<int>() };
        inspector.CommandKey += commands[0].Add;
        layers.CommandKey += commands[1].Add;
        ToolOptionsKeyboardRoute.Attach(options, () => true, () => false, commands[2].Add);
        var sources = surfaces.Select(window =>
        {
            var panel = new StackPanel();
            ((Panel)window.Content).Children.Add(panel);
            var button = new Button { Content = "Shortcut routing fixture" }; panel.Children.Add(button);
            return (Panel: panel, Button: button);
        }).ToArray();
        var originalKeys = new byte[256];
        bool stateSaved = GetKeyboardState(originalKeys);
        check("shortcut matrix can preserve this test thread's keyboard state", stateSaved);
        try
        {
            if (!stateSaved) return;
            SetModifiers(ModifierKeys.None);
            var ordinary = new[]
            {
                Key.V, Key.E, Key.B, Key.H, Key.L, Key.R, Key.O, Key.T, Key.N, Key.S, Key.J,
                Key.M, Key.Tab, Key.OemMinus, Key.OemPlus, Key.D1, Key.D2, Key.D3, Key.D4, Key.D5,
                Key.D6, Key.D7, Key.D8, Key.D9, Key.D0, Key.Enter, Key.Delete,
                Key.Left, Key.Up, Key.Right, Key.Down
            };
            for (int surface = 0; surface < surfaces.Length; surface++)
            {
                foreach (var key in ordinary)
                {
                    int before = commands[surface].Count;
                    var args = Send(sources[surface].Button, key);
                    bool keepListNavigation = surface == 1 && key is Key.Left or Key.Up or Key.Right or Key.Down;
                    check($"{names[surface]} ordinary control {(keepListNavigation ? "retains list navigation" : "routes")} {key}",
                        keepListNavigation ? commands[surface].Count == before :
                        args.Handled && commands[surface].Count == before + 1 && commands[surface][^1] == KeyInterop.VirtualKeyFromKey(key));
                }
            }

            SetModifiers(ModifierKeys.Control);
            check("shortcut matrix controls only its thread-local Ctrl state", Keyboard.Modifiers == ModifierKeys.Control);
            foreach (var key in new[] { Key.Z, Key.Y, Key.A, Key.C, Key.V, Key.D, Key.S, Key.O, Key.N, Key.Back, Key.Delete })
                for (int surface = 0; surface < surfaces.Length; surface++) ExpectRoute(surface, sources[surface].Button, key);
            foreach (bool shift in new[] { false, true })
            {
                SetModifiers(ModifierKeys.Control | (shift ? ModifierKeys.Shift : ModifierKeys.None));
                ExpectRoute(0, sources[0].Button, Key.G);
                ExpectRoute(2, sources[2].Button, Key.G);
                int before = commands[1].Count;
                var args = Send(sources[1].Button, Key.G);
                check($"Layers Ctrl{(shift ? "+Shift" : "")}+G executes its real local grouping command once",
                    args.Handled && commands[1].Count == before && state.Document.Groups.Count == (shift ? 0 : 1) && state.Selection.SetEquals([first.Id, second.Id]));
            }

            for (int surface = 0; surface < surfaces.Length; surface++)
            {
                foreach (var control in new Control[] { new TextBox(), new PasswordBox(), new ComboBox(), new Slider() })
                {
                    sources[surface].Panel.Children.Add(control);
                    SetModifiers(ModifierKeys.None);
                    foreach (var key in new[] { Key.B, Key.Tab, Key.D4, Key.OemMinus, Key.OemPlus, Key.Enter, Key.Delete, Key.Right }) ExpectLocal(surface, control, key);
                    SetModifiers(ModifierKeys.Control);
                    foreach (var key in new[] { Key.Z, Key.Y, Key.A, Key.C, Key.V, Key.D, Key.G, Key.Back, Key.Delete }) ExpectLocal(surface, control, key);
                    foreach (var key in new[] { Key.S, Key.O, Key.N }) ExpectRoute(surface, control, key);
                }
            }

            SetModifiers(ModifierKeys.Control);
            int inspectorBefore = commands[0].Count;
            var applyKey = Send(sources[0].Button, Key.Enter);
            check("Inspector Ctrl+Enter remains its local Apply action", applyKey.Handled && commands[0].Count == inspectorBefore);

            SetModifiers(ModifierKeys.None);
            var list = (ListBox)typeof(LayersWindow).GetField("layers", Private)!.GetValue(layers)!;
            foreach (var key in new[] { Key.Left, Key.Up, Key.Right, Key.Down }) ExpectLocal(1, list, key);
            typeof(LayersWindow).GetMethod("BeginRename", Private)!.Invoke(layers, [first.Id]);
            foreach (var key in new[] { Key.B, Key.Tab, Key.D4, Key.Enter, Key.Delete }) ExpectLocal(1, sources[1].Button, key);
            SetModifiers(ModifierKeys.Control);
            foreach (var key in new[] { Key.S, Key.O, Key.N })
            {
                typeof(LayersWindow).GetMethod("BeginRename", Private)!.Invoke(layers, [first.Id]);
                ExpectRoute(1, sources[1].Button, key);
                check("Layers completes rename before the project command " + key, !layers.IsRenaming);
            }
            typeof(LayersWindow).GetMethod("BeginRename", Private)!.Invoke(layers, [first.Id]);
            foreach (var key in new[] { Key.G, Key.Back, Key.Delete }) ExpectLocal(1, sources[1].Button, key);
            typeof(LayersWindow).GetMethod("CancelRename", Private)!.Invoke(layers, null);

            check("shortcut routing matrix uses only detached windows", surfaces.All(window => !window.IsVisible && new WindowInteropHelper(window).Handle == 0));
        }
        finally
        {
            if (stateSaved) check("shortcut matrix restores this test thread's keyboard state", SetKeyboardState(originalKeys));
            foreach (var window in surfaces) window.Close();
        }
        report("complete shortcut surface matrix has no missing routes or local-key interceptions (" + failures.Count + " failures)", failures.Count == 0);

        void SetModifiers(ModifierKeys modifiers)
        {
            var keys = new byte[256];
            if ((modifiers & ModifierKeys.Control) != 0) keys[0x11] = keys[0xA2] = 0x80;
            if ((modifiers & ModifierKeys.Shift) != 0) keys[0x10] = keys[0xA0] = 0x80;
            if (!SetKeyboardState(keys)) throw new InvalidOperationException("Unable to set the fixture thread's keyboard state.");
        }
        void ExpectRoute(int surface, UIElement source, Key key)
        {
            int before = commands[surface].Count; var args = Send(source, key);
            check($"{names[surface]} {source.GetType().Name} routes {Keyboard.Modifiers}+{key}",
                args.Handled && commands[surface].Count == before + 1 && commands[surface][^1] == KeyInterop.VirtualKeyFromKey(key));
        }
        void ExpectLocal(int surface, UIElement source, Key key)
        {
            int before = commands[surface].Count, undoBefore = state.UndoCount;
            Send(source, key);
            check($"{names[surface]} {source.GetType().Name} retains local {Keyboard.Modifiers}+{key}",
                commands[surface].Count == before && state.UndoCount == undoBefore);
        }
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
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetKeyboardState(byte[] keys);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetKeyboardState(byte[] keys);
}
