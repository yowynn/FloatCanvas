using System.IO;
using System.Text.Json;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FloatCanvas.App;

/// <summary>Document-style previews, with bounded stroke samples for readable toolbar patterns.</summary>
internal static class ToolParameterIcon
{
    private static readonly Dictionary<string,BitmapSource> Cache = new(StringComparer.Ordinal);
    private static readonly Queue<string> Order = new();
    private static readonly object Gate = new();
    private static readonly Dictionary<double,double> PenPreviewWidths = new();

    internal static UIElement Create(ToolParameters parameters,double size=16)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if(!double.IsFinite(size)||size<=0)throw new ArgumentOutOfRangeException(nameof(size));
        if(parameters.Tool==EditorTool.Emoji)return new Image {Source=EmojiArtwork.Image(parameters.Emoji),Width=size,Height=size,Stretch=Stretch.Uniform,Opacity=parameters.Style.Opacity};
        if(parameters.Tool==EditorTool.CaptureRegion)return CaptureIcon(parameters,size);
        if(parameters.Tool is EditorTool.Blur or EditorTool.Pixelate)return EffectIcon(parameters,size);
        if(parameters.Tool==EditorTool.Presentation)return KeyboardIcon(parameters,size);
        if(parameters.Tool==EditorTool.PointerPresentation)return PointerIcon(parameters,size);
        string? control=parameters.Tool switch
        {
            EditorTool.Select=>"M3,1 L3,15 L7,11 L10,16 L12,15 L9,10 L15,10 Z",
            EditorTool.Eraser=>"M1,10 L8,2 L15,9 L8,16 L6,16 Z M5,6 L12,13 M8,16 L16,16",
            EditorTool.Image=>"M1,1 L15,1 L15,15 L1,15 Z M1,12 L6,7 L10,11 L13,8 L15,10 M11,4 L11.1,4",
            EditorTool.Crop=>parameters.ConstrainAspect
                ? "M1,5 L1,1 L5,1 M11,1 L15,1 L15,5 M15,11 L15,15 L11,15 M5,15 L1,15 L1,11"
                : "M1,6 L1,3 L5,3 M11,3 L15,3 L15,6 M15,10 L15,13 L11,13 M5,13 L1,13 L1,10",
            EditorTool.Stamp=>"M2,7 L14,7 L14,15 L2,15 Z M5,7 L5,1 L11,1 L11,7",
            _=>null
        };
        if(control is not null)
        {
            var icon=ControlIcon(control,size,centerArtwork: parameters.Tool is EditorTool.Select or EditorTool.Eraser);
            if(parameters.Tool==EditorTool.Image)icon.Opacity=parameters.Style.Opacity;
            return icon;
        }
        string key=JsonSerializer.Serialize(parameters);
        BitmapSource source;
        lock(Gate)
        {
            if(!Cache.TryGetValue(key,out source!))
            {
                source=Render(parameters,out _);Cache.Add(key,source);Order.Enqueue(key);
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
        if (parameters.CaptureSource == CaptureSource.BackgroundAndCanvas)
            ((Canvas)icon.Child).Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M5,13 L8,10 L10,12 L14,8"), Stroke = ColorBrush(ColorLibrary.ForegroundRed),
                StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false
            });
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

