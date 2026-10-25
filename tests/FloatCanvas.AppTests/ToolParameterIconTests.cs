using System.Text.Json;
using System.IO;
using FloatCanvas.App;
using FloatCanvas.Core;
using Controls=System.Windows.Controls;
using Media=System.Windows.Media;
using Imaging=System.Windows.Media.Imaging;
using Wpf=System.Windows;

internal static class ToolParameterIconTests
{
    internal static void Run(Action<string,bool> check)
    {
        check("new and older profiles display shortcut legends by default",new UserSettings().ShowToolShortcutHints&&JsonSerializer.Deserialize<UserSettings>("{}")!.ShowToolShortcutHints);
        var settings=new UserSettings {ShowToolShortcutHints=false};
        check("shortcut legend visibility is a persistent preference",!JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(settings))!.ShowToolShortcutHints);
        check("all reserved single-letter legends are unique",ToolShortcutCatalog.All.Count==15&&ToolShortcutCatalog.All.Select(item=>item.VirtualKey).Distinct().Count()==15&&ToolShortcutCatalog.All.All(item=>item.Hint.Length==1));
        foreach(var binding in ToolShortcutCatalog.All)
            check("catalog routes "+binding.Hint+" consistently",binding.Tool is { } tool?ToolShortcutCatalog.TryGetTool(binding.VirtualKey,out var actual)&&actual==tool:ToolShortcutCatalog.IsReserved(binding.VirtualKey)&&!ToolShortcutCatalog.TryGetTool(binding.VirtualKey,out _));
        var parameters=new ToolParameters {Tool=EditorTool.Rectangle,Style=new ElementStyle {Stroke=0x801234D0,Fill=0x4024BC68,Opacity=.5,StrokeWidth=4}};
        string before=JsonSerializer.Serialize(parameters);
        var shape=Bitmap(parameters);
        check("style icon retains actual partial alpha and object opacity",MaxAlpha(shape) is > 30 and < 110);
        check("style icon never changes the user's translucent drawing parameters",JsonSerializer.Serialize(parameters)==before);
        check("cached style artwork is frozen and reusable at distinct UI sizes",shape.IsFrozen&&ReferenceEquals(shape,((Controls.Image)ToolParameterIcon.Create(parameters,32)).Source));
        var line=new ToolParameters {Tool=EditorTool.Line};
        string plain=Hash(Bitmap(line));line.Style.EndArrowHead=ArrowHeadStyle.FilledTriangle;
        check("line endpoint presets change the actual rendered arrow geometry",plain!=Hash(Bitmap(line)));
        line.Style.StrokeWidth=12;
        var arrowPreview=Bitmap(line);
        check("arrow preset preview retains a tapered upper-right tip inside its padded image",HasTaperedTip(arrowPreview)&&HasTransparentEdges(arrowPreview));
        line.Style.EndArrowHead=ArrowHeadStyle.None;line.Style.StrokePattern=StrokePattern.Dashed;string dashed=Hash(Bitmap(line));
        line.Style.StrokePattern=StrokePattern.Dotted;
        check("solid dashed and dotted presets have distinct rendered strokes",plain!=dashed&&dashed!=Hash(Bitmap(line)));
        var rectangle=new ToolParameters {Tool=EditorTool.Rectangle};string squareCorners=Hash(Bitmap(rectangle));rectangle.CornerRadius=24;
        check("rounded corner presets change their image geometry",squareCorners!=Hash(Bitmap(rectangle)));
        foreach(var tool in new[]{EditorTool.Rectangle,EditorTool.RoundedRectangle,EditorTool.Ellipse})
        foreach(double ratio in new[]{.5,1,2})
        {
            var sample=new ToolParameters {Tool=tool,ConstrainAspect=true,DefaultWidth=100*ratio,DefaultHeight=100,Style=new() {StrokeWidth=0,Fill=0xFF123456}};
            var bounds=InkBounds(Bitmap(sample));
            check(tool+" locked preset preview preserves configured ratio "+ratio,Math.Abs((double)bounds.Width/bounds.Height-ratio)<.06);
        }
        var highlighter=new ToolParameters {Tool=EditorTool.Highlighter,Style=new ElementStyle {Stroke=0x8053C7D3,Opacity=.5,StrokeWidth=24}};
        check("highlighter preview describes its selected alpha and opacity",MaxAlpha(Bitmap(highlighter)) is > 30 and < 110);
        var symbols=new ToolParameters {Tool=EditorTool.Symbol,Symbol=SymbolKind.Heart};string heart=Hash(Bitmap(symbols));symbols.Symbol=SymbolKind.Hexagon;
        check("symbol preset previews retain distinct actual outlines",heart!=Hash(Bitmap(symbols)));
        var toolbarTools=new[]{EditorTool.Freehand,EditorTool.Highlighter,EditorTool.Line,EditorTool.Arrow,EditorTool.DoubleArrow,EditorTool.Rectangle,EditorTool.Ellipse,EditorTool.Text,EditorTool.Number,EditorTool.Symbol};
        foreach(var tool in toolbarTools)
        {
            var image=ToolParameterIcon.Create(new ToolParameters {Tool=tool});
            var pixels=Rasterize(image,16);
            var bounds=InkBounds(pixels);
            Console.WriteLine($"ICON INK {tool}: {bounds.Width}x{bounds.Height} at 16 DIP");
            check(tool+" artwork fills a readable portion of the actual 16-DIP toolbar icon",Math.Max(bounds.Width,bounds.Height)>=13&&Math.Max(bounds.Width,bounds.Height)<=16);
        }
        var readableLine=Rasterize(ToolParameterIcon.Create(new ToolParameters {Tool=EditorTool.Line}),16);
        var readableDashed=Rasterize(ToolParameterIcon.Create(new ToolParameters {Tool=EditorTool.Line,Style=new ElementStyle {StrokePattern=StrokePattern.Dashed}}),16);
        check("solid and dashed line samples retain visible ink and distinct pixels at toolbar size",InkCount(readableLine)>=16&&InkCount(readableDashed)>=10&&Hash(readableLine)!=Hash(readableDashed));
        foreach(var tool in new[]{EditorTool.Eraser,EditorTool.Blur,EditorTool.Pixelate})
            check(tool+" presets retain a distinct bounded control-tool illustration",ToolParameterIcon.Create(new ToolParameters {Tool=tool},32) is Controls.Image {Width:32,Height:32,Source:Media.DrawingImage});
        var examples=new List<(string Label,ToolParameters Parameters)>();
        foreach(string label in new[]{"Line","Arrow","Double","Dashed","Dotted"})
        {
            var sample=new ToolParameters {Tool=EditorTool.Line};sample.Style.StrokeWidth=12;
            sample.Style.StartArrowHead=label=="Double"?ArrowHeadStyle.FilledTriangle:ArrowHeadStyle.None;
            sample.Style.EndArrowHead=label is "Arrow" or "Double"?ArrowHeadStyle.FilledTriangle:ArrowHeadStyle.None;
            sample.Style.StrokePattern=label=="Dashed"?StrokePattern.Dashed:label=="Dotted"?StrokePattern.Dotted:StrokePattern.Solid;
            examples.Add((label,sample));
        }
        check("large drawing width cannot erase the five line-preset identities",examples.Select(example=>Hash(Bitmap(example.Parameters))).Distinct().Count()==5);
        string? previewDirectory=Environment.GetEnvironmentVariable("FLOATCANVAS_TOOL_ICON_PREVIEW_DIR");
        if(!string.IsNullOrWhiteSpace(previewDirectory))
        {
            Directory.CreateDirectory(previewDirectory);
            var row=new Controls.StackPanel {Orientation=Controls.Orientation.Horizontal,Background=Media.Brushes.White};
            foreach(var example in examples)
            {
                var column=new Controls.StackPanel {Width=80,Margin=new Wpf.Thickness(0,5,0,0)};
                column.Children.Add(ToolParameterIcon.Create(example.Parameters,32));column.Children.Add(new Controls.TextBlock {Text=example.Label,Foreground=Media.Brushes.Black,FontSize=11,TextAlignment=Wpf.TextAlignment.Center,Margin=new Wpf.Thickness(0,5,0,0)});row.Children.Add(column);
            }
            row.Measure(new Wpf.Size(400,60));row.Arrange(new Wpf.Rect(0,0,400,60));row.UpdateLayout();
            var pixels=new Imaging.RenderTargetBitmap(800,120,192,192,Media.PixelFormats.Pbgra32);pixels.Render(row);
            var encoder=new Imaging.PngBitmapEncoder();encoder.Frames.Add(Imaging.BitmapFrame.Create(pixels));using var stream=File.Create(Path.Combine(previewDirectory,"line-style-width-12.png"));encoder.Save(stream);
            var toolbar=new Controls.StackPanel {Orientation=Controls.Orientation.Horizontal,Background=Media.Brushes.White};
            foreach(var tool in toolbarTools)
            {
                var column=new Controls.StackPanel {Width=64,Margin=new Wpf.Thickness(0,5,0,0)};
                column.Children.Add(ToolParameterIcon.Create(new ToolParameters {Tool=tool}));
                column.Children.Add(new Controls.TextBlock {Text=tool.ToString(),Foreground=Media.Brushes.Black,FontSize=8,TextAlignment=Wpf.TextAlignment.Center,Margin=new Wpf.Thickness(0,5,0,0)});toolbar.Children.Add(column);
            }
            toolbar.Measure(new Wpf.Size(640,40));toolbar.Arrange(new Wpf.Rect(0,0,640,40));toolbar.UpdateLayout();
            var toolbarPixels=new Imaging.RenderTargetBitmap(640,40,96,96,Media.PixelFormats.Pbgra32);toolbarPixels.Render(toolbar);
            var toolbarEncoder=new Imaging.PngBitmapEncoder();toolbarEncoder.Frames.Add(Imaging.BitmapFrame.Create(toolbarPixels));using var toolbarStream=File.Create(Path.Combine(previewDirectory,"toolbar-icons-at-16-dip.png"));toolbarEncoder.Save(toolbarStream);
        }
    }
    private static Imaging.BitmapSource Bitmap(ToolParameters parameters)=>(Imaging.BitmapSource)((Controls.Image)ToolParameterIcon.Create(parameters)).Source;
    private static Imaging.BitmapSource Rasterize(Wpf.UIElement image,int size)
    {
        image.Measure(new Wpf.Size(size,size));image.Arrange(new Wpf.Rect(0,0,size,size));image.UpdateLayout();
        var pixels=new Imaging.RenderTargetBitmap(size,size,96,96,Media.PixelFormats.Pbgra32);pixels.Render(image);return pixels;
    }
    private static Wpf.Int32Rect InkBounds(Imaging.BitmapSource bitmap)
    {
        byte[] bytes=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(bytes,bitmap.PixelWidth*4,0);
        int left=bitmap.PixelWidth,top=bitmap.PixelHeight,right=-1,bottom=-1;
        for(int y=0;y<bitmap.PixelHeight;y++)for(int x=0;x<bitmap.PixelWidth;x++)if(bytes[(y*bitmap.PixelWidth+x)*4+3]>=32)
        {left=Math.Min(left,x);top=Math.Min(top,y);right=Math.Max(right,x);bottom=Math.Max(bottom,y);}
        return right<left?Wpf.Int32Rect.Empty:new(left,top,right-left+1,bottom-top+1);
    }
    private static int InkCount(Imaging.BitmapSource bitmap)
    {byte[] bytes=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(bytes,bitmap.PixelWidth*4,0);int count=0;for(int i=3;i<bytes.Length;i+=4)if(bytes[i]>=96)count++;return count;}
    private static bool HasTaperedTip(Imaging.BitmapSource bitmap)
    {
        byte[] bytes=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(bytes,bitmap.PixelWidth*4,0);
        var points=new List<(int X,int Y)>();
        for(int y=0;y<bitmap.PixelHeight;y++)for(int x=0;x<bitmap.PixelWidth;x++)if(bytes[(y*bitmap.PixelWidth+x)*4+3]>=96)points.Add((x,y));
        int tip=points.Max(point=>point.X-point.Y);
        int tipInk=points.Count(point=>point.X-point.Y>=tip-2),shoulderInk=points.Count(point=>point.X-point.Y>=tip-10&&point.X-point.Y<tip-7);
        return tipInk>0&&shoulderInk>tipInk;
    }
    private static bool HasTransparentEdges(Imaging.BitmapSource bitmap)
    {
        byte[] bytes=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(bytes,bitmap.PixelWidth*4,0);
        for(int x=0;x<bitmap.PixelWidth;x++)if(bytes[x*4+3]!=0||bytes[((bitmap.PixelHeight-1)*bitmap.PixelWidth+x)*4+3]!=0)return false;
        for(int y=0;y<bitmap.PixelHeight;y++)if(bytes[(y*bitmap.PixelWidth)*4+3]!=0||bytes[(y*bitmap.PixelWidth+bitmap.PixelWidth-1)*4+3]!=0)return false;
        return true;
    }
    private static string Hash(Imaging.BitmapSource bitmap)
    {byte[] bytes=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(bytes,bitmap.PixelWidth*4,0);return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));}
    private static int MaxAlpha(Imaging.BitmapSource bitmap)
    {byte[] bytes=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(bytes,bitmap.PixelWidth*4,0);return bytes.Where((_,i)=>i%4==3).Max();}
    private static int Count(Imaging.BitmapSource bitmap,Media.Color color)
    {byte[] bytes=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(bytes,bitmap.PixelWidth*4,0);int count=0;for(int i=0;i<bytes.Length;i+=4)if(bytes[i+3]>250&&Math.Abs(bytes[i+2]-color.R)<4&&Math.Abs(bytes[i+1]-color.G)<4&&Math.Abs(bytes[i]-color.B)<4)count++;return count;}
}
