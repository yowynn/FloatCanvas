using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class Phase2FocusTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, DesktopSession session, Window toolbar)
    {
        var editor = Field<EditorController>(session, "_editor");
        editor.Session.Add(new CanvasElement { Kind = ElementKind.Rectangle, Width = 40, Height = 30 });
        editor.Session.SelectAll();
        var launcher = Field<ToolbarLauncher>(session, "_toolbarLauncher");
        launcher.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(toolbar), Environment.TickCount, Key.Enter)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        check("Enter after selection opens properties even when its source is the mascot", Field<InspectorWindow?>(session, "_inspector")?.IsVisible == true &&
            !Field<ContextMenu>(session, "_toolbarMenu").IsOpen);
        check("editing mascot does not retain a hidden keyboard focus target", !launcher.Focusable);
        check("pass-through launcher retains its standalone keyboard entry", new ToolbarLauncher(editing: false).Focusable);
    }
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
}
