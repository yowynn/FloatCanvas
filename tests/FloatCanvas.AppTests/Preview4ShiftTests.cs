using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class Preview4ShiftTests
{
    internal static void Run(Action<string,bool> check)
    {
        foreach(var tool in new[] {EditorTool.Freehand,EditorTool.Highlighter})
        {
            var session=new EditorSession(new SceneDocument {Viewport=new(0,0,200,160)});var editor=new EditorController(session) {Tool=tool};
            Send(editor,OverlayPointerKind.Down,new(20,20),OverlayModifiers.Shift);
            Send(editor,OverlayPointerKind.Move,new(50,70),OverlayModifiers.Shift);
            Send(editor,OverlayPointerKind.Move,new(80,30),OverlayModifiers.Shift);
            check(tool+" keeps only the starting point and the current straight-line endpoint while Shift is held",
                session.Document.Elements.Single().Points.SequenceEqual([new PointD(0,0),new PointD(60,10)]));
            editor.Cancel();
            check(tool+" cancels the complete straight draft",session.Document.Elements.Count==0&&!session.IsTransactionActive);
            Send(editor,OverlayPointerKind.Down,new(20,20));Send(editor,OverlayPointerKind.Move,new(30,40));Send(editor,OverlayPointerKind.Move,new(45,35));
            editor.ModifiersChanged(OverlayModifiers.Shift,new(50,37));
            Send(editor,OverlayPointerKind.Move,new(70,75),OverlayModifiers.Shift);Send(editor,OverlayPointerKind.Move,new(90,60),OverlayModifiers.Shift);
            check(tool+" starts the straight preview at the key-time position and preserves the preceding curve",
                session.Document.Elements.Single().Points.SequenceEqual([new PointD(0,0),new(10,20),new(25,15),new(30,17),new(70,40)]));
            using(var renderer=new FloatCanvas.Rendering.SceneRenderer())
            {
                var png=renderer.ExportPng(session.Document,session.Assets,true);
                using var pixels=SkiaSharp.SKBitmap.Decode(png);
                check(tool+" renders the straight preview and removes its superseded endpoint",pixels.GetPixel(70,48).Alpha>0&&pixels.GetPixel(70,75).Alpha==0);
                if(tool==EditorTool.Freehand)
                {
                    string imagePath=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"FloatCanvas-Shift-preview-"+Guid.NewGuid().ToString("N")+".png");
                    System.IO.File.WriteAllBytes(imagePath,png);Console.WriteLine("SHIFT_PREVIEW_RENDER "+imagePath);
                }
            }
            var before=session.Document.Elements.Single().Points.ToArray();editor.ModifiersChanged(OverlayModifiers.Shift,new(999,999));
            check(tool+" ignores repeated Shift packets without moving the segment",session.Document.Elements.Single().Points.SequenceEqual(before));
            editor.ModifiersChanged(OverlayModifiers.None,new(95,65));Send(editor,OverlayPointerKind.Move,new(100,80));
            editor.ModifiersChanged(OverlayModifiers.Shift,new(104,79));Send(editor,OverlayPointerKind.Move,new(110,100),OverlayModifiers.Shift);
            editor.ModifiersChanged(OverlayModifiers.None,new(120,100));Send(editor,OverlayPointerKind.Move,new(130,90));Send(editor,OverlayPointerKind.Up,new(130,90));
            PointD[] expected=[new(0,0),new(10,20),new(25,15),new(30,17),new(75,45),new(80,60),new(84,59),new(100,80),new(110,70)];
            check(tool+" freezes each release endpoint then resumes the curve through multiple Shift intervals",session.Document.Elements.Single().Points.SequenceEqual(expected)&&session.UndoCount==1);
            check(tool+" retains one undo and redo for the mixed curve and straight gesture",session.Undo()&&session.Document.Elements.Count==0&&session.Redo()&&session.Document.Elements.Single().Points.SequenceEqual(expected));
        }
        SmoothFreehand(check);
        FlatHighlighter(check);
        Boxes(check);Lines(check);Captures(check);ImageRegions(check);ProjectedStroke(check);Options(check);InputOrdering(check);
    }
    private static void FlatHighlighter(Action<string, bool> check)
    {
        var session = new EditorSession();
        var editor = new EditorController(session) { Tool = EditorTool.Highlighter };
        editor.Style.HighlighterTip = HighlighterTip.Flat;
        editor.Style.HighlighterTipAngle = 30;
        Send(editor, OverlayPointerKind.Down, new(40, 40));
        Send(editor, OverlayPointerKind.Move, new(120, 40));
        var stroke = session.Document.Elements.Single();
        check("flat highlighter uses its chosen angle during the pointer preview",
            stroke.Style is { HighlighterTip: HighlighterTip.Flat, HighlighterTipAngle: 30 } &&
            ElementGeometry.HitTest(stroke, new(80, 40)));
        Send(editor, OverlayPointerKind.Up, new(150, 40));
        editor.Style.HighlighterTipAngle = 0;
        check("completed flat highlighter retains its authored tip angle",
            session.Document.Elements.Single().Style.HighlighterTipAngle == 30 && session.UndoCount == 1);
    }

    private static void SmoothFreehand(Action<string, bool> check)
    {
        var session = new EditorSession();
        var editor = new EditorController(session) { SmoothFreehand = true };
        Send(editor, OverlayPointerKind.Down, new(20, 20));
        foreach (PointD point in new PointD[] { new(38, 29), new(54, 12), new(74, 32), new(95, 13), new(125, 20) })
            Send(editor, OverlayPointerKind.Move, point);
        var points = session.Document.Elements.Single().Points;
        check("a noisy straight stroke fits the live endpoint-to-endpoint line without Shift",
            points.SequenceEqual([default, new PointD(105, 0)]));
        Send(editor, OverlayPointerKind.Up, new(125.25, 20));
        check("straight shape correction includes the actual mouse-up endpoint",
            session.Document.Elements.Single().Points[^1] == new PointD(105.25, 0));

        var arcInput = Enumerable.Range(0, 37).Select(i =>
        {
            double angle = i * Math.PI / 36;
            double radius = 45 + 3 * Math.Sin(4 * angle);
            return new PointD(180 + radius * Math.Cos(angle), 180 + radius * Math.Sin(angle));
        }).ToArray();
        var arc = Draw(arcInput);
        var arcPoints = arc.Session.Document.Elements.Single().Points;
        check("a half-drawn circle only refines the authored arc and keeps the tip under the pointer",
            arc.Session.IsTransactionActive && arcPoints[0] == default && arcPoints[^1] == arcInput[^1] - arcInput[0] &&
            arcPoints.All(point => point.Y >= -1e-6) && arcPoints.Max(point => point.Y) > 35);
        Send(arc, OverlayPointerKind.Up, arcInput[^1]);
        var encoded = System.Text.Json.JsonSerializer.Serialize(arc.Session.Document);
        var reopened = System.Text.Json.JsonSerializer.Deserialize<SceneDocument>(encoded)!;
        check("the open arc persists as ordinary points with a single undo entry",
            reopened.Elements.Single().Points.SequenceEqual(arc.Session.Document.Elements.Single().Points) &&
            !encoded.Contains(nameof(EditorController.SmoothFreehand)) && arc.Session.UndoCount == 1);

        var sharp = Draw(Bend(0));
        var sharpPoints = sharp.Session.Document.Elements.Single().Points;
        check("a decisive sharp turn remains a sharp fitted corner in the live preview",
            sharpPoints.SequenceEqual([default, new PointD(100, 0), new PointD(100, 100)]));
        var shallow = Draw(Bend(16)).Session.Document.Elements.Single().Points;
        var wide = Draw(Bend(37)).Session.Document.Elements.Single().Points;
        var vertex = new PointD(100, 0);
        Console.WriteLine($"FREEHAND_ROUNDING shallowCount={shallow.Count} shallowGap={shallow.Min(point => point.DistanceTo(vertex)):F1} wideCount={wide.Count} wideGap={wide.Min(point => point.DistanceTo(vertex)):F1}");
        check("two rounded bends preserve different rounding sizes",
            shallow.Count > 3 && wide.Count > 3 && shallow.Min(point => point.DistanceTo(vertex)) + 5 <
                wide.Min(point => point.DistanceTo(vertex)) && shallow[^1] == new PointD(100, 100) && wide[^1] == shallow[^1]);
        RoundedRunTransitions(check);
        BroadArcTangency(check);
        WidthScaledRefinement(check);

        PointD[] stairs = [new(20, 20), new(80, 21), new(79, 65), new(139, 63), new(141, 109), new(195, 110)];
        var stairInput = new List<PointD> { stairs[0] };
        for (int i = 1; i < stairs.Length; i++)
        {
            var segment = stairs[i] - stairs[i - 1];
            var across = new PointD(-segment.Y, segment.X) * (1 / segment.DistanceTo(default));
            for (int step = 1; step < 10; step++)
                stairInput.Add(stairs[i - 1] + segment * (step / 10d) + across * (step % 2 == 0 ? 1.2 : -1.2));
            stairInput.Add(stairs[i]);
        }
        var staircase = Draw(stairInput.ToArray()).Session.Document.Elements.Single().Points;
        check("one stroke keeps all four staircase corners and straightens all five runs",
            staircase.Count == stairs.Length &&
            Enumerable.Range(1, staircase.Count - 1).All(i => i % 2 == 1 ? staircase[i - 1].Y == staircase[i].Y : staircase[i - 1].X == staircase[i].X) &&
            staircase[^1] == stairs[^1] - stairs[0]);

        var lengthy = Enumerable.Range(0, 700).Select(i => new PointD(20 + i, 40)).ToArray();
        var longPoints = Draw(lengthy).Session.Document.Elements.Single().Points;
        check("long live strokes keep a continuous line across settled windows",
            longPoints.Count < 10 && longPoints[^1] == new PointD(699, 0) && longPoints.All(point => point.Y == 0));

        var prefix = sharpPoints.ToArray();
        sharp.ModifiersChanged(OverlayModifiers.Shift, new(120, 120));
        Send(sharp, OverlayPointerKind.Move, new(139, 145), OverlayModifiers.Shift);
        Send(sharp, OverlayPointerKind.Move, new(152, 137), OverlayModifiers.Shift);
        sharpPoints = sharp.Session.Document.Elements.Single().Points;
        check("holding Shift keeps the fitted prefix and one exact straight endpoint",
            sharpPoints.Take(prefix.Length).SequenceEqual(prefix) && sharpPoints.Count == prefix.Length + 1 &&
            sharpPoints[^1] == new PointD(132, 117));
        sharp.ModifiersChanged(OverlayModifiers.None, new(154, 141));
        Send(sharp, OverlayPointerKind.Move, new(172, 143));
        Send(sharp, OverlayPointerKind.Up, new(187, 142));
        sharpPoints = sharp.Session.Document.Elements.Single().Points;
        check("shape correction resumes after Shift without moving the straight section",
            sharpPoints.Take(prefix.Length).SequenceEqual(prefix) && sharpPoints[prefix.Length] == new PointD(134, 121));

        PointD[] squiggle = [new(30, 30), new(55, 75), new(80, 30), new(105, 75), new(130, 30), new(155, 70)];
        var fallback = Draw(squiggle);
        var original = Draw(squiggle, enabled: false);
        check("unrecognized input restores the original live points",
            fallback.Session.Document.Elements.Single().Points.SequenceEqual(original.Session.Document.Elements.Single().Points));
        Send(fallback, OverlayPointerKind.Up, squiggle[^1]);
        check("unknown shape stays raw after mouse-up",
            fallback.Session.Document.Elements.Single().Points.SequenceEqual(original.Session.Document.Elements.Single().Points));
        PointD[] square = [new(30, 30), new(50, 30), new(70, 30), new(90, 30), new(90, 50), new(90, 70),
            new(90, 90), new(70, 90), new(50, 90), new(30, 90), new(30, 70), new(30, 50), new(30, 30)];
        check("a closed square retains its corners instead of being classified as a circle",
            Draw(square).Session.Document.Elements.Single().Points.SequenceEqual([default, new PointD(60, 0), new PointD(60, 60), new PointD(0, 60), default]));

        var recipe = editor.Parameters;
        editor.ApplyParameters(recipe);
        check("shape correction stays a local preference outside preset data",
            editor.SmoothFreehand && !System.Text.Json.JsonSerializer.Serialize(recipe).Contains(nameof(EditorController.SmoothFreehand)));
        check("shape correction defaults off and old local settings still load off",
            !new EditorController(new()).SmoothFreehand && !System.Text.Json.JsonSerializer.Deserialize<UserSettings>("{}")!.SmoothFreehand);
    }

    private static PointD[] Bend(double radius)
    {
        var points = new List<PointD>();
        for (int i = 0; i <= 16; i++) points.Add(new(20 + (100 - radius) * i / 16, 20));
        for (int i = 1; radius > 0 && i <= 16; i++)
        {
            double angle = Math.PI * i / 32;
            points.Add(new(120 - radius + radius * Math.Sin(angle), 20 + radius * (1 - Math.Cos(angle))));
        }
        for (int i = 1; i <= 16; i++) points.Add(new(120, 20 + radius + (100 - radius) * i / 16));
        return points.ToArray();
    }

    private static void RoundedRunTransitions(Action<string, bool> check)
    {
        foreach (double degrees in new[] { 90d, 135d })
        {
            const double radius = 6;
            var start = new PointD(100, 100);
            var raw = Enumerable.Range(0, 61).Select(i => start + new PointD(i, 0)).ToList();
            var center = raw[^1] + new PointD(0, radius);
            double angle = degrees * Math.PI / 180;
            for (int i = 1; i <= 24; i++)
            {
                double theta = angle * i / 24;
                raw.Add(center + new PointD(Math.Sin(theta), -Math.Cos(theta)) * radius);
            }
            var tangent = new PointD(Math.Cos(angle), Math.Sin(angle));
            var exit = raw[^1];
            for (int i = 1; i <= 12; i++) raw.Add(exit + tangent * i);
            var pen = Draw(raw.ToArray());
            double live = LargestTurn();
            for (int i = 13; i <= 230; i++) Send(pen, OverlayPointerKind.Move, exit + tangent * i);
            double extended = LargestTurn();
            Send(pen, OverlayPointerKind.Up, exit + tangent * 230);
            double finished = LargestTurn();
            Console.WriteLine($"REFINEMENT_JOIN source={degrees} preview={live:F1} extended={extended:F1} finished={finished:F1}");
            check($"a {degrees} degree round transition does not become a sharp corner as its outgoing line grows",
                live < 40 && extended < 40 && finished < 40);

            double LargestTurn()
            {
                var points = pen.Session.Document.Elements.Single().Points;
                double maximum = 0;
                for (int i = 1; i < points.Count - 1; i++)
                {
                    var before = points[i] - points[i - 1];
                    var after = points[i + 1] - points[i];
                    if (before.DistanceTo(default) < .01 || after.DistanceTo(default) < .01) continue;
                    double turn = Math.Atan2(before.X * after.Y - before.Y * after.X, before.X * after.X + before.Y * after.Y);
                    maximum = Math.Max(maximum, Math.Abs(turn) * 180 / Math.PI);
                }
                return maximum;
            }
        }
    }

    private static void BroadArcTangency(Action<string, bool> check)
    {
        const double radius = 240;
        var center = new PointD(420, 420);
        var input = Enumerable.Range(0, 641).Select(i =>
        {
            double angle = i * Math.PI * .75 / 640;
            return center + new PointD(Math.Cos(angle), Math.Sin(angle)) * radius;
        }).ToArray();
        var pen = Draw(input);
        Send(pen, OverlayPointerKind.Up, input[^1]);
        var points = pen.Session.Document.Elements.Single().Points;
        var localCenter = center - input[0];
        double worst = 0;
        for (int i = 1; i < points.Count; i++)
        {
            var chord = points[i] - points[i - 1];
            var radial = (points[i] + points[i - 1]) * .5 - localCenter;
            var tangent = new PointD(-radial.Y, radial.X);
            var error = Math.Atan2(chord.X * tangent.Y - chord.Y * tangent.X, chord.X * tangent.X + chord.Y * tangent.Y);
            worst = Math.Max(worst, Math.Abs(error) * 180 / Math.PI);
        }
        Console.WriteLine($"REFINEMENT_BROAD_ARC points={points.Count} maximumHeadingError={worst:F2}");
        check("a broad arc follows the source curvature smoothly across settled window boundaries",
            worst < 5 && points[0] == default && points[^1] == input[^1] - input[0]);
    }

    private static void WidthScaledRefinement(Action<string, bool> check)
    {
        var wave = Enumerable.Range(0, 331).Select(i => new PointD(50 + i * .91,
            100 + 7 * Math.Sin(i * .085) + .7 * Math.Sin(i * .61))).ToArray();
        var thin = Draw(wave, strokeWidth: 4).Session.Document.Elements.Single().Points;
        var thick = Draw(wave.Select(point => point * 10).ToArray(), strokeWidth: 40).Session.Document.Elements.Single().Points;
        double difference = thin.Count == thick.Count
            ? Enumerable.Range(0, thin.Count).Max(i => thin[i].DistanceTo(thick[i] * .1))
            : double.PositiveInfinity;
        double height = thin.Max(point => point.Y) - thin.Min(point => point.Y);
        Console.WriteLine($"REFINEMENT_WIDTH_SCALE thinPoints={thin.Count} thickPoints={thick.Count} normalizedDifference={difference:F4} waveHeight={height:F2}");
        check("a tenfold increase in input and brush width preserves the refined wave across settled windows",
            difference < .05 && height > 8);

        var zoomed = new FreehandPathSmoother(4);
        var zoom = Projective2D.FromAffine(Affine2D.Scale(10, 10));
        PointCollection points = [default];
        foreach (var position in wave.Skip(1))
            points = zoomed.Update(points, position - wave[0], zoom, finishStroke: false);
        check("the same refinement follows projected screen width when viewing scale changes",
            points.Count == thin.Count && Enumerable.Range(0, thin.Count).All(i => points[i].DistanceTo(thin[i]) < .05));
    }

    private static EditorController Draw(PointD[] points, bool enabled = true, double strokeWidth = 4)
    {
        var pen = new EditorController(new()) { SmoothFreehand = enabled };
        pen.Style.StrokeWidth = strokeWidth;
        Send(pen, OverlayPointerKind.Down, points[0]);
        foreach (var point in points.Skip(1)) Send(pen, OverlayPointerKind.Move, point);
        return pen;
    }

    private static void Boxes(Action<string,bool> check)
    {
        foreach(var tool in new[] {EditorTool.Rectangle,EditorTool.RoundedRectangle,EditorTool.Ellipse,EditorTool.Symbol})
        foreach(bool keep in new[] {false,true})foreach(bool shift in new[] {false,true})
        {
            var editor=new EditorController(new()) {Tool=tool,ConstrainAspect=keep,DefaultWidth=90,DefaultHeight=40};
            var modifiers=shift?OverlayModifiers.Shift:OverlayModifiers.None;
            Send(editor,OverlayPointerKind.Down,new(20,20),modifiers);Send(editor,OverlayPointerKind.Up,new(100,50),modifiers);
            var shape=editor.Session.Document.Elements.Single();
            double expectedRatio = keep && !shift ? 90d / 40 : !keep && shift ? 1 : 80d / 30;
            check($"{tool} creation keep={keep} Shift={shift} reverses the stored ratio constraint", Math.Abs(shape.Width / shape.Height - expectedRatio) < 1e-9 && shape.ConstrainAspect==keep);
            editor.Session.SetSelection([]);
            Send(editor,OverlayPointerKind.Down,new(300,200),modifiers);Send(editor,OverlayPointerKind.Up,new(300,200),modifiers);
            var clicked=editor.Session.Document.Elements.Last();
            check($"{tool} default insertion keep={keep} Shift={shift} uses configured dimensions",clicked.Width==90&&clicked.Height==(!keep && shift ? 90 : 40));
        }
    }
    private static bool Eight(PointD delta)=>Math.Abs(delta.X)<1e-6||Math.Abs(delta.Y)<1e-6||Math.Abs(Math.Abs(delta.X)-Math.Abs(delta.Y))<1e-6;
    private static void Lines(Action<string,bool> check)
    {
        foreach(var tool in new[] {EditorTool.Line,EditorTool.Arrow,EditorTool.DoubleArrow})
        foreach(var delta in new PointD[] {new(90,10),new(90,65),new(10,90),new(-70,90),new(-90,10),new(-90,-60),new(-10,-90),new(70,-90)})
        {
            var editor=new EditorController(new()) {Tool=tool};
            Send(editor,OverlayPointerKind.Down,new(200,200));Send(editor,OverlayPointerKind.Move,new PointD(200,200)+delta,OverlayModifiers.Shift);
            var item=editor.Session.Document.Elements.Single();
            check(tool+" previews one of eight directions for "+delta,Eight(item.Points[^1]));
            editor.ModifiersChanged(OverlayModifiers.None);
            check(tool+" releases the angle constraint without waiting for another pointer move",item.Points[^1].DistanceTo(delta)<1e-7);
            Send(editor,OverlayPointerKind.Up,new PointD(200,200)+delta);
            editor.Tool=EditorTool.Select;editor.Session.SetSelection([item.Id]);
            var endpoint=ElementGeometry.LocalToPlane(item).TransformPoint(item.Points[^1]);
            Send(editor,OverlayPointerKind.Down,endpoint);Send(editor,OverlayPointerKind.Up,new(280,260),OverlayModifiers.Shift);
            var line=editor.Session.Document.Elements.Single();
            var map=ElementGeometry.LocalToPlane(line);
            check(tool+" constrains a selected endpoint around its fixed opposite end",Eight(map.TransformPoint(line.Points[^1])-map.TransformPoint(line.Points[0])));
        }
    }
    private static void Captures(Action<string,bool> check)
    {
        foreach(var shape in new[] {CaptureShape.Rectangle,CaptureShape.Ellipse})
        foreach(bool keep in new[] {false,true})foreach(bool shift in new[] {false,true})
        {
            var editor=new EditorController(new()) {Tool=EditorTool.CaptureRegion,CaptureShape=shape,ConstrainAspect=keep};
            CaptureSelection? result=null;editor.CaptureSelectionCompleted+=value=>result=value;
            var modifiers=shift?OverlayModifiers.Shift:OverlayModifiers.None;
            Send(editor,OverlayPointerKind.Down,new(20,20),modifiers);Send(editor,OverlayPointerKind.Up,new(100,60),modifiers);
            check($"{shape} capture keep={keep} Shift={shift} follows only the held Shift constraint",result is not null&&(Math.Abs(result.Bounds.Width-result.Bounds.Height)<1e-6)==shift);
        }
        var lasso=new EditorController(new()) {Tool=EditorTool.CaptureRegion,CaptureShape=CaptureShape.FreehandLasso};
        CaptureSelection? finished=null;lasso.CaptureSelectionCompleted+=value=>finished=value;
        Send(lasso,OverlayPointerKind.Down,new(20,20));Send(lasso,OverlayPointerKind.Move,new(40,50));
        lasso.ModifiersChanged(OverlayModifiers.Shift,new(50,45));
        Send(lasso,OverlayPointerKind.Move,new(70,80),OverlayModifiers.Shift);Send(lasso,OverlayPointerKind.Move,new(100,60),OverlayModifiers.Shift);
        check("freehand capture previews a single straight edge while retaining earlier contour points",lasso.CapturePreview!.Points.SequenceEqual([new PointD(20,20),new(40,50),new(50,45),new(100,60)]));
        lasso.ModifiersChanged(OverlayModifiers.None,new(110,70));Send(lasso,OverlayPointerKind.Up,new(70,100));
        check("freehand capture freezes the key release endpoint and completes the subsequent free segment",finished is not null&&finished.Points.SequenceEqual([new PointD(20,20),new(40,50),new(50,45),new(110,70),new(70,100)]));
        lasso.CaptureShape=CaptureShape.PolygonLasso;
        Send(lasso,OverlayPointerKind.Down,new(10,10));Send(lasso,OverlayPointerKind.Up,new(10,10));
        Send(lasso,OverlayPointerKind.Move,new(100,80),OverlayModifiers.Shift);
        check("polygon lasso hover snaps its next edge to eight directions",Eight(lasso.CapturePreview!.Points[^1]-lasso.CapturePreview.Points[0]));
        Send(lasso,OverlayPointerKind.Down,new(100,80),OverlayModifiers.Shift);Send(lasso,OverlayPointerKind.Up,new(100,80),OverlayModifiers.Shift);
        check("polygon lasso commits the same snapped hover endpoint",Eight(lasso.CapturePreview!.Points[1]-lasso.CapturePreview.Points[0]));lasso.Cancel();
    }
    private static void ImageRegions(Action<string,bool> check)
    {
        foreach(var tool in new[] {EditorTool.Crop,EditorTool.Blur,EditorTool.Pixelate})
        foreach(bool keep in new[] {false,true})foreach(bool shift in new[] {false,true})
        {
            var session=new EditorSession(new SceneDocument {Kind=DocumentKind.FrozenCapture});
            var image=new CanvasElement {Kind=ElementKind.Image,Width=200,Height=140};session.Document.Elements.Add(image);session.SetSelection([image.Id]);
            var editor=new EditorController(session) {Tool=tool,ConstrainAspect=keep};
            var modifiers=OverlayModifiers.Control|(shift?OverlayModifiers.Shift:OverlayModifiers.None);
            Send(editor,OverlayPointerKind.Down,new(20,20),modifiers);Send(editor,OverlayPointerKind.Move,new(100,60),modifiers);
            var bounds=editor.Marquee!.Value;
            check($"{tool} image region keep={keep} Shift={shift} reverses the rectangular constraint",(Math.Abs(bounds.Width-bounds.Height)<1e-6)==(keep^shift));editor.Cancel();
        }
        foreach(var tool in new[] {EditorTool.Blur,EditorTool.Pixelate})
        foreach(bool keep in new[] {false,true})foreach(bool shift in new[] {false,true})
        {
            var editor=new EditorController(new()) {Tool=tool,ConstrainAspect=keep};var modifiers=shift?OverlayModifiers.Shift:OverlayModifiers.None;
            Send(editor,OverlayPointerKind.Down,new(10,10),modifiers);Send(editor,OverlayPointerKind.Move,new(100,60),modifiers);
            var bounds=editor.Marquee!.Value;
            check($"live {tool} keep={keep} Shift={shift} uses the same region constraint",(Math.Abs(bounds.Width-bounds.Height)<1e-6)==(keep^shift));editor.Cancel();
        }
        var limited=new EditorSession(new SceneDocument {Kind=DocumentKind.FrozenCapture});
        var target=new CanvasElement {Kind=ElementKind.Image,Width=200,Height=140};limited.Document.Elements.Add(target);limited.SetSelection([target.Id]);
        var crop=new EditorController(limited) {Tool=EditorTool.Crop};
        Send(crop,OverlayPointerKind.Down,new(180,120),OverlayModifiers.Control|OverlayModifiers.Shift);Send(crop,OverlayPointerKind.Move,new(300,200),OverlayModifiers.Control|OverlayModifiers.Shift);
        check("constrained image regions retain their ratio at the image boundary",crop.Marquee==new RectD(180,120,20,20));crop.Cancel();
    }
    private static void ProjectedStroke(Action<string,bool> check)
    {
        var document=new SceneDocument {Viewport=new(0,0,800,600)};
        check("projected stroke fixture has a valid perspective",CanvasCalibration.TryCalibrate(CanvasCalibration.CreateInitial(document.Viewport),
            [new(60,40),new(740,100),new(650,550),new(100,500)],document.Viewport,out var projection,out _));
        document.Canvas.Projection=projection;
        var editor=new EditorController(new(document)) {Tool=EditorTool.Freehand,ScreenOrigin=new(-500,200)};
        var mapping=CanvasMapping.Create(document);
        PointD Screen(PointD plane)=>mapping.ToScreen(plane)+editor.ScreenOrigin;
        Send(editor,OverlayPointerKind.Down,Screen(new(200,180)));Send(editor,OverlayPointerKind.Move,Screen(new(210,200)));
        var press=Screen(new(225,190));editor.ModifiersChanged(OverlayModifiers.Shift,new(press.X,press.Y));
        Send(editor,OverlayPointerKind.Move,Screen(new(400,240)),OverlayModifiers.Shift);
        var points=document.Elements.Single().Points;
        check("key-time anchors are mapped from physical pixels through screen origin and perspective",points.Count==4&&points[2].DistanceTo(new(25,10))<1e-6&&points[^1].DistanceTo(new(200,60))<1e-6);
        var release=Screen(new(420,260));editor.ModifiersChanged(OverlayModifiers.None,new(release.X,release.Y));
        check("projected Shift release retains its actual plane endpoint",document.Elements.Single().Points[^1].DistanceTo(new(220,80))<1e-6);editor.Cancel();
    }
    private static void Options(Action<string,bool> check)
    {
        _=System.Windows.Application.Current??new System.Windows.Application {ShutdownMode=System.Windows.ShutdownMode.OnExplicitShutdown};
        AppTheme.Apply("light");
        string root=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"FloatCanvas-ShiftOptions",Guid.NewGuid().ToString("N"));
        var editor=new EditorController(new());
        var panel=new ToolOptionsPanel(editor,new(System.IO.Path.Combine(root,"presets.json")),new(System.IO.Path.Combine(root,"colors.json"))); panel.PrepareForShow();
        check("the freehand header uses the shared caption switch and defaults off",
            panel.Header.Actions is CaptionModeSwitch { SelectedFirst: false });
        editor.SmoothFreehand = true;
        panel.SynchronizeFreehandSmoothing();
        check("the freehand header reflects the local smoothing preference",
            panel.Header.Actions is CaptionModeSwitch { SelectedFirst: true });
        editor.Tool = EditorTool.Line;
        panel.PrepareForShow();
        check("the straight line tool has no smoothing action", panel.Header.Actions is null);
        var privateFields=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        var aspectRows=(Dictionary<string,System.Windows.FrameworkElement>)typeof(ToolOptionsPanel).GetField("rows",privateFields)!.GetValue(panel)!;
        var aspect=aspectRows["Aspect"];
        var empty=(System.Windows.Controls.TextBlock)typeof(ToolOptionsPanel).GetField("noParameters",privateFields)!.GetValue(panel)!;
        foreach(var tool in new[] {EditorTool.CaptureRegion,EditorTool.Crop,EditorTool.Blur,EditorTool.Pixelate})
        {
            if(tool==EditorTool.Crop)
            {var image=new CanvasElement {Kind=ElementKind.Image,Width=100,Height=100};editor.Session.Document.Elements.Add(image);editor.Session.SetSelection([image.Id]);}
            editor.Tool=tool;panel.PrepareForShow();
            check(tool+" exposes only its supported rectangular-region settings",aspect.Visibility==(tool==EditorTool.CaptureRegion?System.Windows.Visibility.Collapsed:System.Windows.Visibility.Visible)&&empty.Visibility==System.Windows.Visibility.Collapsed);
        }
        editor.Tool=EditorTool.CaptureRegion;editor.CaptureShape=CaptureShape.FreehandLasso;panel.PrepareForShow();
        check("freehand capture hides the rectangular aspect setting",aspect.Visibility==System.Windows.Visibility.Collapsed);
    }
    private static void InputOrdering(Action<string,bool> check)
    {
        var editor=new EditorController(new()) {Tool=EditorTool.Freehand};
        Send(editor,OverlayPointerKind.Down,new(10,10));Send(editor,OverlayPointerKind.Move,new(20,30));
        editor.ModifiersChanged(OverlayModifiers.Shift,new(30,20),time:100,pointerLeftDown:true);
        var points=editor.Session.Document.Elements.Single().Points.ToArray();
        editor.Pointer(new(OverlayPointerKind.Move,new(25,25),OverlayMouseButton.Left,OverlayModifiers.None) {Time=99});
        check("an older queued pointer move cannot undo a Shift transition or overwrite its anchor",editor.Session.Document.Elements.Single().Points.SequenceEqual(points));
        editor.Pointer(new(OverlayPointerKind.Move,new(70,60),OverlayMouseButton.Left,OverlayModifiers.Shift) {Time=101});
        check("a newer pointer event continues from the key-time anchor",editor.Session.Document.Elements.Single().Points.SequenceEqual([new PointD(0,0),new(10,20),new(20,10),new(60,50)]));editor.Cancel();
        Send(editor,OverlayPointerKind.Down,new(10,10));Send(editor,OverlayPointerKind.Move,new(20,30));
        editor.ModifiersChanged(OverlayModifiers.Shift,new(100,100),time:300,pointerLeftDown:false);
        editor.Pointer(new(OverlayPointerKind.Up,new(30,30),OverlayMouseButton.Left,OverlayModifiers.None) {Time=299});
        check("a key captured after physical mouse release cannot change the still-queued completed stroke",editor.Session.Document.Elements.Single().Points.SequenceEqual([new PointD(0,0),new(10,20),new(20,20)]));
        editor.Session.SetSelection([]);Send(editor,OverlayPointerKind.Down,new(10,10));
        editor.ModifiersChanged(OverlayModifiers.Shift,new(20,20),time:uint.MaxValue-2,pointerLeftDown:true);
        editor.Pointer(new(OverlayPointerKind.Move,new(40,50),OverlayMouseButton.Left,OverlayModifiers.Shift) {Time=1});
        check("pointer ordering remains correct when the Windows millisecond clock wraps",editor.Session.Document.Elements.Last().Points[^1]==new PointD(30,40));editor.Cancel();
        editor.Tool=EditorTool.CaptureRegion;editor.CaptureShape=CaptureShape.PolygonLasso;
        Send(editor,OverlayPointerKind.Down,new(10,10));Send(editor,OverlayPointerKind.Up,new(10,10));
        editor.ModifiersChanged(OverlayModifiers.Shift,new(100,80),time:400,pointerLeftDown:false);
        check("polygon hover still responds to Shift without holding a mouse button",Eight(editor.CapturePreview!.Points[^1]-editor.CapturePreview.Points[0]));editor.Cancel();
    }
    private static void Send(EditorController editor,OverlayPointerKind kind,PointD point,OverlayModifiers modifiers=OverlayModifiers.None)=>
        editor.Pointer(new(kind,new(point.X,point.Y),OverlayMouseButton.Left,modifiers));
}
