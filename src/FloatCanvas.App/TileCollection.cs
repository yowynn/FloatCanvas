using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FloatCanvas.App;

/// <summary>Updates a tile list without replacing its focused controls on ordinary parameter refreshes.</summary>
internal static class TileCollection
{
    internal sealed record FocusState(string Id, DependencyObject Scope, bool Keyboard);

    internal static FocusState? Capture(FrameworkElement root, IEnumerable<Button> buttons)
    {
        var scope = FocusManager.GetFocusScope(root); var logical = FocusManager.GetFocusedElement(scope);
        var focused = buttons.FirstOrDefault(button => button.IsKeyboardFocusWithin || ReferenceEquals(button, logical));
        return focused?.Tag is string id ? new(id, scope, focused.IsKeyboardFocusWithin) : null;
    }
    internal static void Restore(IEnumerable<Button> buttons, FocusState? focus, string? fallback = null)
    {
        if (focus is null) return;
        var available = buttons.ToArray();
        var target = available.FirstOrDefault(button => Equals(button.Tag, focus.Id)) ?? available.FirstOrDefault(button => Equals(button.Tag, fallback));
        if (target is null) return;
        if (focus.Keyboard && !target.IsKeyboardFocusWithin) target.Focus();
        if (!ReferenceEquals(FocusManager.GetFocusedElement(focus.Scope), target)) FocusManager.SetFocusedElement(focus.Scope, target);
    }
    internal static void Synchronize(Panel panel, IEnumerable<string> ids, Func<string, Button> create, Action<Button, string> update)
    {
        var keys = ids.ToArray(); var old = panel.Children.OfType<Button>().ToDictionary(button => (string)button.Tag, StringComparer.Ordinal);
        for (int index = 0; index < keys.Length; index++)
        {
            string id = keys[index]; var button = old.GetValueOrDefault(id) ?? create(id);
            int previousIndex = panel.Children.IndexOf(button);
            if (previousIndex != index)
            {
                if (previousIndex >= 0) panel.Children.RemoveAt(previousIndex);
                panel.Children.Insert(index, button);
            }
            update(button, id);
        }
        while (panel.Children.Count > keys.Length) panel.Children.RemoveAt(panel.Children.Count - 1);
    }
}
