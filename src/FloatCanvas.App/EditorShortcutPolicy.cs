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
    internal static readonly HashSet<Window> RecordingWindows = [];
    internal static readonly HashSet<Window> CommandWindows = [];
    internal static bool IsCommand(int key, OverlayModifiers modifiers) => EditorCommandCatalog.Resolve(key, modifiers) is not null;
    internal static bool AllowsRepeat(int key, OverlayModifiers modifiers) => EditorCommandCatalog.Resolve(key, modifiers)?.Repeat == true;
    internal static bool CanRouteProjectCommandFromText(int key, OverlayModifiers modifiers) =>
        IsProjectChordUsableFromText(new(key, modifiers)) &&
        EditorCommandCatalog.Resolve(key, modifiers)?.Id is "File.Save" or "File.SaveAs" or "File.Open" or "File.New";

    internal static bool IsProjectChordUsableFromText(CommandChord chord) =>
        (chord.Modifiers & OverlayModifiers.Control) != 0 &&
        (chord.Modifiers & (OverlayModifiers.Alt | OverlayModifiers.Windows)) == 0 &&
        chord.Key is not (0x41 or 0x43 or 0x58 or 0x56 or 0x5A or 0x59 or >= 0x21 and <= 0x28);

    internal static bool ShouldRoute(int key, ModifierKeys modifiers, DependencyObject? source,
        bool preserveListNavigation = false, bool localInput = false)
    {
        if (source is not null && Window.GetWindow(source) is {} window && (RecordingWindows.Contains(window) || CommandWindows.Contains(window))) return false;
        if (SessionKeyboardRouter.IsModal || EditorCommandCatalog.Resolve(key, (OverlayModifiers)modifiers) is not { } command) return false;
        var ancestors = Ancestors(source).ToArray();
        if (ancestors.Any(value => value is MenuItem or MenuBase or ComboBox { IsDropDownOpen: true })) return false;
        bool control = (modifiers & ModifierKeys.Control) != 0;
        if (CanRouteProjectCommandFromText(key, (OverlayModifiers)modifiers)) return true;
        if (!control && key == 0x1B)
            return !ancestors.Any(value => value is ComboBox { IsDropDownOpen: true } or Thumb { IsDragging: true });
        if (localInput || ancestors.Any(IsLocalInput)) return false;
        // Read-only text keeps selection and copying, without owning editing-mode Space/Tab.
        if (ancestors.Any(value => value is TextBoxBase { IsReadOnly: true }) &&
            (key is >= 0x21 and <= 0x28 || control && key is 0x41 or 0x43)) return false;
        if (OwnsNumericKey(key, modifiers, ancestors) && command.Id is not ("Canvas.OpacityUp" or "Canvas.OpacityDown")) return false;
        if ((preserveListNavigation || OwnsList(source)) && key is >= 0x25 and <= 0x28) return false;
        if (!control && key is 0x0D or 0x2E && ancestors.Any(IsPresetTile)) return false;
        return true;
    }

    internal static bool OwnsLocalEditingKeys(DependencyObject? source) => Ancestors(source).Any(IsLocalInput);
    internal static bool OwnsMenu(DependencyObject? source) => Ancestors(source).Any(value => value is MenuItem or MenuBase or ComboBox { IsDropDownOpen: true });
    internal static bool OwnsList(DependencyObject? source)
    {
        var ancestors = Ancestors(source).ToArray();
        if (ancestors.Any(IsPresetTile)) return true;
        if (ancestors.Any(value => value is ButtonBase or ComboBox or NumericValueField)) return false;
        return ancestors.Any(value => value is ListBox or TreeView);
    }
    private static bool IsPresetTile(DependencyObject value) => value is SelectionTileButton or ColorSwatchButton { Tag: string };
    internal static bool OwnsNumericKey(int key, ModifierKeys modifiers, DependencyObject? source) => OwnsNumericKey(key, modifiers, Ancestors(source));
    private static bool OwnsNumericKey(int key, ModifierKeys modifiers, IEnumerable<DependencyObject> ancestors) =>
        (modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) == 0 &&
        key is >= 0x21 and <= 0x28 && ancestors.Any(value => value is Slider or NumericValueField or ToolbarOpacitySlider);
    private static bool IsLocalInput(DependencyObject value) => value is UIElement { IsEnabled: true, Visibility: Visibility.Visible } &&
        value is TextBoxBase { IsReadOnly: false } or PasswordBox;
    internal static IEnumerable<DependencyObject> Ancestors(DependencyObject? source)
    {
        while (source is not null)
        {
            yield return source;
            source = source switch
            {
                ComboBoxItem item => ItemsControl.ItemsControlFromItemContainer(item),
                ContextMenu menu => menu.PlacementTarget,
                Popup popup => popup.PlacementTarget ?? popup.TemplatedParent,
                OverlayPopup popup=>popup.PlacementTarget??popup.TemplatedParent,
                _ => source is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(source) ?? LogicalTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source)
            };
        }
    }
}
