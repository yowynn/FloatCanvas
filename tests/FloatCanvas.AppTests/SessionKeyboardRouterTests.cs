using System.Reflection;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

/// <summary>Owned HWND messages exercise PreviewKeyDown, control handling and KeyUp together.</summary>
internal static class SessionKeyboardRouterTests
{
    private static PrivateKeyboardBridge? bridge;
    private static SessionKeyboardRouter? routing;
    private static long sequence;
    internal static void Run(Action<string, bool> check)
    {
        ApplicationPaths.Configure(["--profile", Path.Combine(Path.GetTempPath(), "FloatCanvas-keyboard-router", Guid.NewGuid().ToString("N"))]);
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var configuration in new (string Theme, bool Private)[] { ("light", false), ("dark", false), ("light", true), ("dark", true) })
        {
            string theme = configuration.Theme;
            AppTheme.Apply(theme);
            bridge = configuration.Private ? new PrivateKeyboardBridge(_ => false, () => true) : null;
            theme = (configuration.Private ? "private bridge " : "ordinary HWND ") + theme;
            var ordinary = new Button { Content = "Action" };
            var mode = new CanvasModeSwitch(); mode.Synchronize(GroupCanvasMode.Temporary, true);
            var segment = new SegmentedChoice();
            segment.Add("A", 0, new Border()); segment.Add("B", 1, new Border()); segment.SelectedIndex = 0;
            var pins = new CanvasPinSelector();
            var combo = new ComboBox { ItemsSource = new[] { "One", "Two" }, SelectedIndex = 0 };
            var numeric = new NumericValueField(0, 100) { Value = 50 };
            var text = new TextBox { Text = "draft", AcceptsReturn = true };
            var readOnly = new TextBox { Text = "copyable", IsReadOnly = true };
            var list = new ListBox { ItemsSource = new[] { "First", "Second" }, SelectedIndex = 0 };
            var root = new StackPanel { Children = { ordinary, mode, segment, pins, combo, numeric, text, readOnly, list } };
            var window = new Window { Width = 440, Height = 700, Content = root, ShowInTaskbar = false };
            var commands = new List<int>(); int clicks = 0; bool pan = false, active = true;
            ordinary.Click += (_, _) => clicks++; mode.Click += (_, _) => clicks++;
            var bounds = new PixelBounds(-20000, -20000, 440, 700);
            Func<IReadOnlyList<MonitorInfo>> monitors = () => [new("keyboard-router", bounds, bounds, 96, 96, true)];
            using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), BindingFlags.Instance | BindingFlags.NonPublic,
                null, [OverlayMonitorScope.PrimaryScreen, monitors, false], null)!;
            using var coordinator = new SessionWindowCoordinator(host, window);
            using var router = new SessionKeyboardRouter(() => active, coordinator.OwnsKeyboardSource, () => false, commands.Add, value => pan = value);
            routing = router;
            ToolOptionsKeyboardRoute.Attach(window, () => true, commands.Add);
            WindowFocusLifecycle.Attach(window);
            try
            {
                window.Show(); window.Activate(); Pump();
                var pin = ((ToggleButton[])typeof(CanvasPinSelector).GetField("points", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pins)!)[0];
                foreach (var control in new UIElement[] { ordinary, mode, segment.Items[0], pin, combo, numeric.Slider, readOnly, list })
                {
                    string label = theme + " " + control.GetType().Name;
                    control.Focus(); Pump();
                    int before = commands.Count;
                    Transition(Key.Space, true); check(label + " Space starts temporary canvas pan", pan);
                    Transition(Key.Space, true, repeat: true); Transition(Key.Space, false);
                    check(label + " Space release does not issue a canvas command", !pan && commands.Count == before);
                    Press(Key.Tab); check(label + " Tab toggles toolbar without moving focus", commands.Count == before + 1 && control.IsKeyboardFocused);
                    control.Focus();
                    Press(Key.B); check(label + " B dispatches tool shortcut", commands.Count == before + 2 && commands[^1] == 0x42);
                }
                check(theme + " Space never activates stale ordinary button focus", clicks == 0);
                ordinary.Focus();
                using (new ThreadModifierScope(ModifierKeys.Shift)) Press(Key.Tab);
                check(theme + " Shift Tab does not move invisible focus", ordinary.IsKeyboardFocused);
                int beforeFocusKeys = commands.Count; Press(Key.F6);
                using (new ThreadModifierScope(ModifierKeys.Shift)) Press(Key.F6);
                check(theme + " F6 has no hidden focus traversal", ordinary.IsKeyboardFocused && commands.Count == beforeFocusKeys);
                segment.Items[0].Focus(); Press(Key.Right);
                check(theme + " segmented arrows do not change the selected mode", segment.SelectedIndex == 0 && commands[^1] == 0x27);
                int beforeSettings = commands.Count;
                // Ordinary settings are a separate, unregistered window. A tool
                // window retains its own shortcut route even when hidden.
                var settingChoice = new SegmentedChoice(); settingChoice.Add("A", 0, new Border()); settingChoice.Add("B", 1, new Border()); settingChoice.SelectedIndex = 0;
                var settings = new Window { Content = settingChoice, Width = 220, Height = 100, ShowInTaskbar = false };
                OverlayWindowScope.UseRegularWindow(settings);
                try
                {
                    settings.Show(); settings.Activate(); Pump(); settingChoice.Items[0].Focus(); Press(Key.Right);
                    check(theme + " ordinary settings retain segmented keyboard navigation", settingChoice.SelectedIndex == 1 && commands.Count == beforeSettings);
                }
                finally { settings.Close(); window.Activate(); Pump(); }
                segment.Items[0].Focus(); Press(Key.Left);
                check(theme + " returning to editing restores command ownership without switching segments", segment.SelectedIndex == 0 && commands.Count == beforeSettings + 1 && commands[^1] == 0x25);
                var originalPin = pins.SelectedValue; pin.Focus(); Press(Key.Right);
                check(theme + " pin arrows do not move the pin", pins.SelectedValue == originalPin && commands[^1] == 0x27);
                combo.Focus(); Press(Key.Down); int comboBefore = commands.Count; Press(Key.Enter);
                check(theme + " collapsed combo forwards Enter without opening dropdown", !combo.IsDropDownOpen && combo.SelectedIndex == 0 && commands.Count == comboBefore + 1);
                numeric.Slider.Focus(); int n = commands.Count;
                Press(Key.Right); check(theme + " numeric Right remains local and exact", numeric.Value == 51 && commands.Count == n);
                Press(Key.Left); check(theme + " numeric Left remains local and exact", numeric.Value == 50 && commands.Count == n);
                ((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0)).Focus(); Press(Key.Down);
                check(theme + " explicitly focused list retains Down", list.SelectedIndex == 1 && commands.Count == n);

                text.Focus(); text.CaretIndex = 3; n = commands.Count;
                Transition(Key.Space, true); check(theme + " editable text does not start pan", !pan);
                Transition(Key.Space, false); int caret = text.CaretIndex; Press(Key.Left);
                check(theme + " text retains caret movement", text.CaretIndex == Math.Max(0, caret - 1) && commands.Count == n);
                Press(Key.Tab); check(theme + " text Tab does not invoke toolbar command", commands.Count == n);
                text.Focus(); text.Visibility = Visibility.Collapsed; Pump();
                check(theme + " collapsed editor releases keyboard focus", !text.IsKeyboardFocused);
                Press(Key.Tab); check(theme + " collapsed editor returns Tab to toolbar", commands.Count == n + 1);
                text.Visibility = Visibility.Visible; Pump();
                check(theme + " showing editor does not revive text focus", !text.IsKeyboardFocused);
                text.Focus(); text.IsEnabled = false; Pump();
                check(theme + " disabled editor releases focus", !text.IsKeyboardFocused);
                text.IsEnabled = true; text.Focus(); root.Children.Remove(text); Pump();
                check(theme + " replacing content releases the removed editor", !text.IsKeyboardFocused);

                var tilePanel = new StackPanel(); var tileA = new SelectionTileButton { Tag = "a", Content = "Preset A" };
                var tileB = new SelectionTileButton { Tag = "b", Content = "Preset B" };
                tilePanel.Children.Add(tileA); tilePanel.Children.Add(tileB); root.Children.Add(tilePanel); Pump();
                int applied = 0; tileA.Click += (_, _) => applied++;
                tileA.Focus(); Press(Key.Enter);
                check(theme + " preset Enter still applies the focused tile", applied == 1);
                var focus = TileCollection.Capture(tilePanel, new[] { tileA, tileB });
                ordinary.Focus(); TileCollection.Restore(new[] { tileA, tileB }, focus);
                check(theme + " preset refresh cannot reclaim another control's focus", ordinary.IsKeyboardFocused);
                tileA.Focus(); focus = TileCollection.Capture(tilePanel, new[] { tileA, tileB });
                TileCollection.Synchronize(tilePanel, new[] { "b", "a" }, _ => throw new InvalidOperationException(), (_, _) => { });
                TileCollection.Restore(new[] { tileA, tileB }, focus);
                check(theme + " same-list refresh retains live preset navigation", tileA.IsKeyboardFocused);
                tilePanel.Visibility = Visibility.Collapsed; TileCollection.Restore(new[] { tileA, tileB }, focus);
                check(theme + " hidden preset list cannot restore its old focus", !tileA.IsKeyboardFocused);

                var colors = new ColorField(ColorRole.Foreground, new ColorLibrary(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "-colors.json")));
                root.Children.Add(colors); Pump(); colors.OpenPicker(); Pump();
                var picker = Field<ColorPickerPanel>(colors, "picker");
                var alpha = Field<NumericValueField>(picker, "alphaField");
                var hex = Field<TextBox>(picker, "hex");
                alpha.Slider.Focus(); n = commands.Count; Press(Key.Tab); alpha.Slider.Focus(); Press(Key.B);
                check(theme + " owned color popup forwards non-text editing shortcuts", commands.Count == n + 2 && commands[^1] == 0x42);
                Transition(Key.Space, true); check(theme + " color numeric slider permits temporary pan", pan); Transition(Key.Space, false);
                hex.Focus(); n = commands.Count; Transition(Key.Space, true); Transition(Key.Space, false);
                check(theme + " color hex text owns Space", !pan && commands.Count == n);
                colors.TryClosePicker(); Pump();

                var stampState = new EditorSession();
                var stampLibrary = new StampLibrary(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "-stamps"));
                var stamps = new StampLibraryWindow(stampLibrary, () => stampState, _ => throw new InvalidOperationException()) { Owner = window };
                stamps.CommandKey += commands.Add;
                try
                {
                    stamps.Show(); stamps.Activate(); Pump(); Field<TextBox>(stamps, "search").Focus(); n = commands.Count;
                    using (new ThreadModifierScope(ModifierKeys.Control)) Press(Key.N);
                    check(theme + " stamp search Ctrl N retains save-stamp semantics", commands.Count == n &&
                        Field<TextBlock>(stamps, "status").Text == FloatCanvas.Localization.AppText.Get("Stamp.NoSelection"));
                }
                finally { stamps.Close(); Pump(); }

                ordinary.Focus(); n = commands.Count;
                combo.IsDropDownOpen = true; combo.Focus(); Pump();
                Transition(Key.Space, true); check(theme + " open dropdown owns Space", !pan);
                combo.IsDropDownOpen = false; ordinary.Focus(); Pump();
                Transition(Key.Space, true, repeat: true);
                check(theme + " closing dropdown cannot transfer a held Space", !pan && commands.Count == n);
                Transition(Key.Space, false);
                Press(Key.Space); check(theme + " next Space has a balanced release", !pan);

                var menu = new OverlayContextMenu { PlacementTarget = ordinary };
                menu.Items.Add(new MenuItem { Header = "Menu item" });
                menu.IsOpen = true; Pump();
                Transition(Key.Space, true); check(theme + " open menu does not pan canvas", !pan);
                menu.IsOpen = false; ordinary.Focus(); Pump();
                Transition(Key.Space, true, repeat: true); check(theme + " closed menu repeat stays out of canvas", !pan);
                Transition(Key.Space, false);

                // Modal entry ends an editor-owned hold. A fresh press inside the
                // modal still belongs to its button and receives its release.
                ordinary.Focus(); Transition(Key.Space, true);
                var modalButton = new Button { Content = "Confirm" };
                var modal = new Window { Owner = window, Content = modalButton, Width = 200, Height = 100, ShowInTaskbar = false };
                int confirmations = 0; modalButton.Click += (_, _) => confirmations++;
                modal.Loaded += (_, _) => modal.Dispatcher.BeginInvoke(() =>
                {
                    check(theme + " modal entry cancels canvas pan", !pan);
                    modalButton.Focus(); Transition(Key.Space, false);
                    Press(Key.Space); check(theme + " modal Space still activates its button once", confirmations == 1 && !pan);
                    Transition(Key.Space, true); modal.Close();
                });
                modal.ShowDialog(); ordinary.Focus(); Pump();
                Transition(Key.Space, true, repeat: true);
                check(theme + " modal close requires a fresh canvas press", !pan);
                Transition(Key.Space, false);
                Transition(Key.Space, true); check(theme + " new press after modal starts temporary pan", pan);
                active = false; router.Cancel(); check(theme + " input ownership loss clears pan", !pan);
                active = true; Transition(Key.Space, true, repeat: true); check(theme + " ownership return ignores held repeat", !pan);
                Transition(Key.Space, false);
                using (new ThreadModifierScope(ModifierKeys.Control))
                { Transition(Key.Space, true); check(theme + " Ctrl Space never pans", !pan); Transition(Key.Space, false); }
                using (new ThreadModifierScope(ModifierKeys.Shift))
                { Transition(Key.Space, true); check(theme + " Shift Space never starts an undeclared canvas gesture", !pan); Transition(Key.Space, false); }
            }
            finally { window.Close(); Pump(); routing = null; bridge = null; }
        }
        AppTheme.Apply("light");
    }

    private static void Press(Key key) { Transition(key, true); Transition(key, false); }
    private static void Transition(Key key, bool down, bool repeat = false)
    {
        if (Keyboard.FocusedElement is not Visual focus || PresentationSource.FromVisual(focus) is not HwndSource source)
            throw new InvalidOperationException("Keyboard fixture needs an owned, focused HWND.");
        byte[] original = ThreadModifierScope.Read(); int code = KeyInterop.VirtualKeyFromKey(key);
        try
        {
            var state = (byte[])original.Clone(); state[code] = down ? (byte)0x80 : (byte)0;
            if (bridge is not null)
            {
                var packet = (KeyboardInputPacket)Activator.CreateInstance(typeof(KeyboardInputPacket), BindingFlags.Instance | BindingFlags.NonPublic, null,
                    [++sequence, 1L, KeyboardInputPacketKind.Key, (uint)code, 0u, 0u, (uint)Environment.TickCount, !down, repeat, (nint)0, state, false, null, down ? 0x100u : 0x101u], null)!;
                if (key == Key.Space) routing!.Space(down, repeat, packet.Modifiers, Keyboard.FocusedElement as DependencyObject);
                int acknowledgements = 0;
                if (!bridge.Process(packet, () => acknowledgements++) || acknowledgements != 1)
                    throw new InvalidOperationException("Private keyboard packet must dispatch and acknowledge exactly once.");
            }
            else
            {
                SetKeyboardState(state);
                var message = new MSG { hwnd = source.Handle, message = down ? 0x100 : 0x101, wParam = code,
                    lParam = down ? repeat ? 0x40000001 : 1 : unchecked((nint)0xC0000001), time = Environment.TickCount };
                if (!ComponentDispatcher.RaiseThreadMessage(ref message)) DispatchMessage(ref message);
            }
            Pump();
        }
        finally { SetKeyboardState(original); }
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(ref MSG message);
}
