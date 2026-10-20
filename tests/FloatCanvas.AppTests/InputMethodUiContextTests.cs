using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

/// <summary>Real application focus callbacks with a production, internally armed Win+Space candidate; no global keys are generated.</summary>
internal static class InputMethodUiContextTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check, DesktopSession session, OverlayHost host, Window toolbar)
    {
        var editor = Field<EditorController>(session, "_editor");
        var isolation = Field<object>(host, "_keyboardIsolation");
        var gate = Field<object>(isolation, "gate");
        var state = Field<object>(isolation, "state");
        bool armed = false;
        try
        {
            editor.Cancel();
            editor.Session.Execute("input method UI fixture", document => document.Elements.Clear());
            var element = new CanvasElement { Kind = ElementKind.Rectangle, Name = "Before IME switch", Width = 80, Height = 40 };
            editor.Session.Add(element); editor.Session.SetSelection([element.Id]);
            var layers = Field<LayersWindow?>(session, "_layers");
            if (layers is null) { Invoke(session, "ToggleLayers"); layers = Field<LayersWindow>(session, "_layers"); }
            SessionWindowCoordinator.Show(layers, true); layers.Refresh(); Pump();
            check("IME UI fixture begins a real layer rename", (bool)Invoke(layers, "BeginRename", element.Id)!);
            Pump();
            var list = Field<ListBox>(layers, "layers");
            var row = list.Items.Cast<ListBoxItem>().Single(item => (Guid)item.Tag == element.Id);
            var input = ((Grid)row.Content).Children.OfType<TextBox>().Single();
            input.Text = "Rename survives input method switch";
            ArmCandidate();
            check("The production host exposes the bounded input-method candidate", host.IsInputMethodSwitchPending);
            list.Focus(); Pump(); // Actual LostKeyboardFocus callback, with the editor no longer focused.
            check("A real rename LostKeyboardFocus callback retains the draft during Win+Space", !input.IsKeyboardFocusWithin &&
                layers.IsRenaming && input.Visibility == Visibility.Visible && input.Text == "Rename survives input method switch" &&
                editor.Session.Document.Elements.Single().Name == "Before IME switch");
            Deactivate(layers); Pump();
            check("The production layer Deactivated callback also retains the same rename draft", layers.IsRenaming && input.Visibility == Visibility.Visible);
            ClearCandidate();
            SessionWindowCoordinator.Hide(layers); Pump();
            check("Actual hiding after the candidate ends commits and closes the retained rename", !layers.IsVisible && !layers.IsRenaming &&
                editor.Session.Document.Elements.Single().Name == "Rename survives input method switch");

            Invoke(session, "ShowInspectorNear", new PointD(100, 100)); Pump();
            var inspector = Field<InspectorWindow>(session, "_inspector");
            if (inspector.IsPinned) Field<Button>(inspector, "pin").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("IME UI fixture can transfer from properties to its own toolbar", toolbar.Activate());
            ArmCandidate(); Pump(); // The real Deactivated event queued the production focus check above.
            check("A queued properties focus-loss check keeps the unpinned window during Win+Space", !inspector.IsActive && inspector.IsVisible);
            ClearCandidate(); Deactivate(inspector); Pump();
            check("The same properties focus callback dismisses normally after the candidate ends", !inspector.IsVisible);

            Invoke(session, "ShowTextEditor", null, new PointD(300, 200));
            var edit = Field<InlineTextEdit>(session, "_inlineText");
            var textInput = Field<CanvasTextInput>(session, "_canvasTextInput");
            textInput.Focus(); Pump(); edit.Input.Text = "Inline draft survives IME";
            check("IME UI fixture can transfer the invisible text input back to its own toolbar", toolbar.Activate());
            ArmCandidate(); Pump();
            check("A real inline-input Deactivated callback preserves the active draft during Win+Space", ReferenceEquals(Field<InlineTextEdit?>(session, "_inlineText"), edit) &&
                edit.IsActive && !textInput.IsActive && edit.Input.Text == "Inline draft survives IME");
            ClearCandidate(); Deactivate(Field<Window>(textInput, "window")); Pump();
            check("The inline focus callback finishes normally after the candidate ends", Field<InlineTextEdit?>(session, "_inlineText") is null &&
                editor.Session.Document.Elements.Any(item => item.Kind == ElementKind.Text && item.Text == "Inline draft survives IME"));
            check("IME UI callback guards preserve the real editing session", host.IsEditing && !host.IsInputMethodSwitchPending);
        }
        finally { ClearCandidate(); }

        void ArmCandidate()
        {
            ClearCandidate();
            nint foreground = GetForegroundWindow();
            lock (gate)
            {
                Process(0x5B, up: false, foreground);
                Process(0x20, up: false, foreground);
            }
            armed = true;
            check("Only production suppression state is used to arm Win+Space, without sending Windows input", host.IsInputMethodSwitchPending);
        }
        void ClearCandidate()
        {
            lock (gate)
            {
                if (armed)
                {
                    nint foreground = GetForegroundWindow();
                    Process(0x20, up: true, foreground);
                    Process(0x5B, up: true, foreground);
                    armed = false;
                }
                Invoke(isolation, "CancelInputMethodSwitch");
            }
        }
        void Process(uint key, bool up, nint foreground)
        {
            long now = Environment.TickCount64;
            Invoke(state, "Process", key, 0u, 0u, unchecked((uint)now), up, up ? 0x101u : 0x100u,
                foreground, true, true, now, null, null);
        }
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, Private | BindingFlags.Public)!.Invoke(target, args);
    private static void Deactivate(Window window) => typeof(Window).GetMethod("OnDeactivated", Private)!.Invoke(window, [EventArgs.Empty]);
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
