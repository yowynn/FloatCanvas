using System.Windows;
using System.Windows.Input;

namespace FloatCanvas.App;

internal static class AppInputPolicy
{
    private static bool initialized;
    internal static void AttachToolbarToggle(Window window, Action toggle, Func<bool>? canRoute = null)
    {
        // Tab is an editor command in this mouse-first application, not button navigation.
        // Editor surfaces opt in without borrowing Tab from local input controls.
        window.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key != Key.Tab || canRoute?.Invoke() == false) return;
            var source = e.OriginalSource as DependencyObject ?? Keyboard.FocusedElement as DependencyObject;
            if (!EditorShortcutPolicy.ShouldRoute(0x09, Keyboard.Modifiers, source)) return;
            e.Handled = true;
            if (!e.IsRepeat) toggle();
        }), handledEventsToo: true);
    }

    internal static void Initialize()
    {
        if (initialized) return;
        initialized=true;
        EventManager.RegisterClassHandler(typeof(UIElement),Keyboard.PreviewKeyDownEvent,
            new KeyEventHandler((_,e)=>{if(e.Key==Key.Tab)e.Handled=true;}),true);
    }
}
