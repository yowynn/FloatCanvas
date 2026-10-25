using System.Reflection;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using NotifyIcon = System.Windows.Forms.NotifyIcon;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using Application = System.Windows.Application;
using L = FloatCanvas.Localization.AppText;

internal static class ColorModalInputTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check, bool interactive = false, bool externalReceiver = false)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        new UserSettings { Language = "en", Theme = "light", ShowFloatingLogo = false }.Save();
        var primary = ((IReadOnlyList<MonitorInfo>)typeof(OverlayHost).GetMethod("EnumerateMonitors", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, null)!).First(m => m.IsPrimary);
        var tile = primary with { Bounds = new(primary.WorkArea.Left + 30, primary.WorkArea.Top + 30, 640, 360) };
        Func<IReadOnlyList<MonitorInfo>> monitors = () => [tile];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null,
            [OverlayMonitorScope.PrimaryScreen, monitors, false], null)!;
        host.EnableSystemInput();
        using var session = new DesktopSession(app, host);
        Field<NotifyIcon>(session, "_tray").Visible = false;
        host.SetVisible(true);
        var toolbar = Field<Window>(session, "_toolbar");
        string receiverOutput = Path.Combine(ApplicationPaths.ProfileDirectory, "modal-receiver.json");
        using var receiver = interactive || externalReceiver ? Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
        { ArgumentList = { "--color-modal-receiver", receiverOutput } }) : null;
        try
        {
        if (receiver is not null)
        {
            for (int i = 0; i < 50 && receiver.MainWindowHandle == 0; i++) { Pump(TimeSpan.FromMilliseconds(50)); receiver.Refresh(); }
            Console.WriteLine("RECEIVER_READY activate only FloatCanvas Modal Background Receiver to start the isolated check."); Console.Out.Flush();
            for (int i = 0; i < 900 && GetForegroundWindow() != receiver.MainWindowHandle; i++) Pump(TimeSpan.FromMilliseconds(100));
            check("the owned background receiver has real foreground", GetForegroundWindow() == receiver.MainWindowHandle);
        }
        nint background = GetForegroundWindow();
        GetWindowThreadProcessId(background, out uint backgroundProcess);
        check("the fixture starts over an independent external foreground", background != 0 && backgroundProcess != Environment.ProcessId);
        // Balance only the fixture's inherited thread-local queue. No global
        // keyboard or pointer input is synthesized by this regression gate.
        var keys = new byte[256];
        for (int key = 0; key < keys.Length; key++) keys[key] = (byte)((GetAsyncKeyState(key) < 0 ? 0x80 : 0) | (GetKeyState(key) & 1));
        SetKeyboardState(keys);
        Invoke(session, "SetEditing", true, false);
        for (int i = 0; i < 30 && !host.HasEditingInput; i++) Pump(TimeSpan.FromMilliseconds(100));
        check("real enhanced input is acquired before opening tool options", host.HasEditingInput);
        Pump(TimeSpan.FromMilliseconds(100));
        string? receiverBaseline = receiver is null ? null : ReceiverState(receiverOutput);
        Field<EditorController>(session, "_editor").Tool = EditorTool.Rectangle;
        Invoke(session, "OpenToolOptions");
        Pump(TimeSpan.FromMilliseconds(50));
        var options = Field<Window>(session, "_toolOptionsWindow");
        var panel = Field<ToolOptionsPanel>(session, "_toolOptionsPanel");
        var field = Field<ColorField>(panel, "foreground");
        field.OpenPicker();
        Pump(TimeSpan.FromMilliseconds(50));
        var color = Window.GetWindow(field.PickerRoot)!;
        var picker = Field<ColorPickerPanel>(field, "picker");
        check("the naming command starts from visible tool options and color picker", options.IsVisible && color.IsVisible && field.IsPickerOpen);
        bool lostEditing = false;
        host.EditingFocusLost += () => lostEditing = true;
        foreach (string action in new[] { "cancel", "escape", "caption", "confirm" })
        {
            bool confirm = action == "confirm";
            bool sawDialog = false, handedOffInput = false, allowsActivation = false, ownsForeground = false, ownsTextFocus = false;
            bool blocksCanvas = false;
            int before = Field<ColorLibrary>(session, "_colorLibrary").Swatches.Count;
            using var timeout = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(TimeSpan.FromSeconds(interactive ? 120 : 5), timeout.Token); }
                catch (OperationCanceledException) { return; }
                Console.Error.WriteLine("FAIL: SaveSwatch is stuck in a modal loop after its dialog vanished; editing input remains disabled.");
                Environment.Exit(1);
            });
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            Window? heldDialog = null;
            timer.Tick += (_, _) =>
            {
                if (heldDialog is not null)
                {
                    string? evidence = Environment.GetEnvironmentVariable("FLOATCANVAS_MODAL_EVIDENCE");
                    if (evidence is not null) File.WriteAllText(evidence, JsonSerializer.Serialize(new
                    { Text = Descendants(heldDialog).OfType<TextBox>().Single().Text, host.HasEditingInput, Focus = GetFocus().ToInt64(), Foreground = GetForegroundWindow().ToInt64() }));
                    return;
                }
                var dialog = app.Windows.Cast<Window>().FirstOrDefault(w => w.IsVisible && w.Owner == color && w.Title == L.Get("Color.SwatchName"));
                if (dialog is null) return;
                sawDialog = true;
                handedOffInput = host.HasEditingInput && host.Diagnostics.All(pair => pair.InputVisible && IsWindowEnabled(pair.InputHandle)) &&
                    ComponentDispatcher.IsThreadModal && host.CanBeginCanvasGesture?.Invoke() == false;
                nint handle = new WindowInteropHelper(dialog).Handle;
                allowsActivation = (GetWindowLongPtrW(handle, -20).ToInt64() & 0x08000000) != 0;
                ownsForeground = GetForegroundWindow() == background;
                ownsTextFocus = GetFocus() == handle && Descendants(dialog).OfType<TextBox>().Single().IsKeyboardFocused;
                int beforeCanvasPress = Field<EditorController>(session, "_editor").Session.Document.Elements.Count;
                SendMessage(host.Diagnostics.Single().InputHandle, 0x201, 1, (nint)0x000A000A);
                SendMessage(host.Diagnostics.Single().InputHandle, 0x202, 0, (nint)0x000A000A);
                blocksCanvas = host.HasEditingInput && GetFocus() == handle &&
                    Field<EditorController>(session, "_editor").Session.Document.Elements.Count == beforeCanvasPress;
                if (interactive && confirm)
                {
                    heldDialog = dialog;
                    Console.WriteLine("MODAL_INPUT_READY type 'Modal keyboard verified' into the name editor and press Enter.");
                    Console.Out.Flush();
                }
                else
                {
                    timer.Stop();
                    if (action == "escape")
                        dialog.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                            HwndSource.FromHwnd(handle), Environment.TickCount, System.Windows.Input.Key.Escape)
                            { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
                    else
                    {
                        var button = action == "caption" ? (Button)dialog.Template.FindName("Close", dialog) :
                            Descendants(dialog).OfType<Button>().Single(value => confirm ? value.IsDefault : value.IsCancel);
                        typeof(Button).GetMethod("OnClick", Private)!.Invoke(button, null);
                    }
                }
            };
            timer.Start();
            try { Invoke(picker, "SaveSwatch"); }
            finally { timeout.Cancel(); timer.Stop(); }
            Pump(TimeSpan.FromMilliseconds(100));
            check(action + " shows and completes the real naming dialog", sawDialog);
            check(action + " retains delegated input and a modal canvas barrier", handedOffInput);
            check(action + " retains the overlay no-activate policy", allowsActivation);
            check(action + " retains the independent background foreground without delivering input", ownsForeground);
            check(action + " gives the name editor actual native and WPF keyboard focus", ownsTextFocus);
            check(action + " blocks canvas clicks without releasing input or stealing text focus", blocksCanvas);
            check(action + " preserves tool options, picker and the editing lease", !lostEditing && host.HasEditingInput && options.IsVisible && field.IsPickerOpen);
            check(action + " reenables the real toolbar and canvas HWNDs",
                IsWindowEnabled(new WindowInteropHelper(toolbar).Handle) &&
                host.Diagnostics.All(pair => IsWindowEnabled(pair.InputHandle) && IsWindowEnabled(pair.DisplayHandle)));
            check(action + " saves a swatch only after confirmation", Field<ColorLibrary>(session, "_colorLibrary").Swatches.Count == before + (confirm ? 1 : 0));
            if (interactive && confirm)
                check("real UI keyboard input reaches and saves the naming text field", Field<ColorLibrary>(session, "_colorLibrary").Swatches.Last().Name == "Modal keyboard verified");
        }
        var library = Field<ColorLibrary>(session, "_colorLibrary");
        var saved = library.Swatches.Last();
        var swatch = Field<Panel>(picker, "pinned").Children.OfType<Button>().Single(button => Equals(button.Tag, saved.Id));
        var menu = (OverlayContextMenu)swatch.ContextMenu;
        menu.PlacementTarget = swatch;
        menu.IsOpen = true; Pump(TimeSpan.FromMilliseconds(60));
        check("opening the swatch context menu retains both parent layers", field.IsPickerOpen && color.IsVisible && options.IsVisible);
        menu.IsOpen = false; Pump(TimeSpan.FromMilliseconds(60));
        check("closing the swatch context menu returns to the same color window", field.IsPickerOpen && color.IsVisible && GetFocus() == new WindowInteropHelper(color).Handle);

        bool sawRename = false, typedRename = false;
        var renameTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        renameTimer.Tick += (_, _) =>
        {
            var dialog = app.Windows.Cast<Window>().FirstOrDefault(value => value.IsVisible && value.Owner == color && value.Title == L.Get("Color.SwatchName"));
            if (dialog is null) return;
            sawRename = true; renameTimer.Stop();
            var name = Descendants(dialog).OfType<TextBox>().Single();
            if (GetFocus() == new WindowInteropHelper(dialog).Handle && name.IsKeyboardFocused)
                foreach (char letter in "Renamed swatch") SendMessage(new WindowInteropHelper(dialog).Handle, 0x102, letter, 1);
            typedRename = name.Text == "Renamed swatch";
            SessionWindowCoordinator.SetModalResult(dialog, true);
        };
        menu.IsOpen = true; Pump(TimeSpan.FromMilliseconds(30)); renameTimer.Start();
        try { ((MenuItem)menu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, menu.Items[1])); }
        finally { renameTimer.Stop(); }
        Pump(TimeSpan.FromMilliseconds(100));
        check("rename receives native text input immediately without an extra click", sawRename && typedRename && library.Swatches.Single(value => value.Id == saved.Id).Name == "Renamed swatch");
        check("closing rename retains the same first and second level windows", options.IsVisible && color.IsVisible && field.IsPickerOpen && GetFocus() == new WindowInteropHelper(color).Handle);

        var eye = Descendants(picker).OfType<Button>().Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == L.Get("Color.Eyedropper"));
        int elementsBeforeSample = Field<EditorController>(session, "_editor").Session.Document.Elements.Count;
        eye.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, eye)); Pump(TimeSpan.FromMilliseconds(50));
        check("eyedropper suspends rather than destroys its parent color session", field.IsPickerOpen && !color.IsVisible && !options.IsVisible);
        nint input = host.Diagnostics.Single().InputHandle;
        SendMessage(input, 0x201, 1, (nint)0x000A000A);
        SendMessage(input, 0x202, 0, (nint)0x000A000A);
        Pump(TimeSpan.FromMilliseconds(100));
        check("sampling a color restores the exact original tool and color windows", options.IsVisible && color.IsVisible && field.IsPickerOpen && Window.GetWindow(field.PickerRoot) == color);
        check("sampling returns keyboard focus to the color layer without drawing", GetFocus() == new WindowInteropHelper(color).Handle && Field<EditorController>(session, "_editor").Session.Document.Elements.Count == elementsBeforeSample);
        eye.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, eye)); Invoke(session, "EndEyedropper", true); Pump(TimeSpan.FromMilliseconds(60));
        check("cancelling eyedropper also restores both original parent windows", options.IsVisible && color.IsVisible && field.IsPickerOpen);
        eye.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, eye)); Invoke(session, "SetEditing", false, false); Pump(TimeSpan.FromMilliseconds(60));
        check("leaving edit mode while sampling never resurrects either popup layer", !host.IsEditing && !options.IsVisible && !color.IsVisible && !field.IsPickerOpen);
        field.ClosePicker();
        if (receiverBaseline is not null)
            check("the background receiver gets no keyboard or pointer input during the modal", ReceiverState(receiverOutput) == receiverBaseline);
        Invoke(session, "SetEditing", false, false);
        }
        finally { if (receiver is { HasExited: false }) { receiver.CloseMainWindow(); receiver.WaitForExit(3000); } }
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static string ReceiverState(string path)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var json = JsonDocument.Parse(stream);
                return json.RootElement.GetRawText();
            }
            catch (Exception exception) when (exception is IOException or JsonException) { Pump(TimeSpan.FromMilliseconds(10)); }
        }
        throw new IOException("The isolated background receiver did not publish readable input counters.");
    }
    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(System.Windows.Media.VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);
}
