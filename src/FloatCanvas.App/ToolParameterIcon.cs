using System.IO;
using System.Text.Json;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FloatCanvas.App;

/// <summary>Small style samples rendered by the same geometry path as the actual document.</summary>
internal static class ToolParameterIcon
{
    private static readonly Dictionary<string,BitmapSource> Cache = new(StringComparer.Ordinal);
    private static readonly Queue<string> Order = new();
    private static readonly object Gate = new();

    internal static UIElement Create(ToolParameters parameters,double size=16)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if(!double.IsFinite(size)||size<=0)throw new ArgumentOutOfRangeException(nameof(size));
        if(parameters.Tool==EditorTool.Emoji)return new Image {Source=EmojiArtwork.Image(parameters.Emoji),Width=size,Height=size,Stretch=Stretch.Uniform};
        string? control=parameters.Tool switch
        {
            EditorTool.Select=>"M3,1 L3,15 L7,11 L10,16 L12,15 L9,10 L15,10 Z",
            EditorTool.Eraser=>"M1,10 L8,2 L15,9 L8,16 L6,16 Z M5,6 L12,13 M8,16 L16,16",
            EditorTool.Blur=>"M1,1 L15,1 L15,15 L1,15 Z M5,5 L5.1,5 M11,5 L11.1,5 M8,8 L8.1,8 M5,11 L5.1,11 M11,11 L11.1,11",
            EditorTool.Pixelate=>"M1,1 L15,1 L15,15 L1,15 Z M1,6 L15,6 M1,11 L15,11 M6,1 L6,15 M11,1 L11,15",
            EditorTool.Image=>"M1,1 L15,1 L15,15 L1,15 Z M1,12 L6,7 L10,11 L13,8 L15,10 M11,4 L11.1,4",
            EditorTool.Crop or EditorTool.CaptureRegion=>"M1,5 L1,1 L5,1 M11,1 L15,1 L15,5 M15,11 L15,15 L11,15 M5,15 L1,15 L1,11",
            EditorTool.Stamp=>"M2,7 L14,7 L14,15 L2,15 Z M5,7 L5,1 L11,1 L11,7",
            _=>null
        };
        if(control is not null)return ControlIcon(parameters,control,size);
        string key=JsonSerializer.Serialize(parameters);
        BitmapSource source;
        lock(Gate)
        {
            if(!Cache.TryGetValue(key,out source!))
            {
                source=Render(parameters);Cache.Add(key,source);Order.Enqueue(key);
                while(Cache.Count>256)Cache.Remove(Order.Dequeue());
            }
        }
        return new Image {Source=source,Width=size,Height=size,Stretch=Stretch.Uniform};
    }

    private static UIElement ControlIcon(ToolParameters parameters,string path,double size)
    {
        uint color=parameters.Style.Stroke;
        var stroke=new SolidColorBrush(Color.FromArgb((byte)(color>>24),(byte)(color>>16),(byte)(color>>8),(byte)color)) {Opacity=parameters.Style.Opacity};stroke.Freeze();
        uint fill=parameters.Style.Fill;
        Brush? fillBrush=(fill>>24)==0?null:new SolidColorBrush(Color.FromArgb((byte)(fill>>24),(byte)(fill>>16),(byte)(fill>>8),(byte)fill)) {Opacity=parameters.Style.Opacity};
        var drawing=new DrawingGroup();drawing.Children.Add(new GeometryDrawing(Brushes.Transparent,null,new RectangleGeometry(new Rect(0,0,18,18))));
        var pen=new Pen(stroke,1.5) {StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round};
        var geometry=Geometry.Parse(path).Clone();geometry.Transform=new TranslateTransform(1,1);
        drawing.Children.Add(new GeometryDrawing(fillBrush,pen,geometry));
        drawing.Freeze();var source=new DrawingImage(drawing);source.Freeze();return new Image {Source=source,Width=size,Height=size,Stretch=Stretch.Uniform};
    }

    private static BitmapSource Render(ToolParameters parameters)
    {
        var style=parameters.Style.Clone();
        // Preserve useful width differences while leaving enough room to identify
        // arrow heads and dashed strokes in a 16-DIP sample.
        style.StrokeWidth=style.StrokeWidth==0 && style.Fill!=0?0:Math.Clamp(style.StrokeWidth,2,6);
        if(parameters.Tool is EditorTool.Line or EditorTool.Arrow or EditorTool.DoubleArrow)style.StrokeWidth=Math.Min(style.StrokeWidth,4);
        if(style.StrokePattern!=StrokePattern.Solid)style.StrokeWidth=Math.Min(style.StrokeWidth,3);
        style.FontSize=32;
        style.ArrowHeadSize=parameters.Style.ArrowHeadSize>0?Math.Clamp(parameters.Style.ArrowHeadSize,7,18):Math.Clamp(Math.Max(10,parameters.Style.StrokeWidth*4),10,18);
        var element=new CanvasElement {Style=style,Width=32,Height=28,Transform=Affine2D.Translation(8,10)};
        switch(parameters.Tool)
        {
            case EditorTool.Line: case EditorTool.Arrow:case EditorTool.DoubleArrow:
                element.Kind=parameters.Tool==EditorTool.Line?ElementKind.Line:parameters.Tool==EditorTool.Arrow?ElementKind.Arrow:ElementKind.DoubleArrow;
                element.Points=[new(0,26),new(32,0)];break;
            case EditorTool.Rectangle:case EditorTool.RoundedRectangle:
                element.Kind=ElementKind.Rectangle;element.CornerRadius=parameters.CornerRadius is { } corner?Math.Clamp(corner/3,0,10):parameters.Tool==EditorTool.RoundedRectangle?6:0;
                if(parameters.ConstrainAspect){element.Height=32;element.Transform=Affine2D.Translation(8,8);}break;
            case EditorTool.Ellipse:element.Kind=ElementKind.Ellipse;if(parameters.ConstrainAspect){element.Height=32;element.Transform=Affine2D.Translation(8,8);}break;
            case EditorTool.Symbol:case EditorTool.Triangle:
                element.Kind=ElementKind.Symbol;element.Symbol=parameters.Tool==EditorTool.Triangle?SymbolKind.Triangle:parameters.Symbol;element.Width=32;element.Height=32;element.Transform=Affine2D.Translation(8,8);break;
            case EditorTool.Text:
                element.Kind=ElementKind.Text;element.Text="T";element.Width=38;element.Height=40;element.Transform=Affine2D.Translation(6,2);break;
            case EditorTool.Number:
                element.Kind=ElementKind.Number;element.NumberUsesForeground=true;element.Number=1;element.Width=32;element.Height=32;style.FontSize=24;element.Transform=Affine2D.Translation(8,8);break;
            case EditorTool.Highlighter:
                // A preview describes the chosen color; document highlighter blending must
                // not make its toolbar swatch hard to distinguish.
                element.Kind=ElementKind.Freehand;element.Points=[new(1,23),new(30,4)];style.StrokeWidth=Math.Clamp(parameters.Style.StrokeWidth,8,14);break;
            default:
                element.Kind=ElementKind.Freehand;element.Points=[new(0,25),new(7,5),new(17,21),new(29,3),new(32,15)];break;
        }
        element.Transform=Affine2D.Scale(2,2).Compose(element.Transform);
        using var renderer=new SceneRenderer();
        var document=new SceneDocument {Viewport=new(0,0,96,96),Elements=[element]};
        using var stream=new MemoryStream(renderer.ExportPng(document,new(),true));
        var frame=BitmapFrame.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
        return FitArtwork(frame);
    }

    private static BitmapSource FitArtwork(BitmapSource source)
    {
        var pixels=new FormatConvertedBitmap(source,PixelFormats.Pbgra32,null,0);
        byte[] bytes=new byte[pixels.PixelWidth*pixels.PixelHeight*4];pixels.CopyPixels(bytes,pixels.PixelWidth*4,0);
        int left=pixels.PixelWidth,top=pixels.PixelHeight,right=-1,bottom=-1;
        for(int y=0;y<pixels.PixelHeight;y++)for(int x=0;x<pixels.PixelWidth;x++)if(bytes[(y*pixels.PixelWidth+x)*4+3]!=0)
        {left=Math.Min(left,x);top=Math.Min(top,y);right=Math.Max(right,x);bottom=Math.Max(bottom,y);}
        if(right<left){source.Freeze();return source;}
        // Keep one DIP of breathing room inside the 16-DIP button artwork. Text,
        // numbers and shapes have different actual ink bounds, not different UI sizes.
        int width=right-left+1,height=bottom-top+1;
        double scale=84d/Math.Max(width,height);
        var visual=new DrawingVisual();RenderOptions.SetBitmapScalingMode(visual,BitmapScalingMode.HighQuality);
        using(var drawing=visual.RenderOpen())drawing.DrawImage(new CroppedBitmap(source,new Int32Rect(left,top,width,height)),
            new Rect((96-width*scale)/2,(96-height*scale)/2,width*scale,height*scale));
        var fitted=new RenderTargetBitmap(96,96,96,96,PixelFormats.Pbgra32);fitted.Render(visual);fitted.Freeze();return fitted;
    }
}
