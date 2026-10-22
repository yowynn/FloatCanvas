using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

/// <summary>Tab crosses the actual private keyboard bridge into native and WPF editor surfaces.</summary>
internal static class LauncherTabRuntimeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check, DesktopSession session, OverlayHost host, Window toolbar,
        Action<KeyboardInputPacket, Action> dispatch)
    {
        var settings = Field<UserSettings>(session, "_settings");
        var launcher = Field<ToolbarLauncher>(session, "_toolbarLauncher");
        var expanded = Field<FrameworkElement>(session, "_toolbarExpandedContent");
        var editor = Field<EditorController>(session, "_editor");
        var menu = Field<ContextMenu>(session, "_toolbarMenu");
        try
        {
            Invoke("CloseToolOptions");
            Focus(toolbar, launcher, "the editing logo");
            check("the Tab fixture starts with an expanded toolbar and visible editing canvas", settings.ToolbarExpanded && host.IsEditing && host.IsVisible);
            Send(0x09);
            check("toolbar Tab through the private bridge collapses once without hiding the canvas or leaving editing",
                !settings.ToolbarExpanded && expanded.Visibility == Visibility.Collapsed && host.IsVisible && host.IsEditing);
            Send(0x09, repeat: true); Send(0x09, repeat: true);
            check("held toolbar Tab does not repeatedly toggle expansion", !settings.ToolbarExpanded && host.IsVisible && host.IsEditing);
            Send(0x09, up: true);
            Press();
            check("a new Tab press after release expands the toolbar", settings.ToolbarExpanded && expanded.Visibility == Visibility.Visible);

            check("the visible canvas owns the fixture's native editing input", host.TryFocusEditingCanvas());
            Pump(); Press();
            check("native canvas Tab collapses the toolbar while preserving visible editing", !settings.ToolbarExpanded && host.IsVisible && host.IsEditing);
            Press();
            check("native canvas Tab expands again without changing visibility", settings.ToolbarExpanded && host.IsVisible && host.IsEditing);
            host.SetVisible(false); Pump();
            Focus(toolbar, launcher, "the hidden canvas's editing logo");
            Press();
            check("toolbar Tab can collapse while the canvas remains hidden", !settings.ToolbarExpanded && !host.IsVisible && host.IsEditing);
            Press();
            check("toolbar Tab restores its tools without showing the hidden canvas", settings.ToolbarExpanded && !host.IsVisible && host.IsEditing);
            host.SetVisible(true); Pump();

            foreach (var modifiers in new[] { OverlayModifiers.Control, OverlayModifiers.Shift })
            {
                Focus(toolbar, launcher, modifiers + "+Tab source");
                Press(modifiers);
                check(modifiers + "+Tab does not fold the toolbar", settings.ToolbarExpanded && host.IsEditing);
            }

            editor.Tool = EditorTool.Rectangle;
            Invoke("OpenToolOptions"); Pump();
            var options = Field<Window>(session, "_toolOptionsWindow");
            var panel = Field<ToolOptionsPanel>(session, "_toolOptionsPanel");
            var number = Field<Dictionary<string, TextBox>>(panel, "numbers")["StrokeWidth"];
            Focus(options, number, "the production stroke-width number field");
            string numberBefore = number.Text;
            Press();
            check("Tab in an actual tool-options number field does not fold or edit its value", settings.ToolbarExpanded && number.Text == numberBefore && options.IsVisible && host.IsEditing);
            Invoke("CloseToolOptions"); Pump();

            Focus(toolbar, launcher, "the main-menu owner");
            var openMenu = (Action)typeof(ToolbarLauncher).GetField("MenuRequested", Private)!.GetValue(launcher)!;
            openMenu(); Pump();
            ((MenuItem)menu.Items[0]).Focus(); Pump();
            check("the actual launcher menu owns keyboard focus before Tab", menu.IsOpen && menu.IsKeyboardFocusWithin);
            Press();
            check("Tab from the main menu does not invoke its collapse command", settings.ToolbarExpanded && host.IsEditing);
            menu.IsOpen = false; Pump();

            toolbar.Activate(); Pump();
            check("inline Tab starts from the fixture's native canvas", host.TryFocusEditingCanvas());
            Invoke("ShowTextEditor", null, new PointD(70, 90));
            var edit = Field<InlineTextEdit>(session, "_inlineText");
            var input = Field<CanvasTextInput>(session, "_canvasTextInput");
            input.Focus(); Pump();
            var inputWindow = Field<Window>(input, "window");
            check("inline Tab targets the real production text input", input.IsActive && edit.Input.IsKeyboardFocusWithin &&
                GetForegroundWindow() == new WindowInteropHelper(inputWindow).Handle);
            edit.Input.Text = "Keep this inline draft";
            Press();
            check("Tab in inline text preserves toolbar expansion and the active text transaction", settings.ToolbarExpanded && edit.IsActive &&
                editor.Session.IsTransactionActive && host.IsEditing);
            edit.Finish(commit: false); Pump();
        }
        finally
        {
            menu.IsOpen = false;
            ClearModifiers();
        }

        object? Invoke(string name, params object?[] arguments) => typeof(DesktopSession).GetMethod(name, Private)!.Invoke(session, arguments);
        void Focus(Window window, Control control, string label)
        {
            SessionWindowCoordinator.Show(window, true); Pump();
            bool focused = control.Focus(); Pump();
            check(label + " owns real foreground and keyboard focus", focused && control.IsKeyboardFocusWithin && GetForegroundWindow() == new WindowInteropHelper(window).Handle);
        }
        void Press(OverlayModifiers modifiers = OverlayModifiers.None)
        {
            Send(0x09, modifiers); Send(0x09, modifiers, up: true);
            if (modifiers != OverlayModifiers.None) ClearModifiers();
        }
        void ClearModifiers()
        {
            foreach (uint key in new uint[] { 0x11, 0x10, 0x12, 0x5B }) Send(key, up: true);
        }
        void Send(uint key, OverlayModifiers modifiers = OverlayModifiers.None, bool up = false, bool repeat = false)
        {
            var keys = new byte[256];
            if ((modifiers & OverlayModifiers.Control) != 0) keys[0x11] = keys[0xA2] = 128;
            if ((modifiers & OverlayModifiers.Shift) != 0) keys[0x10] = keys[0xA0] = 128;
            if (!up) keys[key] = 128;
            var packet = (KeyboardInputPacket)Activator.CreateInstance(typeof(KeyboardInputPacket), Private, null,
                [1L, 1L, KeyboardInputPacketKind.Key, key, MapVirtualKeyW(key, 0), 0u,
                    unchecked((uint)Environment.TickCount), up, repeat, GetForegroundWindow(), keys, false, null,
                    up ? 0x101u : 0x100u], null)!;
            dispatch(packet, () => { }); Pump();
        }
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Pump()
    { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint key, uint map);
}
