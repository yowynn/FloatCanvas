using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace FloatCanvas.App;

/// <summary>Application commands for a separate tool window, without consuming its editors' local keys.</summary>
internal static class ToolOptionsKeyboardRoute
{
    internal static void Attach(Window window, Func<bool> dismissOneLevel, Func<bool> colorPickerOpen, Action<int> command)
    {
        AppInputPolicy.AttachToolbarToggle(window, () => command(0x09), () => !colorPickerOpen());
        window.PreviewKeyDown += (_, e) =>
        {
            var source = e.OriginalSource as DependencyObject ?? Keyboard.FocusedElement as DependencyObject;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (e.Handled || (Keyboard.Modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return;
            if (key == Key.Escape)
            {
                if (NumericValueField.TryCancelForInput(source)) { e.Handled = true; return; }
                if (Ancestors(source).Any(value => value is MenuBase or MenuItem || value is ComboBox { IsDropDownOpen: true } || value is Thumb { IsDragging: true })) return;
                e.Handled = true;
                if (!e.IsRepeat && !AppWindowChrome.TryCloseHelp(window) && !dismissOneLevel()) command(0x1B);
                return;
            }
            if (colorPickerOpen()) return;
            int virtualKey = KeyInterop.VirtualKeyFromKey(key);
            if (!ShouldRoute(virtualKey, Keyboard.Modifiers, source)) return;
            // Commands may enter a nested modal loop before returning. Consume the
            // originating key before the dialog receives keyboard focus.
            e.Handled = true;
            if (!e.IsRepeat || EditorShortcutPolicy.AllowsRepeat(virtualKey, (FloatCanvas.Windows.OverlayModifiers)Keyboard.Modifiers)) command(virtualKey);
        };
    }
    internal static bool ShouldRoute(int key, ModifierKeys modifiers, DependencyObject? source)
    {
        return EditorShortcutPolicy.ShouldRoute(key, modifiers, source);
    }
    private static IEnumerable<DependencyObject> Ancestors(DependencyObject? source)
    {
        while (source is not null)
        {
            yield return source;
            source = source switch
            {
                ComboBoxItem item => ItemsControl.ItemsControlFromItemContainer(item),
                ContextMenu menu => menu.PlacementTarget,
                OverlayPopup popup=>popup.PlacementTarget??popup.TemplatedParent,
                _ => source is Visual || source is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(source) ?? LogicalTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source)
            };
        }
    }
}
