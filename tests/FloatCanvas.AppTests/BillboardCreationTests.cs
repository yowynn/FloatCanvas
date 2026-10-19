using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;

/// <summary>Product contracts exercised through production pointer gestures and inline/property geometry under actual perspective.</summary>
internal static class BillboardCreationTests
{
    internal static void Run(Action<string,bool> check)
    {
        _=System.Windows.Application.Current??new System.Windows.Application {ShutdownMode=System.Windows.ShutdownMode.OnExplicitShutdown};
        foreach(var tool in new[]{EditorTool.Freehand,EditorTool.Highlighter,EditorTool.Line,EditorTool.Arrow,EditorTool.DoubleArrow,
            EditorTool.Rectangle,EditorTool.RoundedRectangle,EditorTool.Ellipse,EditorTool.Symbol,EditorTool.Number,EditorTool.Emoji})
            Drawing(check,tool);
        SignedCreation(check);InlineText(check);ImageRegions(check);Resize(check,false);Resize(check,true);
        MixedRotation(check);OrdinaryShift(check);PendingAndSession(check);
    }

    private static EditorSession Session(DocumentKind kind=DocumentKind.LiveOverlay)
    {
        var document=new SceneDocument {Viewport=new(0,0,1000,800),Kind=kind};
        var projection=CanvasCalibration.CreateInitial(document.Viewport,10,8);
        projection.Matrix=new(1,0,0,0,1,0,0,.001,1);projection.Enabled=true;
        projection.Quad=projection.Quad.Select(projection.Matrix.TransformPoint).ToList();document.Canvas.Projection=projection;
        return new(document);
    }
    private static void Send(EditorController editor,OverlayPointerKind kind,PointD p,OverlayModifiers modifiers=OverlayModifiers.None)=>
        editor.Pointer(new(kind,new(p.X+editor.ScreenOrigin.X,p.Y+editor.ScreenOrigin.Y),OverlayMouseButton.Left,modifiers));
    private static BillboardDepthLine Depth(EditorController editor,PointD point)
    {Send(editor,OverlayPointerKind.Move,point);Send(editor,OverlayPointerKind.Down,point);Send(editor,OverlayPointerKind.Up,point);return editor.ActiveBillboardDepthLine!.Value;}
    private static bool Near(double a,double b,double epsilon=1e-6)=>Math.Abs(a-b)<epsilon;
    private static PointD Center(CanvasElement element,CanvasMapping mapping)=>mapping.ElementToScreen(element).TransformPoint(ElementGeometry.LocalBounds(element).Center);
    private static bool Near(RectD a,RectD b)=>Near(a.X,b.X)&&Near(a.Y,b.Y)&&Near(a.Width,b.Width)&&Near(a.Height,b.Height);

