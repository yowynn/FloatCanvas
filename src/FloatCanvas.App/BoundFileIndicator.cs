using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

/// <summary>The file entry inside the toolbar's shared file and scope strip.</summary>
internal sealed class BoundFileIndicator : Grid
{
    private readonly TextBlock label = new() { FontSize=12, FontWeight=FontWeights.SemiBold,
        TextTrimming=TextTrimming.CharacterEllipsis, VerticalAlignment=VerticalAlignment.Center, MaxWidth=300 };
    private readonly Border unsaved;
    private readonly TextBlock state;
    private readonly Button entry;
    private readonly Grid projectIcon = new() { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
    private readonly System.Windows.Shapes.Ellipse modifiedDot = new() { Width = 7, Height = 7, HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Top, StrokeThickness = 1, IsHitTestVisible = false };
    private bool compact, modified;
    private string? shownPath;
    internal event Action? DisplayChanged;
    internal BoundFileIndicator(Action revert,Action locate,Action detach,Func<bool> canRevert,Func<bool> canDetach)
    {
        HorizontalAlignment=HorizontalAlignment.Stretch;VerticalAlignment=VerticalAlignment.Top;
        Height=ToolbarUpperRow.ExtensionHeight;Visibility=Visibility.Collapsed;
        TextOptions.SetTextFormattingMode(this,TextFormattingMode.Display);
        label.FontFamily=new FontFamily("Segoe UI, Microsoft YaHei UI");
        var row=new Grid(); row.ColumnDefinitions.Add(new() { Width=GridLength.Auto }); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width=GridLength.Auto });
        projectIcon.Children.Add(CommandGlyphs.Create(CommandGlyphs.Project));
        modifiedDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,"CanvasFileModified");
        modifiedDot.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"CanvasSurface"); projectIcon.Children.Add(modifiedDot);
        projectIcon.Margin=new(0,0,6,0); row.Children.Add(projectIcon);
        Grid.SetColumn(label,1); row.Children.Add(label);
        var status=new StackPanel {Orientation=Orientation.Horizontal};
        var dot=new System.Windows.Shapes.Ellipse {Width=4,Height=4,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,4,0)};
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,"CanvasHiddenText");status.Children.Add(dot);
        state=new TextBlock {Text=L.Get("Project.Unsaved"),FontSize=10,FontWeight=FontWeights.Medium};
        state.SetResourceReference(TextBlock.ForegroundProperty,"CanvasHiddenText");status.Children.Add(state);
        unsaved=new Border {Child=status,CornerRadius=new CornerRadius(5),Padding=new Thickness(5,2,5,2),
            Margin=new Thickness(9,0,0,0),VerticalAlignment=VerticalAlignment.Center,Visibility=Visibility.Collapsed};
        unsaved.SetResourceReference(Border.BackgroundProperty,"CanvasHiddenSurface");Grid.SetColumn(unsaved,2);row.Children.Add(unsaved);
        entry=new Button { Content=row, Height=ToolbarUpperRow.ButtonSize, Margin=new(2,3,2,4), VerticalAlignment=VerticalAlignment.Top, HorizontalContentAlignment=HorizontalAlignment.Stretch };
        entry.SetResourceReference(StyleProperty,"CanvasToolbarInlineButton");
        label.SetResourceReference(TextBlock.ForegroundProperty,"CanvasText");Children.Add(entry);
        var menu=new OverlayContextMenu();
        var restore=new MenuItem {Header=L.Get("Project.Revert"),Icon=ActionIconButton.Glyph(PanelActionIcon.Refresh)};
        var unbind=new MenuItem {Header=L.Get("Project.Detach"),Icon=ActionIconButton.Glyph(PanelActionIcon.Unpin)};
        var reveal=new MenuItem {Header=L.Get("Project.Locate"),Icon=ActionIconButton.Glyph(PanelActionIcon.Source)};
        restore.Click+=(_,_)=>revert();unbind.Click+=(_,_)=>detach();reveal.Click+=(_,_)=>locate();
        menu.Items.Add(restore);menu.Items.Add(reveal);menu.Items.Add(new Separator());menu.Items.Add(unbind);
        menu.Opened+=(_,_)=>{restore.IsEnabled=canRevert();unbind.IsEnabled=canDetach();};ContextMenu=menu;
        entry.Click+=(_,_)=>{menu.PlacementTarget=entry;menu.IsOpen=true;};
    }
    internal void SetCompact(bool value)
    {
        compact=value; Width=value?ToolbarUpperRow.ButtonSlot:double.NaN;
        label.Visibility=state.Visibility=value?Visibility.Collapsed:Visibility.Visible;
        projectIcon.Margin=value?new Thickness(0):new Thickness(0,0,6,0);
        UpdateModificationIndicators();
    }
    private void UpdateModificationIndicators()
    {
        unsaved.Visibility=modified&&!compact?Visibility.Visible:Visibility.Collapsed;
        modifiedDot.Visibility=modified&&compact?Visibility.Visible:Visibility.Collapsed;
    }
    protected override Size MeasureOverride(Size constraint)
    {
        unsaved.Measure(new Size(double.PositiveInfinity, constraint.Height));
        // Reserve icon, padding and save status before trimming the filename.
        double width = double.IsFinite(constraint.Width) ? Math.Max(0, constraint.Width - 40 - unsaved.DesiredSize.Width) : 300;
        label.MaxWidth = Math.Min(300, width);
        return base.MeasureOverride(constraint);
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
        state.Text=L.Get("Project.Unsaved");
        modified=dirty; UpdateModificationIndicators();ToolTip=path;
        System.Windows.Automation.AutomationProperties.SetName(this,path is null?"":Path.GetFileName(path));
        System.Windows.Automation.AutomationProperties.SetItemStatus(this,dirty?L.Get("Project.Unsaved"):"");
        System.Windows.Automation.AutomationProperties.SetName(entry,path is null?"":Path.GetFileName(path));
        System.Windows.Automation.AutomationProperties.SetItemStatus(entry,dirty?L.Get("Project.Unsaved"):"");
        DisplayChanged?.Invoke();
    }
}
