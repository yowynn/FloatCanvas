using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class CanvasCommandRuntimeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, DesktopSession session, OverlayHost host,
        Window toolbar, Action<uint, bool, bool, bool> key)
    {
        CanvasPointerRoutingTests.Run(check, session, host);
        var editor = Field<EditorController>(session, "_editor");
        var state = editor.Session;
        state.Execute("command fixture", document => document.Elements.Clear());
        var first = new CanvasElement { Kind = ElementKind.Rectangle, Width = 80, Height = 40 };
        var second = new CanvasElement { Kind = ElementKind.Ellipse, Width = 60, Height = 30, Transform = Affine2D.Translation(120, 0) };
        state.Add(first); state.Add(second); state.SetSelection([first.Id]);
        host.TryFocusEditingCanvas(); Pump(); Press(0x0D);
        var inspector = Field<InspectorWindow?>(session, "_inspector");
        check("Enter opens properties for a single selected layer through the native keyboard bridge", inspector?.IsVisible == true && state.Selection.SetEquals([first.Id]));
        inspector!.Dismiss(); Pump();
        state.SetSelection([first.Id, second.Id]);
        Invoke("ToggleLayers"); Pump();
        var layers = Field<LayersWindow>(session, "_layers");
        SessionWindowCoordinator.Show(layers, true); layers.Focus(); Pump(); Press(0x0D);
        check("Enter opens properties for a box/list multiselection while Layers owns focus", inspector.IsVisible && state.Selection.SetEquals([first.Id, second.Id]));
        Field<Button>(inspector, "pin").Focus(); Pump(); Press(0x47, control: true);
        check("Ctrl+G from an ordinary property control groups the retained selection", state.Document.Groups.Count == 1 && state.Selection.SetEquals([first.Id, second.Id]));
        Press(0x47, control: true, shift: true);
        check("Ctrl+Shift+G from properties ungroups without clearing the selection", state.Document.Groups.Count == 0 && state.Selection.SetEquals([first.Id, second.Id]));
        inspector.Dismiss(); Pump(); host.TryFocusEditingCanvas(); Pump();
        Press(0x11, control: true);
        check("Ctrl hides only selection adorners before the canvas grouping command", state.Selection.SetEquals([first.Id, second.Id]) && !editor.ShouldShowSelection(OverlayModifiers.Control));
        Press(0x47, control: true);
        check("canvas Ctrl+G preserves the selected members and creates their group", state.Document.Groups.Count == 1 && state.Selection.SetEquals([first.Id, second.Id]));
        Press(0x47, control: true, shift: true);
        check("canvas Ctrl+Shift+G ungroups the same selected members", state.Document.Groups.Count == 0 && state.Selection.SetEquals([first.Id, second.Id]));
        foreach (uint clear in new uint[] { 0x08, 0x2E })
        {
            var document = state.Document;
            var canvas = document.Canvas;
            Press(clear, control: true); WaitForCommand();
            check($"Ctrl+{(clear == 0x08 ? "Backspace" : "Delete")} clears only the current canvas", ReferenceEquals(editor.Session, state) && ReferenceEquals(state.Document, document) && ReferenceEquals(state.Document.Canvas, canvas) && document.Elements.Count == 0);
            Invoke("Undo");
            check("one undo restores the shortcut-cleared objects", state.Document.Elements.Count == 2);
        }
        var prior = editor.Session;
        Press(0x4E, control: true); WaitForCommand();
        check("Ctrl+N creates a new empty session through the existing archive workflow", !ReferenceEquals(editor.Session, prior) && editor.Session.Document.Elements.Count == 0 && prior.Document.Elements.Count == 2);
        check("canvas shortcuts keep the editing session active", host.IsEditing);

        void Press(uint vk, bool control = false, bool shift = false)
        { key(vk, false, control, shift); key(vk, true, control, shift); Pump(); }
        void WaitForCommand()
        {
            var timeout = DateTime.UtcNow.AddSeconds(10);
            while (Busy() && DateTime.UtcNow < timeout) { Pump(); Thread.Sleep(5); }
            check("canvas command finishes within its bounded wait", !Busy());
        }
        bool Busy() => (bool)typeof(DesktopSession).GetProperty("DocumentOperationBusy", Private)!.GetValue(session)!;
        object? Invoke(string name) => typeof(DesktopSession).GetMethod(name, Private)!.Invoke(session, null);
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
}
