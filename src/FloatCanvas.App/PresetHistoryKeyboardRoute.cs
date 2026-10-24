using System.Windows;
using System.Windows.Input;

namespace FloatCanvas.App;

/// <summary>History keys from detached color popups, preserving local text and control editing.</summary>
internal static class PresetHistoryKeyboardRoute
{
    internal static bool ShouldRoute(Key key, ModifierKeys modifiers, DependencyObject? source) =>
        (key == Key.Z && (modifiers == ModifierKeys.Control || modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) ||
         key == Key.Y && modifiers == ModifierKeys.Control) &&
        EditorShortcutPolicy.ShouldRoute(KeyInterop.VirtualKeyFromKey(key), modifiers, source);

    internal static void Handle(KeyEventArgs args, ModifierKeys modifiers, Action<int> command)
    {
        var key = args.Key == Key.System ? args.SystemKey : args.Key;
        if (args.Handled || !ShouldRoute(key, modifiers, args.OriginalSource as DependencyObject)) return;
        // A command can open a dialog: consume the source event before entering its nested input loop.
        args.Handled = true;
        command(KeyInterop.VirtualKeyFromKey(key));
    }
}
