using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private bool CanExecuteUiCommand(string command)
    {
        if (DocumentOperationBusy || Active.IsTransactionActive) return false;
        return command switch
        {
            "undo" => Active.CanUndo,
            "redo" => Active.CanRedo,
            // Copy also exports the complete canvas when nothing is selected.
            "copy" => true,
            "delete" or "duplicate" => Active.SelectedElements().Any(element => !element.Locked),
            "group" => Active.SelectedElements().Count(element => !element.Locked) >= 2,
            "ungroup" => Active.Document.Groups.Any(group => Active.Document.GroupElements(group.Id).All(element => Active.Selection.Contains(element.Id))),
            "replace-image" => Active.SelectedElements().Count(element => element.Kind == ElementKind.Image && !element.Locked) == 1,
            "save-stamp" => Active.SelectedElements().Any(),
            _ => true
        };
    }

    private string UiCommandDisabledHint(string command)
    {
        if (CanExecuteUiCommand(command)) return "";
        if (DocumentOperationBusy || Active.IsTransactionActive) return L.Get("Audit.CommandBusy");
        return L.Get(command switch
        {
            "undo" => "Audit.NoUndo", "redo" => "Audit.NoRedo",
            "group" => "Audit.GroupRequirement", "ungroup" => "Audit.UngroupRequirement",
            "replace-image" => "Audit.ImageRequirement", "save-stamp" => "Audit.SelectionRequirement",
            _ => "Audit.EditableSelectionRequirement"
        });
    }
}
