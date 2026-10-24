using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class Phase2WindowTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, DesktopSession session, OverlayHost host, Window toolbar, Action<Action> activate)
    {
        var editor = Field<EditorController>(session, "_editor");
        editor.Tool = EditorTool.Rectangle;
        Invoke("OpenToolOptions");
        var options = Field<Window>(session, "_toolOptionsWindow");
        Click(Field<Button>(session, "_toolOptionsPin"));
        check("tool options starts pinned", Field<bool>(session, "_toolOptionsPinned"));
        Invoke("CloseToolOptions"); Invoke("OpenToolOptions");
        check("manually closed tool options reopen unpinned", !Field<bool>(session, "_toolOptionsPinned"));
        Click(Field<Button>(session, "_toolOptionsPin"));
        Invoke("ToggleInspector");
        var inspector = Field<InspectorWindow>(session, "_inspector");
        Click(Field<Button>(inspector, "pin"));
        Invoke("ToggleLayers");
        var layers = Field<LayersWindow>(session, "_layers");
        var coordinator = Field<SessionWindowCoordinator>(session, "_windowCoordinator");
        options.Activate(); Pump();
        check("most recently focused sibling is raised last", coordinator.OrderForRaise([toolbar, inspector, layers, options]).Last() == options);
        inspector.Activate(); Pump();
        check("focusing another sibling changes the stacking order", coordinator.OrderForRaise([toolbar, inspector, layers, options]).Last() == inspector);
        Invoke("SetEditing", false, false); Pump();
        check("leaving edit mode hides pinned windows", !options.IsVisible && !inspector.IsVisible && !layers.IsVisible);
        activate(() => Invoke("SetEditing", true, false)); Pump();
        check("reentering edit mode restores open pinned windows and pins", options.IsVisible && inspector.IsVisible && layers.IsVisible &&
            Field<bool>(session, "_toolOptionsPinned") && inspector.IsPinned);
        Invoke("CloseToolOptions"); inspector.Dismiss(); Invoke("ToggleLayers");
        Invoke("SetEditing", false, false); toolbar.Show(); activate(() => Invoke("SetEditing", true, false)); Pump();
        check("closed windows remain closed on reentry", host.IsEditing && !options.IsVisible && !inspector.IsVisible && !layers.IsVisible);
        Invoke("SwitchToolState", EditorTool.Canvas, false);
        Invoke("OpenCanvasSettings");
        check("canvas settings can open in this editing session", host.IsEditing && Field<CanvasSettingsWindow?>(session, "_canvasSettingsWindow") is not null);
        var canvas = Field<CanvasSettingsWindow>(session, "_canvasSettingsWindow");
        Click(Field<Button>(canvas, "pin"));
        Invoke("SetEditing", false, false); toolbar.Show(); activate(() => Invoke("SetEditing", true, false)); Pump();
        check("canvas settings preserve their open pinned state across edit sessions", canvas.IsVisible && canvas.IsPinned);
        canvas.Close(); Invoke("OpenCanvasSettings"); Pump();
        check("manually closed canvas settings reopen unpinned", canvas.IsVisible && !canvas.IsPinned);
        var help = (Button)canvas.Template.FindName("Help", canvas);
        var actions = (FrameworkElement)canvas.Resources["CanvasWindowActions"];
        check("canvas caption orders help before pin", help.TranslatePoint(new(), canvas).X < actions.TranslatePoint(new(), canvas).X);

        object? Invoke(string method, params object[] args) => typeof(DesktopSession).GetMethod(method, Private)!.Invoke(session, args);
    }
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
