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
        string title=AppText.Get("Billboard.Toggle");
        string mode=AppText.Get(billboard?"Billboard.BillboardMode":"Billboard.DefaultMode");
        string tooltip=PlacementTooltip(billboard);
        button.ToolTip=tooltip;
        AutomationProperties.SetName(button,title);
        AutomationProperties.SetItemStatus(button,mode);
        if(_toolbarMenu is {} menu)
            foreach(var item in menu.Items.OfType<Controls.MenuItem>().SelectMany(group=>group.Items.OfType<Controls.MenuItem>()).Where(item=>Equals(item.Tag,"billboard")))
            {
                item.Header=title;item.ToolTip=tooltip;
                Controls.ToolTipService.SetShowOnDisabled(item,true);
                AutomationProperties.SetName(item,title);AutomationProperties.SetItemStatus(item,mode);
            }
    }

    internal static string PlacementTooltip(bool billboard)=>string.Join("\n",
        AppText.Get("Billboard.Toggle"),
        AppText.Format("Billboard.CurrentMode",AppText.Get(billboard?"Billboard.BillboardMode":"Billboard.DefaultMode")));
}
