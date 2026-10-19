using System.Windows;
using System.Windows.Input;

namespace FloatCanvas.App;

internal static class AppInputPolicy
{
    private static bool initialized;
    internal static void Initialize()
    {
        if (initialized) return;
        initialized=true;
        EventManager.RegisterClassHandler(typeof(UIElement),Keyboard.PreviewKeyDownEvent,
            new KeyEventHandler((_,e)=>{if(e.Key==Key.Tab)e.Handled=true;}),true);
    }
}
