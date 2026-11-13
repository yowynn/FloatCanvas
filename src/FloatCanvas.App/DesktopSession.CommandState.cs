using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private bool CanExecuteUiCommand(string command)
    {
        if (DocumentOperationBusy || Active.IsTransactionActive) return false;
        if (Active.IsReadOnly && command != "copy") return false;
        if (command is "delete" or "duplicate" or "group" or "ungroup")
        {
            var graph = new SceneGraph(Active.Document);
            var nodes = graph.NormalizeSelection(Active.Selection).Where(id => !graph.IsLocked(id)).ToArray();
            return command switch
            {
                "group" => Active.CanInsertInEditScope && nodes.Length > 0,
                "duplicate" => nodes.Length > 0 && Active.CanInsertInEditScope,
                "ungroup" => nodes.Any(id => graph.FindGroup(id) is not null) && !nodes.Any(id => graph.FindGroup(id)?.Billboard is not null),
                _ => nodes.Length > 0
            };
        }
        return command switch
        {
            "undo" => Active.CanUndo,
            "redo" => Active.CanRedo,
            // Copy also exports the complete canvas when nothing is selected.
            "copy" => true,
            "replace-image" => ImageToolEligibility.TryGetReplaceTarget(Active, out _),
            "save-stamp" => Active.SelectedElements().Any() || Active.Selection.Any(id => Active.Document.Instances.Any(instance => instance.Id == id)),
            _ => true
        };
    }

    private string UiCommandDisabledHint(string command)
    {
        if (CanExecuteUiCommand(command)) return "";
        if (DocumentOperationBusy || Active.IsTransactionActive) return L.Get("Audit.CommandBusy");
        if (Active.IsReadOnly) return L.Get("GroupCanvas.ReadOnly");
        if (command == "ungroup" && Active.Selection.Any(id => SceneGraph.Read(Active.Document).FindGroup(id)?.Billboard is not null))
            return L.Get("Billboard.UngroupFirstFlat");
        return L.Get(command switch
        {
            "undo" => "Audit.NoUndo", "redo" => "Audit.NoRedo",
            "group" => "Audit.GroupRequirement", "ungroup" => "Audit.UngroupRequirement",
            "replace-image" => "Audit.ImageRequirement", "save-stamp" => "Audit.SelectionRequirement",
            _ => "Audit.EditableSelectionRequirement"
        });
    }
}
