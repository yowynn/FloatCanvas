using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L = FloatCanvas.Localization.AppText;

internal static class AdditionalShortcutTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    {
        foreach (int key in new[] { 0x49, 0x50 })
            check("new single letter command is accepted: " + (char)key, EditorShortcutPolicy.IsCommand(key, OverlayModifiers.None));
        foreach (int key in new[] { 0x21, 0x22, 0x4C, 0xBC, 0x54 })
        {
            check("new Ctrl command is accepted: " + key, EditorShortcutPolicy.IsCommand(key, OverlayModifiers.Control));
            check("new Ctrl command preserves text input: " + key, !EditorShortcutPolicy.ShouldRoute(key, System.Windows.Input.ModifierKeys.Control, new TextBox()));
        }
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var foreground = GetForegroundWindow();
        var bounds = new PixelBounds(-20000, -20000, 64, 64);
        Func<IReadOnlyList<MonitorInfo>> monitors = () => [new("additional-shortcuts", bounds, bounds, 96, 96, true)];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null,
            [OverlayMonitorScope.PrimaryScreen, monitors, false], null)!;
        using var desktop = new DesktopSession(app, host);
        var isolation = typeof(OverlayHost).GetField("_keyboardIsolation", Private)!;
        ((IDisposable?)isolation.GetValue(host))?.Dispose(); isolation.SetValue(host, null);
        Field<System.Windows.Forms.NotifyIcon>(desktop, "_tray").Visible = false;
        var editor = Field<EditorController>(desktop, "_editor");
        var state = editor.Session; state.Document.LayerOpacity = .5;
        var keys = new byte[256]; check("additional shortcuts preserve thread state", GetKeyboardState(keys));
        try
        {
            SetControl(true); Invoke(desktop, "OnKey", 0x21);
            check("Ctrl+PageUp increases canvas opacity by five percent", Math.Abs(state.Document.LayerOpacity - .55) < 1e-9 && state.UndoCount == 1);
            Invoke(desktop, "OnKey", 0x22);
            check("Ctrl+PageDown decreases canvas opacity reversibly", Math.Abs(state.Document.LayerOpacity - .5) < 1e-9 && state.UndoCount == 2 && state.Undo() && Math.Abs(state.Document.LayerOpacity - .55) < 1e-9);
            state.Document.LayerOpacity = 1; int history = state.UndoCount; Invoke(desktop, "OnKey", 0x21);
            check("opacity at the upper limit adds no history", state.Document.LayerOpacity == 1 && state.UndoCount == history);
            state.Document.LayerOpacity = 0; Invoke(desktop, "OnKey", 0x22);
            check("opacity is clamped at zero", state.Document.LayerOpacity == 0);
            SetControl(false); Invoke(desktop, "OnKey", 0x50);
            check("P selects the actual capture tool", editor.Tool == EditorTool.CaptureRegion);
            var toolbar = Field<Window>(desktop, "_toolbar");
            var buttons = Descendants(toolbar).OfType<Button>().Where(button => button.Tag is string).ToArray();
            foreach (var item in new[] { ("import", "I"), ("capture", "P"), ("properties", "Enter"), ("layers", "Ctrl+L") })
            {
                var button = buttons.Single(button => Equals(button.Tag, item.Item1));
                check(item.Item1 + " toolbar tooltip discloses its shortcut", button.ToolTip?.ToString()?.Contains(item.Item2, StringComparison.Ordinal) == true);
                if (item.Item2.Length == 1) check(item.Item1 + " has a visible single letter legend", Descendants(button).OfType<TextBlock>().Any(label => label.Text == item.Item2));
            }
            var slider = Field<Slider>(desktop, "_opacitySlider");
            check("opacity tooltip discloses both commands", slider.ToolTip?.ToString()?.Contains("Ctrl+PageUp / Ctrl+PageDown", StringComparison.Ordinal) == true);
            var menu = Field<ContextMenu>(desktop, "_toolbarMenu");
            var entries = MenuItems(menu.Items).ToArray();
            foreach (var item in new[] { ("Toolbar.Image", "I"), ("ToolbarV2.Properties", "Enter"), ("ToolbarV2.Layers", "Ctrl+L"), ("Menu.Settings", "Ctrl+,"), ("ToolOptions.Title", "Ctrl+T") })
                check(item.Item1 + " menu discloses its command", entries.Any(entry => Equals(entry.Header, L.Get(item.Item1)) && entry.InputGestureText == item.Item2));
            check("capture menu discloses P", entries.Single(entry => Equals(entry.Tag, EditorTool.CaptureRegion)).InputGestureText == "P");
            foreach (string field in new[] { "_layers", "_settingsWindow", "_toolOptionsWindow" })
            {
                var original = typeof(DesktopSession).GetField(field, Private)!.GetValue(desktop);
                var panel = field == "_layers" ? new LayersWindow(() => state) : new Window();
                panel.ShowActivated = false; panel.ShowInTaskbar = false; panel.Left = panel.Top = -20000; panel.Width = panel.Height = 80;
                Set(desktop, field, panel); panel.Show();
                SetControl(true); int key = field == "_layers" ? 0x4C : field == "_settingsWindow" ? 0xBC : 0x54;
                Invoke(desktop, "OnKey", key); Invoke(desktop, "OnKey", key);
                check(field + " command keeps an already open window open", panel.IsVisible);
                panel.Close();
                typeof(DesktopSession).GetField(field, Private)!.SetValue(desktop, original);
            }
            check("additional shortcut fixture leaves foreground unchanged", GetForegroundWindow() == foreground);
        }
        finally { SetKeyboardState(keys); }
        var frame = new DispatcherFrame(); app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<MenuItem> MenuItems(ItemCollection items)
    { foreach (var item in items.OfType<MenuItem>()) { yield return item; foreach (var child in MenuItems(item.Items)) yield return child; } }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { yield return root; foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach (var value in Descendants(child)) yield return value; }
    private static void SetControl(bool value) { var keys = new byte[256]; if(value) keys[0x11] = keys[0xA2] = 0x80; SetKeyboardState(keys); }
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Invoke(object target, string method, params object[] args) => target.GetType().GetMethod(method, Private)!.Invoke(target, args);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetKeyboardState(byte[] keys);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetKeyboardState(byte[] keys);
}