    private static UIElement ControlIcon(string path,double size,Brush? stroke=null,double thickness=1.5,bool centerArtwork=false)
    {
        var glyph=new System.Windows.Shapes.Path { Data=Geometry.Parse(path), StrokeThickness=thickness,
            StrokeStartLineCap=PenLineCap.Round, StrokeEndLineCap=PenLineCap.Round, StrokeLineJoin=PenLineJoin.Round,
            RenderTransform=new TranslateTransform(1,1), IsHitTestVisible=false };
        if(centerArtwork)
        {
            var bounds = glyph.Data.Bounds;
            glyph.RenderTransform = new TranslateTransform(9 - (bounds.Left + bounds.Right) / 2, 9 - (bounds.Top + bounds.Bottom) / 2);
        }
        if(stroke is null)glyph.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"CanvasText");else glyph.Stroke=stroke;
        var stage=new Canvas {Width=18,Height=18,IsHitTestVisible=false};stage.Children.Add(glyph);
        return new Viewbox {Child=stage,Width=size,Height=size,Stretch=Stretch.Uniform,IsHitTestVisible=false};
    }

    private static UIElement KeyboardIcon(ToolParameters parameters, double size)
    {
        var settings = parameters.Presentation ?? new();
        var background = new GeometryGroup(); var ink = new GeometryGroup();
        static RectangleGeometry Rect(double x,double y,double w,double h,double radius=1) => new(new Rect(x,y,w,h),radius,radius);
        void Key(double x,double y,double width)
        {
            background.Children.Add(Rect(x,y,width,12,3));
            ink.Children.Add(Rect(x+3,y+4,width-6,4,2));
        }
        if (settings.DisplayStyle == PresentationDisplayStyle.Keyboard)
        {
            background.Children.Add(Rect(5,15,62,42,7));
            for (int row=0;row<2;row++) for (int col=0;col<5;col++) ink.Children.Add(Rect(10+col*11,21+row*10,8,7,2));
            ink.Children.Add(Rect(20,43,32,7,2));
        }
        else
        {
            int count=Math.Min(settings.RecentChordLimit,4);
            bool top=settings.KeyPanelAlignment is PresentationPanelAlignment.TopLeft or PresentationPanelAlignment.TopCenter or PresentationPanelAlignment.TopRight;
            double X(double width) => settings.KeyPanelAlignment switch
            {
                PresentationPanelAlignment.TopLeft or PresentationPanelAlignment.BottomLeft => 8,
                PresentationPanelAlignment.TopCenter or PresentationPanelAlignment.BottomCenter => (72-width)/2,
                _ => 64-width
            };
            double start=top?7:65-count*14;
            for(int i=0;i<count;i++)
            {
                double y=start+(top?i:count-1-i)*14;
                if(settings.RecentChordLimit>4 && i==3)
                { double x=X(15); for(int dot=0;dot<3;dot++) ink.Children.Add(new EllipseGeometry(new Point(x+3+dot*4.5,y+6),1.7,1.7)); continue; }
                bool longer=i%2==0;
                if(settings.DisplayStyle==PresentationDisplayStyle.Log)
                { double width=longer?38:20,x=X(width); background.Children.Add(Rect(x,y,width,12,3)); ink.Children.Add(Rect(x+4,y+4,width-8,4,2)); }
                else if(longer) { double x=X(38); Key(x,y,22); Key(x+26,y,12); }
                else Key(X(12),y,12);
            }
        }
        // The two inks occupy disjoint regions, including when either color is transparent.
        // Fill the UI's icon slot; the host already provides spacing around it.
        // Keep a fixed frame for records so their configured alignment stays visible.
        var stage=new Canvas {Width=56,Height=58,IsHitTestVisible=false};
        var artwork=new Canvas {Width=72,Height=72,IsHitTestVisible=false};
        artwork.RenderTransform = settings.DisplayStyle == PresentationDisplayStyle.Keyboard
            ? new MatrixTransform(56d/62,0,0,56d/62,-5*56d/62,(58-42*56d/62)/2-15*56d/62)
            : new TranslateTransform(-8,-7);
        artwork.Children.Add(new System.Windows.Shapes.Path {Data=Geometry.Combine(background,ink,GeometryCombineMode.Exclude,null),Fill=ColorBrush(parameters.Style.Background)});
        artwork.Children.Add(new System.Windows.Shapes.Path {Data=ink,Fill=ColorBrush(parameters.Style.Foreground)});
        stage.Children.Add(artwork);
        return new Viewbox {Child=stage,Width=size,Height=size,Stretch=Stretch.Uniform,Opacity=parameters.Style.Opacity,IsHitTestVisible=false};
    }

    private static UIElement PointerIcon(ToolParameters parameters, double size)
    {
        var settings = parameters.PointerPresentation?.Clone() ?? new(); settings.NormalizeColorAlpha();
        // One vector coordinate space, uniformly scaled at the final UI size. Do not
        // rasterize tiny rounded borders and magnify them: the click rings must stay round.
        var stage = new Canvas { Width = 30, Height = 30, IsHitTestVisible = false,
            UseLayoutRounding = false, SnapsToDevicePixels = false };
        var center = new Point(9.75, 19.5);
        if (settings.CursorEnabled)
            Circle(center, Math.Clamp(settings.CursorRadiusPixels * 9 / 18, 4.5, 9.2), ColorBrush(settings.CursorColor), null, 0);
        if (settings.TrailEnabled)
            stage.Children.Add(new System.Windows.Shapes.Path {
                Data = Geometry.Parse("M15.8,23.2 C19,22.9 20.5,23.2 22.4,24.6 C24.1,25.8 25.9,25.5 27,24.3"),
                Stroke = ColorBrush(settings.TrailColor), StrokeThickness = PenPreviewWidth(settings.TrailWidthPixels) * 30 / 96,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false });
        if (settings.ClicksEnabled)
        {
            Click(settings.LeftClick, 0); Click(settings.MiddleClick, 30); Click(settings.RightClick, 60);
        }
        var pointer = new System.Windows.Shapes.Path {
            Data = Geometry.Parse("M7.366666667,13.2 L18.7,17.866666667 L12.7,19.866666667 L10.7,25.866666667 Z"),
            StrokeThickness = 1.7, StrokeLineJoin = PenLineJoin.Round,
            RenderTransform = new ScaleTransform(1.1, 1.1, 10.5, 19),
            IsHitTestVisible = false };
        pointer.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "CanvasText");
        pointer.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "CanvasSurface");
        stage.Children.Add(pointer);
        // Center the visible combination, keeping the relative positions of cursor,
        // trail and click indicators intact as effects are enabled or disabled.
        Rect bounds = Rect.Empty;
        foreach (System.Windows.Shapes.Path shape in stage.Children)
        {
            if (shape.Fill is SolidColorBrush fill && fill.Color.A == 0 && (shape.Stroke is not SolidColorBrush line || line.Color.A == 0)) continue;
            var part = shape.Data.Bounds;
            if (shape.RenderTransform is { } transform) part = transform.TransformBounds(part);
            if (shape.Stroke is not null) part.Inflate(shape.StrokeThickness / 2, shape.StrokeThickness / 2);
            bounds.Union(part);
        }
        if (!bounds.IsEmpty) stage.RenderTransform = new TranslateTransform(15 - (bounds.Left + bounds.Right) / 2, 15 - (bounds.Top + bounds.Bottom) / 2);
        return new Viewbox { Child = stage, Width = size, Height = size, Stretch = Stretch.Uniform, IsHitTestVisible = false };

        void Click(PresentationClickStyle style, double degrees)
        {
            if (!style.Enabled) return;
            double angle = degrees * Math.PI / 180;
            var position = new Point(center.X + 15.5 * Math.Sin(angle), center.Y - 15.5 * Math.Cos(angle));
            var brush = ColorBrush(style.Color); bool disc = style.Shape == PresentationClickShape.Disc;
            Circle(position, Math.Clamp(style.RadiusPixels * 2.4 / 24, 1.5, 3), disc ? brush : null,
                disc ? null : brush, Math.Clamp(style.StrokeWidthPixels * 1.4 / 2, .7, 1.8));
        }
        void Circle(Point position, double radius, Brush? fill, Brush? stroke, double width)
        {
            // EllipseGeometry describes a true circle, independent of FrameworkElement
            // sizing, border layout rounding and the toolbar/preset host's aspect ratio.
            stage.Children.Add(new System.Windows.Shapes.Path { Data = new EllipseGeometry(position, radius, radius),
                Fill = fill, Stroke = stroke, StrokeThickness = width, IsHitTestVisible = false });
        }
    }
    private static Brush ColorBrush(uint argb)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze(); return brush;
    }

    private static double PenPreviewWidth(double width)
    {
        width = Math.Clamp(width, 2, 6);
        lock (Gate)
        {
            if (!PenPreviewWidths.TryGetValue(width, out double previewWidth))
            {
                var pen = new ToolParameters { Tool = EditorTool.Freehand, Style = new() { Foreground = 0xFF000000, StrokeWidth = width } };
                _ = Render(pen, out double artworkScale);
                previewWidth = width * 2 * artworkScale;
                if (PenPreviewWidths.Count >= 256) PenPreviewWidths.Clear();
                PenPreviewWidths.Add(width, previewWidth);
            }
            return previewWidth;
        }
    }

    private static BitmapSource Render(ToolParameters parameters,out double artworkScale)
    {
        parameters = parameters.Clone(); parameters.NormalizeAppearance();
        if (parameters.Tool is EditorTool.Freehand or EditorTool.Highlighter)
            return RenderStrokeSample(parameters, out artworkScale);
        var style=parameters.Style.Clone();
        // Preserve useful width differences while leaving enough room to identify
        // arrow heads and dashed strokes in a 16-DIP sample.
        style.StrokeWidth=style.StrokeWidth==0?0:Math.Clamp(style.StrokeWidth,2,6);
        if(parameters.Tool is EditorTool.Line or EditorTool.Arrow or EditorTool.DoubleArrow)style.StrokeWidth=Math.Min(style.StrokeWidth,4);
        if(style.StrokePattern!=StrokePattern.Solid)style.StrokeWidth=Math.Min(style.StrokeWidth,3);
        style.FontSize=32;
        style.ArrowHeadSize=parameters.Style.ArrowHeadSize>0?Math.Clamp(parameters.Style.ArrowHeadSize,7,18):Math.Clamp(Math.Max(10,parameters.Style.StrokeWidth*4),10,18);
        var element=new CanvasElement {Style=style,Width=32,Height=28,Transform=Affine2D.Translation(8,10)};
        switch(parameters.Tool)
        {
            case EditorTool.Line: case EditorTool.Arrow:case EditorTool.DoubleArrow:
                element.Kind=parameters.Tool==EditorTool.Line?ElementKind.Line:parameters.Tool==EditorTool.Arrow?ElementKind.Arrow:ElementKind.DoubleArrow;
                element.Points=[new(0,26),new(32,0)];
                if (ElementGeometry.ArrowStyle(element, reverse: true) != ArrowHeadStyle.None &&
                    ElementGeometry.ArrowStyle(element) != ArrowHeadStyle.None)
                    style.ArrowHeadSize = Math.Min(style.ArrowHeadSize, 9);
                break;
            case EditorTool.Rectangle:case EditorTool.RoundedRectangle:
                element.Kind=ElementKind.Rectangle;element.CornerRadius=parameters.CornerRadius is { } corner?Math.Clamp(corner/3,0,10):parameters.Tool==EditorTool.RoundedRectangle?6:0;
                break;
            case EditorTool.Ellipse:element.Kind=ElementKind.Ellipse;break;
            case EditorTool.Symbol:case EditorTool.Triangle:
                element.Kind=ElementKind.Symbol;element.Symbol=parameters.Tool==EditorTool.Triangle?SymbolKind.Triangle:parameters.Symbol;element.Width=32;element.Height=32;element.Transform=Affine2D.Translation(8,8);break;
            case EditorTool.Text:
                element.Kind=ElementKind.Text;element.Text="T";element.Width=38;element.Height=40;element.Transform=Affine2D.Translation(6,2);break;
            case EditorTool.Number:
                element.Kind=ElementKind.Number;element.NumberTextSizeMode=parameters.NumberTextSizeMode;element.Number=1;element.Width=32;element.Height=32;style.FontSize=24;element.Transform=Affine2D.Translation(8,8);break;
            default:
                element.Kind=ElementKind.Freehand;
                var curve = Geometry.Parse(LayerTypeIcon.Geometry(ElementKind.Freehand)).GetFlattenedPathGeometry(.01, ToleranceType.Absolute);
                element.Points = curve.Figures.SelectMany(figure => new[] { figure.StartPoint }.Concat(figure.Segments
                    .OfType<PolyLineSegment>().SelectMany(segment => segment.Points))).Select(point => new PointD(point.X * 2, point.Y * 2)).ToList();
                break;
        }
        if(parameters.Tool is EditorTool.Rectangle or EditorTool.RoundedRectangle or EditorTool.Ellipse or EditorTool.Symbol or EditorTool.Triangle)
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
        return FitArtwork(frame,out artworkScale);
    }

    private static BitmapSource RenderStrokeSample(ToolParameters parameters, out double artworkScale)
    {
        bool highlighter = parameters.Tool == EditorTool.Highlighter;
        double authoredWidth = parameters.Style.StrokeWidth;
        // Compress the full width range without flattening ordinary slider values.
        // Every pattern uses the same width and fixed frame, never a separate ink crop.
        double width = authoredWidth <= 0 ? 0 : highlighter
            ? 20 + 12 * (Math.Max(16, authoredWidth) - 16) / (Math.Max(16, authoredWidth) + 32)
            : 2 + 12 * authoredWidth / (authoredWidth + 12);
        artworkScale = authoredWidth > 0 ? width / (2 * authoredWidth) : 1;
        var geometry = Geometry.Parse(highlighter ? "M18,64 L78,32" : LayerTypeIcon.Geometry(ElementKind.Freehand)).Clone();
        if (!highlighter)
        {
            var bounds = geometry.Bounds;
            double scale = 78 / Math.Max(bounds.Width, bounds.Height);
            geometry.Transform = new MatrixTransform(scale, 0, 0, scale,
                48 - (bounds.Left + bounds.Right) * scale / 2, 48 - (bounds.Top + bounds.Bottom) * scale / 2);
        }
        var path = geometry.GetFlattenedPathGeometry(.01, ToleranceType.Absolute);
        var points = path.Figures.SelectMany(figure => new[] { figure.StartPoint }.Concat(figure.Segments
            .SelectMany(segment => segment switch
            {
                PolyLineSegment polyline => polyline.Points.AsEnumerable(),
                LineSegment line => new[] { line.Point },
                _ => Enumerable.Empty<Point>()
            }))).ToArray();
        double length = points.Zip(points.Skip(1), (a, b) => (b - a).Length).Sum();
        if (width > 0 && !highlighter && parameters.Style.StrokePattern is StrokePattern.Dashed or StrokePattern.DashDot)
        {
            var style = parameters.Style.Clone();
            // Fit at least one long mark and one dot inside the preview frame.
            style.StrokeWidth = Math.Min(width, length / 5);
            var stroke = new CanvasElement
            {
                Kind = highlighter ? ElementKind.Highlighter : ElementKind.Freehand,
                Points = points.Select(point => new PointD(point.X, point.Y)).ToList(),
                Style = style
            };
            using var renderer = new SceneRenderer();
            using var stream = new MemoryStream(renderer.ExportPng(new() { Viewport = new(0, 0, 96, 96), Elements = [stroke] }, new(), true));
            return BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        }
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.PushOpacity(parameters.Style.Opacity);
            var brush = ColorBrush(parameters.Style.Foreground);
            bool dashDot = parameters.Style.StrokePattern == StrokePattern.DashDot;
            if (width > 0 && highlighter && parameters.Style.HighlighterTip == HighlighterTip.Flat)
            {
                var style = parameters.Style.Clone();
                style.StrokeWidth = width;
                style.StrokePattern = StrokePattern.Solid;
                var sample = new StreamGeometry { FillRule = FillRule.Nonzero };
                using (var context = sample.Open())
                {
                    bool dotted = parameters.Style.StrokePattern == StrokePattern.Dotted;
                    int runs = dotted ? 3 : dashDot ? 2 : 1;
                    for (int i = 0; i < runs; i++)
                    {
                        var a = points[0];
                        var b = points[^1];
                        var dot = a + (b - a) * ((i + .5) / runs);
                        if (dashDot)
                        {
                            dot = a + (b - a) * .94;
                            b = a + (b - a) * .34;
                        }
                        PointD[] run = dotted || dashDot && i == 1 ? [new(dot.X, dot.Y)] : [new(a.X, a.Y), new(b.X, b.Y)];
                        foreach (var polygon in ElementGeometry.FlatHighlighterPolygons(run, style))
                        {
                            context.BeginFigure(new(polygon[0].X, polygon[0].Y), true, true);
                            for (int j = 1; j < polygon.Length; j++) context.LineTo(new(polygon[j].X, polygon[j].Y), true, false);
                        }
                    }
                }
                if (parameters.Style.StrokePattern == StrokePattern.Dashed)
                {
                    // Cut gaps from the full tip footprint. Sweeping the tip
                    // separately for each dash can fill those gaps at small sizes.
                    const double halfGap = 4.5;
                    var start = points[0];
                    var direction = points[^1] - start;
                    var bands = new GeometryGroup
                    {
                        Transform = new RotateTransform(Math.Atan2(direction.Y, direction.X) * 180 / Math.PI, start.X, start.Y)
                    };
                    for (int i = 0; i < 3; i++)
                    {
                        double left = i == 0 ? -width : i * length / 3 + halfGap;
                        double right = i == 2 ? length + width : (i + 1) * length / 3 - halfGap;
                        bands.Children.Add(new RectangleGeometry(new Rect(start.X + left, start.Y - width, right - left, 2 * width)));
                    }
                    drawing.PushClip(bands);
                    drawing.DrawGeometry(brush, null, sample);
                    drawing.Pop();
                }
                else drawing.DrawGeometry(brush, null, sample);
            }
            else if (width > 0 && highlighter && dashDot)
            {
                var start = points[0];
                var direction = points[^1] - start;
                var pen = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                drawing.DrawLine(pen, start, start + direction * .34);
                drawing.DrawEllipse(brush, null, start + direction * .94, width / 2, width / 2);
            }
            else if (width > 0 && parameters.Style.StrokePattern == StrokePattern.Dotted)
            {
                int count = highlighter ? 3 : 11;
                for (int i = 0; i < count; i++)
                {
                    path.GetPointAtFractionLength((double)i / (count - 1), out Point point, out _);
                    drawing.DrawEllipse(brush, null, point, width / 2, width / 2);
                }
            }
            else if (width > 0)
            {
                var pen = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                if (parameters.Style.StrokePattern == StrokePattern.Dashed)
                {
                    // Leave room for both round caps and the visible gap at icon scale.
                    int count = highlighter ? 2 : 3;
                    double gap = width + (highlighter ? 9 : 5);
                    double dash = (length - (count - 1) * gap) / count;
                    pen.DashStyle = new DashStyle(new[] { dash / width, gap / width }, 0);
                    pen.DashCap = PenLineCap.Round;
                }
                drawing.DrawGeometry(null, pen, path);
            }
            drawing.Pop();
        }
        var bitmap = new RenderTargetBitmap(96, 96, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }

    private static BitmapSource FitArtwork(BitmapSource source,out double scale)
    {
        var pixels=new FormatConvertedBitmap(source,PixelFormats.Pbgra32,null,0);
        byte[] bytes=new byte[pixels.PixelWidth*pixels.PixelHeight*4];pixels.CopyPixels(bytes,pixels.PixelWidth*4,0);
        int left=pixels.PixelWidth,top=pixels.PixelHeight,right=-1,bottom=-1;
        for(int y=0;y<pixels.PixelHeight;y++)for(int x=0;x<pixels.PixelWidth;x++)if(bytes[(y*pixels.PixelWidth+x)*4+3]!=0)
        {left=Math.Min(left,x);top=Math.Min(top,y);right=Math.Max(right,x);bottom=Math.Max(bottom,y);}
        if(right<left){scale=1;source.Freeze();return source;}
        // Keep one DIP of breathing room inside the 16-DIP button artwork. Text,
        // numbers and shapes have different actual ink bounds, not different UI sizes.
        int width=right-left+1,height=bottom-top+1;
        scale=84d/Math.Max(width,height);
        var visual=new DrawingVisual();RenderOptions.SetBitmapScalingMode(visual,BitmapScalingMode.HighQuality);
        using(var drawing=visual.RenderOpen())drawing.DrawImage(new CroppedBitmap(source,new Int32Rect(left,top,width,height)),
            new Rect((96-width*scale)/2,(96-height*scale)/2,width*scale,height*scale));
        var fitted=new RenderTargetBitmap(96,96,96,96,PixelFormats.Pbgra32);fitted.Render(visual);fitted.Freeze();return fitted;
    }
}
