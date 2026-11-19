using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class InnerStrokeRenderingTests
{
    internal static void Run(Action<string, bool> check)
    {
        PatternCuts(check);
        PatternEdgesAndProjection(check);
        FlatHighlighter(check);
        HighlighterOverlap(check);
        RotatedHighlighterCoverage(check);
        HighlighterRasterReuse(check);
        LargeFlatHighlighterMask(check);
        HighlighterOverlapStaysPut(check);
        HighlighterInkMatchesDabs(check);
        HighlighterSamplingAndContrast(check);
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
    private static void FlatHighlighter(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer();
        var stroke = new CanvasElement { Kind = ElementKind.Highlighter, Points = [new(40, 40), new(220, 40), new(220, 100)],
            Style = new() { HighlighterTip = HighlighterTip.Flat, StrokeWidth = 24 } };
        var document = new SceneDocument { Viewport = new(0, 0, 280, 120), Elements = [stroke] };
        using var vertical = Render(renderer, document);
        byte ink = vertical.GetPixel(100, 40).Alpha;
        check("vertical flat tip gives a broad horizontal stroke, a narrow vertical stroke and square caps",
            ink == 89 && vertical.GetPixel(38, 29).Alpha is > 0 and < 89 && vertical.GetPixel(36, 40).Alpha == 0 &&
            vertical.GetPixel(220, 88).Alpha > ink && vertical.GetPixel(225, 88).Alpha == 0);
        check("flat tip ink picking matches its cap and thin direction",
            ElementGeometry.HitTest(stroke, new(38, 29), 0) && !ElementGeometry.HitTest(stroke, new(36, 40), 0) &&
            !ElementGeometry.HitTest(stroke, new(225, 88), 0));
        check("flat tip expands the centerline's bounds by its oriented footprint",
            ElementGeometry.StrokedLocalBounds(stroke) == new RectD(37, 28, 186, 84));
        check("ink changes with the distance traveled over the inside and outside of a corner",
            vertical.GetPixel(219, 44).Alpha > vertical.GetPixel(220, 31).Alpha);

        stroke.Style.HighlighterTipAngle = 90;
        using var horizontal = Render(renderer, document);
        check("rotating the tip updates the existing render cache and swaps the wide and narrow directions",
            horizontal.GetPixel(38, 29).Alpha == 0 && horizontal.GetPixel(225, 88).Alpha > 0 &&
            Math.Abs(horizontal.GetPixel(220, 80).Alpha - ink) <= 1);

        stroke.Style.HighlighterTipAngle = 0;
        stroke.Points = [new(40, 40), new(220, 40), new(40, 40)];
        using var retrace = Render(renderer, document);
        check("a flat tip darkens through the return with a lighter outer cap",
            retrace.GetPixel(100, 40).Alpha > ink + 45 && retrace.GetPixel(220, 40).Alpha > retrace.GetPixel(222, 40).Alpha);

        stroke.Points = [new(40, 40), new(220, 40)];
        foreach (var pattern in new[] { StrokePattern.Dashed, StrokePattern.Dotted })
        {
            stroke.Style.StrokePattern = pattern;
            using var marks = Render(renderer, document);
            check("flat " + pattern + " keeps the tip's square corners and visible gaps",
                marks.GetPixel(38, 29).Alpha > 0 && marks.GetPixel(151, 40).Alpha == 0 &&
                !ElementGeometry.HitTest(stroke, new(151, 40), 0));
        }
        stroke.Points = [new(40, 40)];
        using var dab = Render(renderer, document);
        check("a flat highlighter click produces one aligned rectangular dab",
            dab.GetPixel(38, 29).Alpha == 41 && dab.GetPixel(44, 40).Alpha == 0);
    }

    private static void HighlighterOverlap(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer();
        foreach (var tip in new[] { HighlighterTip.Round, HighlighterTip.Flat })
        foreach (var opacity in new[] { 1d, .5d })
        {
            var stroke = new CanvasElement
            {
                Kind = ElementKind.Highlighter,
                Points = [new(25, 25), new(80, 65), new(135, 105), new(25, 105), new(135, 25)],
                Style = new() { HighlighterTip = tip, StrokeWidth = 24, Foreground = 0xFF1872D4, Opacity = opacity }
            };
            var document = new SceneDocument { Viewport = new(0, 0, 160, 135), Elements = [stroke] };
            using var crossing = Render(renderer, document);
            byte one = crossing.GetPixel(49, 42).Alpha;
            byte two = crossing.GetPixel(80, 65).Alpha;
            check($"{tip} at {opacity} accumulates two visits where one gesture crosses itself ({one}, {two})",
                one >= 87 * opacity && two > one + 40 * opacity && two < 180 * opacity);
            check($"{tip} at {opacity} keeps the foreground color when accumulating",
                Math.Abs(crossing.GetPixel(80, 65).Blue - 0xD4) <= 3 && crossing.GetPixel(80, 65).Red < 30);

            stroke.Points = [new(25, 40), new(36, 39.5), new(54, 40), new(85, 40.5), new(135, 40)];
            using var forward = Render(renderer, document);
            check($"{tip} at {opacity} stays uniform across pointer samples on an ordinary line",
                Enumerable.Range(40, 78).All(x => Math.Abs(forward.GetPixel(x, 40).Alpha - 89 * opacity) <= 2));
            stroke.Points.Add(new(25, 40)); stroke.Points.Add(new(135, 40));
            using var retraced = Render(renderer, document);
            check($"{tip} at {opacity} accumulates three coats from repeated passes",
                Math.Abs(retraced.GetPixel(80, 40).Alpha - 184 * opacity) <= 2);

            stroke.Points = [new(25, 40), new(145, 40), new(25, 40)];
            foreach (var pattern in new[] { StrokePattern.Dashed, StrokePattern.Dotted })
            {
                stroke.Style.StrokePattern = pattern;
                using var patterned = Render(renderer, document);
                byte once = (byte)Math.Round((pattern == StrokePattern.Dotted ? 41 : 89) * opacity);
                check($"{tip} {pattern} at {opacity} accumulates ink at repeated marks",
                    patterned.GetPixel(85, 40).Alpha > once + 25 * opacity);
                if (pattern == StrokePattern.Dashed)
                    check($"{tip} at {opacity} does not deposit a second coat from a dash gap",
                        Math.Abs(patterned.GetPixel(105, 40).Alpha - 89 * opacity) <= 1);
            }
        }
    }

    private static void RotatedHighlighterCoverage(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer();
        var stroke = new CanvasElement
        {
            Kind = ElementKind.Highlighter,
            Points = Enumerable.Range(0, 145).Select(i => new PointD(20 + i * 1.3, 60 + .0006 * i * i)).ToList(),
            Style = new() { HighlighterTip = HighlighterTip.Flat, HighlighterTipAngle = 42, StrokeWidth = 32 }
        };
        var document = new SceneDocument { Viewport = new(0, 0, 240, 110), Elements = [stroke] };
        using var pixels = Render(renderer, document);
        var interior = Enumerable.Range(48, 144).SelectMany(x =>
            Enumerable.Range(-5, 11).Select(offset => (X: x, Y: (int)(60 + .0006 * Math.Pow((x - 20) / 1.3, 2)) + offset)))
            .Select(p => (int)pixels.GetPixel(p.X, p.Y).Alpha).ToArray();
        check($"rotated highlighter has a smooth transverse density profile ({interior.Min()}–{interior.Max()})",
            interior.Min() > 40 && interior.Max() > interior.Min() + 12 && interior.Max() < 130);

        stroke.Points = [new(25, 25), new(195, 85), new(25, 85), new(195, 25)];
        using var crossed = Render(renderer, document);
        check("rotated highlighter keeps both coats at a real crossing",
            crossed.GetPixel(110, 55).Alpha > crossed.GetPixel(60, 37).Alpha + 40);

        stroke.Style.HighlighterTipAngle = 19.782;
        stroke.Style.StrokeWidth = 28.166;
        stroke.Points = [new(24, 145)];
        for (int i = 0; i < 80; i++)
        {
            stroke.Points.Add(new(25 + i * 2, 145 - i * 3 / 2));
            stroke.Points.Add(new(25 + i * 2, 145 - (i + 1) * 3 / 2));
            stroke.Points.Add(new(26 + i * 2, 145 - (i + 1) * 3 / 2));
        }
        document.Viewport = new(0, 0, 240, 175);
        using var diagonal = Render(renderer, document);
        int peak = 0;
        for (int y = 0; y < diagonal.Height; y++)
            for (int x = 0; x < diagonal.Width; x++) peak = Math.Max(peak, diagonal.GetPixel(x, y).Alpha);
        var centers = Enumerable.Range(0, 66).Select(i => (int)diagonal.GetPixel(35 + 2 * i, 145 - (11 + 2 * i) * 3 / 4).Alpha).ToArray();
        check($"a rotated highlighter has no repeated nib-edge bands on integer staircase motion (peak alpha {peak})",
            centers.Min() > 80 && centers.Max() - centers.Min() <= 5 && peak < 147);
    }

    private static void HighlighterRasterReuse(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer();
        using var reference = new SceneRenderer { CacheByteLimit = 0 };
        using var cache = new OverlaySceneCache(renderer);
        using var referenceCache = new OverlaySceneCache(reference);
        using var surface = SKSurface.Create(new SKImageInfo(240, 200, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var expected = SKSurface.Create(new SKImageInfo(240, 200, SKColorType.Bgra8888, SKAlphaType.Premul));
        var assets = new AssetLibrary();
        var document = new SceneDocument { Viewport = new(0, 0, 240, 200) };
        for (int i = 0; i < 6; i++)
            document.Elements.Add(new()
            {
                Kind = ElementKind.Highlighter,
                Points = [new(25, 25), new(175, 95), new(25, 95), new(175, 25)],
                Style = new() { HighlighterTip = HighlighterTip.Flat, HighlighterTipAngle = 19.782, StrokeWidth = 24 },
                Transform = Affine2D.Translation(i * 8, i * 9)
            });
        for (int i = 0; i < 5; i++)
        {
            surface.Canvas.Clear(SKColors.Transparent);
            cache.DrawOnClearedRasterTarget(surface, document, assets, 1, 0);
        }
        check("six unchanged strokes retain their raster across idle tile frames", renderer.HighlighterRasterBuildCount == 6);
        document.Elements.Add(document.Elements[0].Clone(newId: true));
        DrawGesture();
        check("the next gesture rebuilds only its new highlighter raster", renderer.HighlighterRasterBuildCount == 7);
        CheckPixels("retained rasters preserve uncached overlapping highlighter pixels");

        document.Elements[^1].Style.Foreground = 0xFF3344BB;
        document.Elements[^1].Style.HighlighterTipAngle = -31;
        document.Elements[^1].Points.Add(new(35, 26));
        DrawGesture();
        check("edited highlighter geometry and color invalidate its raster", renderer.HighlighterRasterBuildCount == 8);
        CheckPixels("edited highlighter pixels match the uncached renderer");
        renderer.CacheByteLimit = 0;
        DrawGesture();
        check("disabling the renderer cache releases retained highlighter rasters", renderer.HighlighterRasterBytes == 0);

        void DrawGesture()
        {
            surface.Canvas.Clear(SKColors.Transparent);
            cache.DrawOnClearedRasterTarget(surface, document, assets, 1, 1, 6, 6);
        }

        void CheckPixels(string name)
        {
            expected.Canvas.Clear(SKColors.Transparent);
            referenceCache.DrawOnClearedRasterTarget(expected, document, assets, 1, 1, 6, 6);
            using var actualPixels = surface.PeekPixels();
            using var expectedPixels = expected.PeekPixels();
            var actual = actualPixels.GetPixelSpan();
            var baseline = expectedPixels.GetPixelSpan();
            int max = 0;
            for (int i = 0; i < actual.Length; i++) max = Math.Max(max, Math.Abs(actual[i] - baseline[i]));
            // Raster reuse introduces one additional 8-bit quantization before source-over.
            check($"{name} (maximum channel difference {max})", max <= 3);
        }
    }

    private static void LargeFlatHighlighterMask(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer();
        using var reference = new SceneRenderer { CacheByteLimit = 0 };
        PointD[] corners = [new(150, 150), new(1450, 1000), new(150, 1000), new(1450, 150)];
        var stroke = new CanvasElement
        {
            Kind = ElementKind.Highlighter,
            Points = corners.Zip(corners.Skip(1), (start, end) => Enumerable.Range(0, 360)
                .Select(i => start + (end - start) * (i / 360d))).SelectMany(segment => segment).Append(corners[^1]).ToList(),
            Style = new() { HighlighterTip = HighlighterTip.Flat, HighlighterTipAngle = 19.782, StrokeWidth = 28, Opacity = .7 }
        };
        var document = new SceneDocument { Viewport = new(0, 0, 1600, 1100), Elements = [stroke] };
        using var actual = Render(renderer, document);
        using var baseline = Render(reference, document);
        var solo = actual.GetPixel(410, 320);
        var crossing = actual.GetPixel(800, 575);
        check("large CPU highlighter mask retains single and double coats with element opacity",
            solo.Alpha is > 55 and < 100 && crossing.Alpha > solo.Alpha + 25 && crossing.Alpha < 255 * .7);
        int max = 0;
        for (int y = 0; y < actual.Height; y++)
            for (int x = 0; x < actual.Width; x++)
                max = Math.Max(max, Math.Abs(actual.GetPixel(x, y).Alpha - baseline.GetPixel(x, y).Alpha));
        check($"large retained highlighter mask matches uncached coverage (maximum alpha difference {max})", max == 0);
    }

    private static void HighlighterOverlapStaysPut(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer();
        foreach (var tip in new[] { HighlighterTip.Round, HighlighterTip.Flat })
            CheckTip(tip);

        void CheckTip(HighlighterTip tip)
        {
            PointD[] corners = [new(30, 35), new(150, 145), new(30, 145), new(150, 35), new(290, 35)];
            var stroke = new CanvasElement
            {
                Kind = ElementKind.Highlighter,
                Points = corners.Zip(corners.Skip(1), (start, end) => Enumerable.Range(0, 35).Select(i =>
                    start + (end - start) * (i / 35d) + new PointD(0, .7 * Math.Sin(i * 1.7))))
                    .SelectMany(segment => segment).Append(corners[^1]).ToList(),
                Style = new() { HighlighterTip = tip, HighlighterTipAngle = 19.782, StrokeWidth = 32 }
            };
            var document = new SceneDocument { Viewport = new(0, 0, 450, 340), Elements = [stroke] };
            using var crossed = Render(renderer, document);
            check($"{tip} overlap stability fixture has a visibly darker crossing", crossed.GetPixel(90, 90).Alpha > 135);
            int largestChange = 0;
            for (int i = 1; i <= 14; i++)
            {
                stroke.Points.Add(new(290 + i * 7, 35 + i * 16));
                using var continued = Render(renderer, document);
                for (int y = 72; y <= 108; y++)
                    for (int x = 70; x <= 110; x++)
                        largestChange = Math.Max(largestChange, Math.Abs(crossed.GetPixel(x, y).Alpha - continued.GetPixel(x, y).Alpha));
            }
            check($"{tip} extension far from an earlier crossing keeps it stable (delta {largestChange})", largestChange == 0);
        }
    }

    private static void HighlighterInkMatchesDabs(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer();
        var stroke = new CanvasElement { Kind = ElementKind.Highlighter, Style = new() { StrokeWidth = 44 } };
        var document = new SceneDocument { Viewport = new(0, 0, 275, 220), Elements = [stroke] };
        foreach (var tip in new[] { HighlighterTip.Round, HighlighterTip.Flat })
        {
            stroke.Style.HighlighterTip = tip;
            var c = Enumerable.Range(0, 121).Select(i =>
            {
                double angle = Math.PI / 3 + Math.PI * i / 90;
                return new PointD(120 + 55 * Math.Cos(angle), 100 + 55 * Math.Sin(angle));
            }).ToArray();
            using var arc = Compare("C", c, [(65, 100), (120, 45), (120, 51), (120, 39), (175, 100)]);
            check($"{tip} leaves the opening of the C transparent", arc.GetPixel(175, 100).Alpha == 0);
            if (tip == HighlighterTip.Round)
                check("a round C is darker toward the inside at equal distance from the centerline",
                    arc.GetPixel(120, 51).Alpha > arc.GetPixel(120, 38).Alpha);

            foreach (int radius in new[] { 4, 15, 35 })
            {
                PointD[] u = [new(45, 75 - radius), .. Enumerable.Range(0, 65).Select(i =>
                {
                    double angle = Math.PI * i / 64;
                    return new PointD(150 + radius * Math.Sin(angle), 75 - radius * Math.Cos(angle));
                }), new(45, 75 + radius)];
                using var bend = Compare($"U radius {radius}", u,
                    [(150 + radius, 75), (147, 75), (95, 75), (95, 75 - radius), (95, 83 + radius)]);
                int between = bend.GetPixel(95, 75).Alpha;
                check($"{tip} wide U leaves a gap and narrow U overlaps (radius {radius}, alpha {between})",
                    radius == 35 ? between == 0 : radius == 15 ? between > 50 : between > 130);
            }

            var eight = Enumerable.Range(0, 181).Select(i =>
            {
                double angle = Math.PI / 2 + Math.PI * i / 90;
                return new PointD(125 + 70 * Math.Sin(angle), 110 + 45 * Math.Sin(2 * angle));
            }).ToArray();
            using var crossing = Compare("figure eight", eight, [(125, 110), (76, 65), (139, 110)]);
            check($"{tip} figure eight darkens at the crossing",
                crossing.GetPixel(125, 110).Alpha > crossing.GetPixel(76, 65).Alpha + 35);

            PointD[] incoming = [new(35, 110), new(182, 65)];
            PointD[] returning = [.. incoming, new(181.6, 65.5), new(182, 65), new(37, 137)];
            stroke.Style.HighlighterTipAngle = 25;
            using var before = Compare("incoming leg", incoming, [(182, 65), (161, 72)]);
            foreach (var next in returning.Skip(incoming.Length))
            {
                stroke.Points.Add(next);
                using var preview = Render(renderer, document);
            }
            using var live = Render(renderer, document);
            using var uncached = new SceneRenderer { CacheByteLimit = 0 };
            using var complete = Render(uncached, document);
            check($"{tip} fold with hesitation matches the final render at every pixel", live.Pixels.AsSpan().SequenceEqual(complete.Pixels));
            using var folded = Compare("sharp return with hesitation", returning, [(182, 65), (161, 72), (176, 70), (177, 61)]);
            check($"{tip} deposits new ink at the pivot while reversing", folded.GetPixel(182, 65).Alpha > before.GetPixel(182, 65).Alpha + 20);
            stroke.Style.HighlighterTipAngle = 0;
        }

        SKBitmap Compare(string name, PointD[] points, (int X, int Y)[] samples)
        {
            stroke.Points = points.ToList();
            var pixels = Render(renderer, document);
            int maxDifference = samples.Max(p => Math.Abs(pixels.GetPixel(p.X, p.Y).Alpha - SampleDenseDabs(points, stroke.Style, p.X, p.Y)));
            check($"{stroke.Style.HighlighterTip} {name} agrees with closely spaced weighted dabs (max alpha delta {maxDifference})",
                maxDifference <= 2);
            return pixels;
        }
    }

    // Independent numerical reference: place low-flow nib impressions every 0.12 local pixels.
    // Nothing here clips a segment to the footprint or uses the renderer's polynomial integrals.
    private static int SampleDenseDabs(PointD[] points, ElementStyle style, int x, int y)
    {
        bool flat = style.HighlighterTip == HighlighterTip.Flat;
        double radius = style.StrokeWidth / 2, contrast = style.HighlighterInkContrast;
        var (sin, cos) = Math.SinCos(style.HighlighterTipAngle * Math.PI / 180);
        double baseDepth = -Math.Log(1 - 89d / 255 * (.93 - .47 * contrast));
        double broadDepth = -Math.Log(1 - 89d / 255);
        double centerDose = Math.Pow(Math.Pow(broadDepth, 4) - Math.Pow(baseDepth, 4), .25);
        double alpha = 0;
        for (int sy = 0; sy < 2; sy++)
        for (int sx = 0; sx < 2; sx++)
        {
            var sample = new PointD(x + .25 + sx * .5, y + .25 + sy * .5);
            double amount = 0;
            bool touched = Weight(sample - points[0]) >= 0;
            for (int i = 1; i < points.Length; i++)
            {
                var delta = points[i] - points[i - 1];
                double length = delta.DistanceTo(default);
                if (length == 0) continue;
                var unit = delta * (1 / length);
                double scale = centerDose / (4 * radius / 3);
                if (flat)
                {
                    double chord = 2 * radius / Math.Max(Math.Abs(-sin * unit.X + cos * unit.Y),
                        4 * Math.Abs(cos * unit.X + sin * unit.Y));
                    double weightSum = 0;
                    for (int k = 0; k < 32; k++)
                        weightSum += Weight(unit * (chord * ((k + .5) / 32 - .5)));
                    scale = centerDose * Math.Pow(2 * chord / radius, .44 * contrast) / (chord * weightSum / 32);
                }
                int count = (int)Math.Ceiling(length / .12);
                double ds = length / count;
                for (int j = 0; j < count; j++)
                {
                    double weight = Weight(sample - (points[i - 1] + unit * ((j + .5) * ds)));
                    if (weight < 0) continue;
                    touched = true;
                    amount += scale * weight * ds;
                }
            }
            if (touched) alpha += 1 - Math.Exp(-Math.Pow(Math.Pow(baseDepth, 4) + Math.Pow(amount, 4), .25));
        }
        return (int)Math.Round(255 * style.Opacity * alpha / 4);

        double Weight(PointD offset)
        {
            if (!flat) return 1 - (offset.X * offset.X + offset.Y * offset.Y) / (radius * radius);
            double u = (-sin * offset.X + cos * offset.Y) / radius;
            double v = 4 * (cos * offset.X + sin * offset.Y) / radius;
            return Math.Abs(u) > 1 || Math.Abs(v) > 1 ? -1 : (1 - u * u) * (1 - v * v);
        }
    }

    private static void HighlighterSamplingAndContrast(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer();
        var stroke = new CanvasElement { Kind = ElementKind.Highlighter, Style = new() { StrokeWidth = 32 } };
        var document = new SceneDocument { Viewport = new(0, 0, 260, 160), Elements = [stroke] };
        PointD[] corners = [new(25, 32), new(202, 84), new(51, 112)];
        foreach (var tip in new[] { HighlighterTip.Round, HighlighterTip.Flat })
        {
            stroke.Style.HighlighterTip = tip;
            stroke.Style.HighlighterTipAngle = 28;
            stroke.Points = corners.ToList();
            using var sparse = Render(renderer, document);
            stroke.Points = corners.Zip(corners.Skip(1), (a, b) => Enumerable.Range(0, 80)
                .Select(i => a + (b - a) * (i / 80d))).SelectMany(s => s).Append(corners[^1]).ToList();
            using var dense = Render(renderer, document);
            int delta = sparse.Pixels.Zip(dense.Pixels, (a,b) => Math.Abs(a.Alpha - b.Alpha)).Max();
            check($"{tip} subdividing the same path leaves the ink unchanged (max alpha delta {delta})", delta <= 1);
            stroke.Points.AddRange(Enumerable.Repeat(corners[^1], 40));
            using var stationary = Render(renderer, document);
            check($"{tip} stationary input never adds ink", dense.Pixels.AsSpan().SequenceEqual(stationary.Pixels));

            stroke.Style.HighlighterTipAngle = 0;
            stroke.Points = [new(25, 50.5), new(205, 50.5), new(205, 135)];
            stroke.Style.HighlighterInkContrast = 0;
            using var soft = Render(renderer, document);
            stroke.Style.HighlighterInkContrast = 1;
            using var strong = Render(renderer, document);
            check($"{tip} full contrast preserves broad center density and lowers the edge",
                soft.GetPixel(95, 50).Alpha == 89 && strong.GetPixel(95, 50).Alpha == 89 &&
                soft.GetPixel(95, 64).Alpha - strong.GetPixel(95, 64).Alpha > 35);
            int narrow = strong.GetPixel(205, 98).Alpha;
            check($"{tip} direction response follows the shape of the nib",
                tip == HighlighterTip.Flat ? narrow > 125 && soft.GetPixel(205, 98).Alpha < 92 : narrow == 89);
            stroke.Style.HighlighterInkContrast = .55;
            using var intermediate = Render(renderer, document);
            check($"{tip} middle contrast lies between the two edge densities",
                intermediate.GetPixel(95, 64).Alpha > strong.GetPixel(95, 64).Alpha && intermediate.GetPixel(95, 64).Alpha < soft.GetPixel(95, 64).Alpha);
            stroke.Style.HighlighterInkContrast = 1;
        }
    }

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
