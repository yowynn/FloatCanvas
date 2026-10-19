using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FloatCanvas.App;
using L = FloatCanvas.Localization.AppText;

internal static class ToolOptionsKeyboardRouteTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; AppTheme.Apply("light");
        var panel = new StackPanel(); var button = new Button { Content = "Recipe" }; var text = new TextBox(); var slider = new Slider(); var combo = new ComboBox();
        panel.Children.Add(button); panel.Children.Add(text); panel.Children.Add(slider); panel.Children.Add(combo);
        var window = new Window { Content = panel }; var commands = new List<int>(); bool colorOpen = false; int dismissals = 0; bool pinned = false;
        ToolOptionsKeyboardRoute.Attach(window, () =>
        {
            if (colorOpen) { colorOpen = false; dismissals++; return true; }
            if (!pinned) { dismissals++; return true; }
            return false;
        }, () => colorOpen, commands.Add);
        try
        {
            foreach (var key in new[] { Key.B, Key.L, Key.M, Key.OemMinus, Key.OemPlus })
            {
                int previous = commands.Count; var args = Send(button, key);
                check("independent options window routes " + key + " from an ordinary control", args.Handled && commands.Count == previous + 1 && commands[^1] == KeyInterop.VirtualKeyFromKey(key));
            }
            int before = commands.Count; Send(text, Key.B); Send(text, Key.D3); Send(text, Key.D2); Send(slider, Key.Right); Send(combo, Key.B);
            check("text, numeric, slider and font controls keep their local keys", commands.Count == before);
            foreach(var source in new UIElement[]{button,text,slider,combo})
                check("Tab navigation is disabled in "+source.GetType().Name,Send(source,Key.Tab).Handled);
            foreach (var source in new DependencyObject[] { button, text, slider, combo })
                check(source.GetType().Name + " preserves project save and open shortcuts", ToolOptionsKeyboardRoute.ShouldRoute(0x53, ModifierKeys.Control, source) && ToolOptionsKeyboardRoute.ShouldRoute(0x4F, ModifierKeys.Control, source));
            check("textbox undo and clipboard keys remain local", !ToolOptionsKeyboardRoute.ShouldRoute(0x5A, ModifierKeys.Control, text) && !ToolOptionsKeyboardRoute.ShouldRoute(0x56, ModifierKeys.Control, text));
            check("menus and system modifier keys are not repurposed as tools", !ToolOptionsKeyboardRoute.ShouldRoute(0x42, ModifierKeys.None, new MenuItem()) && !ToolOptionsKeyboardRoute.ShouldRoute(0x42, ModifierKeys.Alt, button));
            var modalSource = new Button(); var modalWindow = new Window { Content = modalSource };
            var modalKey = new KeyEventArgs(Keyboard.PrimaryDevice, new DetachedSource(), Environment.TickCount, Key.OemPlus) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            bool consumedBeforeCommand = false;
            ToolOptionsKeyboardRoute.Attach(modalWindow, () => false, () => false, _ => consumedBeforeCommand = modalKey.Handled);
            try { modalSource.RaiseEvent(modalKey); check("a modal-capable command consumes its source key before invoking the command", consumedBeforeCommand); }
            finally { modalWindow.Close(); }
            colorOpen = true; Send(button, Key.L); check("a nested color editor prevents tool switching", commands.Count == before);
            Send(button, Key.Escape); check("first Escape dismisses only the color level", !colorOpen && dismissals == 1 && commands.Count == before);
            Send(button, Key.Escape); check("next Escape dismisses an unpinned tool window", dismissals == 2 && commands.Count == before);
            pinned = true; Send(button, Key.Escape); check("Escape from a pinned window reaches the application mode policy", commands.Last() == 0x1B && dismissals == 2);
            check("visibility guidance no longer promises a mode change", L.Get("Acceptance2.Welcome.ToggleVisibilityDescription", "zh-CN").Contains("不改变当前模式") && L.Get("Acceptance2.Welcome.ToggleVisibilityDescription", "en").Contains("current mode"));
            check("keyboard route fixture creates no native window", !window.IsVisible && new System.Windows.Interop.WindowInteropHelper(window).Handle == 0);
        }
        finally { window.Close(); }
    }
    private static KeyEventArgs Send(UIElement target, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, new DetachedSource(), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(args); return args;
    }
    private sealed class DetachedSource : PresentationSource
    {
        public override System.Windows.Media.Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override System.Windows.Media.CompositionTarget? GetCompositionTargetCore() => null;
    }
}
