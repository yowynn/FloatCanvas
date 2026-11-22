using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>Edits one emoji. Incomplete composition or invalid input stays in the field until corrected.</summary>
internal sealed class EmojiField : StackPanel
{
    private readonly TextBox input = new() { FontFamily = new("Segoe UI Emoji"), FontSize = 18, MaxLength = 128,
        VerticalContentAlignment = VerticalAlignment.Center, Padding = new(8,0,8,0) };
    private readonly Button choose = new() { Content=L.Get("Emoji.Choose"), MinHeight=PropertyPanelLayout.ControlHeight, Padding=new(9,0,9,0) };
    private readonly EmojiThumbnail image = new() { Width=22, Height=22, HorizontalAlignment=HorizontalAlignment.Left };
    private readonly TextBlock mixed = new() { Text=L.Get("Emoji.Mixed"),VerticalAlignment=VerticalAlignment.Center };
    private readonly Border readout = new() { Margin=new(2),CornerRadius=new(4),Padding=new(7,0,0,0),IsHitTestVisible=false };
    private readonly TextBlock error = new() { Text=L.Get("Emoji.Invalid"),Visibility=Visibility.Collapsed,TextWrapping=TextWrapping.Wrap,Margin=new(0,3,0,0) };
    private PopupHandle? popup;
    private EmojiPickerPanel? picker;
    private string? value;
    private string fontId=EmojiFontIds.Default;
    private bool synchronizing, draft;
    internal bool IsOpen => popup?.IsOpen == true;
    internal string? Value => value;
    internal string FontId => fontId;
    internal event Action<string>? Committed;
    internal event Action? Closed;

    internal EmojiField(bool showButton = true)
    {
        var row = new Grid { Height=PropertyPanelLayout.ControlHeight };
        row.ColumnDefinitions.Add(new());row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var face=new Grid();face.Children.Add(input);
        readout.SetResourceReference(Border.BackgroundProperty,"CanvasInput");
        mixed.SetResourceReference(TextBlock.ForegroundProperty,"CanvasMuted");
        readout.Child=new Grid { Children={image,mixed} };face.Children.Add(readout);row.Children.Add(face);
        choose.Margin=new(6,0,0,0);choose.Visibility=showButton?Visibility.Visible:Visibility.Collapsed;
        Grid.SetColumn(choose,1);row.Children.Add(choose);Children.Add(row);
        error.SetResourceReference(TextBlock.ForegroundProperty,"CanvasErrorText");Children.Add(error);
        AutomationProperties.SetName(input,L.Get("Emoji.Input"));AutomationProperties.SetName(choose,L.Get("Emoji.Choose"));
        choose.Click+=(_,_)=>{
            if(IsOpen)Close();else Open();
        };
        input.GotKeyboardFocus+=(_,_)=>{input.SelectAll();RefreshReadout();};
        input.LostKeyboardFocus+=(_,_)=>{if(draft)CommitInput();RefreshReadout();};
        input.TextChanged+=(_,_)=>{if(!synchronizing){draft=true;SetError(false);RefreshReadout();}};
        input.PreviewKeyDown+=(_,e)=>
        {
            if(e.Key==Key.Enter){e.Handled=true;CommitInput();}
            else if(e.Key==Key.Escape && draft){e.Handled=true;ResetDraft();}
        };
        Unloaded+=(_,_)=>ResetContext();
        IsVisibleChanged+=(_,_)=>{if(!IsVisible)ResetContext();};
        SetModel(null, true);
    }

    internal void SetModel(string? glyph, bool force=false)
    {
        if (!force && value==glyph) return;
        value=glyph;ResetDraft();
    }
    internal void SetFontId(string? id)
    {
        string next=EmojiFontIds.Normalize(id);
        if(fontId==next)return;
        fontId=next;image.SetFont(fontId);picker?.Begin(value,fontId);
        RefreshReadout();
    }
    internal void ResetContext(){Close();ResetDraft();}
    private void ResetDraft()
    {
        synchronizing=true;
        try { input.Text=value ?? ""; }
        finally { synchronizing=false; }
        draft=false;SetError(false);RefreshReadout();
    }
    private void SetError(bool invalid)
    {
        error.Visibility=invalid?Visibility.Visible:Visibility.Collapsed;
        AutomationProperties.SetHelpText(input,invalid?error.Text:"");
        if(invalid)input.SetResourceReference(Control.BorderBrushProperty,"CanvasErrorText");
        else input.ClearValue(Control.BorderBrushProperty);
    }
    private void RefreshReadout()
    {
        readout.Visibility=!draft && !input.IsKeyboardFocusWithin?Visibility.Visible:Visibility.Collapsed;
        mixed.Visibility=value is null?Visibility.Visible:Visibility.Collapsed;
        EmojiCatalog.TryFind(value,out var entry);
        image.SetFont(fontId);
        if (!ReferenceEquals(image.DataContext,entry)) image.DataContext=entry;
    }
    private void CommitInput()
    {
        if (!draft) return;
        if (EmojiCatalog.TryFind(input.Text,out var entry)) Commit(entry);
        else SetError(true);
    }
    private void Commit(EmojiEntry entry, bool remember = true)
    {
        bool changed=value!=entry.Glyph;
        SetModel(entry.Glyph,true);
        if(remember)EmojiPickerPanel.Remember(entry);
        if(changed)Committed?.Invoke(entry.Glyph);
    }
    internal void Open()
    {
        if (!IsEnabled || !IsVisible || IsOpen) return;
        if (popup is null)
        {
            picker=new EmojiPickerPanel();
            var host=new Popup { PlacementTarget=this, Placement=PlacementMode.Bottom, StaysOpen=false, VerticalOffset=4, Child=picker };
            popup=new PopupHandle(host,L.Get("Emoji.Choose"),compact:true) { TargetPressToggles=true };
            PopupChildDialog.Attach(picker,popup.Key);
            SessionWindowCoordinator? session=null;
            popup.OnOpened((_,_)=>{
                session=SessionWindowCoordinator.Find(Window.GetWindow(this));session?.RegisterPopup(popup.Key,Close);
                picker.FocusSearch();
            });
            popup.OnClosed((_,_)=>{session?.UnregisterPopup(popup.Key);session=null;Closed?.Invoke();});
            picker.Picked+=entry=>{Commit(entry,remember:false);Close();};
            picker.CloseRequested+=Close;
        }
        picker!.Begin(value,fontId);popup.IsOpen=true;
    }
    internal void Close(){if(IsOpen)popup!.IsOpen=false;}
    internal bool TryClose(){if(!IsOpen)return false;Close();return true;}
}
