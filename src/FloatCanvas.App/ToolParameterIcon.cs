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
        if(parameters.Tool==EditorTool.CaptureRegion)return CaptureIcon(parameters,size);
        if(parameters.Tool is EditorTool.Blur or EditorTool.Pixelate)return EffectIcon(parameters,size);
        string? control=parameters.Tool switch
        {
            EditorTool.Select=>"M3,1 L3,15 L7,11 L10,16 L12,15 L9,10 L15,10 Z",
            EditorTool.Eraser=>"M1,10 L8,2 L15,9 L8,16 L6,16 Z M5,6 L12,13 M8,16 L16,16",
            EditorTool.Image=>"M1,1 L15,1 L15,15 L1,15 Z M1,12 L6,7 L10,11 L13,8 L15,10 M11,4 L11.1,4",
            EditorTool.Crop=>"M1,5 L1,1 L5,1 M11,1 L15,1 L15,5 M15,11 L15,15 L11,15 M5,15 L1,15 L1,11",
            EditorTool.Stamp=>"M2,7 L14,7 L14,15 L2,15 Z M5,7 L5,1 L11,1 L11,7",
            _=>null
        };
        if(control is not null)
        {
            var icon=ControlIcon(control,size);
            if(parameters.Tool==EditorTool.Image)icon.Opacity=parameters.Style.Opacity;
            return icon;
        }
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

    private static UIElement CaptureIcon(ToolParameters parameters,double size)
    {
        string path=parameters.CaptureShape switch
        {
            CaptureShape.Ellipse=>"M1,8 A7,7 0 1 0 15,8 A7,7 0 1 0 1,8 Z",
            CaptureShape.FreehandLasso=>"M3,3 C8,-1 15,4 15,9 C15,15 5,17 2,12 C-1,8 1,5 3,3 Z",
            CaptureShape.PolygonLasso=>"M2,4 L11,1 L15,10 L9,15 L1,12 Z",
            _=>parameters.CaptureBorderWidth>0?"M1,1 H15 V15 H1 Z":"M1,5 L1,1 L5,1 M11,1 L15,1 L15,5 M15,11 L15,15 L11,15 M5,15 L1,15 L1,11"
        };
        Brush? stroke=null;
        if(parameters.CaptureBorderWidth>0)
        {
            uint color=parameters.CaptureBorderColor;
            stroke=new SolidColorBrush(Color.FromArgb((byte)(color>>24),(byte)(color>>16),(byte)(color>>8),(byte)color));stroke.Freeze();
        }
        var icon=(Viewbox)ControlIcon(path,size,stroke,parameters.CaptureBorderWidth>0?Math.Clamp(parameters.CaptureBorderWidth,1.5,3):1.5);
        // The outline describes the selection; the landscape inside identifies captured pixels.
        var content=new System.Windows.Shapes.Path {Data=Geometry.Parse("M4.5,10.5 L7,8 L8.75,9.75 L10,8.5 L11.5,10.5 M10,5.75 A.65,.65 0 1 0 11.3,5.75 A.65,.65 0 1 0 10,5.75 Z"),
            StrokeThickness=1.2,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round,
            RenderTransform=new TranslateTransform(1,1),IsHitTestVisible=false};
        content.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"CanvasText");
        ((Canvas)icon.Child).Children.Add(content);
        return icon;
    }

    private static UIElement EffectIcon(ToolParameters parameters,double size)
    {
        var icon=(Viewbox)ControlIcon("M2,1 H14 Q15,1 15,2 V14 Q15,15 14,15 H2 Q1,15 1,14 V2 Q1,1 2,1 Z",size);
        var sample=new Canvas {Width=12,Height=12,Clip=new RectangleGeometry(new Rect(0,0,12,12),.5,.5),IsHitTestVisible=false};
        Canvas.SetLeft(sample,3);Canvas.SetTop(sample,3);
        void Add(System.Windows.Shapes.Shape shape,double opacity)
        {
            shape.Opacity=opacity;shape.IsHitTestVisible=false;
            shape.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,"CanvasText");sample.Children.Add(shape);
        }
        if(parameters.Tool==EditorTool.Blur)
        {
            // Soft opacity masks keep the preview smooth without a shader effect or a cached theme color.
            Add(new System.Windows.Shapes.Rectangle {Width=12,Height=12},.18);
            double spread=Math.Clamp(parameters.EffectStrength/6,1.2,3.5);
            foreach(var (x,y,radius) in new[]{(8.5,3d,2d),(3.5,9d,5d),(10d,10d,4d)})
            {
                double extent=radius+spread;
                var patch=new System.Windows.Shapes.Ellipse {Width=extent*2,Height=extent*2,
                    OpacityMask=new RadialGradientBrush {GradientStops=new GradientStopCollection {new(Colors.White,0),new(Colors.White,Math.Max(0,1-spread/radius)),new(Colors.Transparent,1)}}};
                Canvas.SetLeft(patch,x-extent);Canvas.SetTop(patch,y-extent);Add(patch,1);
            }
        }
        else
        {
            int cells=parameters.EffectStrength<8?4:parameters.EffectStrength<24?3:2;
            double cell=12d/cells;
            for(int y=0;y<cells;y++)for(int x=0;x<cells;x++)
            {
                var block=new System.Windows.Shapes.Rectangle {Width=cell-.6,Height=cell-.6};
                Canvas.SetLeft(block,x*cell+.3);Canvas.SetTop(block,y*cell+.3);
                Add(block,((x+y*2)%4) switch {0=>.25,1=>.55,2=>1d,_=>.75});
            }
        }
        ((Canvas)icon.Child).Children.Insert(0,sample);
        return icon;
    }

    private static UIElement ControlIcon(string path,double size,Brush? stroke=null,double thickness=1.5)
    {
        var glyph=new System.Windows.Shapes.Path { Data=Geometry.Parse(path), StrokeThickness=thickness,
            StrokeStartLineCap=PenLineCap.Round, StrokeEndLineCap=PenLineCap.Round, StrokeLineJoin=PenLineJoin.Round,
            RenderTransform=new TranslateTransform(1,1), IsHitTestVisible=false };
        if(stroke is null)glyph.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"CanvasText");else glyph.Stroke=stroke;
        var stage=new Canvas {Width=18,Height=18,IsHitTestVisible=false};stage.Children.Add(glyph);
        return new Viewbox {Child=stage,Width=size,Height=size,Stretch=Stretch.Uniform,IsHitTestVisible=false};
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
                break;
            case EditorTool.Ellipse:element.Kind=ElementKind.Ellipse;break;
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
        if(parameters.ConstrainAspect && parameters.Tool is EditorTool.Rectangle or EditorTool.RoundedRectangle or EditorTool.Ellipse or EditorTool.Symbol or EditorTool.Triangle)
        {
            double scale=32/Math.Max(parameters.DefaultWidth,parameters.DefaultHeight);
            element.Width=parameters.DefaultWidth*scale;element.Height=parameters.DefaultHeight*scale;
            element.Transform=Affine2D.Translation((48-element.Width)/2,(48-element.Height)/2);
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
