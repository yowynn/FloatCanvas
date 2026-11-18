using FloatCanvas.Windows;
using FloatCanvas.Core;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private bool HandleCanvasContextMenu(OverlayPointerEvent input)
    {
        if(input.Button!=OverlayMouseButton.Right || input.Kind!=OverlayPointerKind.Down) return false;
        if(_editor.InteractionTool==EditorTool.PointerPresentation || DocumentOperationBusy) return false;
        if(_globalCapture is not null) {CompleteGlobalCapture(inserted:false);return true;}
        if(_editor.HasPointerGesture || Active.IsTransactionActive || _editor.HasPendingBillboardOperation)
        { _editor.Cancel(); return true; }
        // Sampling owns the complete cancel click; restore its windows on release,
        // so the mouse-up cannot land in a picker that has just reappeared.
        if(_eyedropper is not null) return HandleEyedropperPointer(input);
        FinishInlineText(); _inspector?.CompletePendingEdit();
        _editor.SelectContextTarget(new(input.Position.X-_editor.CanvasOrigin.X,input.Position.Y-_editor.CanvasOrigin.Y));
        var menu=new OverlayContextMenu { PlacementTarget=_toolbar,Placement=PlacementMode.MousePoint };
        if (Active.Selection.Count > 1)
        {
            menu.Items.Add(new MenuItem { Header = L.Format("Shortcuts.ContextSelectionCount", Active.Selection.Count), IsEnabled = false });
            menu.Items.Add(new Separator());
        }
        foreach(var id in new[]{"Edit.Copy","Edit.Cut","Edit.Paste","Edit.Duplicate","Edit.Delete","Edit.Group","Edit.Ungroup",
            "Layer.Up","Layer.Down","Layer.Top","Layer.Bottom","Selection.Properties","View.Fit","View.Reset"})
        {
            var command=EditorCommandCatalog.All.Single(c=>c.Id==id);
            var item=new MenuItem {Header=command.Name,InputGestureText=EditorCommandCatalog.Hint(id),IsEnabled=CanExecuteEditorCommand(command)};
            item.Click+=(_,_)=>RunUiAction(()=>ExecuteEditorCommand(command));menu.Items.Add(item);
        }
        menu.IsOpen=true;return true;
    }
}