    private static void Drawing(Action<string,bool> check,EditorTool tool)
    {
        var session=Session();var editor=new EditorController(session){Tool=tool,BillboardMode=true,Symbol=SymbolKind.Heart};editor.Style.StrokeWidth=5;
        var depth=Depth(editor,new(300,300));
        check(tool+" first depth click creates no layer or undo under actual perspective",session.Document.Canvas.Projection.Quad.Count==4&&Near(depth.Scale,.7)&&session.Document.Elements.Count==0&&session.UndoCount==0&&!session.IsTransactionActive&&editor.BillboardDrawingGuide is null);
        Send(editor,OverlayPointerKind.Down,new(100,100));Send(editor,OverlayPointerKind.Move,new(150,140));Send(editor,OverlayPointerKind.Move,new(200,180));
        var element=session.Document.Elements.Single();var mapping=CanvasMapping.Create(session.Document);
        check(tool+" preview uses billboard placement with fixed depth and a vertical guide",element.IsBillboard&&editor.VisibleBillboardDepthLine is not null&&editor.BillboardDrawingGuide is {} guide&&Near(guide.Center.X,guide.Anchor.X)&&Near(guide.Anchor.Y,300)&&mapping.TryGetDepthScale(element.BillboardAnchor,out double scale)&&Near(scale,depth.Scale));
        if(tool is EditorTool.Rectangle or EditorTool.RoundedRectangle or EditorTool.Ellipse or EditorTool.Symbol)
            check(tool+" preview reverses screen dimensions while retaining logical stroke",Near(element.Width,100/depth.Scale)&&Near(element.Height,80/depth.Scale)&&element.Style.StrokeWidth==5&&Near(mapping.ProjectedBounds(element,false).Width,100)&&Near(mapping.ProjectedBounds(element,false).Height,80));
        if(tool is EditorTool.Freehand or EditorTool.Highlighter or EditorTool.Line or EditorTool.Arrow or EditorTool.DoubleArrow)
        {
            var screen=mapping.ElementToScreen(element);
            check(tool+" keeps drawn path endpoints in screen space",screen.TransformPoint(element.Points[0]).DistanceTo(new(100,100))<1e-6&&screen.TransformPoint(element.Points[^1]).DistanceTo(new(200,180))<1e-6);
        }
        double expectedStroke=tool==EditorTool.Highlighter?16:5;
        check(tool+" keeps its logical tool stroke without inverse-depth compensation",element.Style.StrokeWidth==expectedStroke);
        Send(editor,OverlayPointerKind.Up,new(200,180));
        check(tool+" finishes one creation and retires its depth guide",session.Document.Elements.Count==1&&session.Document.Elements[0].IsBillboard&&session.UndoCount==1&&!session.IsTransactionActive&&editor.VisibleBillboardDepthLine is null);
        check(tool+" creation undo and redo preserve billboard metadata",session.Undo()&&session.Document.Elements.Count==0&&session.Redo()&&session.Document.Elements.Single().IsBillboard&&session.Document.Elements.Single().BillboardAnchor==element.BillboardAnchor&&Near(session.Document.Elements.Single().BillboardHeight,element.BillboardHeight));
    }
    private static void SignedCreation(Action<string,bool> check)
    {
        var session=Session();var editor=new EditorController(session){Tool=EditorTool.Rectangle,BillboardMode=true};
        var depth=Depth(editor,new(300,200));Send(editor,OverlayPointerKind.Down,new(100,300));Send(editor,OverlayPointerKind.Up,new(200,380));
        var item=session.Document.Elements.Single();var mapping=CanvasMapping.Create(session.Document);
        check("creating below the chosen depth line yields signed negative elevation without moving the screen rectangle",item.IsBillboard&&Near(item.BillboardHeight,-140/depth.Scale)&&Near(mapping.ProjectedBounds(item,false),new RectD(100,300,100,80))&&Near(mapping.ToScreen(item.BillboardAnchor).Y,200));
    }
    private static void InlineText(Action<string,bool> check)
    {
        var session=Session();var mapping=CanvasMapping.Create(session.Document);mapping.TryGetEqualDepthLine(new(300,300),out var depth);
        using var renderer=new SceneRenderer();
        Guid id;
        using(var edit=new InlineTextEdit(session,null,new(100/depth.Scale,100/depth.Scale),new(){FontSize=20,StrokeWidth=5},renderer,depth))
        {
            edit.Input.Text="Billboard\ninline text";var target=edit.Target;id=target.Id;
            check("new inline text uses the chosen depth and original logical font and stroke",target.IsBillboard&&target.Style.FontSize==20&&target.Style.StrokeWidth==5&&mapping.TryGetDepthScale(target.BillboardAnchor,out var scale)&&Near(scale,depth.Scale)&&Near(mapping.ToScreen(target.BillboardAnchor).Y,300));
            var textStart=edit.LocalToScreen.TransformPoint(new(0,0));
            check("new inline text remains at its drawn screen origin while fitting text",textStart.DistanceTo(new(100,100))<1e-6&&edit.Contains(Center(target,mapping)));
            var caret=edit.Geometry.Caret(0);var hit=edit.LocalToScreen.TransformPoint(new(caret.X,caret.Y+caret.Height/2));
            edit.Pointer(hit,true,true);check("billboard inline pointer hit-testing uses the displayed text plane",edit.Input.CaretIndex==0);
            edit.Finish(true);
        }
        var item=session.Document.Elements.Single(e=>e.Id==id);var anchor=item.BillboardAnchor;double height=item.BillboardHeight;var center=Center(item,mapping);double beforeHeight=item.Height;
        using(var edit=new InlineTextEdit(session,item,new(0,0),new(){FontSize=99},renderer,depth with {Scale=.2}))
        {
            edit.Input.Text="Changed\nexisting\ntext\nheight";
            check("existing inline text ignores new-creation depth and keeps its ground anchor elevation and center",item.BillboardAnchor==anchor&&item.BillboardHeight==height&&item.Height>beforeHeight&&Center(item,mapping).DistanceTo(center)<1e-6&&item.Style.FontSize==20);
            edit.Finish(true);
        }
        check("existing billboard text edit is independently undoable",session.Undo()&&session.Document.Elements.Single().Text=="Billboard\ninline text"&&session.Document.Elements.Single().BillboardAnchor==anchor&&session.Document.Elements.Single().BillboardHeight==height);
    }
    private static void ImageRegions(Action<string,bool> check)
    {
        foreach(var tool in new[]{EditorTool.Crop,EditorTool.Blur,EditorTool.Pixelate})
        {
            var session=Session(DocumentKind.FrozenCapture);var image=new CanvasElement {Kind=ElementKind.Image,Width=100,Height=60,IsBillboard=true,BillboardAnchor=new(300,500),BillboardHeight=60,Transform=Affine2D.Rotation(25)};
            session.Document.Elements.Add(image);session.SetSelection([image.Id]);var editor=new EditorController(session){Tool=tool,BillboardMode=true};
            var mapping=CanvasMapping.Create(session.Document);var screen=mapping.ElementToScreen(image);var center=Center(image,mapping);var anchor=image.BillboardAnchor;
            var start=screen.TransformPoint(new(20,15));var end=screen.TransformPoint(new(80,45));
            Send(editor,OverlayPointerKind.Down,start);Send(editor,OverlayPointerKind.Move,end);
            var expected=new[]{new PointD(20,15),new(80,15),new(80,45),new(20,45)}.Select(screen.TransformPoint).ToArray();
            check(tool+" existing billboard image edits skip depth selection and preview the screen-facing local region",editor.ActiveBillboardDepthLine is null&&editor.EffectScreenPreview.Count==4&&editor.EffectScreenPreview.Zip(expected).All(pair=>pair.First.DistanceTo(pair.Second)<1e-6));
            Send(editor,OverlayPointerKind.Up,end);
            if(tool==EditorTool.Crop)
                check("billboard crop stores normalized source region while preserving anchor and displayed center",image.ImageCrop is {} crop&&Near(crop,new RectD(.2,.25,.6,.5))&&Near(image.Width,60)&&Near(image.Height,30)&&image.BillboardAnchor==anchor&&image.BillboardHeight==60&&Center(image,mapping).DistanceTo(center)<1e-6);
            else
                check(tool+" stores its source effect region without changing billboard geometry",image.Effects.Count==1&&Near(image.Effects[0].Bounds,new RectD(.2,.25,.6,.5))&&image.Width==100&&image.Height==60&&image.BillboardAnchor==anchor&&Center(image,mapping).DistanceTo(center)<1e-6);
            check(tool+" image edit commits exactly one undo",session.UndoCount==1&&!session.IsTransactionActive);
        }
    }
    private static void Resize(Action<string,bool> check,bool text)
    {
        var session=Session();var element=new CanvasElement {Kind=text?ElementKind.Text:ElementKind.Rectangle,Width=120,Height=80,Text=text?"Fixed billboard frame":"",TextFrameFixed=text,
            IsBillboard=true,BillboardAnchor=new(350,500),BillboardHeight=90,Transform=Affine2D.Rotation(30),Style=new(){FontSize=20,StrokeWidth=5,Fill=0x40224466}};
        session.Document.Elements.Add(element);session.SetSelection([element.Id]);var editor=new EditorController(session){Tool=EditorTool.Select};
        var mapping=CanvasMapping.Create(session.Document);var center=Center(element,mapping);var anchor=element.BillboardAnchor;
        var corner=editor.SelectionScreenCorners[2];var end=center+(corner-center)*1.5;
        Send(editor,OverlayPointerKind.Down,corner);Send(editor,OverlayPointerKind.Move,end);Send(editor,OverlayPointerKind.Up,end);
        check((text?"text":"shape")+" corner resize changes logical dimensions around a fixed center and anchor",Near(element.Width,180)&&Near(element.Height,120)&&element.BillboardAnchor==anchor&&element.BillboardHeight==90&&Center(element,mapping).DistanceTo(center)<1e-6&&element.Style.StrokeWidth==5);
        check((text?"text":"shape")+" resize preserves own angle and commits once",Near(Math.Atan2(element.Transform.M21,element.Transform.M11)*180/Math.PI,30)&&session.UndoCount==1&&!session.IsTransactionActive);
    }
    private static void MixedRotation(Action<string,bool> check)
    {
        var session=Session();Guid group=Guid.NewGuid();session.Document.Groups.Add(new(){Id=group});
        var board=new CanvasElement {Kind=ElementKind.Rectangle,Width=100,Height=60,IsBillboard=true,BillboardAnchor=new(500,500),BillboardHeight=80,Transform=Affine2D.Rotation(17),GroupId=group};
        var surface=new CanvasElement {Kind=ElementKind.Rectangle,Width=80,Height=60,Transform=Affine2D.Translation(180,200),GroupId=group};
        session.Document.Elements.AddRange([surface,board]);session.SetSelection([surface.Id,board.Id]);var editor=new EditorController(session){Tool=EditorTool.Select};
        string boardBefore=JsonSerializer.Serialize(board);var surfaceBefore=surface.Transform;var start=editor.SelectionScreenRotationHandle!.Value;
        Send(editor,OverlayPointerKind.Down,start);Send(editor,OverlayPointerKind.Up,start+new PointD(100,30));
        check("mixed-group rotation moves only surface members and keeps every billboard field unchanged",surface.Transform!=surfaceBefore&&JsonSerializer.Serialize(board)==boardBefore&&session.UndoCount==1);
    }
    private static void OrdinaryShift(Action<string,bool> check)
    {
        foreach(bool held in new[]{false,true})
        {
            var session=Session();var item=new CanvasElement {Kind=ElementKind.Rectangle,Width=200,Height=120,Transform=Affine2D.Translation(100,100),Style=new(){Fill=0xFF224466}};
            session.Document.Elements.Add(item);session.SetSelection([item.Id]);var editor=new EditorController(session){Tool=EditorTool.Select};var map=CanvasMapping.Create(session.Document);
            var start=map.ToScreen(new(140,150));var next=map.ToScreen(new(180,180));
            Send(editor,OverlayPointerKind.Down,start,held?OverlayModifiers.Shift:OverlayModifiers.None);Send(editor,OverlayPointerKind.Move,next,held?OverlayModifiers.Shift:OverlayModifiers.None);
            if(!held)editor.ModifiersChanged(OverlayModifiers.Shift,new(next.X,next.Y));
            check("ordinary perspective move constrains from its original canvas origin; Shift held="+held,Near(item.Transform.OffsetX,140)&&Near(item.Transform.OffsetY,100));
            editor.ModifiersChanged(OverlayModifiers.None,new(next.X,next.Y));
            var vertical=map.ToScreen(new(150,220));editor.ModifiersChanged(OverlayModifiers.Shift,new(vertical.X,vertical.Y));
            check("repeated late Shift switches axes without resetting the original move origin; held="+held,Near(item.Transform.OffsetX,100)&&Near(item.Transform.OffsetY,170));
            Send(editor,OverlayPointerKind.Up,vertical,OverlayModifiers.Shift);
            check("Shift drag keeps its existing selection and one move history; held="+held,session.Selection.SetEquals([item.Id])&&session.UndoCount==1&&!session.IsTransactionActive);
        }
    }
    private static void PendingAndSession(Action<string,bool> check)
    {
        foreach(string mutation in new[]{"content","projection","document"})
        {
            var session=Session();var editor=new EditorController(session){Tool=EditorTool.Image,BillboardMode=true};int inserted=0;
            editor.BeginBillboardInsertion((_,_)=>inserted++);Depth(editor,new(300,300));
            if(mutation=="content")session.Add(new(){Kind=ElementKind.Rectangle,Width=20,Height=20});
            else if(mutation=="projection")session.Document.Canvas.Projection.Matrix=session.Document.Canvas.Projection.Matrix with {M13=10};
            else session.ReplaceDocument(session.Document.Clone());
            Send(editor,OverlayPointerKind.Down,new(120,100));
            check("pending billboard insertion is invalidated by "+mutation+" changes without calling its stale callback",inserted==0&&!editor.HasPendingBillboardInsertion&&editor.ActiveBillboardDepthLine is null&&!session.IsTransactionActive);
        }
        var first=Session();var controller=new EditorController(first){Tool=EditorTool.Image,BillboardMode=true};int calls=0;
        controller.BeginBillboardInsertion((_,_)=>calls++);Depth(controller,new(300,300));controller.ReplaceSession(new EditorSession(),new(50,60));
        Send(controller,OverlayPointerKind.Down,new(100,100));
        check("replacing with a nonperspective session clears global creation mode pending placement and guide",!controller.BillboardMode&&!controller.HasPendingBillboardInsertion&&controller.VisibleBillboardDepthLine is null&&calls==0);
    }
}
