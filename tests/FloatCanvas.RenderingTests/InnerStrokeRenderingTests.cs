using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class InnerStrokeRenderingTests
{
    internal static void Run(Action<string, bool> check)
    {
        PatternCuts(check);
        PatternEdgesAndProjection(check);
        using var renderer = new SceneRenderer();
        var rectangle = Shape(ElementKind.Rectangle);
        var document = new SceneDocument { Viewport = new(0, 0, 120, 100), Elements = [rectangle] };
        using var outline = Render(renderer, document);
        check("inside rectangle stroke never expands the declared outer size", outline.GetPixel(19, 40).Alpha == 0 && outline.GetPixel(80, 40).Alpha == 0 && outline.GetPixel(40, 19).Alpha == 0 && outline.GetPixel(40, 60).Alpha == 0);
        check("inside stroke keeps its full requested eight-pixel thickness", outline.GetPixel(20, 40).Alpha == 255 && outline.GetPixel(27, 40).Alpha == 255 && outline.GetPixel(28, 40).Alpha == 0);
        check("zero-radius rectangle stroke keeps square outer corners", outline.GetPixel(20, 20).Alpha == 255 && outline.GetPixel(21, 21).Alpha == 255);
        check("inside stroke bounds equal the declared rectangle", ElementGeometry.StrokedLocalBounds(rectangle) == new RectD(0, 0, 60, 40));
        check("inside stroke is picked on the inner half formerly omitted", ElementGeometry.HitTest(rectangle, new(27, 40), 0));
        check("inside stroke is not picked beyond the declared outer contour", !ElementGeometry.HitTest(rectangle, new(18, 40), 0));

        rectangle.Style.Fill = 0x800000FF; rectangle.Style.Stroke = 0x80FF0000;
        using var translucent = Render(renderer, document);
        check("translucent fill and stroke do not stack their alpha", translucent.GetPixel(22, 40).Alpha == 128 && translucent.GetPixel(40, 40).Alpha == 128);
        check("stroke owns its region while fill owns the remaining interior", translucent.GetPixel(22, 40).Red > 250 && translucent.GetPixel(22, 40).Blue < 3 && translucent.GetPixel(40, 40).Blue > 250);
        check("the fill-stroke transition has no doubled-alpha pixels", Enumerable.Range(20, 60).All(x => translucent.GetPixel(x, 40).Alpha <= 129));
        rectangle.Style.Stroke = 0;
        using var noStroke = Render(renderer, document);
        check("a fully transparent stroke does not cut away the fill", noStroke.GetPixel(20, 40).Alpha == 128 && noStroke.GetPixel(20, 40).Blue > 250);
        rectangle.Style.Stroke = 0x80FF0000;
        using (var background = new SKBitmap(120, 100))
        using (var canvas = new SKCanvas(background))
        {
            canvas.Clear(SKColors.Green);
            renderer.Draw(canvas, document, new(), includeBackground: false, includeCanvasBackground: false, includeGrid: false);
            var edge = background.GetPixel(22, 40); var center = background.GetPixel(40, 40);
            check("replacing fill never clears the scene behind a translucent stroke", edge.Alpha == 255 && edge.Red >= 127 && edge.Green >= 63 && edge.Blue == 0);
            check("the remaining translucent fill still composites over the scene", center.Alpha == 255 && center.Blue >= 127 && center.Green >= 63 && center.Red == 0);
        }
        rectangle.Style.Opacity = .5;
        using var elementOpacity = Render(renderer, document);
        check("element opacity is applied once after fill-stroke replacement", elementOpacity.GetPixel(22, 40).Alpha == 64 && elementOpacity.GetPixel(40, 40).Alpha == 64);
        rectangle.Style.Opacity = 1;

        foreach (var kind in new[] { ElementKind.Rectangle, ElementKind.RoundedRectangle, ElementKind.Ellipse, ElementKind.Triangle, ElementKind.Symbol })
        {
            foreach (var pattern in Enum.GetValues<StrokePattern>())
            {
                var element = Shape(kind); element.Style.StrokePattern = pattern; document.Elements = [element];
                using var ink = Render(renderer, document);
                check(kind + " " + pattern + " inner stroke remains inside its declared rectangular envelope", OutsideInk(ink, new(20, 20, 60, 40)) == 0);
                if (pattern == StrokePattern.Solid)
                {
                    var fillOnly = element.Clone(); fillOnly.Style.StrokeWidth = 0; fillOnly.Style.Fill = 0xFFFF0000;
                    document.Elements = [fillOnly]; using var filledShape = Render(renderer, document); document.Elements = [element];
                    check(kind + " fill-only and outline-only have the same visible outer dimensions", InkBounds(ink) == InkBounds(filledShape));
                }
                int missedInk = 0;
                for (int y = 0; y < ink.Height; y++) for (int x = 0; x < ink.Width; x++)
                    if (ink.GetPixel(x, y).Alpha > 240 && !ElementGeometry.HitTest(element, new(x + .5, y + .5), 0)) missedInk++;
                check(kind + " " + pattern + " fully covered inner ink can be picked without extra tolerance (" + missedInk + ")", missedInk == 0);
                element.Style.Stroke = 0x80FF0000; element.Style.Fill = 0x800000FF;
                using var combined = Render(renderer, document);
                int maxAlpha = 0;
                for (int y = 0; y < combined.Height; y++) for (int x = 0; x < combined.Width; x++) maxAlpha = Math.Max(maxAlpha, combined.GetPixel(x, y).Alpha);
                check(kind + " " + pattern + " complementary translucent regions never double alpha (max " + maxAlpha + ")", maxAlpha <= 129 && maxAlpha >= 128);
            }
        }
        foreach (var symbol in Enum.GetValues<SymbolKind>())
        foreach (var pattern in Enum.GetValues<StrokePattern>())
        {
            var element = Shape(ElementKind.Symbol); element.Symbol = symbol; element.Style.StrokePattern = pattern; document.Elements = [element];
            using var ink = Render(renderer, document);
            int missed = 0;
            for (int y = 0; y < ink.Height; y++) for (int x = 0; x < ink.Width; x++)
                if (ink.GetPixel(x, y).Alpha > 240 && !ElementGeometry.HitTest(element, new(x + .5, y + .5), 0)) missed++;
            check(symbol + " " + pattern + " inner outline has consistent zero-tolerance picking (" + missed + ")", missed == 0);
        }
        var line = new CanvasElement { Kind = ElementKind.Line, Points = [new(20, 40), new(80, 40)], Style = new() { Stroke = 0xFFFF0000, StrokeWidth = 8 } };
        document.Elements = [line]; using var lineInk = Render(renderer, document);
        check("open lines retain centered strokes and round caps", lineInk.GetPixel(40, 37).Alpha == 255 && lineInk.GetPixel(40, 43).Alpha == 255 && lineInk.GetPixel(18, 40).Alpha > 200);
    }

    private static void PatternCuts(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer();
        var shape = Shape(ElementKind.Rectangle); shape.Width = 64; shape.Style.StrokeWidth = 8; shape.Style.StrokePattern = StrokePattern.Dashed;
        var document = new SceneDocument { Viewport = new(0,0,120,100), Elements = [shape] };
        using var dash = Render(renderer,document);
        check("rectangle dashes cut flat gaps from the complete square-cornered solid band", dash.GetPixel(20,20).Alpha == 255 && dash.GetPixel(45,27).Alpha == 255 && dash.GetPixel(56,23).Alpha == 0);
        shape.Kind = ElementKind.Ellipse; shape.Width = 80; shape.Height = 60; shape.Style.StrokePattern = StrokePattern.Dotted;
        using var dot = Render(renderer,document);
        var bounds = ComponentBounds(dot, 95, 50);
        check("ellipse dotted outline contains full round dots rather than clipped semicircles", bounds.Width >= 7 && bounds.Width <= 10 && Math.Abs(bounds.Width-bounds.Height) <= 1);
    }

    private static RectD ComponentBounds(SKBitmap bitmap, int x, int y)
    {
        if (bitmap.GetPixel(x,y).Alpha == 0) return default;
        var pending = new Queue<(int X,int Y)>(); var visited = new HashSet<(int,int)>(); pending.Enqueue((x,y)); visited.Add((x,y));
        int left=x,top=y,right=x,bottom=y;
        while(pending.TryDequeue(out var p))
        {
            left=Math.Min(left,p.X);right=Math.Max(right,p.X);top=Math.Min(top,p.Y);bottom=Math.Max(bottom,p.Y);
            foreach(var delta in new[]{(-1,0),(1,0),(0,-1),(0,1)})
            {
                var next=(p.X+delta.Item1,p.Y+delta.Item2);
                if(next.Item1>=0&&next.Item1<bitmap.Width&&next.Item2>=0&&next.Item2<bitmap.Height&&bitmap.GetPixel(next.Item1,next.Item2).Alpha>0&&visited.Add(next))pending.Enqueue(next);
            }
        }
        return new(left,top,right-left+1,bottom-top+1);
    }

    private static void PatternEdgesAndProjection(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer();
        var line = new CanvasElement { Kind=ElementKind.Line, Points=[new(20,40),new(80,40)],
            Style=new() { Stroke=0xFFFF0000, StrokeWidth=8, StrokePattern=StrokePattern.Dashed } };
        var document = new SceneDocument { Viewport=new(0,0,600,260), Elements=[line] };
        using (var ink = Render(renderer,document))
        {
            check("open dashed line cuts a flat full-width gap",ink.GetPixel(51,37).Alpha==255 && ink.GetPixel(52,40).Alpha==0);
            check("open dashed line preserves both original solid endpoint caps when visible",ink.GetPixel(17,40).Alpha>200 && ink.GetPixel(82,40).Alpha>200);
        }
        line.Points=[new(20,20),new(40,20),new(40,60)];
        using (var ink=Render(renderer,document))
            check("a retained dash crosses a polyline corner without a triangular hole",ink.GetPixel(41,18).Alpha==255 && ink.GetPixel(40,34).Alpha==0);
        line.Style.StrokePattern=StrokePattern.Dotted;
        using (var ink=Render(renderer,document))
        {
            var circle=ComponentBounds(ink,40,40);
            check("open dotted polylines keep complete full-diameter circles",circle.Width==8 && circle.Height==8);
        }
        line.Points=[new(20,20),new(40,20),new(20,20)];line.Style.StrokePattern=StrokePattern.Dashed;
        using(var ink=Render(renderer,document))
            check("a retained dash preserves the solid round join at a reversing polyline corner",ink.GetPixel(42,20).Alpha>200);

        var rounded=Shape(ElementKind.RoundedRectangle); rounded.Width=140; rounded.Height=120;
        rounded.Style.StrokeWidth=14; rounded.Style.StrokePattern=StrokePattern.Dashed; document.Elements=[rounded];
        using (var dashed=Render(renderer,document))
        {
            var solid=rounded.Clone(); solid.Style.StrokePattern=StrokePattern.Solid; document.Elements=[solid];
            using var original=Render(renderer,document); document.Elements=[rounded];
            bool corners=true;
            for(int y=24;y<32;y++)for(int x=24;x<32;x++)
                if(original.GetPixel(x,y).Alpha==255) corners &= dashed.GetPixel(x,y).Alpha>240;
            check("a retained rounded corner preserves the solid band without radial seams",corners);
        }
        var bubble=Shape(ElementKind.Symbol); bubble.Symbol=SymbolKind.SpeechBubble; bubble.Width=140; bubble.Height=120;
        bubble.Style.StrokeWidth=14; bubble.Style.StrokePattern=StrokePattern.Dashed; document.Elements=[bubble];
        using(var ink=Render(renderer,document))
            check("retained speech-bubble right-angle corner has no isolated triangle or hole",ink.GetPixel(152,26).Alpha==255 && ink.GetPixel(151,27).Alpha==255);

        foreach(var kind in new[]{ElementKind.Rectangle,ElementKind.RoundedRectangle,ElementKind.Ellipse,ElementKind.Symbol})
        foreach(var pattern in new[]{StrokePattern.Dashed,StrokePattern.Dotted})
        foreach(bool narrow in new[]{false,true})
        {
            var element=Shape(kind);element.Symbol=SymbolKind.SpeechBubble;
            element.Width=narrow?28:80;element.Height=narrow?20:60;element.Style.StrokeWidth=narrow?18:12;element.Style.StrokePattern=pattern;
            element.Transform=new(4,.15,.3,1.5,30,30);
            element.ProjectivePlacement=new(1,.05,0,.025,1,0,.002,.001,1);document.Elements=[element];
            using var ink=Render(renderer,document);int missed=0,painted=0;
            for(int y=0;y<ink.Height;y++)for(int x=0;x<ink.Width;x++)
                if(ink.GetPixel(x,y).Alpha==255){painted++;if(!ElementGeometry.HitTest(element,new(x+.5,y+.5),0))missed++;}
            check(kind+" "+pattern+" "+(narrow?"narrow thick":"large projected")+" ink keeps exact picking ("+missed+" misses)",missed==0 && (narrow||painted>0));
            if(pattern==StrokePattern.Dotted)
            {
                var geometry=ElementGeometry.StrokePatternGeometry(element); double boundaryError=0;
                using var boundary=new SKPathBuilder();
                if(kind==ElementKind.Ellipse)boundary.AddOval(new(0,0,(float)element.Width,(float)element.Height));
                else if(kind is ElementKind.Rectangle or ElementKind.RoundedRectangle)boundary.AddRoundRect(new(0,0,(float)element.Width,(float)element.Height),(float)ElementGeometry.CornerRadius(element),(float)ElementGeometry.CornerRadius(element));
                else {var outline=ElementGeometry.Outline(element);boundary.MoveTo((float)outline[0].X,(float)outline[0].Y);foreach(var p in outline.Skip(1))boundary.LineTo((float)p.X,(float)p.Y);boundary.Close();}
                using var outer=boundary.Detach();
                using var pen=new SKPaint { Style=SKPaintStyle.Stroke,StrokeWidth=(float)element.Style.StrokeWidth*2,StrokeJoin=SKStrokeJoin.Miter,StrokeMiter=4,StrokeCap=SKStrokeCap.Round };
                using var expanded=pen.GetFillPath(outer,16);using var band=outer.Op(expanded,SKPathOp.Intersect)!;
                foreach(var dot in geometry.Dots)for(int i=0;i<512;i++)
                {
                    var direction=new PointD(Math.Cos(i*Math.Tau/512),Math.Sin(i*Math.Tau/512));
                    var p=dot+direction*(geometry.Radius-1e-4);
                    if(band.Contains((float)p.X,(float)p.Y))continue;
                    double first=0,last=geometry.Radius;
                    for(int step=0;step<24;step++) {double middle=(first+last)/2;var q=dot+direction*middle;if(band.Contains((float)q.X,(float)q.Y))first=middle;else last=middle;}
                    boundaryError=Math.Max(boundaryError,geometry.Radius-first);
                }
                Console.WriteLine("DOT_NATIVE_BAND_ERROR "+kind+" narrow="+narrow+" maxLocal="+boundaryError.ToString("F6")+" dots="+geometry.Dots.Count);
                check(kind+" "+(narrow?"narrow thick":"projected")+" full dots fit within native stroke curve approximation",boundaryError<=.01);
            }
        }
        // A local epsilon must never become a visible hit margin after a large scale.
        line.Points=[new(0,0),new(60,0)];line.Style.StrokePattern=StrokePattern.Dashed;
        line.Transform=Affine2D.Translation(30,30).Compose(Affine2D.Scale(10000,100));line.ProjectivePlacement=null;
        check("large nonuniform transforms preserve a truly empty dash gap at zero tolerance",!ElementGeometry.HitTest(line,new(30+32.001*10000,30),0));
    }

    internal static void WriteEvidence(string directory)
    {
        Directory.CreateDirectory(directory);
        using var renderer = new SceneRenderer();
        var document = new SceneDocument { Viewport = new(0, 0, 600, 190) };
        for (int index = 0; index < 3; index++)
        {
            var element = Shape(ElementKind.Rectangle); element.Width = 140; element.Height = 120;
            element.Transform = Affine2D.Translation(30 + 200 * index, 35); element.Style.StrokeWidth = 14;
            element.Style.Fill = index == 1 ? 0 : 0x800000FF;
            element.Style.Stroke = index == 0 ? 0 : 0x80FF0000;
            document.Elements.Add(element);
        }
        File.WriteAllBytes(Path.Combine(directory, "inside-stroke-and-fill-regions.png"), renderer.ExportPng(document, new(), true));
        document.Elements.Clear();
        for (int index = 0; index < 3; index++)
        {
            var element = Shape(index == 0 ? ElementKind.RoundedRectangle : index == 1 ? ElementKind.Ellipse : ElementKind.Symbol);
            element.Symbol = SymbolKind.SpeechBubble; element.Width = 140; element.Height = 120;
            element.Transform = Affine2D.Translation(30 + 200 * index, 35); element.Style.StrokeWidth = 14;
            element.Style.Stroke = 0x80FF0000; element.Style.Fill = 0x800000FF;
            document.Elements.Add(element);
        }
        File.WriteAllBytes(Path.Combine(directory, "inside-stroke-closed-shapes.png"), renderer.ExportPng(document, new(), true));
        document.Elements[0].Style.StrokePattern = StrokePattern.Dashed;
        document.Elements[1].Style.StrokePattern = StrokePattern.Dotted;
        document.Elements[2].Style.StrokePattern = StrokePattern.Dashed;
        File.WriteAllBytes(Path.Combine(directory, "inside-stroke-patterns.png"), renderer.ExportPng(document, new(), true));
        using (var bitmap = new SKBitmap(2400, 760))
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White); canvas.Scale(4);
            renderer.Draw(canvas, document, new(), includeBackground:false, includeCanvasBackground:false, includeGrid:false);
            using var image = SKImage.FromBitmap(bitmap); using var png = image.Encode(SKEncodedImageFormat.Png,100);
            File.WriteAllBytes(Path.Combine(directory,"inside-stroke-patterns-white-4x.png"),png.ToArray());
        }
    }

    private static CanvasElement Shape(ElementKind kind) => new()
    {
        Kind = kind, Width = 60, Height = 40, CornerRadius = kind == ElementKind.RoundedRectangle ? 12 : 0,
        Transform = Affine2D.Translation(20, 20), Style = new() { Fill = 0, Stroke = 0xFFFF0000, StrokeWidth = 8 }
    };
    private static SKBitmap Render(SceneRenderer renderer, SceneDocument document) => SKBitmap.Decode(renderer.ExportPng(document, new(), true));
    private static int OutsideInk(SKBitmap bitmap, RectD box)
    {
        int count = 0;
        for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++)
            if ((x < box.Left || x >= box.Right || y < box.Top || y >= box.Bottom) && bitmap.GetPixel(x, y).Alpha > 0) count++;
        return count;
    }
    private static RectD InkBounds(SKBitmap bitmap)
    {
        int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
        for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++)
            if (bitmap.GetPixel(x, y).Alpha > 0) { left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
        return right < left ? default : new(left, top, right - left + 1, bottom - top + 1);
    }
}
