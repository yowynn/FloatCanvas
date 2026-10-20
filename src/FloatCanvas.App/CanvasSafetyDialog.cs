using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal static class CanvasSafetyDialog
{
    internal static (Window Window, Func<CanvasSafetyAction> Result) Create(Window? owner, int count, bool canRelocate)
    {
        var dialog = AppDialog.Create(owner, L.Format("Canvas.UnsafeLayers", count) +
            (canRelocate ? "" : "\n\n" + L.Get("Canvas.RelocateFailed")), L.Get("Canvas.SafetyTitle"),
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, "Canvas.ApplyRelocate", "Canvas.ApplyDelete");
        var panel = (DockPanel)dialog.Window.Content;
        var buttons = (StackPanel)panel.Children[0];
        buttons.Orientation = Orientation.Vertical; buttons.HorizontalAlignment = HorizontalAlignment.Stretch;
        var controls = buttons.Children.OfType<Button>().ToArray();
        for (int i = 0; i < controls.Length; i++)
        {
            controls[i].HorizontalAlignment = HorizontalAlignment.Stretch;
            controls[i].Margin = new Thickness(0, i == 0 ? 0 : 8, 0, 0);
            controls[i].MinHeight = 34;
        }
        controls[0].IsEnabled = canRelocate; controls[0].IsDefault = canRelocate;
        controls[2].Content = L.Get("Canvas.CancelAdjustment"); controls[2].IsDefault = !canRelocate;
        AutomationProperties.SetAutomationId(controls[0], "RelocateUnsafeLayers");
        AutomationProperties.SetAutomationId(controls[1], "DeleteUnsafeLayers");
        AutomationProperties.SetAutomationId(controls[2], "CancelCanvasAdjustment");
        return (dialog.Window, () => dialog.Result() switch
        { MessageBoxResult.Yes => CanvasSafetyAction.Relocate, MessageBoxResult.No => CanvasSafetyAction.Delete, _ => CanvasSafetyAction.Cancel });
    }
}
