using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using FloatCanvas.Windows;

/// <summary>Own bounded native input and real WPF editors, through the production private keyboard bridge.
/// Default scenarios use no SendInput/desktop cursor changes. The explicit ctrl-native-selection
/// scenarios additionally exercise guarded physical input. No default hotkeys or user-profile writes.</summary>
internal static class NativeInputRoutingFixture
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, string? scenarioOverride = null)
    {
        if (Application.Current is not null) throw new InvalidOperationException("Run --native-input-routing-only in a fresh process.");
        string profile = Path.Combine(Path.GetTempPath(), "FloatCanvasNativeInput", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile); ApplicationPaths.Configure(["--profile", profile, "--primary-screen"]);
        new UserSettings { Language = Environment.GetEnvironmentVariable("FLOATCANVAS_ROUND2_LANGUAGE")??"en", Theme = Environment.GetEnvironmentVariable("FLOATCANVAS_ROUND2_THEME")??"light" }.Save();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var oldContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        nint originalForeground = GetForegroundWindow(); DesktopSession? session = null; OverlayHost? host = null; Window? permissionGate = null; nint[] handles = [];
        var originalKeys = new byte[256]; GetKeyboardState(originalKeys);
        TextBox? observedHex = null;
        KeyboardInputIsolation? observedIsolation = null;
        ProcessInputEventHandler beforePointerFocus = (_, e) => ObserveHexPress("registered-before-session", e);
        ProcessInputEventHandler afterPointerFocus = (_, e) => ObserveHexPress("registered-after-session", e);
        InputManager.Current.PostProcessInput += beforePointerFocus;
        try
        {
            var primary = ((IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!).First(m => m.IsPrimary);
            var tile = new PixelBounds(primary.WorkArea.Left + 30, primary.WorkArea.Top + 30, 640, 360);
            var monitor = primary with { Bounds = tile };
            Func<IReadOnlyList<MonitorInfo>> provider = () => [monitor];
            host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), PrivateInstance, null, [OverlayMonitorScope.PrimaryScreen, provider, false], null)!;
            handles = host.Diagnostics.SelectMany(pair => new[] { pair.InputHandle, pair.DisplayHandle }).Append(host.ControlHandle).ToArray();
            session = new DesktopSession(app, host);
            InputManager.Current.PostProcessInput += afterPointerFocus;
            Field<System.Windows.Forms.NotifyIcon>(session, "_tray").Visible = false;
            var isolation = typeof(OverlayHost).GetField("_keyboardIsolation", PrivateInstance)!;
            bool realCtrlInput = Environment.GetEnvironmentVariable("FLOATCANVAS_INPUT_CASE")?.StartsWith("ctrl-native-selection", StringComparison.Ordinal) == true;
            if (realCtrlInput) observedIsolation = (KeyboardInputIsolation)isolation.GetValue(host)!;
            if (!realCtrlInput && scenarioOverride != "input-method-ui") { (isolation.GetValue(host) as IDisposable)?.Dispose(); isolation.SetValue(host, null); }
            var editor = Field<EditorController>(session, "_editor");
            var dispatch = Field<Action<KeyboardInputPacket, Action>>(host, "_dispatchPrivateKey");
            var library = Field<StampLibrary>(session, "_stampLibrary");
            string scenario = scenarioOverride ?? Environment.GetEnvironmentVariable("FLOATCANVAS_INPUT_CASE") ?? "all";
            if (scenario is not ("feedback4-panels" or "feedback4-workspace" or "preview4-settings" or "preview4-menu" or "round2-stamps" or "move-into-view" or "launcher-tab")) Task.Run(async () =>
            {
                await library.LoadAsync();
                var source = new EditorSession(new SceneDocument { Elements = [new CanvasElement { Kind = ElementKind.Rectangle, Width = 40, Height = 30 }] });
                source.SelectAll(); var entry = await library.SaveSelectionAsync(source, "Slot two must not intercept editor input");
                await library.PinAsync(1, entry.Id);
            }).GetAwaiter().GetResult(); Pump();
            bool waitForForeground = Environment.GetEnvironmentVariable("FLOATCANVAS_INPUT_WAIT_FOR_FOCUS") == "1";
            var toolbar = Field<Window>(session, "_toolbar");
            if (waitForForeground) toolbar.Title = "FloatCanvas C3 Input Check";
            toolbar.ShowActivated = !waitForForeground && scenario != "popup-ownership"; toolbar.Show(); Pump();
            if (scenario != "popup-ownership")
            {
                if (waitForForeground)
                {
                    bool permissionRequested = false;
                    var start = new Button { Content = "Start", Width = 100, Height = 30, Margin = new Thickness(0, 12, 0, 0), Style = null };
                    start.Click += (_, _) => permissionRequested = true;
                    var content = new StackPanel { Margin = new Thickness(18) };
                    content.Children.Add(new TextBlock { Text = "Start the isolated input check.", Foreground = Brushes.Black }); content.Children.Add(start);
                    permissionGate = new Window { Title = "FloatCanvas C3 Input Start", Width = 300, Height = 140, Content = content,
                        WindowStyle = WindowStyle.SingleBorderWindow, ResizeMode = ResizeMode.NoResize, AllowsTransparency = false, Style = null,
                        Background = Brushes.White, Foreground = Brushes.Black, ShowInTaskbar = true, ShowActivated = false, Topmost = true,
                        WindowStartupLocation = WindowStartupLocation.CenterScreen };
                    permissionGate.Show(); Pump();
                    nint gateHandle = new WindowInteropHelper(permissionGate).Handle;
                    Console.WriteLine($"WAIT_FOR_FOCUS title=FloatCanvas C3 Input Start hwnd={gateHandle:X} timeoutSeconds=90");
                    Console.Out.Flush();
                    check("the fixture receives real foreground permission within its bounded wait", WaitForForeground(() => permissionRequested && IsFixtureForeground()));
                    check("the permission click is fully released before the native canvas opens", WaitForPermissionClickRelease());
                }
                // Construction deliberately keeps the bounded tile hidden. Editing no longer
                // changes canvas visibility, so drawing fixtures must request display explicitly.
                host.SetVisible(true);
                bool entered = false;
                ActivateFixtureWindow(new WindowInteropHelper(toolbar).Handle, () => entered = (bool)Invoke("SetEditing", true, false)!); Pump();
                permissionGate?.Close(); permissionGate = null; Pump();
                Console.WriteLine($"ENTRY result={entered} editing={host.IsEditing} visible={host.IsVisible} foreground={GetForegroundWindow():X} toolbar={new WindowInteropHelper(toolbar).Handle:X} input={host.Diagnostics.Single().InputHandle:X}");
                check("native input fixture enters only its own bounded edit surface", entered && host.IsEditing && host.IsVisible && host.Diagnostics.All(pair => pair.Bounds.Width == 640 && pair.Bounds.Height == 360));
            }
            if (scenario is "all" or "ctrl")
            {
                editor.Tool = EditorTool.Rectangle;
                var input = host.Diagnostics.Single().InputHandle;
                SetForegroundWindow(input); SetFocus(input);
                Key(0x11, false, control: true);
                SendMessage(input, 0x201, 0x0009, Pack(100, 100));
                SendMessage(input, 0x200, 0x0009, Pack(180, 140));
                Console.WriteLine($"CTRL before repeat transaction={editor.Session.IsTransactionActive} elements={editor.Session.Document.Elements.Count}");
                check("Ctrl creation starts a real transaction through the native canvas press", editor.Session.IsTransactionActive);
                Key(0x11, false, control: true, repeat: true);
                Console.WriteLine($"CTRL after repeat transaction={editor.Session.IsTransactionActive} elements={editor.Session.Document.Elements.Count}");
                check("a modifier packet during Ctrl-drag does not cancel the in-progress drawing", editor.Session.IsTransactionActive);
                SendMessage(input, 0x202, 0x0008, Pack(180, 140)); Key(0x11, true);
                check("Ctrl-drag completes one editable rectangle", editor.Session.Document.Elements.Count == 1 && editor.Session.UndoCount == 1);
                editor.Session.Execute("fixture clear", doc => doc.Elements.Clear()); editor.Session.SetSelection([]);
            }
            if (realCtrlInput)
            {
                toolbar.Left = (host.Diagnostics.Single().Bounds.Right + 40) * 96d / host.PrimaryMonitor.DpiX;
                Field<Dictionary<EditorTool, Button>>(session, "_toolbarToolButtons")[EditorTool.Rectangle].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
                if (Environment.GetEnvironmentVariable("FLOATCANVAS_INPUT_DISABLE_PRESS_SNAPSHOTS") == "1")
                {
                    var actualIsolation = Field<KeyboardInputIsolation>(host, "_keyboardIsolation");
                    var pointerHook = typeof(KeyboardInputIsolation).GetField("pointerHook", PrivateInstance)!;
                    nint installed = (nint)pointerHook.GetValue(actualIsolation)!;
                    if (installed == 0 || !UnhookWindowsHookEx(installed)) throw new InvalidOperationException("Could not disable the fixture's own observation hook for its history-only negative control.");
                    pointerHook.SetValue(actualIsolation, (nint)0);
                }
                Action? nestedInput = null;
                bool delayOnePacket = scenario.Contains("-pending", StringComparison.Ordinal);
                if (delayOnePacket)
                    typeof(OverlayHost).GetField("_dispatchPrivateKey", PrivateInstance)!.SetValue(host,
                        (Action<KeyboardInputPacket, Action>)((packet, complete) =>
                        {
                            if (packet.VirtualKey == 0x78 && !packet.IsKeyUp)
                            {
                                dispatch(packet, () => { });
                                var frame = new DispatcherFrame(); Exception? failure = null;
                                app.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                                { try { nestedInput!(); } catch (Exception error) { failure = error; } finally { frame.Continue = false; } });
                                Dispatcher.PushFrame(frame);
                                if (failure is not null) throw failure;
                            }
                            else dispatch(packet, complete);
                        }));
                void DuringPendingPacket(Action input, Action trigger) { nestedInput = input; trigger(); }
                try { CtrlCreationRoutingFixture.Run(check, host, editor, scenario.EndsWith("-toolbar", StringComparison.Ordinal) ? toolbar : null,
                    delayOnePacket ? DuringPendingPacket : null, scenario.Contains("-buffered", StringComparison.Ordinal)); }
                finally
                {
                    typeof(OverlayHost).GetField("_dispatchPrivateKey", PrivateInstance)!.SetValue(host, dispatch);
                }
            }
            if (scenario == "stamp-shortcut")
            {
                editor.Session.Add(new CanvasElement { Kind = ElementKind.Rectangle, Width = 60, Height = 35 });
                editor.Session.SelectAll();
                nint layout = GetKeyboardLayout(0), english = LoadKeyboardLayout("00000409", 0);
                try
                {
                    check("shortcut fixture selects English only for its own UI thread", english != 0 && ActivateKeyboardLayout(english, 0) != 0);
                    foreach (string origin in new[] { "toolbar", "canvas", "options" })
                    foreach (bool shift in new[] { false, true })
                    {
                        bool initial = false, repeat = false, fresh = false;
                        Window source = toolbar;
                        if (origin == "options")
                        {
                            Invoke("OpenToolOptions"); source = Field<Window>(session, "_toolOptionsWindow");
                        }
                        source.Activate(); source.Focus(); Pump();
                        nint sourceHandle = origin == "canvas" ? host.Diagnostics.Single().InputHandle : new WindowInteropHelper(source).Handle;
                        SetForegroundWindow(sourceHandle); SetFocus(sourceHandle); ActivateKeyboardLayout(english, 0); Pump();
                        using var observer = ObservePrompt(dialog =>
                        {
                            var name = ((StackPanel)dialog.Content).Children.OfType<TextBox>().Single();
                            initial = name.Text == FloatCanvas.Localization.AppText.Get("Stamp.DefaultName") && name.SelectionLength == name.Text.Length;
                            Key(0xBB, false, repeat: true, shift: shift);
                            repeat = name.Text == FloatCanvas.Localization.AppText.Get("Stamp.DefaultName") && name.SelectionLength == name.Text.Length;
                            Key(0xBB, true); Key(0xBB, false, shift: shift); Key(0xBB, true);
                            fresh = name.Text == (shift ? "+" : "=");
                            Console.WriteLine($"STAMP_SHORTCUT origin={origin} shift={shift} initial={initial} heldRepeat={repeat} freshText={name.Text}");
                        });
                        Key(0xBB, false, shift: shift); observer.Wait(); Key(0xBB, true);
                        check($"{origin} {(shift ? "+" : "=")} shortcut preserves the selected default name", initial);
                        check($"{origin} held shortcut repeats do not replace the default name", repeat);
                        check($"{origin} release followed by a new press remains editable text", fresh);
                        if (origin == "options") Invoke("CloseToolOptions");
                    }
                    bool ordinaryText = false;
                    toolbar.Activate(); toolbar.Focus(); Pump(); ActivateKeyboardLayout(english, 0);
                    using (var observer = ObservePrompt(dialog =>
                    {
                        var name = ((StackPanel)dialog.Content).Children.OfType<TextBox>().Single();
                        Key(0xBB, false); Key(0xBB, true); ordinaryText = name.Text == "=";
                    })) { Invoke("SaveStampSelection"); observer.Wait(); }
                    check("mouse-command stamp creation keeps ordinary equals text input", ordinaryText);
                }
                finally { ActivateKeyboardLayout(layout, 0); }
            }
            if (scenario == "feedback4-panels") Feedback4Panels(check, app, session, host, toolbar, editor);
            if (scenario == "canvas-commands") CanvasCommandRuntimeTests.Run(check, session, host, toolbar,
                (vk, up, ctrl, shift) => Key(vk, up, control: ctrl, shift: shift));
            if (scenario == "input-method-ui") InputMethodUiContextTests.Run(check, session, host, toolbar);
            if (scenario == "shortcut-matrix") ShortcutRoutingMatrixRuntimeTests.Run(check, session, host, toolbar, dispatch);
            if (scenario == "aux-shortcut-runtime") AuxiliaryShortcutRuntimeTests.Run(check, session, host, toolbar, dispatch);
            if (scenario == "launcher-tab") LauncherTabRuntimeTests.Run(check, session, host, toolbar, dispatch);
            if (scenario == "feedback4-workspace") Feedback4Workspace(check, session, host, toolbar, editor);
            if (scenario == "preview4-settings") Preview4SettingsRuntimeFixture.Run(check, session, host, toolbar, action => ActivateFixtureWindow(new WindowInteropHelper(toolbar).Handle, action));
            if (scenario == "preview4-menu") Preview4MenuRuntimeFixture.Run(check, session, host, toolbar);
            if (scenario == "round2-stamps") Round2StampRuntimeFixture.Run(check,session,toolbar,editor);
            if (scenario == "move-into-view") MoveIntoViewRuntimeFixture.Run(check, session, host);
            if (scenario is "all" or "options")
            {
                Invoke("ActivateTool", EditorTool.Rectangle); if (!Field<Window>(session, "_toolOptionsWindow").IsVisible) Invoke("ActivateTool", EditorTool.Rectangle); Pump();
                var popup = Field<Window>(session, "_toolOptionsWindow");
                var panel = Field<ToolOptionsPanel>(session, "_toolOptionsPanel");
                var box = Field<Dictionary<string, TextBox>>(panel, "numbers")["StrokeWidth"];
                bool focused;
                if (Environment.GetEnvironmentVariable("FLOATCANVAS_INPUT_MOUSE") == "1")
                {
                    Click(box);
                    focused = box.IsKeyboardFocusWithin;
                }
                else focused = box.Focus();
                box.SelectAll(); Pump();
                Trace("OPTIONS before", box, popup);
                Key(0x33, false); Key(0x33, true); Invoke("RenderFrame"); Pump(); Trace("OPTIONS after3", box, popup);
                Key(0x32, false); Key(0x32, true); Trace("OPTIONS after32", box, popup);
                check("the numeric options field keeps 32 and keyboard focus through the actual bridge", focused && box.Text == "32" && box.IsKeyboardFocusWithin && popup.IsVisible && editor.Tool == EditorTool.Rectangle && editor.Style.StrokeWidth == 32);
            }
            if (scenario is "all" or "toolbar-shortcuts")
            {
                var button = Field<Dictionary<EditorTool, Button>>(session, "_toolbarToolButtons")[EditorTool.Select];
                foreach (var (key, expected) in new[] { (0x48u, EditorTool.Highlighter), (0x53u, EditorTool.Symbol), (0x4Au, EditorTool.Emoji) })
                {
                    editor.Tool = EditorTool.Rectangle; Invoke("CloseToolOptions");
                    SetForegroundWindow(new WindowInteropHelper(toolbar).Handle); Click(button); button.Focus(); Pump();
                    Key(key, false); Key(key, true);
                    check($"toolbar-focused {(char)key} routes through the shared tool shortcut catalog", button.IsKeyboardFocusWithin && editor.Tool == expected);
                }
                Invoke("CloseToolOptions"); SetForegroundWindow(new WindowInteropHelper(toolbar).Handle); Click(button); button.Focus(); Pump();
                var previousTool = editor.Tool;
                Key(0x4D, false); Key(0x4D, true);
                var more = Field<ContextMenu>(session, "_moreToolsMenu");
                check("toolbar-focused M opens its actual More menu and retains the current tool", more.IsOpen && more.PlacementTarget is FrameworkElement target && Window.GetWindow(target) == toolbar && editor.Tool == previousTool);
                more.IsOpen = false; Pump();
            }
            if (scenario is "all" or "inspector")
            {
                editor.Session.Add(new CanvasElement { Kind = ElementKind.Rectangle, Width = 80, Height = 60 });
                Invoke("ToggleInspector"); Pump();
                var inspector = Field<InspectorWindow>(session, "_inspector");
                var box = Field<Dictionary<string, TextBox>>(inspector, "fields")["W"];
                Click(box); bool focused = box.IsKeyboardFocusWithin; box.SelectAll(); Pump();
                Trace("INSPECTOR before", box, null);
                Key(0x33, false); Key(0x33, true); Invoke("RenderFrame"); inspector.Refresh(); Pump(); Trace("INSPECTOR after3", box, null);
                Key(0x32, false); Key(0x32, true); Trace("INSPECTOR after32", box, null);
                check("the property field keeps 32 without invoking the stamp shortcut", focused && box.Text == "32" && box.IsKeyboardFocusWithin && inspector.IsVisible && editor.Tool != EditorTool.Stamp);
            }
            if (scenario is "all" or "popup-controls" or "popup-ownership" or "color-controls")
            {
                if (scenario == "popup-ownership") { editor.Tool = EditorTool.Rectangle; Pump(); Invoke("OpenToolOptions"); }
                else { Invoke("ActivateTool", EditorTool.Rectangle); if (!Field<Window>(session, "_toolOptionsWindow").IsVisible) Invoke("ActivateTool", EditorTool.Rectangle); }
                Pump();
                var popup = Field<Window>(session, "_toolOptionsWindow");
                var panel = Field<ToolOptionsPanel>(session, "_toolOptionsPanel");
                if (scenario is not ("popup-ownership" or "color-controls"))
                {
                    if (editor.Session.Document.Elements.Count == 0) editor.Session.Add(new CanvasElement { Kind = ElementKind.Rectangle, Width = 80, Height = 60, Transform = Affine2D.Translation(100, 100) });
                    var combo = Field<Dictionary<string, ComboBox>>(panel, "pickers")["StrokePattern"];
                    // The standard dropdown is opened through its public state here. Physical
                    // pointer hit testing is outside this private-keyboard routing fixture.
                    Click(combo); combo.Focus(); combo.IsDropDownOpen = true; Pump();
                    var focused = Keyboard.FocusedElement as DependencyObject;
                    nint focusSource = focused is null ? 0 : ((HwndSource?)PresentationSource.FromDependencyObject(focused))?.Handle ?? 0;
                    nint parentSource = ((HwndSource?)PresentationSource.FromVisual(combo))?.Handle ?? 0;
                    Console.WriteLine($"COMBO programmatically opened standard dropdown, native keyboard routing native={GetFocus():X} logical={focusSource:X} focused={focused?.GetType().Name} parent={popup.IsVisible} opened={combo.IsDropDownOpen}");
                    check("the programmatically opened standard dropdown retains its own input scope", combo.IsDropDownOpen && popup.IsVisible && focusSource != 0 &&
                        (bool)Invoke("IsOptionsInput", focused!)! && (GetFocus() == focusSource || GetFocus() == parentSource));
                    int originalIndex = combo.SelectedIndex; var transforms = editor.Session.Document.Elements.Select(e => e.Transform).ToArray();
                    Key(0x28, false); Key(0x28, true); Key(0x0D, false); Key(0x0D, true);
                    check("the programmatically opened dropdown consumes native navigation and commit", combo.SelectedIndex != originalIndex && !combo.IsDropDownOpen && popup.IsVisible && editor.Tool == EditorTool.Rectangle && editor.Session.Document.Elements.Select(e => e.Transform).SequenceEqual(transforms));
                }
                if (scenario != "color-controls")
                {
                var savedPreset = Field<ToolPresetLibrary>(session, "_toolPresets").Save("Native routing fixture preset", editor.Parameters);
                panel.Refresh(); Pump();
                var presets = Field<ToolPresetGallery>(panel, "presets");
                presets.Apply(savedPreset.Id); Pump();
                var presetTile = presets.UserTiles.Children.OfType<Button>().Single(item => Equals(item.Tag, savedPreset.Id));
                var presetMenu = presetTile.ContextMenu!; presetMenu.PlacementTarget = presetTile; presetMenu.IsOpen = true; Pump();
                var presetItem = presetMenu.Items.OfType<MenuItem>().First();
                check("the mixed routing prelude uses a visible selected preset and enabled menu command", presets.IsVisible && presets.UserTiles.Children.Count > 0 && presets.SelectedId == savedPreset.Id && presetItem.IsEnabled);
                check("preset context-menu items retain their originating tool-options ownership", (bool)Invoke("IsOptionsInput", presetItem)!);
                presetMenu.IsOpen = false; Pump();
                }

                var foreground = Field<ColorField>(panel, "foreground");
                var colorPopup = Field<Popup>(foreground, "popup");
                var picker = Field<ColorPickerPanel>(foreground, "picker");
                bool hexResult = true, hueResult = true, spectrumResult = true;
                OpenColor();
                if (scenario != "popup-ownership")
                {
                if (editor.Session.Document.Elements.Count == 0) editor.Session.Add(new CanvasElement { Kind = ElementKind.Rectangle, Width = 80, Height = 60, Transform = Affine2D.Translation(100, 100) });
                var hex = Field<TextBox>(picker, "hex"); observedHex = hex; TraceColor("COLOR_HEX beforePress", hex);
                Click(hex);
                hex.SelectAll(); Pump(); TraceColor("COLOR_HEX before3", hex);
                Key(0x33, false); Key(0x33, true); Invoke("RenderFrame"); Pump(); TraceColor("COLOR_HEX after3", hex);
                Key(0x32, false); Key(0x32, true); TraceColor("COLOR_HEX after32", hex);
                hexResult = hex.Text == "32" && hex.IsKeyboardFocusWithin && colorPopup.IsOpen && popup.IsVisible && editor.Tool == EditorTool.Rectangle;
                OpenColor();
                picker.Value = 0xFF2383E2;
                var hue = Field<Slider>(picker, "hue"); hue.Focus(); hue.Value = 120; Pump(); TraceColor("COLOR_HUE before", hue);
                double previousHue = hue.Value; var previousTransforms = editor.Session.Document.Elements.Select(e => e.Transform).ToArray();
                Key(0x27, false); Key(0x27, true); TraceColor("COLOR_HUE after", hue);
                hueResult = hue.Value != previousHue && popup.IsVisible && colorPopup.IsOpen && editor.Tool == EditorTool.Rectangle &&
                    editor.Session.Document.Elements.Select(e => e.Transform).SequenceEqual(previousTransforms);
                OpenColor();
                var spectrum = Field<ColorSpectrum>(picker, "spectrum"); spectrum.Focus(); Pump(); TraceColor("COLOR_SPECTRUM before", spectrum);
                spectrum.SelectedColor = 0xFF2383E2; uint previousColor = spectrum.SelectedColor;
                Key(0x25, false); Key(0x25, true); TraceColor("COLOR_SPECTRUM after", spectrum);
                spectrumResult = spectrum.IsKeyboardFocusWithin && spectrum.SelectedColor != previousColor && popup.IsVisible && colorPopup.IsOpen && editor.Tool == EditorTool.Rectangle;
                }

                if (scenario != "color-controls")
                {
                OpenColor();
                Field<ColorLibrary>(picker, "library").Pin(0xFF2383E2); picker.RefreshCollections(); Pump();
                var swatch = Field<WrapPanel>(picker, "pinned").Children.OfType<Button>().Single();
                var swatchMenu = swatch.ContextMenu!; swatchMenu.PlacementTarget = swatch; swatchMenu.IsOpen = true; Pump();
                var removePin = swatchMenu.Items.OfType<MenuItem>().First();
                check("a nested color context-menu redirects ownership through its swatch placement target", (bool)Invoke("IsOptionsInput", removePin)!);
                swatchMenu.IsOpen = false; foreground.ClosePicker(); Pump();

                var pin = Field<Button>(session, "_toolOptionsPin");
                var close = (Button)popup.Template.FindName("Close", popup);
                check("tool-options pin and close share the app caption sizing and filled button surfaces", pin.Content is FrameworkElement { Width: 16, Height: 16 } && close.Width == pin.Width && close.Height == pin.Height &&
                    pin.Background is SolidColorBrush { Color.A: > 0 } && close.Background is SolidColorBrush { Color.A: > 0 });
                pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                check("pinning rotates the pin while retaining the shared background", Field<bool>(session, "_toolOptionsPinned") &&
                    ((FrameworkElement)pin.Content).RenderTransform is RotateTransform { Angle: -35 } && pin.Background is SolidColorBrush { Color.A: > 0 });
                pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                check("unpinning restores the upright pin and keeps its visible button surface", !Field<bool>(session, "_toolOptionsPinned") && ((FrameworkElement)pin.Content).RenderTransform is RotateTransform { Angle: 0 } && pin.Background is SolidColorBrush { Color.A: > 0 });
                }
                if (scenario != "popup-ownership")
                {
                    OpenColor();
                    var closingHex = Field<TextBox>(picker, "hex"); closingHex.Focus(); Pump(); TraceColor("CLOSE_COLOR before", closingHex);
                    foreground.SetUserValue(0xFF336699); Pump();
                    nint afterColorFocus = GetFocus();
                    Console.WriteLine($"CLOSE_COLOR automatic SetUserValue native={afterColorFocus:X} foreground={GetForegroundWindow():X} logical={Keyboard.FocusedElement?.GetType().Name} picker={colorPopup.IsOpen} options={popup.IsVisible}");
                    var returnedButton = Field<Button>(foreground, "button");
                    bool returnedToTrigger = !colorPopup.IsOpen && ReferenceEquals(Keyboard.FocusedElement, returnedButton) &&
                        afterColorFocus == ((HwndSource)PresentationSource.FromVisual(returnedButton)).Handle;
                    var beforeColorReturnTransforms = editor.Session.Document.Elements.Select(e => e.Transform).ToArray();
                    Key(0x20, false); Key(0x20, true);
                    bool afterColor = returnedToTrigger && colorPopup.IsOpen && popup.IsVisible && editor.Tool == EditorTool.Rectangle &&
                        editor.Session.Document.Elements.Select(e => e.Transform).SequenceEqual(beforeColorReturnTransforms);

                    Invoke("ActivateTool", EditorTool.Rectangle); if (!popup.IsVisible) Invoke("ActivateTool", EditorTool.Rectangle); Pump();
                    Click(Field<Dictionary<string, TextBox>>(panel, "numbers")["StrokeWidth"]); Pump();
                    Key(0x1B, false); Key(0x1B, true);
                    nint afterOptionsFocus = GetFocus();
                    Console.WriteLine($"CLOSE_OPTIONS native bridge Escape native={afterOptionsFocus:X} foreground={GetForegroundWindow():X} logical={Keyboard.FocusedElement?.GetType().Name} options={popup.IsVisible}");
                    Key(0x42, false); Key(0x42, true);
                    bool afterOptions = !popup.IsVisible && afterOptionsFocus != 0 && editor.Tool == EditorTool.Freehand;
                    Console.WriteLine($"COLOR_RESULTS hex={hexResult} hue={hueResult} spectrum={spectrumResult} automaticColorReturnInput={afterColor} optionsEscapeToBrush={afterOptions}");
                    check("a nested color hex editor retains both typed digits without invoking stamps", hexResult);
                    check("a nested hue slider receives its native arrow key without moving canvas objects", hueResult);
                    check("the nested color spectrum consumes arrow keys in its own focus source", spectrumResult);
                    check("automatic color-picker close restores its trigger and native Space reopens the picker", afterColor);
                    check("tool-options Escape close retains native keyboard input and the B tool shortcut", afterOptions);
                }
                void OpenColor()
                {
                    if (editor.Tool != EditorTool.Rectangle) Invoke("ActivateTool", EditorTool.Rectangle);
                    if (!popup.IsVisible) Invoke("ActivateTool", EditorTool.Rectangle);
                    if (!colorPopup.IsOpen) Field<Button>(foreground, "button").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Pump();
                }
                void TraceColor(string label, FrameworkElement target)
                {
                    var logical = Keyboard.FocusedElement as DependencyObject;
                    Console.WriteLine($"{label} text={(target as TextBox)?.Text} native={GetFocus():X} foreground={GetForegroundWindow():X} targetSource={((HwndSource?)PresentationSource.FromVisual(target))?.Handle:X} logical={logical?.GetType().Name} logicalSource={(logical is null ? 0 : ((HwndSource?)PresentationSource.FromDependencyObject(logical))?.Handle ?? 0):X} targetFocused={target.IsKeyboardFocusWithin} editing={host.IsEditing} parent={popup.IsVisible} color={colorPopup.IsOpen} tool={editor.Tool} captured={Mouse.Captured?.GetType().Name} directlyOver={Mouse.DirectlyOver?.GetType().Name} nativeCapture={GetCapture():X}");
                }
            }
            void Key(uint key, bool up, bool control = false, bool repeat = false, bool shift = false)
            {
                var state = new byte[256]; if (control) { state[0x11] = 128; state[0xA2] = 128; }
                if (shift) { state[0x10] = 128; state[0xA0] = 128; }
                if (!up) state[key] = 128;
                var packet = (KeyboardInputPacket)Activator.CreateInstance(typeof(KeyboardInputPacket), PrivateInstance, null,
                    [1L, 1L, KeyboardInputPacketKind.Key, key, MapVirtualKey(key, 0), 0u, (uint)Environment.TickCount,
                        up, repeat, GetForegroundWindow(), state, false, null, up ? 0x101u : 0x100u], null)!;
                dispatch(packet, () => { }); Pump();
            }
            void Trace(string label, TextBox box, Window? popup) => Console.WriteLine($"{label} text={box.Text} focus={GetFocus():X} source={((HwndSource?)PresentationSource.FromVisual(box))?.Handle:X} toolbar={new WindowInteropHelper(toolbar).Handle:X} canvas={host.Diagnostics.Single().InputHandle:X} keyboard={Keyboard.FocusedElement?.GetType().Name} boxFocused={box.IsKeyboardFocusWithin} foreground={GetForegroundWindow():X} popup={popup?.IsVisible} tool={editor.Tool}");
        }
        finally
        {
            InputManager.Current.PostProcessInput -= beforePointerFocus;
            InputManager.Current.PostProcessInput -= afterPointerFocus;
            if (host?.IsEditing == true) host.ExitEditing();
            permissionGate?.Close();
            session?.Dispose(); if (session is null) host?.Dispose();
            SetKeyboardState(originalKeys);
            SynchronizationContext.SetSynchronizationContext(oldContext);
            foreach (var window in app.Windows.Cast<Window>().ToArray()) window.Close(); Pump();
            if (originalForeground != 0 && IsWindow(originalForeground)) SetForegroundWindow(originalForeground);
            app.Shutdown();
            string cleanupRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FloatCanvasNativeInput")) + Path.DirectorySeparatorChar;
            string cleanupTarget = Path.GetFullPath(profile);
            if (!cleanupTarget.StartsWith(cleanupRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected native fixture profile");
            Directory.Delete(cleanupTarget, recursive: true);
        }
        check("native input fixture destroys its own host windows", handles.All(handle => !IsWindow(handle)));
        if (observedIsolation is not null)
            check("the real Ctrl fixture uninstalls both own hooks and terminates its isolation thread", !observedIsolation.IsThreadAlive && observedIsolation.LastWin32Error == 0 &&
                Field<nint>(observedIsolation, "hook") == 0 && Field<nint>(observedIsolation, "pointerHook") == 0);
        object? Invoke(string name, params object[] values) => typeof(DesktopSession).GetMethod(name, PrivateInstance)!.Invoke(session, values);
        void ObserveHexPress(string phase, ProcessInputEventArgs e)
        {
            if (observedHex is null || e.StagingItem.Input is not MouseButtonEventArgs
                { ButtonState: MouseButtonState.Pressed, OriginalSource: DependencyObject source } mouse ||
                !ReferenceEquals(source, observedHex)) return;
            var logical = Keyboard.FocusedElement as DependencyObject;
            Console.WriteLine($"HEX_POINTER {phase} routed={mouse.RoutedEvent.Name} handled={mouse.Handled} device={mouse.MouseDevice.GetType().Name} native={GetFocus():X} foreground={GetForegroundWindow():X} source={((HwndSource?)PresentationSource.FromVisual(observedHex))?.Handle:X} logical={logical?.GetType().Name} logicalSource={(logical is null ? 0 : ((HwndSource?)PresentationSource.FromDependencyObject(logical))?.Handle ?? 0):X} focused={observedHex.IsKeyboardFocusWithin}");
        }
    }
    private static PromptObserver ObservePrompt(Action<Window> observe) => new(observe);
    private sealed class PromptObserver : IDisposable
    {
        private readonly DispatcherTimer timer = new(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(20) };
        private readonly long started = Environment.TickCount64;
        public PromptObserver(Action<Window> observe)
        {
            timer.Tick += (_, _) =>
            {
                var dialog = Application.Current.Windows.Cast<Window>().FirstOrDefault(window => window.IsVisible && window.Title == FloatCanvas.Localization.AppText.Get("Stamp.SaveSelection"));
                if (dialog is null) { if (Environment.TickCount64 - started > 3000) timer.Stop(); return; }
                timer.Stop();
                try { observe(dialog); }
                finally { dialog.Close(); }
            };
            timer.Start();
        }
        public void Wait() { while (timer.IsEnabled) Pump(); }
        public void Dispose() => timer.Stop();
    }
    private static void Feedback4Panels(Action<string, bool> check, Application app, DesktopSession session, OverlayHost host, Window toolbar, EditorController editor)
    {
        Invoke("ActivateTool", EditorTool.Rectangle); Invoke("OpenToolOptions"); Pump();
        var options = Field<Window>(session, "_toolOptionsWindow");
        var panel = Field<ToolOptionsPanel>(session, "_toolOptionsPanel");
        var field = Field<ColorField>(panel, "foreground");
        var child = Field<Popup>(field, "popup");
        var picker = Field<ColorPickerPanel>(field, "picker");
        var parentInput = Field<Dictionary<string, TextBox>>(panel, "numbers")["StrokeWidth"];
        check("tool options are a visible independently owned window", options.IsVisible && options.Owner == toolbar && host.IsEditing && toolbar.IsVisible);

        foreach (bool editAlpha in new[] { false, true })
        {
            Field<Button>(field, "button").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            check("the nested color picker opens above the tool window", child.IsOpen && options.IsVisible && host.IsEditing);
            var alpha = Field<Slider>(picker, "alpha"); alpha.Focus();
            uint previous = editor.Style.Stroke;
            if (editAlpha) alpha.Value = 40;
            // Model a StaysOpen=false child losing capture on a parent-window press.
            // Closing its real Popup invokes the production auto-commit/refresh chain.
            Click(parentInput); child.IsOpen = false; Pump(); parentInput.Focus(); Pump();
            check((editAlpha ? "changed" : "unchanged") + " child close and parent press preserve options, toolbar and editing", !child.IsOpen && options.IsVisible && toolbar.IsVisible && host.IsEditing);
            check((editAlpha ? "changed" : "unchanged") + " child close commits exactly its current color", editAlpha ? (editor.Style.Stroke >> 24) == 102 : editor.Style.Stroke == previous);
        }

        // Live color application recreates preset preview visuals synchronously.
        // Keep the event's real OriginalSource alive while its owning visual is removed before
        // PostProcessInput, matching a parent press during that same production commit.
        Field<Button>(field, "button").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        var gallery = Field<ToolPresetGallery>(panel, "presets");
        var previewTile = gallery.OfficialTiles.Children.OfType<Button>().First();
        var oldPreview = (UIElement)previewTile.Content;
        bool detachedDuringPress = false;
        MouseButtonEventHandler commitAndRefresh = (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, oldPreview)) return;
            Field<Slider>(picker, "alpha").Value = 99;
            child.IsOpen = false;
            // Popup.Closed is dispatched by WPF; complete that real callback within
            // this press before post-processing inspects its original target.
            Pump(); options.UpdateLayout();
            detachedDuringPress = Window.GetWindow(oldPreview) is null;
        };
        previewTile.AddHandler(Mouse.PreviewMouseDownEvent, commitAndRefresh, true);
        try { Click(oldPreview); }
        finally { previewTile.RemoveHandler(Mouse.PreviewMouseDownEvent, commitAndRefresh); previewTile.ReleaseMouseCapture(); }
        check("the production live color change detaches this actual parent press source before post-processing", detachedDuringPress);
        check("a parent press survives child auto-commit even after its original preview visual is detached", !child.IsOpen && options.IsVisible && toolbar.IsVisible && host.IsEditing && (editor.Style.Stroke >> 24) == 252);

        foreach (double percentage in new[] { 100d, 99d })
        {
            Field<Button>(field, "button").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Field<Slider>(picker, "alpha").Value = percentage;
            var parentPoint = options.PointToScreen(new(options.ActualWidth - 18, AppWindowChrome.CaptionHeight + 50));
            check("the captured-popup parent press point belongs to the actual options HWND", PhysicalRoot(parentPoint) == new WindowInteropHelper(options).Handle);
            CapturedColorPress(parentPoint);
            check($"a captured PopupRoot press on parent blank space commits {percentage}% without closing its parent", !child.IsOpen && options.IsVisible && host.IsEditing && toolbar.IsVisible &&
                (editor.Style.Stroke >> 24) == (uint)Math.Round(percentage * 255 / 100));
        }
        // Keep A active while the captured press physically targets another real HWND.
        // This forbids solving the parent case with an unconditional IsActive exemption.
        var outsideWindow = new Window { Title = "FloatCanvas owned outside-pointer fixture", Width = 120, Height = 80,
            Left = host.PrimaryMonitor.WorkArea.Right * 96d / host.PrimaryMonitor.DpiX - 150,
            Top = host.PrimaryMonitor.WorkArea.Bottom * 96d / host.PrimaryMonitor.DpiY - 110,
            Topmost = true, ShowActivated = false, ShowInTaskbar = false, Content = new Border { Background = Brushes.White } };
        try
        {
            outsideWindow.Show(); Pump();
            ActivateFixtureWindow(new WindowInteropHelper(options).Handle, () => { options.Activate(); parentInput.Focus(); }); Pump();
            Field<Button>(field, "button").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            check("the parent remains active before a captured press on another physical window", options.IsActive);
            var outsidePoint = outsideWindow.PointToScreen(new(outsideWindow.ActualWidth / 2, outsideWindow.ActualHeight / 2));
            check("the captured-popup outside press point belongs to its separate owned HWND", PhysicalRoot(outsidePoint) == new WindowInteropHelper(outsideWindow).Handle);
            CapturedColorPress(outsidePoint);
            check("a captured PopupRoot press outside A and toolbar still dismisses unpinned A", !child.IsOpen && !options.IsVisible && host.IsEditing && toolbar.IsVisible);
        }
        finally { outsideWindow.Close(); }
        Invoke("OpenToolOptions"); Pump();

        void CapturedColorPress(Point screen)
        {
            var source = (HwndSource)PresentationSource.FromVisual(picker);
            var root = (UIElement)source.RootVisual;
            check("a captured color press starts at the real detached PopupRoot rather than a parent child", root.GetType().Name == "PopupRoot" && !(bool)Invoke("IsOptionsInput", root)!);
            var mouse = (MouseDevice)typeof(ToolOptionsInputFixture).GetMethod("ControlledMouse", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [root])!;
            try
            {
                typeof(MouseDevice).GetField("_inputSource", PrivateInstance)!.SetValue(mouse, source);
                typeof(MouseDevice).GetField("_lastPosition", PrivateInstance)!.SetValue(mouse, source.CompositionTarget.TransformToDevice.Transform(root.PointFromScreen(screen)));
                var press = new MouseButtonEventArgs(mouse, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent };
                check("the captured color press preserves the configured physical screen coordinate", (options.PointToScreen(press.GetPosition(options)) - screen).Length < 1);
                InputManager.Current.ProcessInput(press); Pump();
            }
            finally { ToolOptionsInputFixture.ReleaseControlledMouse(mouse); }
        }

        double left = options.Left + 32, top = options.Top + 18; options.Left = left; options.Top = top; Pump();
        toolbar.Left += 29; Invoke("AnchorToolOptions"); Pump();
        check("moving the toolbar preserves the manually placed options window", options.IsVisible && Math.Abs(options.Left - left) < 1 && Math.Abs(options.Top - top) < 1);
        Invoke("ToggleToolbarExpanded"); Pump();
        check("collapsing the toolbar preserves the independently positioned options window", options.IsVisible && Math.Abs(options.Left - left) < 1 && Math.Abs(options.Top - top) < 1 && !Field<UserSettings>(session, "_settings").ToolbarExpanded);

        Field<Button>(field, "button").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        Invoke("OnKey", 0x1B); Pump();
        check("the first Escape closes the child color picker while retaining the parent and editing", !child.IsOpen && options.IsVisible && host.IsEditing);
        Invoke("OnKey", 0x1B); Pump();
        check("the next Escape closes unpinned options before leaving editing", !options.IsVisible && host.IsEditing && toolbar.IsVisible);
        Invoke("OpenToolOptions"); Pump();
        var position = options.PointToScreen(new Point()); var bottom = options.PointToScreen(new Point(options.ActualWidth, options.ActualHeight));
        var work = host.Monitors.FirstOrDefault(m => m.WorkArea.Contains(new(position.X, position.Y)))?.WorkArea ?? host.PrimaryMonitor.WorkArea;
        check("opening options with a collapsed toolbar stays within a screen work area", options.IsVisible && position.X >= work.Left - 2 && position.Y >= work.Top - 2 && bottom.X <= work.Right + 2 && bottom.Y <= work.Bottom + 2);
        Invoke("CloseToolOptions");

        // Avoid registering even temporary system hotkeys: unchanged bindings take the
        // existing-registration branch. This fixture verifies the theme/save UI path.
        var settings = Field<UserSettings>(session, "_settings");
        typeof(OverlayHost).GetField("_toggleHotkey", PrivateInstance)!.SetValue(host, (HotkeyBinding?)settings.Toggle);
        typeof(OverlayHost).GetField("_hideHotkey", PrivateInstance)!.SetValue(host, (HotkeyBinding?)settings.Hide);
        Invoke("ShowHotkeySettings"); Pump();
        var settingsWindow = app.Windows.Cast<Window>().Single(window => window.Title == FloatCanvas.Localization.AppText.Get("Settings.Title"));
        SettingsWindowFixture.Descendants(settingsWindow).OfType<ComboBox>().Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == FloatCanvas.Localization.AppText.Get("Settings.Theme")).SelectedIndex = 2;
        ((AppSettingsWindow)settingsWindow).SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        check("saving a new theme closes settings and retains the toolbar in editing", !settingsWindow.IsVisible && toolbar.IsVisible && host.IsEditing && UserSettings.Load().Theme == "dark");
        check("panel regression keeps native overlay surfaces bounded to the fixture", host.Diagnostics.All(pair => pair.Bounds.Width == 640 && pair.Bounds.Height == 360));
        CheckRecoveryTimerCancellation(check, session);
        object? Invoke(string name, params object[] values) => typeof(DesktopSession).GetMethod(name, PrivateInstance)!.Invoke(session, values);
    }
    private static void CheckRecoveryTimerCancellation(Action<string, bool> check, DesktopSession session)
    {
        object? Invoke(string name) => typeof(DesktopSession).GetMethod(name, PrivateInstance)!.Invoke(session, null);
        void SetPending(Task task) => typeof(DesktopSession).GetField("_documentRecoveryTask", PrivateInstance)!.SetValue(session, task);
        // Finish real recovery first; the controlled pending task then gives disposal a
        // deterministic cancellation boundary without depending on disk-write timing.
        Await((Task)Invoke("FlushRecoveryAsync")!);
        var lifetime = Field<CancellationTokenSource>(session, "_documentLifetime");
        SetPending(Task.FromCanceled(new CancellationToken(true)));
        var unexpected = (Task)Invoke("FlushRecoveryFromTimerAsync")!;
        bool propagated = false;
        try { Await(unexpected); }
        catch (OperationCanceledException) { propagated = true; }
        finally { SetPending(Task.CompletedTask); }
        check("the recovery timer does not swallow an unrelated cancellation while its session is live", propagated && unexpected.IsCanceled && !lifetime.IsCancellationRequested);

        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetPending(pending.Task);
        var flushing = (Task)Invoke("FlushRecoveryFromTimerAsync")!;
        check("the real recovery timer path waits for its pending recovery task before disposal", !flushing.IsCompleted);
        Invoke("StopDocumentTimers");
        pending.SetCanceled(lifetime.Token);
        try
        {
            Await(flushing); Pump();
            check("session disposal cancels its recovery task through the real lifetime token", lifetime.IsCancellationRequested && pending.Task.IsCanceled);
            check("the recovery timer observes disposal cancellation without leaking an exception into WPF", flushing.Status == TaskStatus.RanToCompletion);
        }
        finally { SetPending(Task.CompletedTask); }
    }

    private static void Feedback4Workspace(Action<string, bool> check, DesktopSession session, OverlayHost host, Window toolbar, EditorController editor)
    {
        // Keep this fixture's actual recovery writer deterministic; all I/O remains
        // enabled, while its one-second scheduling timer cannot race the fault case.
        var recoveryTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        SetField("_documentRecoveryTimer", recoveryTimer);
        foreach (bool visible in new[] { true, false })
        {
            host.SetVisible(visible); Pump();
            check($"canvas visibility {visible} preserves active editing", host.IsVisible == visible && host.IsEditing);
            check($"canvas visibility {visible} controls creation independently", (bool)Invoke("CanAddCanvasContent")! == visible && host.CanBeginCanvasGesture!() == visible);
            Invoke("SetEditing", false, false); Pump();
            check($"leaving editing preserves canvas visibility {visible}", !host.IsEditing && host.IsVisible == visible);
            bool entered = false;
            ActivateFixtureWindow(new WindowInteropHelper(toolbar).Handle, () => entered = (bool)Invoke("SetEditing", true, false)!); Pump();
            check($"returning to editing preserves canvas visibility {visible}", entered && host.IsEditing && host.IsVisible == visible);
        }

        var settings = Field<UserSettings>(session, "_settings"); settings.NumberStart = 7; settings.NumberStep = 3;
        var live = Field<EditorSession>(session, "_live");
        live.Add(new CanvasElement { Kind = ElementKind.Number, Number = 41, Width = 35, Height = 35 });
        var assets = Field<AssetLibrary>(session, "_assets");
        string image = assets.Add(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLttAAAAABJRU5ErkJggg=="));
        var capture = new EditorSession(new SceneDocument { Kind = DocumentKind.FrozenCapture, Viewport = new(0, 0, 40, 30),
            Background = new() { Kind = ElementKind.Image, AssetId = image, Width = 40, Height = 30, Locked = true },
            Elements = [new() { Kind = ElementKind.Rectangle, Width = 10, Height = 10 }] }, assets);
        SetField("_capture", capture); SetField("_captureOrigin", new PointD(42, 52));
        editor.ReplaceSession(capture, new(42, 52)); editor.Tool = EditorTool.Ellipse;
        var original = (WorkspaceState)Invoke("SnapshotWorkspace")!;
        var store = Field<RecoveryStore>(session, "_recoveryStore");
        Await(store.SaveAsync(original, assets));
        byte[] recoveryBytes = File.ReadAllBytes(store.CurrentPath);
        string historyPath = Path.Combine(store.DirectoryPath, "history");
        File.WriteAllText(historyPath, "A fixture-owned file deliberately blocks archive directory creation.");
        bool rejected = false;
        try { Await((Task)Invoke("StartNewCanvasAsync")!); }
        catch (IOException) { rejected = true; }
        check("archive failure rejects new canvas before replacing either live or screenshot session", rejected && ReferenceEquals(live, Field<EditorSession>(session, "_live")) &&
            ReferenceEquals(capture, Field<EditorSession>(session, "_capture")) && ReferenceEquals(editor.Session, capture));
        check("archive failure preserves complete workspace and recovery bytes", JsonSerializer.Serialize(original) == JsonSerializer.Serialize((WorkspaceState)Invoke("SnapshotWorkspace")!) &&
            recoveryBytes.AsSpan().SequenceEqual(File.ReadAllBytes(store.CurrentPath)));
        check("archive failure releases busy state and preserves hidden editing", !Field<bool>(session, "_documentBusyValue") && host.IsEditing && !host.IsVisible);

        File.Delete(historyPath);
        Await((Task)Invoke("StartNewCanvasAsync")!); Pump();
        var replacement = Field<EditorSession>(session, "_live");
        check("new canvas uses configured first number and retains the active tool", !ReferenceEquals(live, replacement) && replacement.Document.Elements.Count == 0 &&
            replacement.Document.NextNumber == 7 && editor.Tool == EditorTool.Ellipse && ReferenceEquals(editor.Session, replacement));
        check("new canvas resets screenshot while preserving hidden editing state", Field<EditorSession?>(session, "_capture") is null && host.IsEditing && !host.IsVisible &&
            Field<PointD>(session, "_captureOrigin") == default && !Field<bool>(session, "_documentBusyValue"));
        var history = (WorkspaceRecoveryHistory)typeof(DesktopSession).GetProperty("RecoveryHistory", PrivateInstance)!.GetValue(session)!;
        var entry = history.GetEntries().First(e => !e.IsCurrent);
        var archived = Await(history.LoadAsync(entry));
        check("new canvas archives both previous editable documents and their image resource", archived is not null &&
            JsonSerializer.Serialize(archived.State) == JsonSerializer.Serialize(original) && archived.Assets.TryGet(image, out _));
        var current = Await(store.LoadAsync());
        check("new canvas commits its blank workspace only after the complete archive", current is not null && current.State.LiveDocument.Elements.Count == 0 &&
            current.State.LiveDocument.NextNumber == 7 && current.State.CaptureDocument is null);
        check("workspace regression keeps every overlay HWND bounded to the fixture", host.Diagnostics.All(pair => pair.Bounds.Width == 640 && pair.Bounds.Height == 360));

        string projectPath = Path.Combine(ApplicationPaths.ProfileDirectory, FloatCanvas.Localization.AppText.CurrentLanguage.StartsWith("zh")?"示例画布.floatcanvas":"saved-project.floatcanvas");
        var project = new SceneDocument { Elements = [new() { Kind = ElementKind.Rectangle, Width = 35, Height = 25 }], Canvas = new() { BackgroundColor = 0xFF112233 } };
        Await(new ProjectStore().SaveAsync(projectPath, project, assets));
        Await((Task)Invoke("OpenProjectPathAsync", projectPath)!);
        var bound = Field<EditorSession>(session, "_live");
        Await(history.ArchiveAsync((WorkspaceState)Invoke("SnapshotWorkspace")!,assets,null,projectPath,force:true));
        bound.Add(new() { Kind = ElementKind.Ellipse, Width = 18, Height = 19, Locked = true, Visible = false });
        Await((Task<bool>)Invoke("SaveSessionAsync", bound, false)!);
        byte[] savedProjectBytes = File.ReadAllBytes(projectPath);
        int beforeClearHistory=history.GetEntries().Count(e=>!e.IsCurrent);
        bool clearPrompted=false;
        check("saving an edited project keeps its new-version history eligibility", history.HasNewVersion((WorkspaceState)Invoke("SnapshotWorkspace")!));
        var confirmTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        confirmTimer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.Cast<Window>().FirstOrDefault(w => w.Content is DockPanel);
            if (dialog?.Content is not DockPanel panel) return;
            clearPrompted=true;
            var ok = panel.Children.OfType<StackPanel>().SelectMany(row => row.Children.OfType<Button>()).FirstOrDefault(b => Equals(b.Content, FloatCanvas.Localization.AppText.Get("Dialog.OK")));
            if (ok is not null) { confirmTimer.Stop(); ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        };
        confirmTimer.Start();
        try { Await((Task)Invoke("ClearCurrentCanvasAsync")!); } finally { confirmTimer.Stop(); }
        check("clear is immediate and creates no history entry",!clearPrompted&&history.GetEntries().Count(e=>!e.IsCurrent)==beforeClearHistory);
        string? BoundPath(EditorSession target)
        {
            object state = Invoke("FileState", target)!;
            return (string?)state.GetType().GetField("Path")!.GetValue(state);
        }
        check("clear keeps the same file-bound session and canvas settings", ReferenceEquals(bound, Field<EditorSession>(session, "_live")) && BoundPath(bound) == projectPath && bound.Document.Canvas.BackgroundColor == project.Canvas.BackgroundColor);
        check("clearing a bound canvas does not write its project file before an explicit save", savedProjectBytes.SequenceEqual(File.ReadAllBytes(projectPath)));
        check("clear removes hidden and locked drawings with one undo", bound.Document.Elements.Count == 0 && bound.Undo() && bound.Document.Elements.Count == 2);
        check("clear undo retains file association and redo", BoundPath(bound) == projectPath && bound.Redo() && bound.Document.Elements.Count == 0);
        Await((Task<bool>)Invoke("SaveSessionAsync", bound, false)!);
        check("saving after clear updates the original project file", Await(new ProjectStore().LoadAsync(projectPath)).Document.Elements.Count == 0);
        int countBeforeEmptyNew = history.GetEntries().Count(e => !e.IsCurrent);
        var newButton=Field<NewCanvasButton>(session,"_newCanvasButton");
        Await(newButton.ActivateAsync());
        check("saved clean file starts a new canvas on its first button activation",!newButton.IsConfirming&&!ReferenceEquals(bound,Field<EditorSession>(session,"_live")));
        check("new from a saved empty project detaches its file without archiving empty content", BoundPath(Field<EditorSession>(session, "_live")) is null && history.GetEntries().Count(e => !e.IsCurrent) == countBeforeEmptyNew);
        for (int i = 0; i < 4; i++) Await(newButton.ActivateAsync());
        check("repeated production new-canvas commands do not consume history slots", history.GetEntries().Count(e => !e.IsCurrent) == countBeforeEmptyNew);

        var sourceEntry = history.GetEntries().First(e => e.SourceFile == projectPath);
        var restoreTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        restoreTimer.Tick += (_, _) =>
        {
            var chooser = Application.Current.Windows.Cast<Window>().OfType<WorkspaceHistoryWindow>().FirstOrDefault();
            if (chooser?.Content is not StackPanel panel) return;
            var list = panel.Children.OfType<ListBox>().Single();
            list.SelectedItem = list.Items.OfType<ListBoxItem>().First(i => ((WorkspaceRecoveryEntry)i.Tag).DirectoryPath == sourceEntry.DirectoryPath);
            restoreTimer.Stop();
            panel.Children.OfType<StackPanel>().Single().Children.OfType<Button>().First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        restoreTimer.Start();
        try { Await((Task)Invoke("RestoreWorkspaceHistoryAsync")!); } finally { restoreTimer.Stop(); }
        check("production history restore creates an editable unbound copy", BoundPath(Field<EditorSession>(session, "_live")) is null && Field<EditorSession>(session, "_live").Document.Elements.Count == project.Elements.Count);
        Await(newButton.ActivateAsync());
        check("unchanged history copy needs only one new-canvas click",!newButton.IsConfirming&&Field<EditorSession>(session,"_live").Document.Elements.Count==0);
        check("leaving a restored version without edits neither duplicates nor retimestamps it", history.GetEntries().Count(e => !e.IsCurrent) == countBeforeEmptyNew && history.GetEntries().Any(e => e.DirectoryPath == sourceEntry.DirectoryPath && e.SavedAtUtc == sourceEntry.SavedAtUtc));

        restoreTimer.Start();
        try { Await((Task)Invoke("RestoreWorkspaceHistoryAsync")!); } finally { restoreTimer.Stop(); }
        var restoredCopy=Field<EditorSession>(session,"_live");
        restoredCopy.Add(new() {Kind=ElementKind.Rectangle,Width=29,Height=21});
        int beforeEdited=history.GetEntries().Count(e=>!e.IsCurrent);
        Await(newButton.ActivateAsync());
        check("edited history copy arms the same gray button before archiving",newButton.IsConfirming&&ReferenceEquals(restoredCopy,Field<EditorSession>(session,"_live"))&&history.GetEntries().Count(e=>!e.IsCurrent)==beforeEdited);
        Await(newButton.ActivateAsync());
        check("second click archives the edited copy once and starts blank",!newButton.IsConfirming&&Field<EditorSession>(session,"_live").Document.Elements.Count==0&&history.GetEntries().Count(e=>!e.IsCurrent)==beforeEdited+1);
        var empty=Field<EditorSession>(session,"_live");empty.Document.Canvas.Projection.Rows++;
        int beforeSettings=history.GetEntries().Count(e=>!e.IsCurrent);
        Await(newButton.ActivateAsync());
        check("empty canvas with only setting changes neither confirms nor consumes history",!newButton.IsConfirming&&history.GetEntries().Count(e=>!e.IsCurrent)==beforeSettings);
        Await((Task)Invoke("OpenProjectPathAsync",projectPath)!);
        var dirty=Field<EditorSession>(session,"_live");
        dirty.Add(new(){Kind=ElementKind.Rectangle,Width=31,Height=26});
        bool RespondToNew(string key)
        {
            bool prompted=false;var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(20)};
            timer.Tick+=(_,_)=>
            {
                var dialog=Application.Current.Windows.Cast<Window>().FirstOrDefault(w=>w.Content is DockPanel);
                if(dialog?.Content is not DockPanel panel)return;
                var button=panel.Children.OfType<StackPanel>().SelectMany(row=>row.Children.OfType<Button>()).FirstOrDefault(b=>Equals(b.Content,FloatCanvas.Localization.AppText.Get(key)));
                if(button is null)return;prompted=true;timer.Stop();button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            timer.Start();try{Await(newButton.ActivateAsync());}finally{timer.Stop();}return prompted;
        }
        check("dirty bound file opens save prompt immediately and cancel preserves its session",RespondToNew("Dialog.Cancel")&&!newButton.IsConfirming&&ReferenceEquals(dirty,Field<EditorSession>(session,"_live"))&&BoundPath(dirty)==projectPath&&dirty.Document.Elements.Count==1);
        check("save and leave writes the bound file before creating an unbound blank",RespondToNew("History.SaveLeave")&&Await(new ProjectStore().LoadAsync(projectPath)).Document.Elements.Count==1&&BoundPath(Field<EditorSession>(session,"_live")) is null&&Field<EditorSession>(session,"_live").Document.Elements.Count==0);

        Await((Task)Invoke("OpenProjectPathAsync",projectPath)!);Pump();Invoke("RefreshBoundFileIndicator");
        var bubble=Field<BoundFileIndicator>(session,"_boundFileIndicator");
        check("bound-file speech bubble shows the filename and full-path tooltip",bubble.Visibility==Visibility.Visible&&Equals(bubble.ToolTip,projectPath)&&System.Windows.Automation.AutomationProperties.GetName(bubble)==Path.GetFileName(projectPath));
        var launcher=Field<ToolbarLauncher>(session,"_toolbarLauncher");
        var visibility=(Action)typeof(ToolbarLauncher).GetField("ToggleVisibilityRequested",PrivateInstance)!.GetValue(launcher)!;
        bool wasVisible=host.IsVisible;
        visibility();Pump();
        check("editing logo double-click changes only canvas visibility without folding the toolbar",host.IsVisible!=wasVisible&&host.IsEditing&&
            Field<UserSettings>(session,"_settings").ToolbarExpanded&&Field<FrameworkElement>(session,"_toolbarExpandedContent").Visibility==Visibility.Visible);
        var expansion=(MenuItem)Field<ContextMenu>(session,"_toolbarMenu").Items[0];
        expansion.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Pump();
        check("the first menu item folds the toolbar and filename bubble while preserving canvas visibility and editing",bubble.Visibility==Visibility.Collapsed&&
            !Field<UserSettings>(session,"_settings").ToolbarExpanded&&host.IsVisible!=wasVisible&&host.IsEditing&&
            Equals(expansion.Header,FloatCanvas.Localization.AppText.Get("Toolbar.Expand"))&&expansion.InputGestureText=="Tab");
        expansion.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Pump();
        check("the same first menu item restores the toolbar and filename bubble without changing visibility or editing",bubble.Visibility==Visibility.Visible&&
            Field<UserSettings>(session,"_settings").ToolbarExpanded&&host.IsVisible!=wasVisible&&host.IsEditing&&
            Equals(expansion.Header,FloatCanvas.Localization.AppText.Get("Toolbar.Collapse")));
        visibility();Pump();
        var rebound=Field<EditorSession>(session,"_live");int beforeDetachUndo=rebound.UndoCount;
        rebound.Add(new(){Kind=ElementKind.Ellipse,Width=10,Height=10});Pump();Invoke("RefreshBoundFileIndicator");
        check("bound-file bubble marks unsaved edits",System.Windows.Automation.AutomationProperties.GetItemStatus(bubble)==FloatCanvas.Localization.AppText.Get("Project.Unsaved"));
        string? output=Environment.GetEnvironmentVariable("FLOATCANVAS_ROUND2_PREVIEW_DIR");
        var selectButton=Field<Dictionary<EditorTool,Button>>(session,"_toolbarToolButtons")[EditorTool.Select];
        var selectIcon=selectButton.Content is Grid selectRow?(FrameworkElement)selectRow.Children.OfType<ContentPresenter>().Single().Content:(FrameworkElement)selectButton.Content;
        check("filename text starts at the selection icon's left edge",Math.Abs(Field<TextBlock>(bubble,"label").TranslatePoint(new Point(),toolbar).X-selectIcon.TranslatePoint(new Point(VisualTreeHelper.GetDescendantBounds(selectIcon).Left,0),toolbar).X)<.5);
        if(output is not null)
        {
            Round2StampRuntimeFixture.Render((FrameworkElement)toolbar.Content,Path.Combine(output,"toolbar-bound-file.png"));
            Round2StampRuntimeFixture.Render((FrameworkElement)toolbar.Content,Path.Combine(output,"toolbar-bound-file-"+FloatCanvas.Localization.AppText.CurrentLanguage+"-"+Field<UserSettings>(session,"_settings").Theme+".png"));
        }
        var detach=bubble.ContextMenu.Items.OfType<MenuItem>().Single(item=>Equals(item.Header,FloatCanvas.Localization.AppText.Get("Project.Detach")));
        check("filename context menu includes detach and locate with icons",bubble.ContextMenu.Items.Count==2&&bubble.ContextMenu.Items.OfType<MenuItem>().All(item=>item.Icon is FrameworkElement));
        detach.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Pump();
        check("detaching preserves content and undo while hiding the binding bubble",BoundPath(rebound) is null&&rebound.Document.Elements.Count==2&&rebound.UndoCount==beforeDetachUndo+1&&bubble.Visibility==Visibility.Collapsed);
        check("detaching leaves the saved file unchanged and retains unbound content for history",Await(new ProjectStore().LoadAsync(projectPath)).Document.Elements.Count==1&&(bool)Invoke("NeedsNewCanvasConfirmation")!);

        object? Invoke(string name, params object[] values) => typeof(DesktopSession).GetMethod(name, PrivateInstance)!.Invoke(session, values);
        void SetField(string name, object value) => typeof(DesktopSession).GetField(name, PrivateInstance)!.SetValue(session, value);
    }
    private static void Await(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, PrivateInstance)!.GetValue(target)!;
    private static void Click(IInputElement target) => typeof(ToolOptionsInputFixture).GetMethod("Drive", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [target]);
    private static nint PhysicalRoot(Point screen) => GetAncestor(WindowFromPhysicalPoint(new((int)Math.Round(screen.X), (int)Math.Round(screen.Y))), 2);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct PixelPoint(int X, int Y);
    [DllImport("user32.dll")] private static extern nint WindowFromPhysicalPoint(PixelPoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint handle, uint flags);
    private static nint Pack(int x, int y) => (nint)((y << 16) | (x & 65535));
    private static void Pump() { var f = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => f.Continue = false); Dispatcher.PushFrame(f); }
    private static bool WaitForForeground(Func<bool> permitted)
    {
        if (permitted()) return true;
        var frame = new DispatcherFrame();
        DateTime deadline = DateTime.UtcNow.AddSeconds(90);
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) => { if (permitted() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        try { timer.Start(); Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        bool ready = permitted();
        Console.WriteLine($"WAIT_FOR_FOCUS_COMPLETE accepted={ready}"); Console.Out.Flush();
        return ready;
    }
    private static bool IsFixtureForeground() => GetWindowThreadProcessId(GetForegroundWindow(), out uint process) != 0 && process == Environment.ProcessId;
    private static bool WaitForPermissionClickRelease()
    {
        var frame = new DispatcherFrame();
        DateTime deadline = DateTime.UtcNow.AddSeconds(10), releasedSince = DateTime.MaxValue;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        bool ready = false;
        timer.Tick += (_, _) =>
        {
            DateTime now = DateTime.UtcNow;
            bool released = Mouse.LeftButton == MouseButtonState.Released && (GetAsyncKeyState(1) & 0x8000) == 0;
            if (!released) releasedSince = DateTime.MaxValue;
            else if (releasedSince == DateTime.MaxValue) releasedSince = now;
            if (released && now - releasedSince >= TimeSpan.FromMilliseconds(100)) { ready = true; frame.Continue = false; }
            else if (now >= deadline) frame.Continue = false;
        };
        try { timer.Start(); Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Pump();
        ready &= Mouse.LeftButton == MouseButtonState.Released && (GetAsyncKeyState(1) & 0x8000) == 0;
        Console.WriteLine($"WAIT_FOR_PERMISSION_CLICK_COMPLETE released={ready}"); Console.Out.Flush();
        return ready;
    }
    private static void ActivateFixtureWindow(nint ownWindow, Action activate)
    {
        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _), thread = GetCurrentThreadId();
        bool attached = foregroundThread != thread && AttachThreadInput(thread, foregroundThread, true);
        try
        {
            bool foreground = SetForegroundWindow(ownWindow); SetFocus(ownWindow);
            var name = new StringBuilder(256); GetClassName(GetForegroundWindow(), name, name.Capacity);
            Console.WriteLine($"ACTIVATION attached={attached} accepted={foreground} foregroundClass={name} foregroundThread={foregroundThread} thread={thread}");
            activate();
        }
        finally { if (attached) AttachThreadInput(thread, foregroundThread, false); }
    }
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint process);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint SendMessage(nint handle, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] private static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadKeyboardLayout(string id, uint flags);
    [DllImport("user32.dll")] private static extern nint ActivateKeyboardLayout(nint layout, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint SetFocus(nint handle);
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint handle, StringBuilder name, int count);
}
