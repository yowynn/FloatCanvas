using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>A filename speech bubble in the space above the expanded toolbar.</summary>
internal sealed class BoundFileIndicator : Grid
{
    private const double TextInset=15;
    private readonly TextBlock label = new() { FontSize=12, FontWeight=FontWeights.SemiBold,
        TextTrimming=TextTrimming.CharacterEllipsis, VerticalAlignment=VerticalAlignment.Center, MaxWidth=300 };
    private readonly Border unsaved;
    private string? shownPath;
    internal BoundFileIndicator(Action detach,Action locate,Func<bool> canDetach)
    {
        HorizontalAlignment=HorizontalAlignment.Left;VerticalAlignment=VerticalAlignment.Top;
        Margin=new Thickness(94,5,10,0);Height=28;Visibility=Visibility.Collapsed;
        TextOptions.SetTextFormattingMode(this,TextFormattingMode.Display);
        label.FontFamily=new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
        var tail=new System.Windows.Shapes.Path {Data=Geometry.Parse("M8 17 Q5 20 0 21 Q5 23 11 20 Z")};
        tail.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,"CanvasFileBubble");Children.Add(tail);
        var row=new StackPanel {Orientation=Orientation.Horizontal};row.Children.Add(label);
        var status=new StackPanel {Orientation=Orientation.Horizontal};
        var dot=new System.Windows.Shapes.Ellipse {Width=4,Height=4,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,4,0)};
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,"CanvasHiddenText");status.Children.Add(dot);
        var state=new TextBlock {Text=L.Get("Project.Unsaved"),FontSize=10,FontWeight=FontWeights.Medium};
        state.SetResourceReference(TextBlock.ForegroundProperty,"CanvasHiddenText");status.Children.Add(state);
        unsaved=new Border {Child=status,CornerRadius=new CornerRadius(5),Padding=new Thickness(5,2,5,2),
            Margin=new Thickness(9,0,0,0),VerticalAlignment=VerticalAlignment.Center,Visibility=Visibility.Collapsed};
        unsaved.SetResourceReference(Border.BackgroundProperty,"CanvasHiddenSurface");row.Children.Add(unsaved);
        var body=new Border {Child=row,Margin=new Thickness(5,0,0,0),Padding=new Thickness(10,4,10,4),CornerRadius=new CornerRadius(9)};
        body.SetResourceReference(Border.BackgroundProperty,"CanvasFileBubble");
        label.SetResourceReference(TextBlock.ForegroundProperty,"CanvasText");Children.Add(body);
        Effect=new System.Windows.Media.Effects.DropShadowEffect {BlurRadius=5,ShadowDepth=1,Opacity=.12};
        var menu=new ContextMenu();
        var unbind=new MenuItem {Header=L.Get("Project.Detach"),Icon=ActionIconButton.Glyph(PanelActionIcon.Unpin)};
        var reveal=new MenuItem {Header=L.Get("Project.Locate"),Icon=ActionIconButton.Glyph(PanelActionIcon.Source)};
        unbind.Click+=(_,_)=>detach();reveal.Click+=(_,_)=>locate();
        menu.Items.Add(unbind);menu.Items.Add(reveal);
        menu.Opened+=(_,_)=>unbind.IsEnabled=canDetach();ContextMenu=menu;
    }
    internal void AlignTextLeft(double x)
    {
        var aligned=new Thickness(Math.Max(0,x-TextInset),5,10,0);
        if(Margin!=aligned)Margin=aligned;
    }
    internal void Refresh(string? path,bool dirty,bool expanded)
    {
        Visibility=path is not null&&expanded?Visibility.Visible:Visibility.Collapsed;
        if(shownPath!=path)
        {
            shownPath=path;label.Inlines.Clear();
            if(path is not null)
            {
                label.Inlines.Add(new Run(Path.GetFileNameWithoutExtension(path)));
                var extension=new Run(Path.GetExtension(path)) {FontSize=11,FontWeight=FontWeights.Normal};
                extension.SetResourceReference(TextElement.ForegroundProperty,"CanvasMuted");label.Inlines.Add(extension);
            }
        }
        unsaved.Visibility=dirty?Visibility.Visible:Visibility.Collapsed;ToolTip=path;
        System.Windows.Automation.AutomationProperties.SetName(this,path is null?"":Path.GetFileName(path));
        System.Windows.Automation.AutomationProperties.SetItemStatus(this,dirty?L.Get("Project.Unsaved"):"");
    }
}
