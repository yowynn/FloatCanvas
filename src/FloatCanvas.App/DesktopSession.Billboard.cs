using System.Windows.Automation;
using System.Windows.Media;
using FloatCanvas.Localization;
using Controls=System.Windows.Controls;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private bool? _billboardGlyphMode;
    private void ToggleBillboardMode()
    {
        if(DocumentOperationBusy||!Active.Document.Canvas.Projection.Enabled)return;
        EndCanvasCalibration();
        _editor.BillboardMode=!_editor.BillboardMode;
        RefreshBillboardState();RequestRender();
    }

    private void RefreshBillboardState()
    {
        bool perspective=Active.Document.Canvas.Projection.Enabled;
        if(!perspective&&_editor.BillboardMode)_editor.BillboardMode=false;
        if(_billboardButton is not { } button)return;
        bool billboard=_editor.BillboardMode;
        if(_billboardGlyphMode!=billboard&&button.Content is Controls.Viewbox {Child:Controls.Canvas icon}&&
            icon.Children.OfType<System.Windows.Shapes.Path>().FirstOrDefault() is {} glyph)
        {
            glyph.Data=Geometry.Parse(CommandGlyphs.Placement(billboard));
            _billboardGlyphMode=billboard;
        }
        button.IsEnabled=perspective&&!DocumentOperationBusy;
        ActionIconButton.UpdateToggleAppearance(button,billboard);
        button.ToolTip=AppText.Get(perspective?"Billboard.ToggleHint":"Billboard.RequiresPerspective");
        AutomationProperties.SetName(button,AppText.Get("Billboard.Toggle"));
        AutomationProperties.SetItemStatus(button,AppText.Get(billboard?"Billboard.Name":"Billboard.Surface"));
    }
}
