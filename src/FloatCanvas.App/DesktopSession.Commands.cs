using FloatCanvas.Core;
using FloatCanvas.Windows;
using System.Windows;
using System.Windows.Input;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private OverlayModifiers? _commandModifiers;
    private string? ValidateCommandBindings(Dictionary<string,CommandChord[]> draft)
    {
        if(EditorCommandCatalog.Validate(draft) is {} error) return error;
        var globals=GlobalBindings(_settings.Toggle,_settings.Hide,_settings.GlobalCommandBindings).Where(p=>p.Value.VirtualKey!=0).ToArray();
        foreach(var command in EditorCommandCatalog.All)
            foreach(var key in draft.GetValueOrDefault(command.Id,command.Defaults))
                if(globals.FirstOrDefault(g=>g.Value.VirtualKey==key.Key && g.Value.Modifiers==(uint)key.Modifiers) is var match && match.Key is not null)
                    return key+": "+command.Name+" / "+match.Key;
        return null;
    }

    private void OnCommandInput(OverlayKeyEvent input)
    {
        var command = EditorCommandCatalog.Resolve(input.VirtualKey, input.Modifiers);
        if (input.IsRepeat && command?.Repeat != true) return;
        if (command?.Tool is not null)
        {
            FinishKeyboardNudge();
            if (HandleCanvasSampleKey(input.VirtualKey, input.Modifiers)) return;
            if (!input.IsRepeat) BeginBorrowedTool(command,input.VirtualKey);
            return;
        }
        _commandModifiers = input.Modifiers;
        try { OnKey(input.VirtualKey); }
        finally { _commandModifiers = null; }
    }

    private bool CanExecuteEditorCommand(EditorCommand command)
    {
        if (command.Id is "Interaction.Pan" or "Capture.Back" || DocumentOperationBusy || command.Mutates && Active.IsReadOnly) return false;
        if (command.Id is "Canvas.OpacityUp" or "Canvas.OpacityDown") return CanChangeCanvasOpacity;
        if (command.Id == "Panel.Tools") return HasCurrentToolOptions;
        if (command.Id=="Edit.Undo") return Active.CanUndo;
        if (command.Id=="Edit.Redo") return Active.CanRedo;
        if (command.Tool == EditorTool.Crop && !_editor.CanCropSelection) return false;
        if (command.Tool is {} tool && EditorController.IsCreationTool(tool) && (Active.IsReadOnly || !Active.CanInsertInEditScope)) return false;
        LayerOperation? operation=command.Id switch
        {
            "Edit.Cut" or "Edit.Delete"=>LayerOperation.Delete, "Edit.Duplicate"=>LayerOperation.Duplicate,
            "Edit.Group"=>LayerOperation.Group, "Edit.Ungroup"=>LayerOperation.Ungroup,
            "Layer.Up" or "Layer.Down" or "Layer.Top" or "Layer.Bottom"=>LayerOperation.Reorder, _=>null
        };
        if (operation is {} op) return Active.AnalyzeLayerOperation(op).CanExecute;
        if (command.Id == "Selection.Rename") return _host.IsVisible && Active.Selection.Count == 1;
        if (command.Id is "Edit.Copy" or "Selection.Properties" or "View.Selection" or "Stamp.Create" || command.Id.StartsWith("Move.",StringComparison.Ordinal))
            return _host.IsVisible && Active.Selection.Count>0;
        return true;
    }

    private string EditorCommandDisabledReason(EditorCommand command)
    {
        if(CanExecuteEditorCommand(command)) return "";
        if(DocumentOperationBusy || Active.IsTransactionActive) return L.Get("Audit.CommandBusy");
        if(Active.IsReadOnly) return L.Get("GroupCanvas.ReadOnly");
        if (command.Id == "Panel.Tools" && !HasCurrentToolOptions) return L.Get("Shortcuts.StampPlacementNoOptions");
        if (command.Id is "Canvas.OpacityUp" or "Canvas.OpacityDown")
            return L.Get(CanEditDisplayedCanvas ? "LayerExperience.LockedBrowse" : "GroupCanvas.EditPreviewInIndependent");
        if(command.Id is "Interaction.Pan" or "Capture.Back") return L.Get("Shortcuts.GestureOnly");
        if(command.Tool==EditorTool.Crop) return L.Get("Audit.ImageRequirement");
        if(command.Tool is not null) return L.Get("Inspector.LockedSuffix");
        return command.Id switch
        {
            "Edit.Undo"=>L.Get("Audit.NoUndo"), "Edit.Redo"=>L.Get("Audit.NoRedo"),
            "Edit.Ungroup" when Active.Selection.Any(id=>SceneGraph.Read(Active.Document).FindGroup(id)?.Billboard is not null)=>L.Get("Billboard.UngroupFirstFlat"),
            "Edit.Ungroup"=>L.Get("Audit.UngroupRequirement"),
            "Edit.Group"=>L.Get("Audit.GroupRequirement"),
            _=>L.Get(Active.Selection.Count==0 ? "Audit.SelectionRequirement" : "Audit.EditableSelectionRequirement")
        };
    }

    private void ExecuteEditorCommand(EditorCommand command)
    {
        if (!CanExecuteEditorCommand(command)) return;
        string id = command.Id;
        if(id.StartsWith("File.",StringComparison.Ordinal) || id.StartsWith("Edit.",StringComparison.Ordinal) || id=="Stamp.Create")
            CompleteGlobalCapture(inserted:false,restoreMode:false);
        if (command.Tool is { } tool) { SelectTool(tool); return; }
        if (id.StartsWith("Stamp.Slot", StringComparison.Ordinal) && int.TryParse(id[10..], out int slot))
        { SelectStampSlot(slot); return; }
        if (id.StartsWith("Move.", StringComparison.Ordinal))
        {
            _editor.Cancel();
            var step = SelectionMoveStep(id);
            RunSafeSelectionCommand(() => _editor.TransformSelectionInCanvas(Affine2D.Translation(step.X, step.Y)));
            return;
        }
        switch (id)
        {
            case "Interaction.Cancel":
                if (_globalCapture is not null) CompleteGlobalCapture(inserted:false);
                else if (CancelEyedropper()) { }
                else if (_toolOptionsPanel.TryClosePicker()) { }
                else if (Active.IsTransactionActive || _editor.HasPointerGesture || _editor.HasPendingBillboardOperation || _editor.IsBorrowingSelection) _editor.Cancel();
                else if (_borrowedTool is not null) CancelBorrowedTool();
                else if (IsToolOptionsOpen && !_toolOptionsPinned && _toolOptionsWindow.IsKeyboardFocusWithin) CloseToolOptions();
                else if (Active.Selection.Count > 0) Active.SetSelection([]);
                else if (Active.EditScopeId is not null) { _editor.Cancel(); Active.LeaveScope(); }
                else if (!TryHandlePrefabEscape()) SetEditing(false);
                return;
            case "Panel.Tools": OpenCurrentToolOptions(); return;
            case "Panel.Layers": OpenLayers(); return;
            case "Panel.Settings": ShowHotkeySettings(); return;
            case "Commands.Search": ShowCommandSearch(); return;
            case "UI.ToggleToolbar": ToggleToolbarExpanded(); return;
            case "Selection.Rename":
                if (_layers is not { IsVisible: true }) OpenLayers();
                if (Active.Selection.Count == 1) _layers?.BeginRename(Active.Selection.First()); return;
            case "Selection.Properties":
                if (Active.Selection.Count > 0) { var p = System.Windows.Forms.Cursor.Position; ShowInspectorNear(new(p.X-_editor.CanvasOrigin.X,p.Y-_editor.CanvasOrigin.Y)); } return;
            case "Tools.More": OpenMoreTools(); return;
            case "Stamp.Library": ShowStampLibrary(); return;
            case "Stamp.Create": SaveStampSelection(); return;
            case "Image.Import": _ = RunCommand(() => ImportImagesAsync()); return;
            case "View.Fit": ExecuteCanvasViewing(CanvasViewingCommand.FitContent); return;
            case "View.Actual": ExecuteCanvasViewing(CanvasViewingCommand.OriginalSize); return;
            case "View.Reset": ExecuteCanvasViewing(CanvasViewingCommand.Reset); return;
            case "View.Selection": FitSelectedContent(); return;
        }
        if (id.StartsWith("File.", StringComparison.Ordinal) || command.Mutates || id is "Edit.SelectAll" or "Edit.Deselect" or "Edit.Copy")
        { EndCanvasCalibration(); _editor.Cancel(); }
        switch (id)
        {
            case "Edit.Undo": Undo(); break;
            case "Edit.Redo": Redo(); break;
            case "Edit.SelectAll": Active.SelectAll(); break;
            case "Edit.Deselect": Active.SetSelection([]); break;
            case "Edit.Copy": CopySelection(); break;
            case "Edit.Cut": _ = CopySelectionAsync(cut: true); break;
            case "Edit.Paste": _ = RunCommand(PasteAsync); break;
            case "Edit.Duplicate": if (CanAddCanvasContent()) RunSafeSelectionCommand(() => Active.DuplicateSelection()); break;
            case "Edit.Group": Active.GroupSelection(); break;
            case "Edit.Ungroup": Active.UngroupSelection(); break;
            case "Edit.Delete": Active.DeleteSelection(); break;
            case "Edit.Clear": _ = RunCommand(ClearCurrentCanvasAsync); break;
            case "File.Save": _ = RunCommand(() => SaveProjectAsync()); break;
            case "File.SaveAs": _ = RunCommand(() => SaveProjectAsync(true)); break;
            case "File.Open": _ = RunCommand(OpenProjectAsync); break;
            case "File.New": _ = StartNewCanvasAsync(); break;
            case "Layer.Down": Active.ReorderSelection(LayerOrder.Backward); break;
            case "Layer.Up": Active.ReorderSelection(LayerOrder.Forward); break;
            case "Layer.Bottom": Active.ReorderSelection(LayerOrder.Back); break;
            case "Layer.Top": Active.ReorderSelection(LayerOrder.Front); break;
            case "Canvas.OpacityUp": case "Canvas.OpacityDown":
                ChangeCurrentCanvasOpacity(Math.Clamp(Math.Round(CurrentCanvasLayerOpacity + (id.EndsWith("Up",StringComparison.Ordinal) ? .05 : -.05),10),0,1)); break;
        }
        SynchronizeCanvasTool();
    }

    private void FitSelectedContent()
    {
        if (_editor.HasPointerGesture || Active.IsTransactionActive) return;
        if (_editor.SelectionViewingBounds is not {} screenBounds) return;
        var bounds = _canvasView.Matrix.Inverse().TransformBounds(screenBounds);
        if (!bounds.IsEmpty) ChangeCanvasViewing(CanvasViewTransform.Fit(bounds,_canvasViewViewport,allowEnlarge:true) with {PlaneOffset=_canvasView.PlaneOffset});
    }

}
