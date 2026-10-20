using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using FloatCanvas.Windows;

/// <summary>Real auxiliary-window wiring and inline save in the existing isolated native fixture.</summary>
internal static class AuxiliaryShortcutRuntimeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check, DesktopSession session, OverlayHost host, Window toolbar,
        Action<KeyboardInputPacket, Action> dispatch)
    {
        string profile = Path.GetFullPath(ApplicationPaths.ProfileDirectory);
        string fixtureRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FloatCanvasNativeInput")) + Path.DirectorySeparatorChar;
        check("auxiliary runtime writes only inside the fixture's isolated profile", profile.StartsWith(fixtureRoot, StringComparison.OrdinalIgnoreCase));
        if (!profile.StartsWith(fixtureRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("This fixture requires the native routing fixture's isolated profile.");

        var editor = Field<EditorController>(session, "_editor");
        var state = editor.Session;
        state.Execute("auxiliary shortcut fixture", document => document.Elements.Clear());
        var element = new CanvasElement { Kind = ElementKind.Rectangle, Width = 80, Height = 40 };
        state.Add(element); state.SetSelection([element.Id]);
        editor.Tool = EditorTool.Rectangle;
        try
        {
            Invoke("CloseToolOptions");
            Invoke("ToggleCanvasSettings"); Pump();
            check("Canvas auxiliary command first selects its ordinary tool without opening settings", editor.Tool == EditorTool.Canvas &&
                Field<CanvasSettingsWindow?>(session, "_canvasSettingsWindow")?.IsVisible != true);
            Invoke("ToggleCanvasSettings"); Pump();
            var canvas = Field<CanvasSettingsWindow>(session, "_canvasSettingsWindow");
            check("Canvas auxiliary command opens settings when the tool is already selected", canvas.IsVisible && editor.Tool == EditorTool.Canvas);
            var gridVisible = Field<CheckBox>(canvas, "gridVisible");
            var original = CurrentElement().Transform;
            state.Execute("canvas settings undo marker", document => document.Elements.Single(item => item.Id == element.Id).Transform = Affine2D.Translation(17, 9).Compose(original));
            int undo = state.UndoCount;
            Focus(canvas, gridVisible, "CanvasSettings ordinary checkbox");
            Press(0x5A, OverlayModifiers.Control);
            check("CanvasSettings Ctrl+Z reaches the live DesktopSession undo command", state.UndoCount == undo - 1 && CurrentElement().Transform == original);
            Press(0x42);
            check("CanvasSettings B reaches the live tool command", editor.Tool == EditorTool.Freehand && host.IsEditing);
            Invoke("CloseToolOptions"); SessionWindowCoordinator.Hide(canvas); Pump();

            var opacity = Field<Slider>(session, "_opacitySlider");
            state.Execute("opacity keyboard fixture", document => document.LayerOpacity = .8);
            state.SetSelection([element.Id]); Pump();
            Focus(toolbar, opacity, "toolbar opacity Slider");
            double opacityBefore = state.Document.LayerOpacity;
            var transformBefore = CurrentElement().Transform;
            Press(0x25);
            check("toolbar Slider Left changes its own opacity instead of moving the selected layer",
                state.Document.LayerOpacity < opacityBefore && state.Document.LayerOpacity >= opacity.Minimum &&
                Math.Abs(state.Document.LayerOpacity - opacity.Value) < 1e-9 && CurrentElement().Transform == transformBefore);

            Invoke("ShowStampLibrary");
            Wait(() => Field<StampLibraryWindow?>(session, "_stampWindow")?.IsVisible == true, "the production stamp library becomes visible");
            var stamps = Field<StampLibraryWindow>(session, "_stampWindow");
            var grid = Field<ListBox>(stamps, "grid");
            original = CurrentElement().Transform;
            state.Execute("stamp library undo marker", document => document.Elements.Single(item => item.Id == element.Id).Transform = Affine2D.Translation(23, 5).Compose(original));
            undo = state.UndoCount;
            Focus(stamps, grid, "StampLibrary ordinary list");
            Press(0x5A, OverlayModifiers.Control);
            check("StampLibrary Ctrl+Z reaches the live DesktopSession fallback", state.UndoCount == undo - 1 && CurrentElement().Transform == original);
            editor.Tool = EditorTool.Rectangle;
            Focus(stamps, grid, "StampLibrary tool key source");
            Press(0x42);
            check("StampLibrary B reaches the live tool fallback", editor.Tool == EditorTool.Freehand && host.IsEditing);
            Invoke("CloseToolOptions"); SessionWindowCoordinator.Hide(stamps); Pump();

            string projectPath = Path.Combine(profile, "auxiliary-inline-save-" + Guid.NewGuid().ToString("N") + ".floatcanvas");
            var fileState = typeof(DesktopSession).GetMethod("FileState", Private)!.Invoke(session, [state])!;
            fileState.GetType().GetField("Path", BindingFlags.Instance | BindingFlags.Public)!.SetValue(fileState, projectPath);
            var existingWindows = Application.Current.Windows.Cast<Window>().ToHashSet();
            toolbar.Activate(); Pump();
            check("inline save starts from the fixture's own native canvas", host.TryFocusEditingCanvas());
            Invoke("ShowTextEditor", null, new PointD(70, 90));
            var edit = Field<InlineTextEdit>(session, "_inlineText");
            var input = Field<CanvasTextInput>(session, "_canvasTextInput");
            input.Focus(); Pump();
            var inputWindow = Field<Window>(input, "window");
            check("inline save key targets the actual production text editor", input.IsActive && edit.Input.IsKeyboardFocusWithin && GetForegroundWindow() == new WindowInteropHelper(inputWindow).Handle);
            const string text = "auxiliary inline save\n快捷键已提交";
            edit.Input.Text = text;
            Guid textId = edit.Target.Id;
            check("inline text remains an active transaction before Ctrl+S", edit.IsActive && state.IsTransactionActive && CurrentElement().Transform == transformBefore);
            Press(0x53, OverlayModifiers.Control);
            Wait(() => !Busy(), "inline save completes the production document command");
            check("inline Ctrl+S commits the draft before saving and closes its input surface",
                !edit.IsActive && Field<InlineTextEdit?>(session, "_inlineText") is null && !state.IsTransactionActive &&
                state.Document.Elements.Single(item => item.Id == textId).Text == text && File.Exists(projectPath));
            var load = Field<ProjectStore>(session, "_projectStore").LoadAsync(projectPath);
            Wait(() => load.IsCompleted, "the saved fixture project can be reopened");
            var reopened = load.GetAwaiter().GetResult();
            check("actual project readback contains both the retained layer and committed inline text",
                reopened.Document.Elements.Count == 2 && reopened.Document.Elements.Any(item => item.Id == element.Id && item.Transform == transformBefore) &&
                reopened.Document.Elements.Any(item => item.Id == textId && item.Kind == ElementKind.Text && item.Text == text));
            check("bound inline Ctrl+S opens no extra application dialog",
                Application.Current.Windows.Cast<Window>().All(window => existingWindows.Contains(window) || ReferenceEquals(window, inputWindow)) && host.IsEditing);
        }
        finally
        {
            ClearModifiers();
        }

        CanvasElement CurrentElement() => state.Document.Elements.Single(item => item.Id == element.Id);
        bool Busy() => (bool)typeof(DesktopSession).GetProperty("DocumentOperationBusy", Private)!.GetValue(session)!;
        object? Invoke(string name, params object?[] arguments) => typeof(DesktopSession).GetMethod(name, Private)!.Invoke(session, arguments);
        void Focus(Window window, Control control, string label)
        {
            SessionWindowCoordinator.Show(window, true); Pump();
            bool focused = control.Focus(); Pump();
            check(label + " owns real foreground and keyboard focus", focused && control.IsKeyboardFocusWithin && GetForegroundWindow() == new WindowInteropHelper(window).Handle);
        }
        void Wait(Func<bool> predicate, string label)
        {
            var end = DateTime.UtcNow.AddSeconds(10);
            while (!predicate() && DateTime.UtcNow < end) { Pump(); Thread.Sleep(5); }
            check(label, predicate());
        }
        void Press(uint key, OverlayModifiers modifiers = OverlayModifiers.None)
        {
            Send(key, modifiers); Send(key, modifiers, up: true);
            if (modifiers != OverlayModifiers.None) ClearModifiers();
        }
        void ClearModifiers()
        {
            foreach (uint key in new uint[] { 0x11, 0x10, 0x12, 0x5B }) Send(key, OverlayModifiers.None, up: true);
        }
        void Send(uint key, OverlayModifiers modifiers, bool up = false)
        {
            var keys = new byte[256];
            if ((modifiers & OverlayModifiers.Control) != 0) keys[0x11] = keys[0xA2] = 128;
            if ((modifiers & OverlayModifiers.Shift) != 0) keys[0x10] = keys[0xA0] = 128;
            if ((modifiers & OverlayModifiers.Alt) != 0) keys[0x12] = keys[0xA4] = 128;
            if ((modifiers & OverlayModifiers.Windows) != 0) keys[0x5B] = 128;
            if (!up) keys[key] = 128;
            bool system = (modifiers & OverlayModifiers.Alt) != 0;
            var packet = (KeyboardInputPacket)Activator.CreateInstance(typeof(KeyboardInputPacket), Private, null,
                [1L, 1L, KeyboardInputPacketKind.Key, key, MapVirtualKeyW(key, 0), system ? 0x20u : 0u,
                    unchecked((uint)Environment.TickCount), up, false, GetForegroundWindow(), keys, false, null,
                    up ? system ? 0x105u : 0x101u : system ? 0x104u : 0x100u], null)!;
            dispatch(packet, () => { }); Pump();
        }
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Pump()
    { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint key, uint map);
}
