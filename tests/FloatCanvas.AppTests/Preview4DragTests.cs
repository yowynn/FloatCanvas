using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatCanvas.App;

internal static class Preview4DragTests
{
    internal static void Run(Action<string,bool> check)
    {
        _=Application.Current??new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};AppTheme.Apply("light");
        var gallery=new PixelTilePanel(3,36,4);
        foreach(var color in new[] {Colors.Red,Colors.Green,Colors.Blue})
        {
            var button=new Button {Background=new SolidColorBrush(color),Template=(ControlTemplate)System.Windows.Markup.XamlReader.Parse("<ControlTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"Button\"><Border Background=\"{TemplateBinding Background}\"/></ControlTemplate>")};
            gallery.Children.Add(button);
        }
        var root=new AdornerDecorator {Child=gallery};Layout(root,260,210);
        var source=(Button)gallery.Children[2];var start=source.TranslatePoint(new(18,18),gallery);
        using(var feedback=TileDragFeedback.Create(source,gallery,start))
        {
            feedback!.Move(new(180,140));Layout(root,260,210);
            var pixels=new RenderTargetBitmap(260,210,96,96,PixelFormats.Pbgra32);pixels.Render(root);
            byte[] pixel=new byte[4];pixels.CopyPixels(new Int32Rect(180,140,1,1),pixel,4,0);
            check("a dragged non-first tile produces visible ghost pixels at the pointer",pixel[3]>0&&pixel[0]>pixel[2]);
        }
        var first=(Button)gallery.Children[0];var second=(Button)gallery.Children[1];
        var left=TileDragReorder.ResolveDrop(gallery,first.TranslatePoint(new(30,18),gallery));
        var right=TileDragReorder.ResolveDrop(gallery,second.TranslatePoint(new(6,18),gallery));
        check("both halves of one gallery gap resolve to one canonical insertion target",left==right);
        var a=TileDragReorder.ResolveDropCore(gallery,gallery.Children.Cast<FrameworkElement>(),first.TranslatePoint(new(30,18),gallery));
        var b=TileDragReorder.ResolveDropCore(gallery,gallery.Children.Cast<FrameworkElement>(),second.TranslatePoint(new(6,18),gallery));
        check("one gap also has one marker centered between the neighboring tiles",a==b&&a!.Value.Marker==new Rect(38,0,0,36));
        var moving=new HashSet<FrameworkElement> {first};
        check("the two sides of a dragged item's unchanged slot produce no reorder target",
            TileDragReorder.ResolveDropCore(gallery,gallery.Children.Cast<FrameworkElement>(),new(6,18),moving:moving) is null&&
            TileDragReorder.ResolveDropCore(gallery,gallery.Children.Cast<FrameworkElement>(),new(42,18),moving:moving) is null);
        gallery.Children.Add(new Button());Layout(root,260,210);
        var rowEnd=TileDragReorder.ResolveDropCore(gallery,gallery.Children.Cast<FrameworkElement>(),new(110,18));
        var rowStart=TileDragReorder.ResolveDropCore(gallery,gallery.Children.Cast<FrameworkElement>(),new(6,58));
        check("a wrapped row boundary also has one canonical gap and indicator",rowEnd==rowStart&&rowEnd!.Value.Marker==new Rect(0,40,0,36));
        check("the physical space between two tiles resolves to the same single gap",TileDragReorder.ResolveDropCore(gallery,gallery.Children.Cast<FrameworkElement>(),new(38,18))==a);
        Clipping(check);Vertical(check);
    }
    private static void Clipping(Action<string,bool> check)
    {
        var gallery=new PixelTilePanel(3,36,4);
        for(int i=0;i<6;i++)gallery.Children.Add(new Border {Background=Brushes.Blue});
        var inner=new AdornerDecorator {Child=gallery,ClipToBounds=true};
        var root=new AdornerDecorator {Child=new Border {Padding=new Thickness(12),Child=new Border {Width=116,Height=40,
            HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,ClipToBounds=true,Child=inner}}};
        Layout(root,260,210);var source=(FrameworkElement)gallery.Children[2];
        using var feedback=TileDragFeedback.Create(source,gallery,source.TranslatePoint(new(18,18),gallery));
        feedback!.Move(new(180,140));feedback.ShowInsertion(new(0,0,0,36));Layout(root,260,210);
        var pixels=new RenderTargetBitmap(260,210,96,96,PixelFormats.Pbgra32);pixels.Render(root);
        byte[] pixel=new byte[4];var origin=gallery.TranslatePoint(new(),root);
        pixels.CopyPixels(new Int32Rect((int)origin.X,(int)origin.Y-2,1,1),pixel,4,0);
        check("the leading insertion dot renders fully above a clipped first row",pixel[3]>0&&pixel[0]>pixel[2]);
        pixels.CopyPixels(new Int32Rect((int)origin.X-2,(int)origin.Y,1,1),pixel,4,0);
        check("the leading insertion dot is not clipped at the left edge",pixel[3]>0&&pixel[0]>pixel[2]);
        pixels.CopyPixels(new Int32Rect((int)origin.X+180,(int)origin.Y+140,1,1),pixel,4,0);
        check("the drag image is independent of its inner scrolling clip",pixel[3]>0&&pixel[0]>pixel[2]);
    }
    private static void Vertical(Action<string,bool> check)
    {
        var panel=new StackPanel();var rows=new List<FrameworkElement>();
        for(int i=0;i<4;i++){var row=new Border {Height=36,Margin=new Thickness(0,2,0,2)};panel.Children.Add(row);rows.Add(row);}
        Layout(panel,240,200);
        var above=TileDragReorder.ResolveDropCore(panel,rows,new(100,30),vertical:true);
        var below=TileDragReorder.ResolveDropCore(panel,rows,new(100,50),vertical:true);
        check("the bottom of one layer and top of the next use the same insertion target",above==below);
        check("layer gaps use the shared horizontal marker geometry",above!.Value.Marker.Height==0&&above.Value.Marker.Width==240&&above.Value.Marker.Y==40);
        var moving=new HashSet<FrameworkElement> {rows[1],rows[3]};
        var group=TileDragReorder.ResolveDropCore(panel,rows,new(100,110),vertical:true,moving:moving);
        check("a multi-selection has one insertion target after filtering its moving rows",group is {After:true}&&ReferenceEquals(group.Value.Tile,rows[2]));
        var unrealized=new Border {Height=36};
        var logical=new FrameworkElement[] {unrealized,rows[0],rows[1],rows[2],rows[3]};
        moving=[unrealized,rows[1]];
        check("offscreen selected layers are retained when deciding whether a group drop changes order",
            TileDragReorder.ResolveDropCore(panel,logical,new(100,50),vertical:true,moving:moving) is not null);
    }
    private static void Layout(FrameworkElement root,double width,double height)
    {root.Measure(new(width,height));root.Arrange(new Rect(0,0,width,height));root.UpdateLayout();}
}
