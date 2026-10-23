using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

/// <summary>Replays native canvas messages into hidden production HWNDs; no foreground or injected input.</summary>
internal static class SelectionShortcutRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var foreground = GetForegroundWindow();
        var bounds = new PixelBounds(-20000, -20000, 64, 64);
        Func<IReadOnlyList<MonitorInfo>> monitors = () => [new("shortcut-selection", bounds, bounds, 96, 96, true)];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null,
            [OverlayMonitorScope.PrimaryScreen, monitors, false], null)!;
        using var desktop = new DesktopSession(app, host);
        var isolation = typeof(OverlayHost).GetField("_keyboardIsolation", Private)!;
        ((IDisposable?)isolation.GetValue(host))?.Dispose(); isolation.SetValue(host, null);
        Field<System.Windows.Forms.NotifyIcon>(desktop, "_tray").Visible = false;
        typeof(OverlayHost).GetProperty("IsVisible")!.SetValue(host, true);
        typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(host, true);
        var editor = Field<EditorController>(desktop, "_editor");
        var state = editor.Session;
        var first = new CanvasElement { Kind = ElementKind.Rectangle, Width = 25, Height = 20 };
        var second = new CanvasElement { Kind = ElementKind.Ellipse, Width = 20, Height = 20 };
        state.Add(first); state.Add(second); state.SetSelection([first.Id]);
        Pump(app.Dispatcher);
        var keys = new byte[256];
        check("native shortcut replay preserves its thread keyboard state", GetKeyboardState(keys));
        try
        {
            var handle = host.Diagnostics.Single().InputHandle;
            foreach (int command in new[] { 0x43, 0x44, 0x56, 0x41 })
            {
                var selectedBefore = state.Selection.ToHashSet();
                var existing = state.Document.Elements.Select(element => element.Id).ToHashSet();
                SetControl(true);
                SendMessageW(handle, 0x100, 0x11, 0);
                // Copy/paste touch the OS clipboard; exercise their actual snapshot/insert command
                // beneath the transport instead of replacing the user's clipboard contents.
                if (command == 0x43) _ = ObjectClipboardCommand.CreateSnapshot(state);
                else if (command == 0x56)
                    ObjectClipboardCommand.Insert(state, ObjectClipboardCommand.CreateSnapshot(state), state.Assets, new(0, 0, 200, 200));
                else SendMessageW(handle, 0x100, (nuint)command, 0);
                Pump(app.Dispatcher);
                check($"Ctrl+{(char)command} retains copied selection or selects the newly inserted objects",
                    command == 0x43 ? state.Selection.SetEquals(selectedBefore) :
                    command is 0x44 or 0x56 ? state.Selection.SetEquals(state.Document.Elements.Where(element => !existing.Contains(element.Id)).Select(element => element.Id)) :
                    state.Selection.SetEquals(state.Document.Elements.Select(element => element.Id)));
                check($"Ctrl+{(char)command} keeps real selected objects while held", state.Selection.Count > 0 && !editor.ShouldShowSelection(host.InteractionModifiers));
                var selected = state.Selection.ToHashSet();
                long before = host.PresentedFrameCount;
                SetControl(false); SendMessageW(handle, 0x101, 0x11, 0);
                Pump(app.Dispatcher);
                check($"Ctrl+{(char)command} release repaints the retained selection without mouse motion",
                    host.PresentedFrameCount > before && editor.ShouldShowSelection(host.InteractionModifiers) && state.Selection.SetEquals(selected));
            }
            editor.Tool = EditorTool.Text;
            SetControl(true); SendMessageW(handle, 0x100, 0x41, 0);
            check("Ctrl+A selects all without silently changing the active Text tool", editor.Tool == EditorTool.Text && state.Selection.Count == state.Document.Elements.Count);
            SetControl(false); editor.Tool = EditorTool.Rectangle;
            SendMessageW(handle, 0x100, 0x41, 0);
            check("unbound A leaves the active Rectangle tool unchanged", editor.Tool == EditorTool.Rectangle);
            var listed = ToolShortcutCatalog.All.Select(binding => binding.VirtualKey).ToHashSet();
            check("every routed letter tool is disclosed by the shortcut catalog", Enumerable.Range(0x41, 26)
                .All(key => !ToolShortcutCatalog.TryGetTool(key, out _) || listed.Contains(key)));
            check("native regression remains hidden and leaves foreground untouched", GetForegroundWindow() == foreground && host.Diagnostics.All(pair => !pair.DisplayVisible && !pair.InputVisible));
        }
        finally
        {
            SetKeyboardState(keys);
            typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(host, false);
        }
    }

    private static void SetControl(bool down)
    {
        var keys = new byte[256]; if (down) keys[0x11] = keys[0xA2] = 0x80;
        if (!SetKeyboardState(keys)) throw new InvalidOperationException("Cannot set fixture thread keyboard state.");
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Pump(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame(); dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetKeyboardState(byte[] keys);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetKeyboardState(byte[] keys);
}
