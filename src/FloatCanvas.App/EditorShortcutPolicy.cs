using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

/// <summary>Shared application commands; controls and library windows retain their own editing keys.</summary>
internal static class EditorShortcutPolicy
{
    internal static bool IsCommand(int key, OverlayModifiers modifiers)
    {
        if (key == 0x09) return modifiers == OverlayModifiers.None;
        if ((modifiers & (OverlayModifiers.Alt | OverlayModifiers.Windows)) != 0) return false;
        if ((modifiers & OverlayModifiers.Control) != 0)
            return key is 0x5A or 0x59 or 0x41 or 0x43 or 0x56 or 0x44 or 0x47 or 0x53 or 0x4F or 0x4E or 0x08 or 0x2E;
        return key is 0x1B or 0x0D or 0x08 or 0x2E or >= 0x25 and <= 0x28 or >= 0x30 and <= 0x39 ||
            ToolShortcutCatalog.TryGetTool(key, out _) || ToolShortcutCatalog.IsReserved(key);
    }

    internal static bool AllowsRepeat(int key, OverlayModifiers modifiers) =>
        (modifiers & OverlayModifiers.Control) != 0 ? key is 0x5A or 0x59 : key is 0x08 or 0x2E or >= 0x25 and <= 0x28;

    internal static bool IsProjectCommand(int key, OverlayModifiers modifiers) =>
        IsCommand(key, modifiers) && (modifiers & OverlayModifiers.Control) != 0 && key is 0x53 or 0x4F or 0x4E;

    internal static bool ShouldRoute(int key, ModifierKeys modifiers, DependencyObject? source,
        bool preserveListNavigation = false, bool localInput = false)
    {
        if (!IsCommand(key, (OverlayModifiers)modifiers)) return false;
        var ancestors = Ancestors(source).ToArray();
        if (ancestors.Any(value => value is MenuItem or MenuBase)) return false;
        bool control = (modifiers & ModifierKeys.Control) != 0;
        if (IsProjectCommand(key, (OverlayModifiers)modifiers)) return true;
        if (!control && key == 0x1B)
            return !ancestors.Any(value => value is ComboBox { IsDropDownOpen: true } or Thumb { IsDragging: true });
        if (localInput || ancestors.Any(IsLocalInput)) return false;
        if (!control && key == 0x08) return false; // Polygon capture handles this on the native canvas only.
        if (preserveListNavigation && key is >= 0x25 and <= 0x28) return false;
        if (!control && key is 0x0D or 0x2E && ancestors.Any(value => value is ToolPresetGallery or CanvasPresetGallery)) return false;
        return true;
    }

    internal static bool OwnsLocalEditingKeys(DependencyObject? source) => Ancestors(source).Any(IsLocalInput);
    internal static bool OwnsMenu(DependencyObject? source) => Ancestors(source).Any(value => value is MenuItem or MenuBase);
    private static bool IsLocalInput(DependencyObject value) => value is TextBoxBase or PasswordBox or ComboBox or Slider or Thumb;
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
                _ => source is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(source) ?? LogicalTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source)
            };
        }
    }
}
