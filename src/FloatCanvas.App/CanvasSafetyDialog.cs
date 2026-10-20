using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Documents;
using System.Globalization;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal static class CanvasSafetyDialog
{
    internal static (Window Window, Func<CanvasSafetyAction> Result) Create(Window? owner, int count, bool canRelocate)
    {
        var dialog = AppDialog.Create(owner, L.Format("Canvas.UnsafeLayerSummary", count), L.Get("Canvas.SafetyTitle"),
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, "Canvas.ApplyRelocate", "Canvas.ApplyDelete");
        AppWindowChrome.SetHelp(dialog.Window, L.Get("Canvas.SafetyHelpTitle"), L.Get("Canvas.SafetyHelpText") +
            (canRelocate ? "" : "\n\n" + L.Get("Canvas.RelocateFailed")));
        var panel = (DockPanel)dialog.Window.Content;
        var body = (TextBlock)((ScrollViewer)panel.Children[1]).Content;
        string template = L.Get("Canvas.UnsafeLayerSummary");
        int countPosition = template.IndexOf("{0}", StringComparison.Ordinal);
        var countRun = new Run(count.ToString(CultureInfo.CurrentCulture)) { FontWeight = FontWeights.Bold, FontSize = body.FontSize + 2 };
        countRun.SetResourceReference(TextElement.ForegroundProperty, "CanvasAccent");
        body.Inlines.Clear();
        body.Inlines.Add(new Run(template[..countPosition]));
        body.Inlines.Add(countRun);
        body.Inlines.Add(new Run(template[(countPosition + 3)..]));
        var buttons = (StackPanel)panel.Children[0];
        bool keepLayers = false;
        var keep = new Button { Content = L.Get("Canvas.ApplyKeep"), MinWidth = 76 };
        keep.SetResourceReference(Control.BackgroundProperty, "CanvasButton");
        keep.SetResourceReference(Control.ForegroundProperty, "CanvasText");
        keep.SetResourceReference(Control.BorderBrushProperty, "CanvasBorder");
        keep.Click += (_, _) => { keepLayers = true; SessionWindowCoordinator.Close(dialog.Window); };
        buttons.Children.Insert(2, keep);
        buttons.Orientation = Orientation.Vertical; buttons.HorizontalAlignment = HorizontalAlignment.Stretch;
        var controls = buttons.Children.OfType<Button>().ToArray();
        for (int i = 0; i < controls.Length; i++)
        {
            controls[i].HorizontalAlignment = HorizontalAlignment.Stretch;
            controls[i].Margin = new Thickness(0, i == 0 ? 0 : 8, 0, 0);
            controls[i].MinHeight = 34;
        }
        controls[0].IsEnabled = canRelocate; controls[0].IsDefault = canRelocate;
        controls[3].Content = L.Get("Canvas.CancelAdjustment"); controls[3].IsDefault = !canRelocate;
        AutomationProperties.SetAutomationId(controls[0], "RelocateUnsafeLayers");
        AutomationProperties.SetAutomationId(controls[1], "DeleteUnsafeLayers");
        AutomationProperties.SetAutomationId(controls[2], "KeepUnsafeLayers");
        AutomationProperties.SetAutomationId(controls[3], "CancelCanvasAdjustment");
        return (dialog.Window, () => keepLayers ? CanvasSafetyAction.Keep : dialog.Result() switch
        { MessageBoxResult.Yes => CanvasSafetyAction.Relocate, MessageBoxResult.No => CanvasSafetyAction.Delete, _ => CanvasSafetyAction.Cancel });
    }
}
