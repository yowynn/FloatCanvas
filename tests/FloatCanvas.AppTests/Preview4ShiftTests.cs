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
        Boxes(check);Lines(check);Captures(check);ImageRegions(check);ProjectedStroke(check);Options(check);InputOrdering(check);
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
            check($"{tool} creation keep={keep} Shift={shift} reverses the stored constraint",(shape.Width==shape.Height)==(keep^shift)&&shape.ConstrainAspect==keep);
            editor.Session.SetSelection([]);
            Send(editor,OverlayPointerKind.Down,new(300,200),modifiers);Send(editor,OverlayPointerKind.Up,new(300,200),modifiers);
            var clicked=editor.Session.Document.Elements.Last();
            check($"{tool} default insertion keep={keep} Shift={shift} uses the same inverse rule",clicked.Width==90&&clicked.Height==((keep^shift)?90:40));
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
            check($"{shape} capture keep={keep} Shift={shift} reverses the aspect preference",result is not null&&(Math.Abs(result.Bounds.Width-result.Bounds.Height)<1e-6)==(keep^shift));
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
            var modifiers=shift?OverlayModifiers.Shift:OverlayModifiers.None;
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
        Send(crop,OverlayPointerKind.Down,new(180,120),OverlayModifiers.Shift);Send(crop,OverlayPointerKind.Move,new(300,200),OverlayModifiers.Shift);
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
        check("projected Shift release retains its actual plane endpoint",points[^1].DistanceTo(new(220,80))<1e-6);editor.Cancel();
    }
    private static void Options(Action<string,bool> check)
    {
        _=System.Windows.Application.Current??new System.Windows.Application {ShutdownMode=System.Windows.ShutdownMode.OnExplicitShutdown};
        AppTheme.Apply("light");
        string root=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"FloatCanvas-ShiftOptions",Guid.NewGuid().ToString("N"));
        var editor=new EditorController(new());
        var panel=new ToolOptionsPanel(editor,new(System.IO.Path.Combine(root,"presets.json")),new(System.IO.Path.Combine(root,"colors.json")));
        var privateFields=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        var aspect=(System.Windows.Controls.CheckBox)typeof(ToolOptionsPanel).GetField("aspect",privateFields)!.GetValue(panel)!;
        var empty=(System.Windows.Controls.TextBlock)typeof(ToolOptionsPanel).GetField("noParameters",privateFields)!.GetValue(panel)!;
        foreach(var tool in new[] {EditorTool.CaptureRegion,EditorTool.Crop,EditorTool.Blur,EditorTool.Pixelate})
        {
            if(tool==EditorTool.Crop)
            {var image=new CanvasElement {Kind=ElementKind.Image,Width=100,Height=100};editor.Session.Document.Elements.Add(image);editor.Session.SetSelection([image.Id]);}
            editor.Tool=tool;panel.Refresh();
            check(tool+" exposes its rectangular-region aspect setting",aspect.Visibility==System.Windows.Visibility.Visible&&empty.Visibility==System.Windows.Visibility.Collapsed);
        }
        editor.Tool=EditorTool.CaptureRegion;editor.CaptureShape=CaptureShape.FreehandLasso;panel.Refresh();
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
