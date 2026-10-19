using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal sealed partial class DesktopSession
{
    private async void RefreshLibraryStamp(StampEntry? entry)
    {
        if (_stampLibraryButton is not { } button || _stampLibraryGlyph is not { } glyph) return;
        bool selected=entry is not null;
        if(selected)button.Background=Brushes.Transparent;
        else button.SetResourceReference(Control.BackgroundProperty,"CanvasButton");
        if(selected)button.SetResourceReference(Control.BorderBrushProperty,"CanvasAccent");else button.BorderBrush=Brushes.Transparent;
        System.Windows.Automation.AutomationProperties.SetItemStatus(button,selected?L.Get("ToolbarV2.Selected"):"");
        if(_toolbarLibraryStamp==entry)return;
        _toolbarLibraryStamp=entry;
        glyph.Content=_stampLibraryDefaultGlyph;
        button.ToolTip=L.Get("Stamp.LibraryTitle")+" · -";
        if(entry is null)return;
        button.ToolTip=StampPreviewTooltip.Create(_stampLibrary,entry,"-");
        try
        {
            var bitmap=await StampPreviewTooltip.GetThumbnailAsync(_stampLibrary,entry);
            if(_toolbarLibraryStamp!=entry || bitmap is null)return;
            glyph.Content=new Image {Source=bitmap,Width=20,Height=20,Stretch=Stretch.Uniform};
        }
        catch(Exception ex) when(ex is IOException or FormatException or NotSupportedException or InvalidOperationException or KeyNotFoundException)
        { if(_toolbarLibraryStamp==entry)button.ToolTip=ex.Message; }
    }

    private void RefreshMascotVisibility(bool visible)
    {
        _toolbarLauncher?.SetCanvasVisible(visible);
        _passThroughLauncher?.Launcher.SetCanvasVisible(visible);
        if(_mascotCanvasVisible==visible)return;
        _mascotCanvasVisible=visible;
        if(_toolbar is not null)_toolbar.Icon=visible?AppIcon.WindowImage:AppIcon.HiddenWindowImage;
        if(_tray is not null)
        {
            var previous=_tray.Icon;
            _tray.Icon=AppIcon.CreateTrayIcon(!visible);
            previous?.Dispose();
        }
    }
}
