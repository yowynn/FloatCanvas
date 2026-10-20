using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

/// <summary>Real auxiliary-window handlers, with detached controls and only thread-local key state.</summary>
internal static class AuxiliaryShortcutRoutingTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        var canvas = new CanvasSettingsWindow(CanvasPresetLibrary.OfficialOnly());
        var state = new EditorSession();
        var library = new StampLibrary(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FloatCanvas-AuxiliaryShortcut-" + Guid.NewGuid().ToString("N")));
        var stamps = new StampLibraryWindow(library, () => state, _ => throw new InvalidOperationException("An absent fixture stamp must never be used."));
        var settings = new AppSettingsWindow();
        var dialog = AppDialog.Create(null, "Detached shortcut fixture", "Fixture").Window;
        var windows = new Window[] { canvas, stamps, settings, dialog };
        var canvasCommands = new List<int>(); var stampCommands = new List<int>();
        var originalKeys = new byte[256];
        bool saved = GetKeyboardState(originalKeys);
        try
        {
            check("auxiliary shortcut fixture preserves its thread keyboard state", saved);
            if (!saved) return;
            SetModifiers(ModifierKeys.None);
            bool canvasRoute = Subscribe(canvas, canvasCommands.Add), stampRoute = Subscribe(stamps, stampCommands.Add);
            check("CanvasSettings exposes its production application command route", canvasRoute);
            check("StampLibrary exposes its production fallback command route", stampRoute);
            if (!canvasRoute || !stampRoute) return;

            var canvasSource = Field<Button>(canvas, "calibrate");
            foreach (var key in new[] { Key.B, Key.D1, Key.D0, Key.Enter, Key.Delete, Key.Right })
                ExpectRoute("CanvasSettings", canvasSource, key, canvasCommands);
            SetModifiers(ModifierKeys.Control);
            check("auxiliary shortcuts use only a thread-local Ctrl snapshot", Keyboard.Modifiers == ModifierKeys.Control);
            foreach (var key in new[] { Key.Z, Key.Y, Key.S, Key.O, Key.N, Key.G, Key.Back, Key.Delete })
                ExpectRoute("CanvasSettings", canvasSource, key, canvasCommands);
            SetModifiers(ModifierKeys.Control | ModifierKeys.Shift);
            ExpectRoute("CanvasSettings", canvasSource, Key.G, canvasCommands);

            var counts = Field<NumericValueField>(canvas, "columns");
            foreach (var control in new Control[] { counts.Input, counts.Slider, Field<ComboBox>(canvas, "pattern") })
            {
                SetModifiers(ModifierKeys.None);
                foreach (var key in new[] { Key.B, Key.D4, Key.Enter, Key.Delete, Key.Right })
                    ExpectLocal("CanvasSettings", control, key, canvasCommands);
                SetModifiers(ModifierKeys.Control);
                foreach (var key in new[] { Key.Z, Key.A, Key.V, Key.G, Key.Back, Key.Delete })
                    ExpectLocal("CanvasSettings", control, key, canvasCommands);
                foreach (var key in new[] { Key.S, Key.O }) ExpectRoute("CanvasSettings", control, key, canvasCommands);
            }
            var presets = Field<CanvasPresetGallery>(canvas, "presets");
            var presetTile = presets.OfficialTiles.Children.OfType<Button>().First();
            SetModifiers(ModifierKeys.None);
            foreach (var key in new[] { Key.Enter, Key.Delete }) ExpectLocal("Canvas preset tile", presetTile, key, canvasCommands);

            int dismissals = 0, canvasBeforeEscape = canvasCommands.Count;
            canvas.Dismissed += () => dismissals++;
            var canvasEscape = Send(canvasSource, Key.Escape);
            check("CanvasSettings Escape dismisses its panel without also issuing canvas Escape", canvasEscape.Handled && dismissals == 1 && canvasCommands.Count == canvasBeforeEscape);

            var stampList = Field<ListBox>(stamps, "grid");
            foreach (var key in new[] { Key.B, Key.D1, Key.D0 }) ExpectRoute("StampLibrary", stampList, key, stampCommands);
            SetModifiers(ModifierKeys.Control);
            foreach (var key in new[] { Key.Z, Key.Y, Key.S, Key.O, Key.G }) ExpectRoute("StampLibrary", stampList, key, stampCommands);
            SetModifiers(ModifierKeys.Control | ModifierKeys.Shift);
            ExpectRoute("StampLibrary", stampList, Key.G, stampCommands);

            var search = Field<TextBox>(stamps, "search");
            SetModifiers(ModifierKeys.None);
            foreach (var key in new[] { Key.B, Key.D1, Key.Enter, Key.F2, Key.Delete, Key.Right })
                ExpectLocal("Stamp search", search, key, stampCommands);
            SetModifiers(ModifierKeys.Control);
            foreach (var key in new[] { Key.A, Key.Z, Key.Y, Key.C, Key.V, Key.G, Key.N, Key.Back, Key.Delete })
                ExpectLocal("Stamp search", search, key, stampCommands);
            foreach (var key in new[] { Key.S, Key.O }) ExpectRoute("Stamp search", search, key, stampCommands);
            search.Text = "retained search draft";
            int before = stampCommands.Count;
            var find = Send(search, Key.F);
            check("StampLibrary local Ctrl+F selects its existing search draft without a canvas command", find.Handled && search.SelectionLength == search.Text.Length && stampCommands.Count == before);
            search.Clear();
            var searchNewStamp = Send(search, Key.N);
            check("Stamp search Ctrl+N retains the library save-selection command", searchNewStamp.Handled && stampCommands.Count == before && Field<TextBlock>(stamps, "status").Text == L.Get("Stamp.NoSelection"));

            var newStamp = Send(stampList, Key.N);
            check("StampLibrary Ctrl+N uses its local save-selection action instead of creating a canvas", newStamp.Handled && stampCommands.Count == before && Field<TextBlock>(stamps, "status").Text == L.Get("Stamp.NoSelection"));
            SetModifiers(ModifierKeys.None);
            Set(stamps, "selected", null);
            foreach (var key in new[] { Key.Enter, Key.F2, Key.Delete })
            {
                var args = Send(stampList, key);
                check("StampLibrary with no selected stamp keeps " + key + " local without changing the canvas", args.Handled && stampCommands.Count == before && state.Document.Elements.Count == 0);
            }
            // No catalog entry exists for this ID: the real local branches return before
            // prompting, mutating a library, or starting asynchronous rendering.
            Set(stamps, "selected", (Guid?)Guid.NewGuid());
            foreach (var key in new[] { Key.Enter, Key.F2, Key.Delete })
            {
                var args = Send(stampList, key);
                check("StampLibrary retains its local " + key + " action without canvas fallback", args.Handled && stampCommands.Count == before && library.Items.Count == 0);
            }
            Set(stamps, "selectedSlot", (int?)4);
            int closes = 0;
            stamps.Closing += (_, _) => closes++;
            var firstEscape = Send(stampList, Key.Escape);
            check("first StampLibrary Escape clears only its slot destination", firstEscape.Handled && Field<int?>(stamps, "selectedSlot") is null && closes == 0 && stampCommands.Count == before);
            var secondEscape = Send(stampList, Key.Escape);
            check("next StampLibrary Escape closes only that window", secondEscape.Handled && closes == 1 && stampCommands.Count == before);

            foreach (var (name, window) in new[] { ("Settings draft", (Window)settings), ("Confirmation dialog", dialog) })
            {
                check(name + " has no canvas command event", window.GetType().GetEvent("CommandKey") is null);
                SetModifiers(ModifierKeys.Control);
                foreach (var key in new[] { Key.S, Key.O, Key.N, Key.Z, Key.G })
                    check(name + " does not consume a canvas shortcut " + key, !Send(window, key).Handled);
                SetModifiers(ModifierKeys.None);
                foreach (var key in new[] { Key.B, Key.D1, Key.Delete })
                    check(name + " does not consume a canvas shortcut " + key, !Send(window, key).Handled);
            }
            check("settings and confirmation retain their own default buttons", settings.SaveButton.IsDefault &&
                ((DockPanel)dialog.Content).Children.OfType<StackPanel>().Single().Children.OfType<Button>().Single().IsDefault);
            check("auxiliary routing uses no native window, visibility or activation", windows.All(window => !window.IsVisible && new WindowInteropHelper(window).Handle == 0));
        }
        finally
        {
            if (saved) check("auxiliary shortcut fixture restores its thread keyboard state", SetKeyboardState(originalKeys));
            canvas.CloseForDispose(); stamps.Close(); settings.Close(); dialog.Close();
        }

        void ExpectRoute(string name, UIElement source, Key key, List<int> commands)
        {
            int before = commands.Count; var args = Send(source, key);
            check($"{name} {source.GetType().Name} routes {Keyboard.Modifiers}+{key}", args.Handled && commands.Count == before + 1 && commands[^1] == KeyInterop.VirtualKeyFromKey(key));
        }
        void ExpectLocal(string name, UIElement source, Key key, List<int> commands)
        {
            int before = commands.Count; Send(source, key);
            check($"{name} {source.GetType().Name} keeps local {Keyboard.Modifiers}+{key}", commands.Count == before);
        }
    }

    private static bool Subscribe(Window window, Action<int> observer)
    {
        var command = window.GetType().GetEvent("CommandKey");
        if (command?.EventHandlerType != typeof(Action<int>)) return false;
        command.AddEventHandler(window, observer); return true;
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static void SetModifiers(ModifierKeys modifiers)
    {
        var keys = new byte[256];
        if ((modifiers & ModifierKeys.Control) != 0) keys[0x11] = keys[0xA2] = 0x80;
        if ((modifiers & ModifierKeys.Shift) != 0) keys[0x10] = keys[0xA0] = 0x80;
        if (!SetKeyboardState(keys)) throw new InvalidOperationException("Unable to set this test thread's keyboard state.");
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
