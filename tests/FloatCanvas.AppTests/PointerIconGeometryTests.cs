using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatCanvas.App;
using FloatCanvas.Core;
using ShapePath=System.Windows.Shapes.Path;

internal static class PointerIconGeometryTests
{
    internal static void Run(Action<string,bool> check)
    {
        var p=new ToolParameters { Tool=EditorTool.PointerPresentation, PointerPresentation=new() };
        var s=p.PointerPresentation!;
        s.CursorEnabled=false;s.TrailEnabled=false;s.ClicksEnabled=true;
        s.LeftClick.Color=0xFFFF0000;s.MiddleClick.Color=0xFF00FF00;s.RightClick.Color=0xFF0000FF;
        string before=JsonSerializer.Serialize(p);
        var view=(Viewbox)ToolParameterIcon.Create(p,78);
        var canvas=(Canvas)view.Child;
        var circles=canvas.Children.OfType<ShapePath>().Select(x=>x.Data).OfType<EllipseGeometry>().ToArray();
        check("pointer has three true circular vector geometries",circles.Length==3&&circles.All(c=>c.RadiusX==c.RadiusY));
        for(int i=0;i<3;i++)
        {
            double a=i*Math.PI/6;
            check("pointer button ring retains clock position "+i,Math.Abs(circles[i].Center.X-(9.75+15.5*Math.Sin(a)))<.0001&&Math.Abs(circles[i].Center.Y-(19.5-15.5*Math.Cos(a)))<.0001);
        }
        s.ClicksEnabled=false;s.TrailEnabled=true;
        double TrailWidth(double width)
        {
            s.TrailWidthPixels=width;
            var stage=(Canvas)((Viewbox)ToolParameterIcon.Create(p,96)).Child;
            return stage.Children.OfType<ShapePath>().First(path=>path.Stroke is SolidColorBrush).StrokeThickness * 96 / 30;
        }
        double minWidth=TrailWidth(2), maxWidth=TrailWidth(6);
        check("pointer trail shares pen preview minimum",Math.Abs(TrailWidth(1)-minWidth)<.0001);
        check("pointer trail shares pen preview maximum",Math.Abs(TrailWidth(64)-maxWidth)<.0001);
        check("pointer trail increases with pen width",minWidth<TrailWidth(4)&&TrailWidth(4)<maxWidth);
        s.TrailWidthPixels=3;s.TrailEnabled=false;s.ClicksEnabled=true;
        var extreme=p.Clone();
        extreme.PointerPresentation!.CursorEnabled=true;extreme.PointerPresentation.TrailEnabled=true;extreme.PointerPresentation.TrailWidthPixels=64;
        extreme.PointerPresentation.CursorRadiusPixels=128;
        foreach(var click in new[]{extreme.PointerPresentation.LeftClick,extreme.PointerPresentation.MiddleClick,extreme.PointerPresentation.RightClick})
        { click.RadiusPixels=128;click.StrokeWidthPixels=32; }
        var extremeStage=(Canvas)((Viewbox)ToolParameterIcon.Create(extreme)).Child;
        check("largest pointer artwork stays within icon bounds",extremeStage.Children.OfType<ShapePath>().All(path=>
        {
            var bounds=path.Data.Bounds;
            if(path.RenderTransform is { } transform) bounds=transform.TransformBounds(bounds);
            double pad=path.Stroke is null?0:path.StrokeThickness/2;
            return bounds.Left-pad>=0&&bounds.Top-pad>=0&&bounds.Right+pad<=30&&bounds.Bottom+pad<=30;
        }));
        var directory=Environment.GetEnvironmentVariable("FLOATCANVAS_TOOL_ICON_PREVIEW_DIR");
        foreach(var shape in new[]{PresentationClickShape.Ring,PresentationClickShape.Disc})
        foreach(int size in new[]{16,24,32,78})
        foreach(int dpi in new[]{96,144,192})
        {
            s.LeftClick.Shape=s.MiddleClick.Shape=s.RightClick.Shape=shape;
            var element=ToolParameterIcon.Create(p,size);
            element.Measure(new Size(size,size));element.Arrange(new Rect(0,0,size,size));element.UpdateLayout();
            int n=(int)Math.Ceiling(size*dpi/96d);
            var bitmap=new RenderTargetBitmap(n,n,dpi,dpi,PixelFormats.Pbgra32);bitmap.Render(element);
            var pixels=new byte[n*n*4];bitmap.CopyPixels(pixels,n*4,0);
            for(int channel=0;channel<3;channel++)
            {
                int l=n,t=n,r=-1,b=-1;
                for(int y=0;y<n;y++)for(int x=0;x<n;x++)
                {
                    int k=(y*n+x)*4;
                    if(pixels[k+channel]<35||pixels[k+channel]<=pixels[k+(channel+1)%3]*2||pixels[k+channel]<=pixels[k+(channel+2)%3]*2)continue;
                    l=Math.Min(l,x);r=Math.Max(r,x);t=Math.Min(t,y);b=Math.Max(b,y);
                }
                check($"pointer raster roundness {shape} {size} DIP {dpi} DPI channel {channel}",r>=l&&Math.Abs((r-l)-(b-t))<=1);
            }
            if(!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var f=File.Create(Path.Combine(directory,$"pointer-{shape}-{size}-{dpi}.png"));encoder.Save(f);
            }
        }
        s.LeftClick.Shape=s.MiddleClick.Shape=s.RightClick.Shape=PresentationClickShape.Ring;
        check("pointer rendering does not mutate supplied settings",JsonSerializer.Serialize(p)==before);
        if(!string.IsNullOrEmpty(directory))
        {
            var sheet=new StackPanel {Orientation=Orientation.Vertical,Width=560,Background=Brushes.White};
            foreach(string theme in new[]{"light","dark"})
            {
                AppTheme.Apply(theme);
                var row=new StackPanel {Orientation=Orientation.Horizontal,Height=130,Background=(Brush)Application.Current.FindResource("CanvasSurface")};
                foreach(int size in new[]{16,24,32,78})
                {
                    var sample=p.Clone();sample.PointerPresentation!.CursorEnabled=true;sample.PointerPresentation.TrailEnabled=true;
                    sample.PointerPresentation.CursorColor=0x85FFD54F;sample.PointerPresentation.TrailColor=0xFF3E8FE5;
                    sample.PointerPresentation.LeftClick.Color=0xFFE88A30;sample.PointerPresentation.MiddleClick.Color=0xFF537DDF;sample.PointerPresentation.RightClick.Color=0xFF27A99A;
                    var item=new StackPanel {Width=140,VerticalAlignment=VerticalAlignment.Center};
                    item.Children.Add(new TextBlock {Text=$"{theme} · {size} DIP",HorizontalAlignment=HorizontalAlignment.Center,Foreground=(Brush)Application.Current.FindResource("CanvasText")});
                    var icon=ToolParameterIcon.Create(sample,size);AppTheme.ApplyPalette(((FrameworkElement)icon).Resources,theme=="dark");item.Children.Add(icon);row.Children.Add(item);
                }
                sheet.Children.Add(row);
            }
            sheet.Measure(new Size(560,260));sheet.Arrange(new Rect(0,0,560,260));sheet.UpdateLayout();
            var rendered=new RenderTargetBitmap(1120,520,192,192,PixelFormats.Pbgra32);rendered.Render(sheet);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(rendered));
            using var file=File.Create(Path.Combine(directory,"pointer-integrated-sheet.png"));encoder.Save(file);
            AppTheme.Apply("light");
        }
        s.MiddleClick.Enabled=false;
        var disabled=(Canvas)((Viewbox)ToolParameterIcon.Create(p)).Child;
        check("pointer respects each button enable setting",disabled.Children.OfType<ShapePath>().Count(x=>x.Data is EllipseGeometry)==2);
        s.LeftClick.Color=0x80336699;
        var alpha=(Canvas)((Viewbox)ToolParameterIcon.Create(p)).Child;
        check("pointer retains configured color alpha",((SolidColorBrush)alpha.Children.OfType<ShapePath>().First(x=>x.Data is EllipseGeometry).Stroke).Color.A==128);
    }
}
